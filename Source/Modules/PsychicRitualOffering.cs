using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Two vanilla psychic ritual problems:
    /// 1) Vomiting (gut worms, food poisoning, ...) starts a Vomit job, which drops whatever the pawn carries before it
    ///    starts and again after it ends. An invoker holding the offering loses it, and the ritual is canceled for a
    ///    missing offering. Vomit now keeps the carried thing for pawns in a psychic ritual.
    /// 2) On cleanup the ritual only ends jobs tagged with its lord, but the duty's StandAndStare job has no lord, so a
    ///    participant of a canceled ritual keeps staring at the spot until drafted. Those jobs are now ended too.
    /// </summary>
    public static class PsychicRitualOffering
    {
        public static bool KeepsCarriedThing(Pawn pawn, JobDef def) =>
            def == JobDefOf.Vomit && pawn.carryTracker?.CarriedThing != null && pawn.GetLord()?.LordJob is LordJob_PsychicRitual;
    }

    [HarmonyPatchCategory("PsychicRitualOffering")]
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class PsychicRitualOffering_StartJob
    {
        public static void Prefix(Pawn ___pawn, Job newJob, ref bool? keepCarryingThingOverride)
        {
            if (keepCarryingThingOverride == null && newJob != null && PsychicRitualOffering.KeepsCarriedThing(___pawn, newJob.def))
                keepCarryingThingOverride = true;
        }
    }

    [HarmonyPatchCategory("PsychicRitualOffering")]
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    public static class PsychicRitualOffering_CleanupCurrentJob
    {
        public static void Prefix(Pawn ___pawn, Job ___curJob, ref bool? carryThingAfterJobOverride)
        {
            if (carryThingAfterJobOverride == null && ___curJob != null && PsychicRitualOffering.KeepsCarriedThing(___pawn, ___curJob.def))
                carryThingAfterJobOverride = true;
        }
    }

    [HarmonyPatchCategory("PsychicRitualOffering")]
    [HarmonyPatch(typeof(LordJob_PsychicRitual), nameof(LordJob_PsychicRitual.EndPawnJobOnCleanup))]
    public static class PsychicRitualOffering_EndPawnJobOnCleanup
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (!__result && pawn.CurJobDef == JobDefOf.StandAndStare)
                __result = true;
        }
    }
}
