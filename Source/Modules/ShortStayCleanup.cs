using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// The world pawn GC keeps every pawn that was ever a colonist, forever and whatever faction they are in now. Refugee
    /// lodgers (5-20 days) and creepjoiners who left are never redressed either, so they only pile up, alive or dead.
    /// A former colonist outside the player faction who spent under MinDaysAsColonist in the colony, or any creepjoiner
    /// outside it, loses that one reason to be kept; every other vanilla reason (faction leader, quest, logs, tales,
    /// relatives in the colony) still applies.
    /// </summary>
    public static class ShortStayCleanup
    {
        public const float MinDaysAsColonist = 30f;

        public static bool KeepAsColonist(Pawn pawn)
        {
            if (!PawnUtility.EverBeenColonistOrTameAnimal(pawn))
                return false;
            if (pawn.Faction != null && pawn.Faction.IsPlayer)
                return true;
            // Creepjoiners who left are never kept for having been colonists, however long they stayed.
            if (pawn.IsCreepJoiner)
                return false;
            return pawn.records.GetValue(RecordDefOf.TimeAsColonistOrColonyAnimal) >= MinDaysAsColonist * GenDate.TicksPerDay;
        }
    }

    [HarmonyPatchCategory("ShortStayCleanup")]
    [HarmonyPatch(typeof(WorldPawnGC), "GetCriticalPawnReason")]
    public static class ShortStayCleanup_GetCriticalPawnReason
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo everBeenColonist = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.EverBeenColonistOrTameAnimal));
            MethodInfo keepAsColonist = AccessTools.Method(typeof(ShortStayCleanup), nameof(ShortStayCleanup.KeepAsColonist));
            int replaced = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (replaced == 0 && code.Calls(everBeenColonist))
                {
                    replaced++;
                    code.operand = keepAsColonist;
                }
                yield return code;
            }
            if (replaced == 0)
                Log.Warning("[Vanilla Tune-Up] ShortStayCleanup: colonist check not found, short-stay former colonists are kept as in vanilla.");
        }
    }
}
