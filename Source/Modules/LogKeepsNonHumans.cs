using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// The world pawn GC keeps a pawn while a combat or social log entry is about them, but only humanlikes. Animals,
    /// mechanoids and insects are discarded, and discarding a pawn deletes every log entry about them, so a fight with
    /// a manhunter pack or a mech raid soon shows as an empty battle in the colonists' logs. Non-humanlikes are now kept
    /// the same way; the logs themselves still expire (battles after about a week, the social log at 150 entries),
    /// after which the GC discards them as before.
    /// </summary>
    [HarmonyPatchCategory("LogKeepsNonHumans")]
    [HarmonyPatch(typeof(WorldPawnGC), "GetCriticalPawnReason")]
    public static class LogKeepsNonHumans
    {
        public static void Postfix(Pawn pawn, ref string __result)
        {
            if (__result != null || pawn.Discarded || pawn.RaceProps.Humanlike || Current.ProgramState != ProgramState.Playing)
                return;
            if (Find.PlayLog.AnyEntryConcerns(pawn))
                __result = "InPlayLog";
            else if (Find.BattleLog.AnyEntryConcerns(pawn))
                __result = "InBattleLog";
        }
    }
}
