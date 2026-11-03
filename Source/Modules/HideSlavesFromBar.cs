using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Leaves slaves (alive or dead) out of the colonist bar, on maps and in caravans. Slave alerts (low
    /// suppression, rebellion) still point at them. The entries are filtered right before vanilla lays them out,
    /// so the layout and every mod reading the entries afterwards only see the remaining pawns. A map left with
    /// only slaves keeps the empty entry vanilla gives a map without colonists.
    /// </summary>
    public static class HideSlavesFromBar
    {
        private static readonly AccessTools.FieldRef<ColonistBar, List<ColonistBar.Entry>> CachedEntries =
            AccessTools.FieldRefAccess<ColonistBar, List<ColonistBar.Entry>>("cachedEntries");
        private static readonly AccessTools.FieldRef<ColonistBar, List<int>> CachedReorderableGroups =
            AccessTools.FieldRefAccess<ColonistBar, List<int>>("cachedReorderableGroups");

        public static void RemoveSlaves(ColonistBar bar)
        {
            List<ColonistBar.Entry> entries = CachedEntries(bar);
            if (!entries.Exists(e => e.pawn != null && e.pawn.IsSlave))
                return;
            var kept = new List<ColonistBar.Entry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                ColonistBar.Entry entry = entries[i];
                if (entry.pawn == null || !entry.pawn.IsSlave)
                {
                    kept.Add(entry);
                    continue;
                }
                // Last entry of a map group with nothing kept: keep the map's empty entry, as vanilla does.
                bool groupEnds = i + 1 == entries.Count || entries[i + 1].group != entry.group;
                bool groupEmpty = kept.Count == 0 || kept[kept.Count - 1].group != entry.group;
                if (groupEnds && groupEmpty && entry.map != null)
                    kept.Add(new ColonistBar.Entry(null, entry.map, entry.group));
            }
            entries.Clear();
            entries.AddRange(kept);
            // Vanilla fills one reorderable group per entry before the layout and sets them while drawing.
            List<int> groups = CachedReorderableGroups(bar);
            groups.Clear();
            for (int i = 0; i < entries.Count; i++)
                groups.Add(-1);
        }
    }

    [HarmonyPatchCategory("HideSlavesFromBar")]
    [HarmonyPatch(typeof(ColonistBarDrawLocsFinder), nameof(ColonistBarDrawLocsFinder.CalculateDrawLocs),
        new[] { typeof(List<Vector2>), typeof(float), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
    public static class HideSlavesFromBar_CalculateDrawLocs
    {
        // Only called at the end of ColonistBar.CheckRecacheEntries, once the entries are built.
        public static void Prefix() => HideSlavesFromBar.RemoveSlaves(Find.ColonistBar);
    }

    [HarmonyPatchCategory("HideSlavesFromBar")]
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    public static class HideSlavesFromBar_SetGuestStatus
    {
        // Vanilla does not always rebuild the bar when a colonist becomes a slave or a slave is freed. Guest status
        // also changes during world generation, where the UI root is not the in-game one (Find.ColonistBar throws).
        public static void Postfix()
        {
            if (Find.UIRoot is UIRoot_Play play)
                play.mapUI?.colonistBar?.MarkColonistsDirty();
        }
    }
}
