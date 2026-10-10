using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Clears the fog of war on every map when it is generated or loaded, unless revealing it would set something off:
    /// a dormant or sleeping pawn (ancient dangers wake when unfogged), a TriggerUnfogged signal, a quest target, a
    /// letter-on-reveal thing, an undiscovered entity codex entry or a hidden item. Such maps are left untouched, so
    /// they play exactly as in vanilla. Pocket maps (labyrinth, undercave and so on) are always skipped.
    /// </summary>
    public static class RemoveFogOfWar
    {
        private static readonly AccessTools.FieldRef<Thing, bool> BeenRevealed =
            AccessTools.FieldRefAccess<Thing, bool>("beenRevealed");

        private static readonly List<IntVec3> tmpFogged = new List<IntVec3>();

        public static void TryReveal(Map map)
        {
            if (map == null || map.IsPocketMap)
                return;
            FogGrid fogGrid = map.fogGrid;
            tmpFogged.Clear();
            foreach (IntVec3 cell in map.AllCells)
            {
                if (fogGrid.IsFogged(cell))
                    tmpFogged.Add(cell);
            }
            if (tmpFogged.Count == 0)
                return;

            string trigger = FindTrigger(map);
            if (trigger != null)
            {
                tmpFogged.Clear();
                Log.Message($"[Vanilla Tune-Up] Kept the fog on {map}: revealing it would trigger {trigger}.");
                return;
            }

            var watch = Stopwatch.StartNew();
            int count = tmpFogged.Count;
            foreach (IntVec3 cell in tmpFogged)
                fogGrid.Unfog(cell);
            tmpFogged.Clear();
            Log.Message($"[Vanilla Tune-Up] Removed the fog on {map}: {count} cells ({watch.ElapsedMilliseconds} ms).");
        }

        /// <summary>Describes the first thing in the fog that would react to being revealed, or null if none.</summary>
        private static string FindTrigger(Map map)
        {
            foreach (IntVec3 cell in tmpFogged)
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
                for (int i = 0; i < things.Count; i++)
                {
                    string reason = Trigger(things[i]);
                    if (reason != null)
                        return $"{reason} ({things[i].LabelShort} at {cell})";
                }
            }
            return null;
        }

        private static string Trigger(Thing thing)
        {
            if (thing is TriggerUnfogged)
                return "an unfog signal";
            if (thing is Pawn pawn)
            {
                if (pawn.mindState != null && !pawn.mindState.Active)
                    return "a dormant pawn";
                if (pawn.GetLord()?.LordJob is LordJob_SleepThenAssaultColony)
                    return "a sleeping group";
            }
            // Everything below only fires the first time a thing is revealed (Thing.Notify_Unfogged).
            if (BeenRevealed(thing))
                return null;
            if (!thing.questTags.NullOrEmpty())
                return "a quest signal";
            if (thing.TryGetComp<CompLetterOnRevealed>() != null)
                return "a letter";
            if (ModsConfig.AnomalyActive && NewCodexEntry(thing))
                return "an entity codex discovery";
            if (Find.HiddenItemsManager.Hidden(thing.def))
                return "a hidden item discovery";
            return null;
        }

        private static bool NewCodexEntry(Thing thing)
        {
            List<EntityCodexEntryDef> entries = AnomalyUtility.GetCodexEntriesFor(thing);
            if (entries.NullOrEmpty())
                return false;
            if (!Find.EntityCodex.Discovered(thing.def))
                return true;
            foreach (EntityCodexEntryDef entry in entries)
            {
                if (!Find.EntityCodex.Discovered(entry))
                    return true;
            }
            return false;
        }
    }

    [HarmonyPatchCategory("RemoveFogOfWar")]
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    public static class Patch_MapGenerator_GenerateMap
    {
        public static void Postfix(Map __result) => RemoveFogOfWar.TryReveal(__result);
    }

    /// <summary>Maps from saves made before the module was enabled are covered on load.</summary>
    [HarmonyPatchCategory("RemoveFogOfWar")]
    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeLoading))]
    public static class Patch_Map_FinalizeLoading
    {
        public static void Postfix(Map __instance) => RemoveFogOfWar.TryReveal(__instance);
    }
}
