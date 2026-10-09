using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Right-drag reordering of the buildings inside an architect category. The gizmo grid sorts by
    /// Gizmo.Order every draw, so the sort call in DrawGizmoGrid is redirected and re-sorted by the saved
    /// order while an architect category is being drawn; the selection's command buttons are handed to
    /// InspectOrdering, and every other gizmo grid keeps the vanilla sort.
    /// </summary>
    [HarmonyPatchCategory("DragReorder")]
    [HarmonyPatch]
    public static class DesignatorOrdering
    {
        private class CategoryCache
        {
            public readonly List<string> defaultKeys = new List<string>();
            public readonly Dictionary<string, int> rank = new Dictionary<string, int>();
            public int version = -1;
        }

        private static readonly ConditionalWeakTable<Designator, string> Keys = new ConditionalWeakTable<Designator, string>();
        private static readonly Dictionary<DesignationCategoryDef, CategoryCache> Caches = new Dictionary<DesignationCategoryDef, CategoryCache>();
        private static readonly DragController<Designator> Drag = new DragController<Designator>();
        private static readonly List<(Designator des, Rect rect)> Rects = new List<(Designator, Rect)>();
        private static readonly List<Designator> LastOrder = new List<Designator>();
        private static readonly List<string> TmpKeys = new List<string>();

        private static DesignationCategoryDef activeCategory;
        private static DesignationCategoryDef lastCategory;
        private static Dictionary<string, int> sortRank;
        private static int version;

        private static readonly Func<Gizmo, Gizmo, int> ByRank = (a, b) => RankOf(sortRank, a).CompareTo(RankOf(sortRank, b));

        public static void Invalidate() => version++;

        [HarmonyPatch(typeof(ArchitectCategoryTab), nameof(ArchitectCategoryTab.DesignationTabOnGUI))]
        [HarmonyPrefix]
        private static void DesignationTabOnGUI_Prefix(ArchitectCategoryTab __instance)
        {
            if (Event.current.type == EventType.Layout)
                return;
            if (lastCategory != __instance.def)
            {
                Drag.Cancel();
                Rects.Clear();
                LastOrder.Clear();
                lastCategory = __instance.def;
            }
            activeCategory = __instance.def;
            if (Drag.HandleEvent(HitTest, out Designator source, out Designator target) && LastOrder.Contains(source))
            {
                List<string> keys = LastOrder.Select(KeyOf).Distinct().ToList();
                DragUtil.Move(keys, KeyOf(source), KeyOf(target));
                VanillaTuneUpMod.Settings.dragReorder.SetDesignatorKeys(activeCategory.defName, keys);
                VanillaTuneUpMod.Save();
                Invalidate();
            }
        }

        [HarmonyPatch(typeof(ArchitectCategoryTab), nameof(ArchitectCategoryTab.DesignationTabOnGUI))]
        [HarmonyPostfix]
        private static void DesignationTabOnGUI_Postfix()
        {
            Designator dragging = Drag.Dragging;
            if (dragging == null)
                return;
            Designator target = HitTest(Event.current.mousePosition);
            DragUtil.DrawFeedback(RectOf(dragging), target != null && target != dragging ? RectOf(target) : null, dragging.LabelCap);
        }

        [HarmonyPatch(typeof(ArchitectCategoryTab), nameof(ArchitectCategoryTab.DesignationTabOnGUI))]
        [HarmonyFinalizer]
        private static void DesignationTabOnGUI_Finalizer() => activeCategory = null;

        [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGrid))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DrawGizmoGrid_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo gizmoOnGUI = AccessTools.Method(typeof(Gizmo), nameof(Gizmo.GizmoOnGUI));
            MethodInfo gizmoOnGUIShrunk = AccessTools.Method(typeof(Command), nameof(Command.GizmoOnGUIShrunk));
            int sorts = 0, draws = 0, shrunkDraws = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.operand is MethodInfo m && m.Name == nameof(GenCollection.SortStable) && m.DeclaringType == typeof(GenCollection))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(DesignatorOrdering), nameof(SortGizmos));
                    sorts++;
                }
                else if (ci.Calls(gizmoOnGUI))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(DesignatorOrdering), nameof(DrawGizmo));
                    draws++;
                }
                else if (ci.Calls(gizmoOnGUIShrunk))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = AccessTools.Method(typeof(DesignatorOrdering), nameof(DrawGizmoShrunk));
                    shrunkDraws++;
                }
                yield return ci;
            }
            if (sorts != 1 || draws != 1 || shrunkDraws != 1)
                Log.Warning($"[Vanilla Tune-Up] GizmoGridDrawer.DrawGizmoGrid has changed (sort calls {sorts}, draw calls {draws}, shrunk draw calls {shrunkDraws}); building and command button reordering may not work.");
        }

        private static void SortGizmos(IList<Gizmo> gizmos, Func<Gizmo, Gizmo, int> comparator)
        {
            gizmos.SortStable(comparator);
            if (activeCategory == null)
            {
                // A shared hotkey goes to the first button drawn, so it follows the user's order on purpose.
                if (InspectOrdering.Active)
                    InspectOrdering.Sort(gizmos);
                return;
            }
            Rects.Clear();
            List<string> saved = VanillaTuneUpMod.Settings.dragReorder.DesignatorKeys(activeCategory.defName);
            if (!saved.NullOrEmpty())
            {
                sortRank = RankFor(activeCategory, gizmos, saved);
                gizmos.SortStable(ByRank);
                sortRank = null;
            }
            LastOrder.Clear();
            foreach (Gizmo gizmo in gizmos)
            {
                if (gizmo is Designator des)
                    LastOrder.Add(des);
            }
        }

        private static GizmoResult DrawGizmo(Gizmo gizmo, Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            GizmoResult result = gizmo.GizmoOnGUI(topLeft, maxWidth, parms);
            if (activeCategory != null && gizmo is Designator des)
                Rects.Add((des, new Rect(topLeft.x, topLeft.y, gizmo.GetWidth(maxWidth), 75f)));
            else if (InspectOrdering.Active)
                InspectOrdering.Record(gizmo, new Rect(topLeft.x, topLeft.y, gizmo.GetWidth(maxWidth), 75f));
            return result;
        }

        private static GizmoResult DrawGizmoShrunk(Command command, Vector2 topLeft, float size, GizmoRenderParms parms)
        {
            GizmoResult result = command.GizmoOnGUIShrunk(topLeft, size, parms);
            if (activeCategory == null && InspectOrdering.Active)
                InspectOrdering.Record(command, new Rect(topLeft.x, topLeft.y, size, size));
            return result;
        }

        private static Dictionary<string, int> RankFor(DesignationCategoryDef category, IList<Gizmo> gizmos, List<string> saved)
        {
            TmpKeys.Clear();
            foreach (Gizmo gizmo in gizmos)
            {
                if (gizmo is Designator des)
                {
                    string key = KeyOf(des);
                    if (!TmpKeys.Contains(key))
                        TmpKeys.Add(key);
                }
            }
            if (!Caches.TryGetValue(category, out CategoryCache cache))
            {
                cache = new CategoryCache();
                Caches[category] = cache;
            }
            if (cache.version != version || !cache.defaultKeys.SequenceEqual(TmpKeys))
            {
                cache.defaultKeys.Clear();
                cache.defaultKeys.AddRange(TmpKeys);
                cache.rank.Clear();
                List<string> merged = DragUtil.Merge(TmpKeys, saved);
                for (int i = 0; i < merged.Count; i++)
                    cache.rank[merged[i]] = i;
                cache.version = version;
            }
            return cache.rank;
        }

        private static int RankOf(Dictionary<string, int> rank, Gizmo gizmo)
        {
            if (gizmo is Designator des && rank.TryGetValue(KeyOf(des), out int r))
                return r;
            return int.MaxValue;
        }

        private static string KeyOf(Designator des)
        {
            if (!Keys.TryGetValue(des, out string key))
            {
                key = BuildKey(des);
                Keys.Add(des, key);
            }
            return key;
        }

        private static string BuildKey(Designator des)
        {
            if (des is Designator_Dropdown dropdown)
            {
                foreach (Designator element in dropdown.Elements)
                {
                    if (element is Designator_Place { PlacingDef: { designatorDropdown: { } group } })
                        return "dropdown:" + group.defName;
                }
                return "type:" + des.GetType().FullName;
            }
            if (des is Designator_Place { PlacingDef: { } def })
                return "def:" + def.defName;
            return "type:" + des.GetType().FullName;
        }

        private static Designator HitTest(Vector2 pos)
        {
            foreach (var (des, rect) in Rects)
            {
                if (rect.Contains(pos))
                    return des;
            }
            return null;
        }

        private static Rect? RectOf(Designator des)
        {
            foreach (var entry in Rects)
            {
                if (entry.des == des)
                    return entry.rect;
            }
            return null;
        }
    }
}
