using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Long names under pawns on the map (common with translated tribal names) cover their neighbours. Cut the map
    /// label to a fixed width, using the truncation vanilla already supports but never asks for here, and show the
    /// whole name while the pawn or its label is under the mouse, or while the pawn is selected. Other places that
    /// draw pawn labels (colonist bar, world map) are untouched.
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
        // Neighbouring pawns stand one cell apart, so the limit follows the zoom: 1.5 cells, never below MinWidth.
        private const float WidthInCells = 1.5f;
        private const float MinWidth = 30f;
        // Rounded so zooming does not refill the cache every frame.
        private const float WidthStep = 4f;
        // Map distance from the pawn's draw position that counts as hovering it (the label sits 0.6 below).
        private const float HoverHalfWidth = 0.5f;
        private const float HoverTop = 0.5f;
        private const float HoverBottom = 0.8f;

        private static readonly Dictionary<string, string> TruncatedCache = new Dictionary<string, string>();
        private static float cachedWidth;
        // "Disable tiny text" swaps the tiny font for a wider one, so truncations made before no longer fit.
        private static bool cachedTinyTextDisabled;

        public static void Prefix(Pawn pawn, Vector2 pos, ref float truncateToWidth, ref Dictionary<string, string> truncatedLabelsCache, GameFont font)
        {
            float maxWidth = Mathf.Max(MinWidth, Mathf.Round(Find.CameraDriver.CellSizePixels * WidthInCells / WidthStep) * WidthStep);
            if (pawn != Patch_PawnUIOverlay_DrawPawnGUIOverlay.Drawing || truncateToWidth < maxWidth)
                return;
            if (Find.Selector.IsSelected(pawn) || IsHovered(pawn, pos, font))
                return;
            if (maxWidth != cachedWidth || Prefs.DisableTinyText != cachedTinyTextDisabled)
            {
                TruncatedCache.Clear();
                cachedWidth = maxWidth;
                cachedTinyTextDisabled = Prefs.DisableTinyText;
            }
            truncateToWidth = maxWidth;
            truncatedLabelsCache = TruncatedCache;
            string label = pawn.LabelShortCap;
            if (!TruncatedCache.ContainsKey(label))
                TruncatedCache[label] = Truncate(label, font, maxWidth);
        }

        // Vanilla Truncate appends "..." but reads the cache first, so the shorter ".." is filled in beforehand.
        private static string Truncate(string label, GameFont font, float maxWidth)
        {
            GameFont oldFont = Text.Font;
            Text.Font = font;
            string result = label;
            if (Text.CalcSize(label).x > maxWidth)
            {
                do
                    result = result.Substring(0, result.Length - 1);
                while (result.Length > 0 && Text.CalcSize(result + "..").x > maxWidth);
                result += "..";
            }
            Text.Font = oldFont;
            return result;
        }

        private static bool IsHovered(Pawn pawn, Vector2 pos, GameFont font)
        {
            Vector3 mouse = UI.MouseMapPosition();
            Vector3 drawPos = pawn.DrawPos;
            if (Mathf.Abs(mouse.x - drawPos.x) <= HoverHalfWidth && mouse.z <= drawPos.z + HoverTop && mouse.z >= drawPos.z - HoverBottom)
                return true;
            // A long label is wider than the pawn; hovering anywhere on its full width also shows it. The row is
            // checked first so only pawns level with the mouse pay for measuring the label.
            Vector2 mousePos = Event.current.mousePosition;
            float height = Prefs.DisableTinyText ? 16f : 12f;
            if (mousePos.y < pos.y || mousePos.y > pos.y + height)
                return false;
            GameFont oldFont = Text.Font;
            Text.Font = font;
            float width = Text.CalcSize(pawn.LabelShortCap).x;
            Text.Font = oldFont;
            return new Rect(pos.x - width / 2f, pos.y, width, height).Contains(mousePos);
        }
    }
}
