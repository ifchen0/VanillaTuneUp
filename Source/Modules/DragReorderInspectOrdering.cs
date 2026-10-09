using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Right-drag reordering of the command buttons shown for the selection (pawns, buildings, animals...).
    /// One order is shared by everything: a button that the selection does not have is simply skipped.
    /// The buttons are recreated every frame, so each one is keyed by the code behind it (its action
    /// delegate's method, ability def or type), and the resulting permutation is reused for as long as the
    /// list looks the same (same type, icon and Order at every index), so a steady selection costs a
    /// cheap comparison per button instead of a key lookup.
    /// </summary>
    [HarmonyPatchCategory("DragReorder")]
    [HarmonyPatch]
    public static class InspectOrdering
    {
        private static readonly Dictionary<MethodInfo, string> MethodKeys = new Dictionary<MethodInfo, string>();
        private static readonly Dictionary<Type, string> TypeKeys = new Dictionary<Type, string>();
        private static readonly Dictionary<AbilityDef, string> AbilityKeys = new Dictionary<AbilityDef, string>();
        private static readonly Dictionary<Type, FieldInfo> ClosureDesignatorFields = new Dictionary<Type, FieldInfo>();
        private static readonly DragController<string> Drag = new DragController<string>();
        private static readonly List<(Gizmo gizmo, Rect rect)> Rects = new List<(Gizmo, Rect)>();
        private static readonly List<Gizmo> LastOrder = new List<Gizmo>();
        private static readonly List<string> TmpKeys = new List<string>();
        private static readonly List<int> TmpIndices = new List<int>();
        private static readonly List<Gizmo> TmpGizmos = new List<Gizmo>();

        // Fingerprint of the last sorted list and the permutation that was applied to it.
        private static Type[] fpTypes = new Type[64];
        private static Texture[] fpIcons = new Texture[64];
        private static float[] fpOrders = new float[64];
        private static int[] perm = new int[64];
        private static int[] ranks = new int[64];
        private static int fpCount = -1;
        private static int fpVersion = -1;
        private static int version;

        public static bool Active { get; private set; }

        public static void Invalidate() => version++;

        [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor))]
        [HarmonyPrefix]
        private static void DrawGizmoGridFor_Prefix()
        {
            if (Event.current.type == EventType.Layout)
                return;
            Active = true;
            if (Drag.HandleEvent(HitTest, out string source, out string target) && source != target)
            {
                TmpKeys.Clear();
                foreach (Gizmo gizmo in LastOrder)
                {
                    string key = KeyOf(gizmo);
                    if (!TmpKeys.Contains(key))
                        TmpKeys.Add(key);
                }
                if (TmpKeys.Contains(source))
                {
                    List<string> keys = MergeKeepAll(VanillaTuneUpMod.Settings.dragReorder.inspectGizmos, TmpKeys);
                    DragUtil.Move(keys, source, target);
                    VanillaTuneUpMod.Settings.dragReorder.inspectGizmos = keys;
                    VanillaTuneUpMod.Save();
                    Invalidate();
                }
            }
        }

        [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor))]
        [HarmonyPostfix]
        private static void DrawGizmoGridFor_Postfix()
        {
            string dragging = Drag.Dragging;
            if (dragging == null || !Active)
                return;
            Gizmo source = GizmoOf(dragging);
            string target = HitTest(Event.current.mousePosition);
            Rect? targetRect = target != null && target != dragging ? RectOf(GizmoOf(target)) : null;
            DragUtil.DrawFeedback(RectOf(source), targetRect, (source as Command)?.LabelCap ?? "");
        }

        [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor))]
        [HarmonyFinalizer]
        private static void DrawGizmoGridFor_Finalizer() => Active = false;

        /// <summary>Called from the redirected sort in DrawGizmoGrid after the vanilla sort.</summary>
        public static void Sort(IList<Gizmo> gizmos)
        {
            Rects.Clear();
            LastOrder.Clear();
            List<string> saved = VanillaTuneUpMod.Settings.dragReorder.inspectGizmos;
            if (!saved.NullOrEmpty() && gizmos.Count > 1)
            {
                if (!SameAsLast(gizmos))
                    BuildPermutation(gizmos, saved);
                TmpGizmos.Clear();
                for (int i = 0; i < gizmos.Count; i++)
                    TmpGizmos.Add(gizmos[perm[i]]);
                for (int i = 0; i < gizmos.Count; i++)
                    gizmos[i] = TmpGizmos[i];
                TmpGizmos.Clear();
            }
            for (int i = 0; i < gizmos.Count; i++)
                LastOrder.Add(gizmos[i]);
        }

        public static void Record(Gizmo gizmo, Rect rect) => Rects.Add((gizmo, rect));

        private static bool SameAsLast(IList<Gizmo> gizmos)
        {
            if (fpCount != gizmos.Count || fpVersion != version)
                return false;
            for (int i = 0; i < gizmos.Count; i++)
            {
                Gizmo gizmo = gizmos[i];
                if (gizmo.GetType() != fpTypes[i] || (gizmo as Command)?.icon != fpIcons[i] || gizmo.Order != fpOrders[i])
                    return false;
            }
            return true;
        }

        private static void BuildPermutation(IList<Gizmo> gizmos, List<string> saved)
        {
            int n = gizmos.Count;
            if (fpTypes.Length < n)
            {
                int size = Mathf.NextPowerOfTwo(n);
                fpTypes = new Type[size];
                fpIcons = new Texture[size];
                fpOrders = new float[size];
                perm = new int[size];
                ranks = new int[size];
            }
            TmpKeys.Clear();
            for (int i = 0; i < n; i++)
            {
                string key = KeyOf(gizmos[i]);
                if (!TmpKeys.Contains(key))
                    TmpKeys.Add(key);
            }
            List<string> merged = DragUtil.Merge(TmpKeys, saved);
            for (int i = 0; i < n; i++)
            {
                Gizmo gizmo = gizmos[i];
                ranks[i] = merged.IndexOf(KeyOf(gizmo));
                fpTypes[i] = gizmo.GetType();
                fpIcons[i] = (gizmo as Command)?.icon;
                fpOrders[i] = gizmo.Order;
            }
            TmpIndices.Clear();
            for (int i = 0; i < n; i++)
                TmpIndices.Add(i);
            // Index as tie-breaker keeps the sort stable.
            TmpIndices.Sort((a, b) => ranks[a] != ranks[b] ? ranks[a].CompareTo(ranks[b]) : a.CompareTo(b));
            for (int i = 0; i < n; i++)
                perm[i] = TmpIndices[i];
            fpCount = n;
            fpVersion = version;
        }

        /// <summary>
        /// The saved order with every key of the current selection added, each new key placed right after
        /// the key that precedes it on screen. Keys of buttons the selection does not have are kept.
        /// </summary>
        private static List<string> MergeKeepAll(List<string> saved, List<string> current)
        {
            var result = new List<string>(saved ?? new List<string>());
            for (int i = 0; i < current.Count; i++)
            {
                if (result.Contains(current[i]))
                    continue;
                int insertAt = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int prev = result.IndexOf(current[j]);
                    if (prev >= 0)
                    {
                        insertAt = prev + 1;
                        break;
                    }
                }
                result.Insert(insertAt, current[i]);
            }
            return result;
        }

        private static string KeyOf(Gizmo gizmo)
        {
            switch (gizmo)
            {
                case Command_Ability { Ability: { def: { } def } }:
                    if (!AbilityKeys.TryGetValue(def, out string key))
                    {
                        key = "ability:" + def.defName;
                        AbilityKeys[def] = key;
                    }
                    return key;
                case Command_Action { action: { } action }:
                    return DelegateKey(action);
                case Command_Toggle { toggleAction: { } toggle }:
                    return DelegateKey(toggle);
                case Command_Target { action: { } target }:
                    return DelegateKey(target);
                default:
                    return TypeKey(gizmo.GetType());
            }
        }

        private static string DelegateKey(Delegate del)
        {
            MethodInfo method = del.Method;
            if (!MethodKeys.TryGetValue(method, out string key))
            {
                key = "method:" + method.DeclaringType?.FullName + "." + method.Name;
                MethodKeys[method] = key;
            }
            // Every reverse designator (haul, uninstall, ...) shares one lambda; tell them apart by designator.
            Designator designator = ClosureDesignator(del.Target);
            return designator != null ? TypeKey(designator.GetType()) : key;
        }

        private static Designator ClosureDesignator(object target)
        {
            if (target == null)
                return null;
            if (target is Designator des)
                return des;
            Type type = target.GetType();
            if (!ClosureDesignatorFields.TryGetValue(type, out FieldInfo field))
            {
                field = null;
                if (type.Name.StartsWith("<>c__DisplayClass"))
                {
                    FieldInfo self = AccessTools.Field(type, "<>4__this");
                    if (self != null && typeof(Designator).IsAssignableFrom(self.FieldType))
                        field = self;
                }
                ClosureDesignatorFields[type] = field;
            }
            return field?.GetValue(target) as Designator;
        }

        private static string TypeKey(Type type)
        {
            if (!TypeKeys.TryGetValue(type, out string key))
            {
                key = "type:" + type.FullName;
                TypeKeys[type] = key;
            }
            return key;
        }

        private static string HitTest(Vector2 pos)
        {
            foreach (var (gizmo, rect) in Rects)
            {
                if (rect.Contains(pos))
                    return KeyOf(gizmo);
            }
            return null;
        }

        private static Gizmo GizmoOf(string key)
        {
            foreach (var (gizmo, _) in Rects)
            {
                if (KeyOf(gizmo) == key)
                    return gizmo;
            }
            return null;
        }

        private static Rect? RectOf(Gizmo gizmo)
        {
            if (gizmo == null)
                return null;
            foreach (var entry in Rects)
            {
                if (entry.gizmo == gizmo)
                    return entry.rect;
            }
            return null;
        }
    }
}
