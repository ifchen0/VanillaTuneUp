using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla drops the food policy whenever the eater is in a mental state, regardless of who is fetching the food.
    /// That is meant for a breaking pawn feeding itself, but it also lets a warden deliver restricted food to a
    /// prisoner who is mid-break. Keep the policy when someone else is the getter; self-feeding stays vanilla.
    /// </summary>
    [HarmonyPatchCategory("PrisonerFoodPolicy")]
    [HarmonyPatch(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.GetCurrentRespectedRestriction))]
    public static class Patch_GetCurrentRespectedRestriction
    {
        public static void Postfix(Pawn_FoodRestrictionTracker __instance, Pawn getter, ref FoodPolicy __result)
        {
            if (__result != null || getter == null)
                return;
            Pawn pawn = __instance.pawn;
            if (getter == pawn || !pawn.InMentalState || !__instance.Configurable)
                return;
            // Same faction gate vanilla applies before its mental state check.
            if (pawn.Faction != Faction.OfPlayer && getter.Faction != Faction.OfPlayer)
                return;
            __result = __instance.CurrentFoodPolicy;
        }
    }

    /// <summary>
    /// Freshly captured prisoners are often both hungry and bleeding. Warden feeding can be picked before doctor
    /// tending, letting the prisoner bleed out while being spoon-fed. Skip non-forced feeding while the prisoner is
    /// still bleeding so the pawn moves on to other work such as tending; bleeding kills far faster than hunger.
    /// </summary>
    [HarmonyPatchCategory("PrisonerBleeding")]
    [HarmonyPatch]
    public static class Patch_WardenFeed_SkipWhileBleeding
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorkGiver_Warden_Feed), nameof(WorkGiver_Warden_Feed.JobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_Warden_DeliverFood), nameof(WorkGiver_Warden_DeliverFood.JobOnThing));
        }

        public static bool Prefix(Thing t, bool forced, ref Job __result)
        {
            if (forced || !(t is Pawn prisoner) || prisoner.health.hediffSet.BleedRateTotal <= 0f)
                return true;
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// Vanilla brings one prisoner's worth per trip, so a crowded prison always has some prisoners waiting hungry.
    /// Keep vanilla's trigger (2% from Hungry, so the food is eaten on arrival and does not sit and rot), but bring
    /// enough for every prisoner in the cell at that point, one delivery per cell at a time unless someone there is
    /// already UrgentlyHungry.
    /// </summary>
    [HarmonyPatchCategory("PrisonerFoodDelivery")]
    [HarmonyPatch(typeof(WorkGiver_Warden_DeliverFood), nameof(WorkGiver_Warden_DeliverFood.JobOnThing))]
    public static class Patch_WardenDeliverFood_WholeRoom
    {
        // Same margin as vanilla WorkGiver_Warden_DeliverFood.
        private const float HungryMargin = 0.02f;

        // A full stack of simple meals (10 x 0.9), what one warden usually carries per trip.
        private const float FullTripNutrition = 9f;

        private static readonly Func<WorkGiver_Warden, Pawn, Thing, bool, bool> ShouldTakeCareOfPrisoner =
            AccessTools.MethodDelegate<Func<WorkGiver_Warden, Pawn, Thing, bool, bool>>(
                AccessTools.Method(typeof(WorkGiver_Warden), "ShouldTakeCareOfPrisoner"));

        // Runs after the bleeding check, which may already have skipped the job.
        [HarmonyPriority(Priority.Low)]
        public static bool Prefix(WorkGiver_Warden_DeliverFood __instance, Pawn pawn, Thing t, bool forced, ref Job __result, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            __result = MakeJob(__instance, pawn, t, forced);
            return false;
        }

        private static Job MakeJob(WorkGiver_Warden giver, Pawn warden, Thing t, bool forced)
        {
            if (!ShouldTakeCareOfPrisoner(giver, warden, t, forced))
                return null;
            Pawn prisoner = (Pawn)t;
            if (!prisoner.guest.CanBeBroughtFood || !prisoner.Position.IsInPrisonCell(prisoner.Map))
                return null;
            if (!WantsFoodSoon(prisoner) || WardenFeedUtility.ShouldBeFed(prisoner))
                return null;
            Room room = prisoner.GetRoom();
            if (room == null)
                return null;
            Thing carried = prisoner.carryTracker.CarriedThing;
            if (carried != null && NutritionAvailableForFrom(prisoner, carried) > 0f)
                return null;

            // Cheap room check before the expensive food search, the reverse of vanilla's order.
            float shortfall = RoomShortfall(prisoner, room, out bool anyoneUrgent);
            if (shortfall <= 0f)
                return null;

            // One delivery per cell at a time: once it arrives, its food counts as supply for the next one.
            // A prisoner already UrgentlyHungry means single trips are falling behind (a very large cell), so allow as
            // many as the shortfall needs full trips; food on the way is not counted, so this caps the overdelivery.
            // Plain Hungry is reached seconds after the trigger, so it cannot tell a slow cell from a normal one.
            int allowed = anyoneUrgent ? Mathf.CeilToInt(shortfall / FullTripNutrition) : 1;
            if (DeliveriesUnderway(room, warden.Map) >= allowed)
                return null;

            if (!FoodUtility.TryFindBestFoodSourceFor(warden, prisoner, prisoner.needs.food.CurCategory == HungerCategory.Starving, out Thing foodSource, out ThingDef foodDef, canRefillDispenser: false, canUseInventory: true, canUsePackAnimalInventory: false, allowForbidden: false, allowCorpse: false, allowSociallyImproper: false, allowHarvest: false, forceScanWholeMap: false, ignoreReservations: false, calculateWantedStackCount: true))
                return null;
            if (foodSource.GetRoom() == room)
                return null;

            int count;
            if (foodSource is Building_NutrientPasteDispenser)
            {
                // A dispenser hands out one meal per trip whatever the count says.
                count = 1;
            }
            else
            {
                float nutrition = FoodUtility.GetNutrition(prisoner, foodSource, foodDef);
                count = nutrition > 0f ? Mathf.CeilToInt(shortfall / nutrition) : 1;
                count = Mathf.Min(count, foodSource.stackCount, warden.carryTracker.MaxStackSpaceEver(foodDef));
                // Leave the part of the stack other pawns have already reserved, e.g. a warden feeding a bedridden prisoner.
                if (foodSource.Spawned)
                    count = Mathf.Min(count, warden.Map.reservationManager.CanReserveStack(warden, foodSource, 10));
                count = Mathf.Max(count, 1);
            }
            Job job = JobMaker.MakeJob(JobDefOf.DeliverFood, foodSource, prisoner);
            job.count = count;
            job.targetC = RCellFinder.SpotToChewStandingNear(prisoner, foodSource);
            return job;
        }

        private static bool WantsFoodSoon(Pawn p)
        {
            Need_Food food = p.needs?.food;
            return food != null && food.CurLevelPercentage < food.PercentageThreshHungry + HungryMargin;
        }

        /// <summary>Nutrition the cell still lacks for prisoners who want food now.</summary>
        private static float RoomShortfall(Pawn prisoner, Room room, out bool anyoneUrgent)
        {
            anyoneUrgent = false;
            float need = 0f;
            float supply = 0f;
            List<Region> regions = room.Regions;
            for (int i = 0; i < regions.Count; i++)
            {
                List<Thing> foods = regions[i].ListerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
                for (int j = 0; j < foods.Count; j++)
                {
                    Thing thing = foods[j];
                    // Same filter as vanilla: dispensers and anything better than kibble-grade food.
                    if (!thing.def.IsIngestible || thing.def.ingestible.preferability > FoodPreferability.DesperateOnlyForHumanlikes)
                        supply += NutritionAvailableForFrom(prisoner, thing);
                }
                List<Thing> pawns = regions[i].ListerThings.ThingsInGroup(ThingRequestGroup.Pawn);
                for (int j = 0; j < pawns.Count; j++)
                {
                    Pawn p = (Pawn)pawns[j];
                    if (!p.IsPrisonerOfColony || !WantsFoodSoon(p) || WardenFeedUtility.ShouldBeFed(p))
                        continue;
                    Thing held = p.carryTracker.CarriedThing;
                    if (held != null && p.WillEat(held))
                        continue;
                    need += p.needs.food.NutritionWanted;
                    if (p.needs.food.CurCategory >= HungerCategory.UrgentlyHungry)
                        anyoneUrgent = true;
                }
            }
            return need - supply;
        }

        /// <summary>Number of pawns bringing food to this cell right now, so wardens do not all fetch a full load.</summary>
        private static int DeliveriesUnderway(Room room, Map map)
        {
            int count = 0;
            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < pawns.Count; i++)
            {
                Job job = pawns[i].CurJob;
                if (job != null && job.def == JobDefOf.DeliverFood && job.targetB.Thing is Pawn deliveree && deliveree.GetRoom() == room)
                    count++;
            }
            return count;
        }

        // Copy of the private vanilla WorkGiver_Warden_DeliverFood.NutritionAvailableForFrom.
        private static float NutritionAvailableForFrom(Pawn p, Thing foodSource)
        {
            if (foodSource.def.IsNutritionGivingIngestible && p.WillEat(foodSource))
                return foodSource.GetStatValue(StatDefOf.Nutrition) * foodSource.stackCount;
            if (p.RaceProps.ToolUser && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                && foodSource is Building_NutrientPasteDispenser dispenser && dispenser.CanDispenseNow
                && p.CanReach(dispenser.InteractionCell, PathEndMode.OnCell, Danger.Some))
                return 99999f;
            return 0f;
        }
    }

    /// <summary>
    /// Vanilla reserves only one prisoner's worth of the food stack for a delivery. Reserve the whole batched count
    /// up front so another pawn cannot claim the part this warden is about to pick up.
    /// </summary>
    [HarmonyPatchCategory("PrisonerFoodDelivery")]
    [HarmonyPatch(typeof(JobDriver_FoodDeliver), nameof(JobDriver_FoodDeliver.TryMakePreToilReservations))]
    public static class Patch_FoodDeliver_ReserveBatch
    {
        public static void Postfix(JobDriver_FoodDeliver __instance, ref bool __result)
        {
            if (!__result)
                return;
            Job job = __instance.job;
            Thing food = job.targetA.Thing;
            if (job.count <= 1 || food == null || !food.Spawned || food is Building_NutrientPasteDispenser)
                return;
            Pawn pawn = __instance.pawn;
            // Someone may have reserved part of the stack since the job was made; shrink the batch instead of failing.
            int count = Mathf.Min(job.count, pawn.Map.reservationManager.CanReserveStack(pawn, food, 10));
            if (count >= 1 && pawn.Reserve(food, job, 10, count, null, errorOnFailed: false))
                job.count = count;
            else
                job.count = 1;
        }
    }

    /// <summary>
    /// Vanilla gives every new prisoner the default food policy, so a prisoner-only policy has to be picked by hand
    /// for each capture. Give the policy named in the settings to a pawn when it becomes a prisoner of the colony, and
    /// put it back on the default policy when it stops being one (recruited, released) while still on that policy.
    /// Policies are saved per game, so the setting stores the policy name and matches it in the loaded save.
    /// </summary>
    [HarmonyPatchCategory("PrisonerDefaultFoodPolicy")]
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    public static class PrisonerDefaultFoodPolicy
    {
        public static void Prefix(Pawn_GuestTracker __instance, out bool __state)
        {
            __state = __instance.IsPrisoner && __instance.HostFaction == Faction.OfPlayer;
        }

        public static void Postfix(Pawn_GuestTracker __instance, Pawn ___pawn, bool __state)
        {
            Pawn_FoodRestrictionTracker tracker = ___pawn?.foodRestriction;
            FoodPolicy policy = Configured();
            if (tracker == null || policy == null)
                return;
            bool prisoner = __instance.IsPrisoner && __instance.HostFaction == Faction.OfPlayer;
            if (prisoner && !__state)
                tracker.CurrentFoodPolicy = policy;
            else if (!prisoner && __state && tracker.CurrentFoodPolicy == policy)
                tracker.CurrentFoodPolicy = Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();
        }

        /// <summary>The policy named in the settings in the current game, or null when unset or missing.</summary>
        public static FoodPolicy Configured()
        {
            string label = VanillaTuneUpMod.Settings.prisonerFoodPolicy;
            if (label.NullOrEmpty())
                return null;
            return Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions.Find(p => p.label == label);
        }

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            string current = settings.prisonerFoodPolicy.NullOrEmpty()
                ? "VTU_PrisonerPolicyNone".Translate().ToString()
                : settings.prisonerFoodPolicy;
            List<FoodPolicy> policies = Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions;
            Rect row = listing.GetRect(30f);
            Widgets.Label(row.LeftHalf(), "VTU_PrisonerPolicyLabel".Translate());
            if (policies == null)
            {
                // Outside a game there is no policy list to pick from.
                Widgets.Label(row.RightHalf(), current);
                GUI.color = Color.gray;
                listing.Label("VTU_PrisonerPolicyNoGame".Translate());
                GUI.color = Color.white;
                return;
            }
            if (Widgets.ButtonText(row.RightHalf(), current))
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("VTU_PrisonerPolicyNone".Translate(), () => settings.prisonerFoodPolicy = "")
                };
                foreach (FoodPolicy policy in policies)
                {
                    string label = policy.label;
                    options.Add(new FloatMenuOption(label, () => settings.prisonerFoodPolicy = label));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (!settings.prisonerFoodPolicy.NullOrEmpty() && Configured() == null)
            {
                GUI.color = ColorLibrary.Gold;
                listing.Label("VTU_PrisonerPolicyMissing".Translate(settings.prisonerFoodPolicy));
                GUI.color = Color.white;
            }
            FoodPolicy configured = Configured();
            if (configured != null && listing.ButtonText("VTU_PrisonerPolicyApplyAll".Translate()))
            {
                int count = 0;
                foreach (Pawn prisoner in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_PrisonersOfColony)
                {
                    if (prisoner.foodRestriction == null)
                        continue;
                    prisoner.foodRestriction.CurrentFoodPolicy = configured;
                    count++;
                }
                Messages.Message("VTU_PrisonerPolicyApplied".Translate(count, configured.label),
                    MessageTypeDefOf.TaskCompletion, historical: false);
            }
        }
    }
}
