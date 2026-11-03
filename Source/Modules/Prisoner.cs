using System;
using System.Collections.Generic;
using System.Linq;
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
    /// A prisoner with an active surgery bill is skipped the same way, so the doctor gets to operate first.
    /// </summary>
    [HarmonyPatchCategory("PrisonerBleeding")]
    [HarmonyPatch]
    public static class Patch_WardenFeed_SkipWhileBleeding
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorkGiver_Warden_Feed), nameof(WorkGiver_Warden_Feed.JobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_Warden_DeliverFood), nameof(WorkGiver_Warden_DeliverFood.JobOnThing));
            // Colony mechs cannot do warden work, so vanilla lets them feed bed-bound prisoners as patients.
            yield return AccessTools.Method(typeof(WorkGiver_FeedPatient), nameof(WorkGiver_FeedPatient.JobOnThing));
        }

        public static bool Prefix(Thing t, bool forced, ref Job __result)
        {
            if (forced || !(t is Pawn prisoner) || !prisoner.IsPrisonerOfColony)
                return true;
            if (prisoner.health.hediffSet.BleedRateTotal <= 0f && !AwaitsSurgery(prisoner))
                return true;
            __result = null;
            return false;
        }

        // Unlike bleeding, a surgery bill can wait forever (no medicine, no capable doctor), so feed anyway once
        // the prisoner is urgently hungry.
        private static bool AwaitsSurgery(Pawn prisoner)
        {
            if (!prisoner.health.surgeryBills.AnyShouldDoNow)
                return false;
            return prisoner.needs?.food == null || prisoner.needs.food.CurCategory < HungerCategory.UrgentlyHungry;
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

        // Runs after the bleeding/surgery check, which may already have skipped the job.
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
    /// Vanilla gives every new prisoner and slave the default food policy and every new slave the default apparel
    /// policy, so a prisoner-only or slave-only policy (Ideology even makes a "Slave" apparel policy at the start) has
    /// to be picked by hand each time. Give the policies named in the settings to a pawn when it becomes a prisoner or
    /// slave of the colony, and put it back on the default policy when it stops being one (recruited, released,
    /// enslaved, emancipated) while still on that policy. Apparel is for slaves only: prisoners never dress by a
    /// policy in vanilla. Policies are saved per game, so the settings store policy names and match them in the
    /// loaded save.
    /// </summary>
    [HarmonyPatchCategory("PrisonerDefaultFoodPolicy")]
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    public static class PrisonerDefaultFoodPolicy
    {
        public enum Role { None, Prisoner, Slave }

        private static Role RoleOf(Pawn pawn)
        {
            if (pawn.IsPrisonerOfColony)
                return Role.Prisoner;
            return pawn.IsSlaveOfColony ? Role.Slave : Role.None;
        }

        private static FoodPolicy PolicyFor(Role role) => role switch
        {
            Role.Prisoner => Configured(),
            Role.Slave => ConfiguredForSlaves(),
            _ => null
        };

        public static void Prefix(Pawn ___pawn, out Role __state)
        {
            __state = ___pawn == null ? Role.None : RoleOf(___pawn);
        }

        public static void Postfix(Pawn ___pawn, Role __state)
        {
            if (___pawn == null)
                return;
            Role role = RoleOf(___pawn);
            if (role == __state)
                return;
            // Enslaving a prisoner leaves the prisoner policy and takes the slave one in the same call.
            Pawn_FoodRestrictionTracker food = ___pawn.foodRestriction;
            if (food != null)
            {
                FoodPolicy old = PolicyFor(__state);
                if (old != null && food.CurrentFoodPolicy == old)
                    food.CurrentFoodPolicy = Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();
                FoodPolicy policy = PolicyFor(role);
                if (policy != null)
                    food.CurrentFoodPolicy = policy;
            }
            Pawn_OutfitTracker outfits = ___pawn.outfits;
            ApparelPolicy apparel = ConfiguredApparelForSlaves();
            if (outfits == null || apparel == null)
                return;
            if (role == Role.Slave)
                outfits.CurrentApparelPolicy = apparel;
            else if (__state == Role.Slave && outfits.CurrentApparelPolicy == apparel)
                outfits.CurrentApparelPolicy = Current.Game.outfitDatabase.DefaultOutfit();
        }

        /// <summary>The prisoner policy named in the settings in the current game, or null when unset or missing.</summary>
        public static FoodPolicy Configured() => FindPolicy(VanillaTuneUpMod.Settings.prisonerFoodPolicy);

        /// <summary>The slave policy named in the settings in the current game, or null when unset or missing.</summary>
        public static FoodPolicy ConfiguredForSlaves() => FindPolicy(VanillaTuneUpMod.Settings.slaveFoodPolicy);

        /// <summary>The slave apparel policy named in the settings in the current game, or null when unset or missing.</summary>
        public static ApparelPolicy ConfiguredApparelForSlaves() => FindApparelPolicy(VanillaTuneUpMod.Settings.slaveApparelPolicy);

        /// <summary>The food policy with this name in the current game, or null when unset or missing.</summary>
        public static FoodPolicy FindPolicy(string label)
        {
            if (label.NullOrEmpty())
                return null;
            return Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions.Find(p => p.label == label);
        }

        private static ApparelPolicy FindApparelPolicy(string label)
        {
            if (label.NullOrEmpty())
                return null;
            return Current.Game?.outfitDatabase?.AllOutfits.Find(p => p.label == label);
        }

        /// <summary>A labelled button that picks a food policy of the loaded game by name, with a warning when it is missing.</summary>
        public static void DrawPolicyPicker(Listing_Standard listing, string labelKey, string noneKey, string missingKey,
            string selected, Action<string> select) =>
            DrawPicker(listing, labelKey, noneKey, missingKey, selected, select,
                Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions.Select(p => p.label).ToList());

        private static void DrawApparelPicker(Listing_Standard listing, string labelKey, string noneKey, string missingKey,
            string selected, Action<string> select) =>
            DrawPicker(listing, labelKey, noneKey, missingKey, selected, select,
                Current.Game?.outfitDatabase?.AllOutfits.Select(p => p.label).ToList());

        // labels is null outside a game, where there is no policy list to pick from.
        private static void DrawPicker(Listing_Standard listing, string labelKey, string noneKey, string missingKey,
            string selected, Action<string> select, List<string> labels)
        {
            string current = selected.NullOrEmpty() ? noneKey.Translate().ToString() : selected;
            Rect row = listing.GetRect(30f);
            Widgets.Label(row.LeftHalf(), labelKey.Translate());
            if (labels == null)
            {
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
                    new FloatMenuOption(noneKey.Translate(), () => select(""))
                };
                foreach (string label in labels)
                    options.Add(new FloatMenuOption(label, () => select(label)));
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (!selected.NullOrEmpty() && !labels.Contains(selected))
            {
                GUI.color = ColorLibrary.Gold;
                listing.Label(missingKey.Translate(selected));
                GUI.color = Color.white;
            }
        }

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            DrawPolicyPicker(listing, "VTU_PrisonerPolicyLabel", "VTU_PrisonerPolicyNone", "VTU_PrisonerPolicyMissing",
                settings.prisonerFoodPolicy, label => settings.prisonerFoodPolicy = label);
            FoodPolicy prisonerFood = Configured();
            DrawApplyButton(listing, prisonerFood?.label, "VTU_PrisonerPolicyApplyAll", "VTU_PrisonerPolicyApplied",
                PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_PrisonersOfColony,
                pawn => SetFood(pawn, prisonerFood));
            if (!ModsConfig.IdeologyActive)
                return;
            listing.Gap(6f);
            DrawPolicyPicker(listing, "VTU_SlavePolicyLabel", "VTU_PrisonerPolicyNone", "VTU_SlavePolicyMissing",
                settings.slaveFoodPolicy, label => settings.slaveFoodPolicy = label);
            FoodPolicy slaveFood = ConfiguredForSlaves();
            DrawApplyButton(listing, slaveFood?.label, "VTU_SlavePolicyApplyAll", "VTU_SlavePolicyApplied",
                PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_SlavesOfColony,
                pawn => SetFood(pawn, slaveFood));
            listing.Gap(6f);
            DrawApparelPicker(listing, "VTU_SlaveApparelLabel", "VTU_PrisonerPolicyNone", "VTU_SlaveApparelMissing",
                settings.slaveApparelPolicy, label => settings.slaveApparelPolicy = label);
            ApparelPolicy slaveApparel = ConfiguredApparelForSlaves();
            DrawApplyButton(listing, slaveApparel?.label, "VTU_SlaveApparelApplyAll", "VTU_SlaveApparelApplied",
                PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_SlavesOfColony,
                pawn => SetApparel(pawn, slaveApparel));
        }

        private static bool SetFood(Pawn pawn, FoodPolicy policy)
        {
            if (pawn.foodRestriction == null)
                return false;
            pawn.foodRestriction.CurrentFoodPolicy = policy;
            return true;
        }

        private static bool SetApparel(Pawn pawn, ApparelPolicy policy)
        {
            if (pawn.outfits == null)
                return false;
            pawn.outfits.CurrentApparelPolicy = policy;
            return true;
        }

        // apply sets the policy on one pawn and says whether it could.
        private static void DrawApplyButton(Listing_Standard listing, string policyLabel, string buttonKey,
            string messageKey, List<Pawn> pawns, Func<Pawn, bool> apply)
        {
            if (policyLabel == null || !listing.ButtonText(buttonKey.Translate()))
                return;
            int count = pawns.Count(apply);
            Messages.Message(messageKey.Translate(count, policyLabel), MessageTypeDefOf.TaskCompletion, historical: false);
        }
    }
}
