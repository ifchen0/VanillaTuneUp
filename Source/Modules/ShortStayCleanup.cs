using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Two kinds of world pawns the GC keeps forever although nothing needs them:
    /// - Everyone who was ever a colonist, whatever faction they are in now. Refugee lodgers (5-20 days) and creepjoiners
    ///   who left are never redressed either, so they only pile up, alive or dead. A former colonist outside the player
    ///   faction who spent under MinDaysAsColonist in the colony, or any creepjoiner outside it, loses that reason.
    /// - Every faction leader ever generated: Faction.TryGenerateNewLeader passes them to the world as KeepForever and
    ///   nothing takes them off that list when they stop being leader or their faction is removed. A pawn of a leader
    ///   kind who leads no faction now and never was a colonist is no longer kept for being on that list.
    /// Every other vanilla reason (current faction leader, quest, logs, tales, relatives in the colony) still applies.
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

        private static HashSet<PawnKindDef> leaderKinds;

        public static bool IsLeaderKind(PawnKindDef kind)
        {
            if (leaderKinds == null)
            {
                leaderKinds = new HashSet<PawnKindDef>(DefDatabase<PawnKindDef>.AllDefs.Where(k => k.factionLeader));
                foreach (FactionDef f in DefDatabase<FactionDef>.AllDefs)
                {
                    if (f.fixedLeaderKinds != null)
                        leaderKinds.UnionWith(f.fixedLeaderKinds);
                }
            }
            return kind != null && leaderKinds.Contains(kind);
        }

        public static bool IsFormerLeader(Pawn pawn) =>
            IsLeaderKind(pawn.kindDef)
            && !PawnUtility.IsFactionLeader(pawn)
            && !PawnUtility.EverBeenColonistOrTameAnimal(pawn);

        // ForcefullyKeptPawns.Contains(pawn), minus former leaders kept only by vanilla's leader generation.
        public static bool ForceKept(HashSet<Pawn> forcefullyKept, Pawn pawn) =>
            forcefullyKept.Contains(pawn) && !IsFormerLeader(pawn);
    }

    [HarmonyPatchCategory("ShortStayCleanup")]
    [HarmonyPatch(typeof(WorldPawnGC), "GetCriticalPawnReason")]
    public static class ShortStayCleanup_GetCriticalPawnReason
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo everBeenColonist = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.EverBeenColonistOrTameAnimal));
            MethodInfo keepAsColonist = AccessTools.Method(typeof(ShortStayCleanup), nameof(ShortStayCleanup.KeepAsColonist));
            MethodInfo forcefullyKept = AccessTools.PropertyGetter(typeof(WorldPawns), nameof(WorldPawns.ForcefullyKeptPawns));
            MethodInfo setContains = AccessTools.Method(typeof(HashSet<Pawn>), nameof(HashSet<Pawn>.Contains));
            MethodInfo forceKept = AccessTools.Method(typeof(ShortStayCleanup), nameof(ShortStayCleanup.ForceKept));
            int colonist = 0, kept = 0;
            bool afterForcefullyKept = false;
            foreach (CodeInstruction code in instructions)
            {
                if (colonist == 0 && code.Calls(everBeenColonist))
                {
                    colonist++;
                    code.operand = keepAsColonist;
                }
                else if (code.Calls(forcefullyKept))
                {
                    afterForcefullyKept = true;
                }
                else if (afterForcefullyKept && code.Calls(setContains))
                {
                    // set.Contains(pawn) -> ForceKept(set, pawn): same stack.
                    afterForcefullyKept = false;
                    kept++;
                    code.opcode = OpCodes.Call;
                    code.operand = forceKept;
                }
                yield return code;
            }
            if (colonist == 0)
                Log.Warning("[Vanilla Tune-Up] ShortStayCleanup: colonist check not found, short-stay former colonists are kept as in vanilla.");
            if (kept == 0)
                Log.Warning("[Vanilla Tune-Up] ShortStayCleanup: force-kept check not found, former faction leaders are kept as in vanilla.");
        }
    }
}
