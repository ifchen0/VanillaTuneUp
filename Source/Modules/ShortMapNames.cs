using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Long names under pawns on the map (common with translated tribal names) cover their neighbours. When another
    /// pawn stands in the same row close by (prisoners in beds side by side), cut the map label to the distance
    /// between them, using the truncation vanilla already supports but never asks for here, and show the whole
    /// name while the pawn or its label is under the mouse, or while the pawn is selected. Other places that draw
    /// pawn labels (colonist bar, world map) are untouched.
    /// </summary>
    [HarmonyPatchCategory("ShortMapNames")]
    [HarmonyPatch(typeof(PawnUIOverlay), nameof(PawnUIOverlay.DrawPawnGUIOverlay))]
    public static class Patch_PawnUIOverlay_DrawPawnGUIOverlay
    {
        public static Pawn Drawing;

        public static void Prefix(Pawn ___pawn) => Drawing = ___pawn;

        public static void Finalizer() => Drawing = null;
    }

    [HarmonyPatchCategory("ShortMapNames")]
    [HarmonyPatch(typeof(GenMapUI), nameof(GenMapUI.DrawPawnLabel), typeof(Pawn), typeof(Vector2), typeof(float), typeof(float),
        typeof(Dictionary<string, string>), typeof(GameFont), typeof(bool), typeof(bool))]
    public static class Patch_GenMapUI_DrawPawnLabel
    {
        // Neighbours further than this many cells are not looked for; only very long names far zoomed out reach them.
        private const int MaxRange = 5;
        // Widths are floored to this step so zooming does not add a cache entry every frame.
        private const float WidthStep = 2f;
        // Map distance from the pawn's draw position that counts as hovering it (the label sits 0.6 below).
        private const float HoverHalfWidth = 0.5f;
        private const float HoverTop = 0.5f;
        private const float HoverBottom = 0.8f;

        // Truncations depend only on width and font, so zooming keeps them; only the font switch clears them.
        private static readonly Dictionary<float, Dictionary<string, string>> TruncatedCaches = new Dictionary<float, Dictionary<string, string>>();
        // "Disable tiny text" swaps the tiny font for a wider one, so truncations made before no longer fit.
        private static bool cachedTinyTextDisabled;
        // A label shrinks as soon as a neighbour comes closer but widens only after this long, so pawns walking in a
        // group keep steady labels instead of flickering.
        private const float HoldSeconds = 0.5f;
        private static readonly Dictionary<Pawn, (int cells, float until)> Holds = new Dictionary<Pawn, (int, float)>();

        public static bool Prefix(Pawn pawn, Vector2 pos, float alpha, ref float truncateToWidth, ref Dictionary<string, string> truncatedLabelsCache,
            GameFont font, bool alwaysDrawBg, bool alignCenter)
        {
            if (pawn != Patch_PawnUIOverlay_DrawPawnGUIOverlay.Drawing)
                return true;
            // Vanilla sets this font itself before drawing, and measures map labels with GetWidthCached in it.
            Text.Font = font;
            if (Prefs.DisableTinyText != cachedTinyTextDisabled)
            {
                TruncatedCaches.Clear();
                cachedTinyTextDisabled = Prefs.DisableTinyText;
            }
            string label = pawn.LabelShortCap;
            float fullWidth = label.GetWidthCached();
            float cellSize = Find.CameraDriver.CellSizePixels;
            // Text no wider than the distance to the neighbour never touches the neighbour's text, which is limited
            // the same way; only the background margins may meet. Text within one cell needs no check at all.
            if (fullWidth <= cellSize)
                return true;
            int cells = HoldNeighbour(pawn, NearestNeighbourCells(pawn, Mathf.Min(MaxRange, Mathf.CeilToInt(fullWidth / cellSize))));
            float maxWidth = Mathf.Floor(cells * cellSize / WidthStep) * WidthStep;
            if (cells == 0 || fullWidth <= maxWidth || truncateToWidth < maxWidth || Find.Selector.IsSelected(pawn))
                return true;
            if (!TruncatedCaches.TryGetValue(maxWidth, out Dictionary<string, string> cache))
            {
                cache = new Dictionary<string, string>();
                TruncatedCaches[maxWidth] = cache;
            }
            if (!cache.TryGetValue(label, out string shortLabel))
            {
                shortLabel = Truncate(label, maxWidth);
                cache[label] = shortLabel;
            }
            float textWidth = shortLabel.GetWidthCached();
            if (IsHovered(pawn, pos, textWidth))
                return true;
            truncateToWidth = maxWidth;
            truncatedLabelsCache = cache;
            // Vanilla's background always fits at least 20 px of name, which covers a close neighbour, so it is sized
            // from the real text here.
            float padding = Prefs.DisableTinyText ? 6f : 4f;
            Rect bgRect = new Rect(pos.x - textWidth / 2f - padding, pos.y, textWidth + padding * 2f, Prefs.DisableTinyText ? 16f : 12f);
            GenMapUI.DrawPawnLabel(pawn, bgRect, alpha, truncateToWidth, truncatedLabelsCache, font, alwaysDrawBg, alignCenter);
            return false;
        }

        // The closest neighbour distance seen in the last HoldSeconds, or 0.
        private static int HoldNeighbour(Pawn pawn, int cells)
        {
            float now = Time.realtimeSinceStartup;
            bool held = Holds.TryGetValue(pawn, out var hold) && now < hold.until;
            if (cells > 0 && (!held || cells <= hold.cells))
            {
                // Despawned pawns are never looked up again; dropping everything now and then keeps the table small.
                if (Holds.Count > 1000)
                    Holds.Clear();
                Holds[pawn] = (cells, now + HoldSeconds);
                return cells;
            }
            return held ? hold.cells : cells;
        }

        // Cells to the closest pawn in the same row within range, or 0. Only whole cells are compared, so a walking
        // pawn's label keeps its length until it changes cell.
        private static int NearestNeighbourCells(Pawn pawn, int range)
        {
            for (int d = 1; d <= range; d++)
            {
                for (int side = -d; side <= d; side += 2 * d)
                {
                    IntVec3 cell = pawn.Position + new IntVec3(side, 0, 0);
                    if (cell.InBounds(pawn.Map) && pawn.Map.thingGrid.ThingAt<Pawn>(cell) != null)
                        return d;
                }
            }
            return 0;
        }

        // Vanilla Truncate appends "..." but reads the cache first, so the shorter ".." is filled in beforehand.
        // At least one character is kept, even if it then overlaps a neighbour a little.
        private static string Truncate(string label, float maxWidth)
        {
            if (label.Length < 2)
                return label;
            string result = label.Substring(0, label.Length - 1);
            while (result.Length > 1 && Text.CalcSize(result + "..").x > maxWidth)
                result = result.Substring(0, result.Length - 1);
            return result + "..";
        }

        // Only the shortened label as drawn counts; once expanded, moving onto the extra part shortens it again.
        private static bool IsHovered(Pawn pawn, Vector2 pos, float width)
        {
            Vector3 mouse = UI.MouseMapPosition();
            Vector3 drawPos = pawn.DrawPos;
            if (Mathf.Abs(mouse.x - drawPos.x) <= HoverHalfWidth && mouse.z <= drawPos.z + HoverTop && mouse.z >= drawPos.z - HoverBottom)
                return true;
            Vector2 mousePos = Event.current.mousePosition;
            float height = Prefs.DisableTinyText ? 16f : 12f;
            return new Rect(pos.x - width / 2f, pos.y, width, height).Contains(mousePos);
        }
    }
}
