using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>Right-drag reordering of the architect category buttons.</summary>
    [HarmonyPatchCategory("DragReorder")]
    [HarmonyPatch]
    public static class CategoryOrdering
    {
        private const float ButHeight = 32f;

        private static readonly AccessTools.FieldRef<MainTabWindow_Architect, List<ArchitectCategoryTab>> PanelsRef =
            AccessTools.FieldRefAccess<MainTabWindow_Architect, List<ArchitectCategoryTab>>("desPanelsCached");

        private static readonly DragController<ArchitectCategoryTab> Drag = new DragController<ArchitectCategoryTab>();
        private static readonly List<(ArchitectCategoryTab panel, Rect rect)> Rects = new List<(ArchitectCategoryTab, Rect)>();

        public static void Apply(MainTabWindow_Architect window)
        {
            List<ArchitectCategoryTab> panels = PanelsRef(window);
            List<ArchitectCategoryTab> byDefault = panels.OrderByDescending(p => p.def.order).ToList();
            List<string> merged = DragUtil.Merge(byDefault.Select(p => p.def.defName).ToList(), VanillaTuneUpMod.Settings.dragReorder.categories);
            panels.Clear();
            panels.AddRange(merged.Select(name => byDefault.First(p => p.def.defName == name)));
        }

        public static void ApplyToCurrent()
        {
            if (Current.ProgramState == ProgramState.Playing && MainButtonDefOf.Architect.TabWindow is MainTabWindow_Architect window)
                Apply(window);
        }

        [HarmonyPatch(typeof(MainTabWindow_Architect), MethodType.Constructor)]
        [HarmonyPostfix]
        private static void Constructor_Postfix(MainTabWindow_Architect __instance) => Apply(__instance);

        [HarmonyPatch(typeof(MainTabWindow_Architect), nameof(MainTabWindow_Architect.DoWindowContents))]
        [HarmonyPrefix]
        private static void DoWindowContents_Prefix(MainTabWindow_Architect __instance, Rect inRect)
        {
            if (Event.current.type == EventType.Layout)
                return;
            List<ArchitectCategoryTab> panels = PanelsRef(__instance);
            CacheRects(panels, inRect.width / 2f);
            if (Drag.HandleEvent(HitTest, out ArchitectCategoryTab source, out ArchitectCategoryTab target))
            {
                DragUtil.Move(panels, source, target);
                VanillaTuneUpMod.Settings.dragReorder.categories = panels.Select(p => p.def.defName).ToList();
                VanillaTuneUpMod.Save();
            }
        }

        [HarmonyPatch(typeof(MainTabWindow_Architect), nameof(MainTabWindow_Architect.DoWindowContents))]
        [HarmonyPostfix]
        private static void DoWindowContents_Postfix()
        {
            ArchitectCategoryTab dragging = Drag.Dragging;
            if (dragging == null)
                return;
            ArchitectCategoryTab target = HitTest(Event.current.mousePosition);
            DragUtil.DrawFeedback(RectOf(dragging), target != null && target != dragging ? RectOf(target) : null, null);
        }

        /// <summary>
        /// Vanilla swaps a row when one tab prefers a column (Biotech and Ideology prefer the right one). That
        /// only lines up in the default order; in a custom order it moves tabs out of place and can leave the
        /// last odd tab alone on the right. Once the player has an order, tabs are laid out exactly in it.
        /// </summary>
        [HarmonyPatch(typeof(MainTabWindow_Architect), nameof(MainTabWindow_Architect.DoWindowContents))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DoWindowContents_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(ArchitectCategoryTab), nameof(ArchitectCategoryTab.PreferredColumn));
            int replaced = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(getter))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(CategoryOrdering), nameof(ColumnOf));
                    replaced++;
                }
                yield return ci;
            }
            if (replaced == 0)
                Log.Warning("[Vanilla Tune-Up] MainTabWindow_Architect.DoWindowContents has changed; architect tabs may still be swapped between columns.");
        }

        public static int? ColumnOf(ArchitectCategoryTab tab) =>
            VanillaTuneUpMod.Settings.dragReorder.categories.NullOrEmpty() ? tab.PreferredColumn : null;

        // Same layout as MainTabWindow_Architect.DoWindowContents, including the preferredColumn swap.
        private static void CacheRects(List<ArchitectCategoryTab> panels, float butWidth)
        {
            Rects.Clear();
            float row = 0f;
            for (int i = 0; i < panels.Count; i += 2)
            {
                ArchitectCategoryTab a = panels[i];
                ArchitectCategoryTab b = i + 1 < panels.Count ? panels[i + 1] : null;
                bool swap = (ColumnOf(a) == 1 || (b != null && ColumnOf(b) == 0))
                    && ColumnOf(a) != 0 && (b == null || ColumnOf(b) != 1);
                ArchitectCategoryTab left = swap ? b : a;
                ArchitectCategoryTab right = swap ? a : b;
                if (left != null)
                    Rects.Add((left, new Rect(0f, row * ButHeight, butWidth + 1f, ButHeight + 1f)));
                if (right != null)
                    Rects.Add((right, new Rect(butWidth, row * ButHeight, butWidth, ButHeight + 1f)));
                row += 1f;
            }
        }

        private static ArchitectCategoryTab HitTest(Vector2 pos)
        {
            foreach (var (panel, rect) in Rects)
            {
                if (rect.Contains(pos))
                    return panel;
            }
            return null;
        }

        private static Rect? RectOf(ArchitectCategoryTab panel)
        {
            foreach (var entry in Rects)
            {
                if (entry.panel == panel)
                    return entry.rect;
            }
            return null;
        }
    }
}
