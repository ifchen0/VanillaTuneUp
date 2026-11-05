using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Prisoners with the right trait help out in their own cell, using vanilla jobs:
    /// - Kind prisoners feed bedridden cellmates from food or a dispenser in the cell. Checked at the medical rest node
    ///   of the prisoner think tree, ahead of their own food, sleep and recreation. Colonists ignore a hostile
    ///   prisoner's reservations (vanilla only has prisoners respect colonists'), so wardens and feeding mechs skip a
    ///   cellmate a kind prisoner is already feeding, unless ordered to.
    /// - Hard-working, industrious or neurotic prisoners clean their cell when idle, unless they are also lazy.
    /// Every check starts with the trait, so prisoners without one cost nothing.
    /// </summary>
    public static class PrisonerHelpers
    {
        private static WorkGiver_CleanFilth cleaner;
        private static TraitDef neurotic;
        private static readonly List<Thing> tmpFilth = new List<Thing>();
        private const int CleanQueueLimit = 15;

        // The feed job last given for each patient. Job objects are pooled and reused, so it only counts while the
        // helper is still on that job object, feeding that patient.
        private sealed class Feeding
        {
            public Pawn Helper;
            public Job Job;
        }

        private static readonly ConditionalWeakTable<Pawn, Feeding> feedingByPatient = new ConditionalWeakTable<Pawn, Feeding>();

        /// <summary>Whether a kind prisoner is feeding this patient right now.</summary>
        public static bool FedByPrisoner(Pawn patient) =>
            feedingByPatient.TryGetValue(patient, out Feeding feeding) && feeding.Helper.Spawned
            && feeding.Helper.CurJob == feeding.Job && feeding.Job.def == JobDefOf.FeedPatient
            && feeding.Job.targetB.Thing == patient;

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

        /// <summary>A feed job for a bedridden cellmate of a kind prisoner, or null.</summary>
        public static Job TryCareJob(Pawn pawn)
        {
            if (!CanHelp(pawn, IsKind) || OwnNeedsFirst(pawn) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return null;
            Room room = PrisonCell(pawn);
            if (room == null)
                return null;
            foreach (Pawn other in pawn.Map.mapPawns.PrisonersOfColonySpawned)
            {
                if (other == pawn || !other.InBed() || other.GetRoom() != room || !NeedsFeeding(other)
                    || !pawn.CanReserveAndReach(other, PathEndMode.Touch, Danger.Deadly))
                    continue;
                Job job = FeedJob(pawn, other, room);
                if (job == null)
                    return null;
                Feeding feeding = feedingByPatient.GetValue(other, _ => new Feeding());
                feeding.Helper = pawn;
                feeding.Job = job;
                return job;
            }
            return null;
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

    /// <summary>Wardens and feeding mechs leave a cellmate to the kind prisoner already feeding it, unless ordered.</summary>
    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch(typeof(WorkGiver_FeedPatient), nameof(WorkGiver_FeedPatient.HasJobOnThing))]
    public static class PrisonerHelpers_LeaveFeedToPrisoner_Patient
    {
        public static void Postfix(Thing t, bool forced, ref bool __result)
        {
            if (__result && !forced && t is Pawn patient && PrisonerHelpers.FedByPrisoner(patient))
                __result = false;
        }
    }

    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch(typeof(WorkGiver_Warden_Feed), nameof(WorkGiver_Warden_Feed.JobOnThing))]
    public static class PrisonerHelpers_LeaveFeedToPrisoner
    {
        public static void Postfix(Thing t, bool forced, ref Job __result)
        {
            if (__result != null && !forced && t is Pawn patient && PrisonerHelpers.FedByPrisoner(patient))
                __result = null;
        }
    }
}
