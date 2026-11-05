using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// Colonists ignore a prisoner's reservations (vanilla only has prisoners respect colonists'), so doctors and
    /// wardens skip a patient a kind prisoner is already tending or feeding (unless ordered), and what the prisoner
    /// drops when such a job ends is not forbidden (vanilla forbids drops by pawns of a hostile faction).
    /// </summary>
    public static class PrisonerHelpers
    {
        private static WorkGiver_CleanFilth cleaner;
        private static TraitDef neurotic;
        private static readonly List<Thing> tmpFilth = new List<Thing>();
        private const int CleanQueueLimit = 15;

        // The last care job given to each kind prisoner and to each patient. Job objects are pooled and reused,
        // so a record only counts while the helper is still on that job object with the same def and patient.
        private sealed class Care
        {
            public Pawn Helper;
            public Pawn Patient;
            public Job Job;
            public JobDef Def;

            public bool Active =>
                Helper.Spawned && Helper.CurJob == Job && Job.def == Def
                && (Job.targetA.Thing == Patient || Job.targetB.Thing == Patient);
        }

        private static readonly ConditionalWeakTable<Pawn, Care> careByPatient = new ConditionalWeakTable<Pawn, Care>();
        private static readonly ConditionalWeakTable<Pawn, Care> careByHelper = new ConditionalWeakTable<Pawn, Care>();

        private static Job TrackCare(Job job, Pawn helper, Pawn patient)
        {
            if (job == null)
                return null;
            var care = new Care { Helper = helper, Patient = patient, Job = job, Def = job.def };
            careByPatient.Remove(patient);
            careByPatient.Add(patient, care);
            careByHelper.Remove(helper);
            careByHelper.Add(helper, care);
            return job;
        }

        /// <summary>Whether this pawn is a kind prisoner on a tend or feed job it was given by this module.</summary>
        public static bool OnCareJob(Pawn helper) =>
            helper != null && careByHelper.TryGetValue(helper, out Care care) && care.Active;

        /// <summary>Whether a kind prisoner is tending or feeding this patient right now.</summary>
        public static bool CaredForByPrisoner(Pawn patient) =>
            careByPatient.TryGetValue(patient, out Care care) && care.Active;

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

        /// <summary>A tend or feed job for a bedridden cellmate of a kind prisoner, or null.</summary>
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
            if (tendTarget != null)
            {
                Thing medicine = HealthAIUtility.FindBestMedicine(pawn, tendTarget);
                return TrackCare(medicine != null
                    ? JobMaker.MakeJob(JobDefOf.TendPatient, tendTarget, medicine)
                    : JobMaker.MakeJob(JobDefOf.TendPatient, tendTarget), pawn, tendTarget);
            }
            return feedTarget != null ? TrackCare(FeedJob(pawn, feedTarget, room), pawn, feedTarget) : null;
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

    /// <summary>Doctors leave a patient to the kind prisoner already tending it, unless ordered to tend.</summary>
    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch]
    public static class PrisonerHelpers_LeaveTendToPrisoner
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // WorkGiver_TendOther and TendOtherUrgent call this base check first.
            yield return AccessTools.Method(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.HasJobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_FeedPatient), nameof(WorkGiver_FeedPatient.HasJobOnThing));
        }

        public static void Postfix(Thing t, bool forced, ref bool __result)
        {
            if (__result && !forced && t is Pawn patient && PrisonerHelpers.CaredForByPrisoner(patient))
                __result = false;
        }
    }

    /// <summary>Wardens leave a prisoner to the kind cellmate already feeding it, unless ordered to feed.</summary>
    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch(typeof(WorkGiver_Warden_Feed), nameof(WorkGiver_Warden_Feed.JobOnThing))]
    public static class PrisonerHelpers_LeaveFeedToPrisoner
    {
        public static void Postfix(Thing t, bool forced, ref Job __result)
        {
            if (__result != null && !forced && t is Pawn patient && PrisonerHelpers.CaredForByPrisoner(patient))
                __result = null;
        }
    }

    /// <summary>
    /// Medicine or food a kind prisoner drops when its tend or feed job ends stays allowed. Vanilla drops the carried
    /// thing after clearing curJob, so the ending job is noted while CleanupCurrentJob runs.
    /// </summary>
    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    public static class PrisonerHelpers_EndingCareJob
    {
        public static Pawn Helper;

        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> jobPawn =
            AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

        public static void Prefix(Pawn_JobTracker __instance, out Pawn __state)
        {
            __state = Helper;
            Pawn pawn = jobPawn(__instance);
            if (pawn != null && pawn.IsPrisonerOfColony && PrisonerHelpers.OnCareJob(pawn))
                Helper = pawn;
        }

        public static void Finalizer(Pawn __state) => Helper = __state;
    }

    [HarmonyPatchCategory("PrisonerHelpers")]
    [HarmonyPatch]
    public static class PrisonerHelpers_KeepDropAllowed
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Pawn_CarryTracker)))
            {
                if (method.Name == nameof(Pawn_CarryTracker.TryDropCarriedThing))
                    yield return method;
            }
        }

        public static void Postfix(Pawn_CarryTracker __instance, bool __result, ref Thing resultingThing)
        {
            if (__result && resultingThing != null && PrisonerHelpers_EndingCareJob.Helper != null
                && __instance.pawn == PrisonerHelpers_EndingCareJob.Helper && resultingThing.IsForbidden(Faction.OfPlayer))
                resultingThing.SetForbidden(false, warnOnFail: false);
        }
    }
}
