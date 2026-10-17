using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Clears the fog of war on every map when it is generated or loaded, except where revealing it would set something
    /// off: a dormant or sleeping pawn (ancient dangers wake when unfogged), a TriggerUnfogged signal, a quest target, a
    /// letter-on-reveal thing, an undiscovered entity codex entry or a hidden item. The whole fogged area connected to
    /// such a thing keeps its fog, so it plays exactly as in vanilla, while the rest of the map (ruins elsewhere and so on)
    /// is still revealed. Mineable cells (rock, ore, collapsed rocks) always keep their fog, and pocket maps (labyrinth,
    /// undercave and so on) are always skipped.
    /// </summary>
    public static class RemoveFogOfWar
    {
        private static readonly AccessTools.FieldRef<Thing, bool> BeenRevealed =
            AccessTools.FieldRefAccess<Thing, bool>("beenRevealed");

        private static readonly List<IntVec3> tmpFogged = new List<IntVec3>();
        private static readonly Queue<IntVec3> tmpQueue = new Queue<IntVec3>();

        public static void TryReveal(Map map)
        {
            if (map == null || map.IsPocketMap)
                return;
            FogGrid fogGrid = map.fogGrid;
            CellIndices indices = map.cellIndices;
            // 0 = not a candidate, 1 = to reveal, 2 = kept (connected to a trigger).
            byte[] state = new byte[indices.NumGridCells];
            tmpFogged.Clear();
            foreach (IntVec3 cell in map.AllCells)
            {
                // Anything mineable (rock, ore, collapsed rocks) stays hidden, so finding it by mining works as in vanilla.
                if (fogGrid.IsFogged(cell) && cell.GetEdifice(map)?.def.mineable != true)
                {
                    tmpFogged.Add(cell);
                    state[indices.CellToIndex(cell)] = 1;
                }
            }
            if (tmpFogged.Count == 0)
                return;

            var watch = Stopwatch.StartNew();
            string firstTrigger = null;
            int kept = 0;
            foreach (IntVec3 cell in tmpFogged)
            {
                if (state[indices.CellToIndex(cell)] != 1)
                    continue;
                string trigger = FindTrigger(map, cell);
                if (trigger == null)
                    continue;
                firstTrigger = firstTrigger ?? $"{trigger} at {cell}";
                kept += KeepConnectedFog(map, cell, state);
            }

            int count = 0;
            foreach (IntVec3 cell in tmpFogged)
            {
                if (state[indices.CellToIndex(cell)] != 1)
                    continue;
                fogGrid.Unfog(cell);
                count++;
            }
            tmpFogged.Clear();
            if (kept == 0)
                Log.Message($"[Vanilla Tune-Up] Removed the fog on {map}: {count} cells ({watch.ElapsedMilliseconds} ms).");
            else
                Log.Message($"[Vanilla Tune-Up] Removed the fog on {map}: {count} cells, kept {kept} cells around things "
                    + $"that would trigger when revealed, first: {firstTrigger} ({watch.ElapsedMilliseconds} ms).");
        }

        /// <summary>Marks the fogged area 8-connected to <paramref name="start"/> as kept; returns how many cells it marked.</summary>
        private static int KeepConnectedFog(Map map, IntVec3 start, byte[] state)
        {
            CellIndices indices = map.cellIndices;
            int marked = 0;
            tmpQueue.Clear();
            state[indices.CellToIndex(start)] = 2;
            tmpQueue.Enqueue(start);
            while (tmpQueue.Count > 0)
            {
                IntVec3 cell = tmpQueue.Dequeue();
                marked++;
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 next = cell + GenAdj.AdjacentCells[i];
                    if (!next.InBounds(map))
                        continue;
                    int index = indices.CellToIndex(next);
                    if (state[index] != 1)
                        continue;
                    state[index] = 2;
                    tmpQueue.Enqueue(next);
                }
            }
            return marked;
        }

        /// <summary>Describes the first thing at the cell that would react to being revealed, or null if none.</summary>
        private static string FindTrigger(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                string reason = Trigger(things[i]);
                if (reason != null)
                    return $"{reason} ({things[i].LabelShort})";
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
