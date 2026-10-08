using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>Right-drag reordering of the bottom bar.</summary>
    [HarmonyPatchCategory("DragReorder")]
    [HarmonyPatch]
    public static class MainButtonOrdering
    {
        private static readonly AccessTools.FieldRef<MainButtonsRoot, List<MainButtonDef>> ButtonsRef =
            AccessTools.FieldRefAccess<MainButtonsRoot, List<MainButtonDef>>("allButtonsInOrder");

        private static readonly DragController<MainButtonDef> Drag = new DragController<MainButtonDef>();
        private static readonly List<(MainButtonDef def, Rect rect)> Rects = new List<(MainButtonDef, Rect)>();

        public static void Apply(MainButtonsRoot root)
        {
            List<MainButtonDef> buttons = ButtonsRef(root);
            List<MainButtonDef> byDefault = buttons.OrderBy(b => b.order).ToList();
            List<string> merged = DragUtil.Merge(byDefault.Select(b => b.defName).ToList(), VanillaTuneUpMod.Settings.dragReorder.mainButtons);
            buttons.Clear();
            buttons.AddRange(merged.Select(name => byDefault.First(b => b.defName == name)));
        }

        public static void ApplyToCurrent()
        {
            if (Find.UIRoot is UIRoot_Play play)
                Apply(play.mainButtonsRoot);
        }

        [HarmonyPatch(typeof(MainButtonsRoot), MethodType.Constructor)]
        [HarmonyPostfix]
        private static void Constructor_Postfix(MainButtonsRoot __instance) => Apply(__instance);

        [HarmonyPatch(typeof(MainButtonsRoot), nameof(MainButtonsRoot.MainButtonsOnGUI))]
        [HarmonyPrefix]
        private static void MainButtonsOnGUI_Prefix(MainButtonsRoot __instance)
        {
            if (Event.current.type == EventType.Layout)
                return;
            List<MainButtonDef> buttons = ButtonsRef(__instance);
            CacheRects(buttons);
            if (Drag.HandleEvent(HitTest, out MainButtonDef source, out MainButtonDef target))
            {
                DragUtil.Move(buttons, source, target);
                VanillaTuneUpMod.Settings.dragReorder.mainButtons = buttons.Select(b => b.defName).ToList();
                VanillaTuneUpMod.Save();
            }
        }

        [HarmonyPatch(typeof(MainButtonsRoot), nameof(MainButtonsRoot.MainButtonsOnGUI))]
        [HarmonyPostfix]
        private static void MainButtonsOnGUI_Postfix()
        {
            MainButtonDef dragging = Drag.Dragging;
            if (dragging == null)
                return;
            MainButtonDef target = HitTest(Event.current.mousePosition);
            DragUtil.DrawFeedback(RectOf(dragging), target != null && target != dragging ? RectOf(target) : null, dragging.LabelCap);
        }

        // Same layout as MainButtonsRoot.DoButtons.
        private static void CacheRects(List<MainButtonDef> buttons)
        {
            Rects.Clear();
            float units = 0f;
            int last = -1;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].Worker.Visible)
                {
                    units += buttons[i].minimized ? 0.5f : 1f;
                    last = i;
                }
            }
            if (units <= 0f)
                return;
            int width = (int)(UI.screenWidth / units);
            int x = 0;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (!buttons[i].Worker.Visible)
                    continue;
                int w = buttons[i].minimized ? width / 2 : width;
                if (i == last)
                    w = UI.screenWidth - x;
                Rects.Add((buttons[i], new Rect(x, UI.screenHeight - 35, w, 36f)));
                x += w;
            }
        }

        private static MainButtonDef HitTest(Vector2 pos)
        {
            foreach (var (def, rect) in Rects)
            {
                if (rect.Contains(pos))
                    return def;
            }
            return null;
        }

        private static Rect? RectOf(MainButtonDef def)
        {
            foreach (var entry in Rects)
            {
                if (entry.def == def)
                    return entry.rect;
            }
            return null;
        }
    }
}
