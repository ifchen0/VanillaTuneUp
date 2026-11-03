using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Prisoners with the right trait help out in their own cell, using vanilla jobs:
    /// - Kind prisoners tend cellmates lying in bed that need tending (with whatever medicine they can reach, usually
    ///   none) and feed bedridden cellmates from food or a dispenser in the cell. Checked at the medical rest node of
    ///   the prisoner think tree, ahead of food, sleep and recreation, since it is the urgent part.
    /// - Hard-working, industrious or neurotic prisoners clean their cell when idle, unless they are also lazy.
    /// Every check starts with the trait, so prisoners without one cost nothing.
    /// </summary>
    public static class PrisonerHelpers
    {
        private static WorkGiver_CleanFilth cleaner;
        private static readonly List<Thing> tmpFilth = new List<Thing>();

        private static bool CanHelp(Pawn pawn) =>
            pawn.IsPrisonerOfColony && pawn.Spawned && !pawn.Downed && pawn.Awake() && !pawn.InMentalState
            && !PrisonBreakUtility.IsPrisonBreaking(pawn) && pawn.story?.traits != null;

        private static bool IsKind(Pawn pawn) => pawn.story.traits.HasTrait(TraitDefOf.Kind);

        private static bool IsTidy(Pawn pawn)
        {
            TraitSet traits = pawn.story.traits;
            TraitDef industriousness = DefDatabase<TraitDef>.GetNamedSilentFail("Industriousness");
            int industry = industriousness != null ? traits.DegreeOfTrait(industriousness) : 0;
            if (industry < 0)
                return false;
            TraitDef neurotic = DefDatabase<TraitDef>.GetNamedSilentFail("Neurotic");
            return industry > 0 || (neurotic != null && traits.HasTrait(neurotic));
        }

        private static Room PrisonCell(Pawn pawn)
        {
            Room room = pawn.GetRoom();
            return room != null && room.IsPrisonCell ? room : null;
        }

        // Too hungry or tired to look after anyone else.
        private static bool OwnNeedsFirst(Pawn pawn) =>
            (pawn.needs?.food != null && pawn.needs.food.CurCategory >= HungerCategory.UrgentlyHungry)
            || (pawn.needs?.rest != null && pawn.needs.rest.CurCategory >= RestCategory.VeryTired);

        /// <summary>A tend or feed job for a bedridden cellmate of a kind prisoner, or null.</summary>
        public static Job TryCareJob(Pawn pawn)
        {
            if (!CanHelp(pawn) || !IsKind(pawn) || OwnNeedsFirst(pawn))
                return null;
            Room room = PrisonCell(pawn);
            if (room == null)
                return null;
            bool canTend = !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);
            Pawn tendTarget = null;
            float worstBleed = -1f;
            Pawn feedTarget = null;
            foreach (Pawn other in pawn.Map.mapPawns.PrisonersOfColonySpawned)
            {
                if (other == pawn || !other.InBed() || other.GetRoom() != room)
                    continue;
                bool tend = canTend && other.health.HasHediffsNeedingTend() && WorkGiver_Tend.GoodLayingStatusForTend(other, pawn);
                bool feed = !tend && feedTarget == null && NeedsFeeding(other);
                if ((!tend && !feed) || !pawn.CanReserveAndReach(other, PathEndMode.Touch, Danger.Deadly))
                    continue;
                if (feed)
                {
                    feedTarget = other;
                    continue;
                }
                float bleed = other.health.hediffSet.BleedRateTotal;
                if (bleed > worstBleed)
                {
                    worstBleed = bleed;
                    tendTarget = other;
                }
            }
            if (tendTarget != null)
            {
                Thing medicine = HealthAIUtility.FindBestMedicine(pawn, tendTarget);
                return medicine != null
                    ? JobMaker.MakeJob(JobDefOf.TendPatient, tendTarget, medicine)
                    : JobMaker.MakeJob(JobDefOf.TendPatient, tendTarget);
            }
            return feedTarget != null ? FeedJob(pawn, feedTarget) : null;
        }

        // Same rule as the warden feeding work: bedridden and getting hungry.
        private static bool NeedsFeeding(Pawn other)
        {
            Need_Food food = other.needs?.food;
            return food != null && WardenFeedUtility.ShouldBeFed(other)
                && food.CurLevelPercentage < food.PercentageThreshHungry + 0.02f;
        }

        // Built like WorkGiver_Warden_Feed; a prisoner only reaches food or a dispenser inside the cell.
        private static Job FeedJob(Pawn pawn, Pawn patient)
        {
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return null;
            if (!FoodUtility.TryFindBestFoodSourceFor(pawn, patient, patient.needs.food.CurCategory == HungerCategory.Starving,
                    out Thing food, out ThingDef foodDef, canRefillDispenser: false, canUseInventory: true,
                    canUsePackAnimalInventory: false, allowForbidden: false, allowCorpse: false))
                return null;
            if (food.GetRoom() != pawn.GetRoom() && !(food is Building_NutrientPasteDispenser))
                return null;
            Job job = JobMaker.MakeJob(JobDefOf.FeedPatient, food, patient);
            job.count = FoodUtility.WillIngestStackCountOf(patient, foodDef, FoodUtility.GetNutrition(patient, food, foodDef));
            return job;
        }

        /// <summary>A cleaning job for the filth in a tidy prisoner's own cell, or null.</summary>
        public static Job TryCleanJob(Pawn pawn)
        {
            if (!CanHelp(pawn) || !IsTidy(pawn) || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning))
                return null;
            Room room = PrisonCell(pawn);
            if (room == null)
                return null;
            cleaner ??= DefDatabase<WorkGiverDef>.GetNamedSilentFail("CleanFilth")?.Worker as WorkGiver_CleanFilth;
            if (cleaner == null)
                return null;
            tmpFilth.Clear();
            try
            {
                foreach (Thing thing in room.ContainedAndAdjacentThings)
                {
                    if (thing is Filth && thing.GetRoom() == room && cleaner.HasJobOnThing(pawn, thing))
                        tmpFilth.Add(thing);
                }
                if (tmpFilth.Count == 0)
                    return null;
                Thing nearest = GenClosest.ClosestThing_Global(pawn.Position, tmpFilth);
                Job job = cleaner.JobOnThing(pawn, nearest);
                // Vanilla also queues filth just past the doors, out of a prisoner's reach.
                job?.targetQueueA?.RemoveAll(t => t.Thing == null || t.Thing.GetRoom() != room);
                return job;
            }
            finally
            {
                tmpFilth.Clear();
            }
        }
    }

    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch(typeof(JobGiver_PatientGoToBed), "TryGiveJob")]
    public static class PrisonerHelpers_Care
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null)
                __result = PrisonerHelpers.TryCareJob(pawn);
        }
    }

    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch(typeof(JobGiver_Wander), "TryGiveJob")]
    public static class PrisonerHelpers_Clean
    {
        public static bool Prefix(JobGiver_Wander __instance, Pawn pawn, ref Job __result)
        {
            if (!(__instance is JobGiver_WanderCurrentRoom))
                return true;
            Job job = PrisonerHelpers.TryCleanJob(pawn);
            if (job == null)
                return true;
            __result = job;
            return false;
        }
    }
}
