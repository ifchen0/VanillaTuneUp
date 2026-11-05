using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Prisoners with the right trait help out in their own cell, using vanilla jobs:
    /// - Kind prisoners tend cellmates lying in bed that need tending, always without medicine (so nothing is carried
    ///   and dropped), and feed bedridden cellmates from food or a dispenser in the cell. Checked at the medical rest
    ///   node of the prisoner think tree, ahead of their own food, sleep and recreation. Doctors still tend as usual
    ///   and usually finish first; the prisoner's tend mostly matters when no doctor gets there.
    /// - Hard-working, industrious or neurotic prisoners clean their cell when idle, unless they are also lazy.
    /// Every check starts with the trait, so prisoners without one cost nothing.
    /// </summary>
    public static class PrisonerHelpers
    {
        private static WorkGiver_CleanFilth cleaner;
        private static TraitDef neurotic;
        private static readonly List<Thing> tmpFilth = new List<Thing>();
        private const int CleanQueueLimit = 15;

        // Trait first (cheap, rules out most pawns), then the state checks.
        private static bool CanHelp(Pawn pawn, System.Func<TraitSet, bool> hasTrait) =>
            pawn.IsPrisonerOfColony && pawn.story?.traits != null && hasTrait(pawn.story.traits)
            && pawn.Spawned && !pawn.Downed && pawn.Awake() && !pawn.InMentalState
            && !PrisonBreakUtility.IsPrisonBreaking(pawn) && !HealthAIUtility.ShouldSeekMedicalRest(pawn);

        private static bool IsKind(TraitSet traits) => traits.HasTrait(TraitDefOf.Kind);

        private static bool IsTidy(TraitSet traits)
        {
            int industry = traits.DegreeOfTrait(TraitDefOf.Industriousness);
            if (industry < 0)
                return false;
            neurotic ??= DefDatabase<TraitDef>.GetNamedSilentFail("Neurotic");
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

        /// <summary>A tend (worst bleeder first) or feed job for a bedridden cellmate of a kind prisoner, or null.</summary>
        public static Job TryCareJob(Pawn pawn)
        {
            if (!CanHelp(pawn, IsKind) || OwnNeedsFirst(pawn) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return null;
            Room room = PrisonCell(pawn);
            if (room == null)
                return null;
            bool canTend = !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor);
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
            // No medicine target: the tend driver then uses none, the way a doctor tends under "no medicine".
            if (tendTarget != null)
                return JobMaker.MakeJob(JobDefOf.TendPatient, tendTarget);
            return feedTarget != null ? FeedJob(pawn, feedTarget, room) : null;
        }

        // Same rule as the warden feeding work: bedridden and getting hungry.
        private static bool NeedsFeeding(Pawn other) => WardenFeedUtility.ShouldBeFed(other) && FeedPatientUtility.IsHungry(other);

        // Built like WorkGiver_Warden_Feed; a prisoner only reaches food or a dispenser inside the cell.
        private static Job FeedJob(Pawn pawn, Pawn patient, Room room)
        {
            if (!FoodUtility.TryFindBestFoodSourceFor(pawn, patient, patient.needs.food.CurCategory == HungerCategory.Starving,
                    out Thing food, out ThingDef foodDef, canRefillDispenser: false, canUseInventory: false,
                    canUsePackAnimalInventory: false, allowForbidden: false, allowCorpse: false))
                return null;
            if (food.GetRoom() != room && !(food is Building_NutrientPasteDispenser))
                return null;
            Job job = JobMaker.MakeJob(JobDefOf.FeedPatient, food, patient);
            job.count = FoodUtility.WillIngestStackCountOf(patient, foodDef, FoodUtility.GetNutrition(patient, food, foodDef));
            return job;
        }

        /// <summary>A cleaning job for the filth in a tidy prisoner's own cell, or null.</summary>
        public static Job TryCleanJob(Pawn pawn)
        {
            if (!CanHelp(pawn, IsTidy) || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning))
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
                    if (thing is Filth && thing.GetRoom() == room)
                        tmpFilth.Add(thing);
                }
                // Built here rather than by WorkGiver_CleanFilth.JobOnThing, which also queues filth just past the
                // doors, out of a prisoner's reach. Nearest first, at most 15 like vanilla; HasJobOnThing only on those.
                IntVec3 at = pawn.Position;
                tmpFilth.SortBy(t => t.Position.DistanceToSquared(at));
                Job job = null;
                foreach (Thing filth in tmpFilth)
                {
                    if (!cleaner.HasJobOnThing(pawn, filth))
                        continue;
                    job ??= JobMaker.MakeJob(JobDefOf.Clean);
                    job.AddQueuedTarget(TargetIndex.A, filth);
                    if (job.GetTargetQueue(TargetIndex.A).Count >= CleanQueueLimit)
                        break;
                }
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
