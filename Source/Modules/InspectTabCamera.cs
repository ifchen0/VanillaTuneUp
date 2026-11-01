using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Camera jumps (next/previous colonist keys, double-clicking a colonist bar portrait, letters) centre the target
    /// on the whole screen, so an open inspect tab (log, gear, social, character, needs, health) covers the left half
    /// around it. While such a tab is open, jumps now centre the target in the part of the screen to the right of the
    /// tab. Only the horizontal position changes. Optionally, opening or switching to a tab that covers the selected
    /// pawn slides the camera the same way, keeping its vertical position.
    /// </summary>
    public static class InspectTabCamera
    {
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> TabSize =
            AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");

        private static readonly Action<InspectTabBase> UpdateSize =
            AccessTools.MethodDelegate<Action<InspectTabBase>>(AccessTools.Method(typeof(InspectTabBase), "UpdateSize"));

        // Target of the TryJumpAndSelect call in progress; its tabs decide whether the open tab survives the jump.
        private static Thing selectingThing;

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.CheckboxLabeled("VTU_InspectTabCameraOnOpen".Translate(), ref settings.inspectTabCameraOnOpen,
                "VTU_InspectTabCameraOnOpen_Desc".Translate());
        }

        /// <summary>The inspect tab currently drawn next to the inspect pane, or null.</summary>
        private static InspectTabBase OpenTab(out MainTabWindow_Inspect pane)
        {
            pane = null;
            if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect)
                return null;
            pane = MainButtonDefOf.Inspect.TabWindow as MainTabWindow_Inspect;
            Type type = pane?.OpenTabType;
            IEnumerable<InspectTabBase> tabs = pane?.CurTabs;
            if (type == null || tabs == null)
                return null;
            foreach (InspectTabBase tab in tabs)
            {
                if (tab.GetType() == type && tab.IsVisible)
                    return tab;
            }
            return null;
        }

        private static float TabWidth(InspectTabBase tab)
        {
            UpdateSize(tab);
            return TabSize(tab).x;
        }

        /// <summary>Map x distance from a target to the camera centre that puts the target mid-way across the uncovered screen.</summary>
        private static float OffsetFor(float tabWidth) => tabWidth / 2f / Find.CameraDriver.CellSizePixels;

        private static void MoveCamera(Vector3 loc, bool smooth)
        {
            CameraDriver driver = Find.CameraDriver;
            if (smooth)
                driver.PanToMapLocAndSize(loc, driver.RootSize);
            else
                driver.JumpToCurrentMapLoc(loc);
        }

        [HarmonyPatchCategory("InspectTabCamera")]
        [HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJumpAndSelect))]
        public static class Patch_TryJumpAndSelect
        {
            public static void Prefix(GlobalTargetInfo target) => selectingThing = target.HasThing ? target.Thing : null;

            public static void Finalizer() => selectingThing = null;
        }

        [HarmonyPatchCategory("InspectTabCamera")]
        [HarmonyPatch(typeof(CameraJumper), "JumpLocalInternal")]
        public static class Patch_JumpLocalInternal
        {
            public static bool Prefix(IntVec3 localCell, CameraJumper.MovementMode mode)
            {
                InspectTabBase tab = OpenTab(out _);
                if (tab == null)
                    return true;
                // A jump that selects something without this tab type closes the tab next frame; keep vanilla centring.
                if (selectingThing != null && !HasTabType(selectingThing, tab.GetType()))
                    return true;
                Vector3 loc = localCell.ToVector3Shifted();
                loc.x -= OffsetFor(TabWidth(tab));
                MoveCamera(loc, mode == CameraJumper.MovementMode.Pan);
                return false;
            }

            private static bool HasTabType(Thing thing, Type type)
            {
                IEnumerable<InspectTabBase> tabs = thing.GetInspectTabs();
                if (tabs == null)
                    return false;
                foreach (InspectTabBase tab in tabs)
                {
                    if (tab.GetType() == type)
                        return true;
                }
                return false;
            }
        }

        [HarmonyPatchCategory("InspectTabCamera")]
        [HarmonyPatch(typeof(InspectPaneUtility), "ToggleTab")]
        public static class Patch_ToggleTab
        {
            public static void Postfix()
            {
                if (!VanillaTuneUpMod.Settings.inspectTabCameraOnOpen || Find.CurrentMap == null || WorldRendererUtility.WorldSelected)
                    return;
                InspectTabBase tab = OpenTab(out _);
                if (tab == null || !(Find.Selector.SingleSelectedThing is Pawn pawn) || !pawn.Spawned || pawn.Map != Find.CurrentMap)
                    return;
                float width = TabWidth(tab);
                Vector2 screen = pawn.DrawPos.MapToUIPosition();
                // Covered = left of the tab's right edge (plus about one cell) and below its top edge.
                float tabTop = ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).PaneTopY - 30f - TabSize(tab).y;
                if (screen.x > width + Find.CameraDriver.CellSizePixels || screen.y < tabTop)
                    return;
                Vector3 loc = new Vector3(pawn.DrawPos.x - OffsetFor(width), 0f, Find.Camera.transform.position.z);
                MoveCamera(loc, Prefs.SmoothCameraJumps);
            }
        }
    }
}
