using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace VanillaTuneUp
{
    /// <summary>
    /// Right-drag reordering of the command buttons shown for the selection (pawns, buildings, animals...).
    /// One order is shared by everything: a button that the selection does not have is simply skipped.
    /// The buttons are recreated every frame, so each one is keyed by the code behind it (its action
    /// delegate's method, ability def or type). Lambda keys leave out the compiler's per-type method
    /// numbering so they survive a rebuild of the game or a mod. Buttons that share a key but are drawn
    /// as separate buttons (one lambda per item or mode) get a numbered key in list order. The resulting
    /// permutation is reused for as long as the list looks the same (same type, icon, label and Order at
    /// every index), so a steady selection costs a cheap comparison per button instead of a key lookup.
    /// </summary>
    [HarmonyPatchCategory("DragReorder")]
    [HarmonyPatch]
    public static class InspectOrdering
    {
        private static readonly Dictionary<MethodInfo, string> MethodKeys = new Dictionary<MethodInfo, string>();
        // Lambda key -> the key an earlier version saved for it, so saved orders can be carried over.
        private static readonly Dictionary<string, string> LegacyKeys = new Dictionary<string, string>();
        private static readonly Dictionary<Type, string> TypeKeys = new Dictionary<Type, string>();
        private static readonly Dictionary<AbilityDef, string> AbilityKeys = new Dictionary<AbilityDef, string>();
        private static readonly Dictionary<Type, FieldInfo> ClosureDesignatorFields = new Dictionary<Type, FieldInfo>();
        private static readonly DragController<string> Drag = new DragController<string>();
        private static readonly List<(Gizmo gizmo, Rect rect)> Rects = new List<(Gizmo, Rect)>();
        private static readonly List<string> RectKeys = new List<string>();
        private static readonly List<Gizmo> LastOrder = new List<Gizmo>();
        private static readonly List<string> LastKeys = new List<string>();
        private static readonly List<string> TmpKeys = new List<string>();
        private static readonly List<string> TmpBaseKeys = new List<string>();
        private static readonly Dictionary<string, int> TmpKeyCounts = new Dictionary<string, int>();
        private static readonly List<int> TmpIndices = new List<int>();
        private static readonly List<Gizmo> TmpGizmos = new List<Gizmo>();

        // Fingerprint of the last sorted list and the permutation that was applied to it.
        private static Fingerprint[] fingerprint = new Fingerprint[64];
        private static int[] perm = new int[64];
        private static int[] ranks = new int[64];
        private static int fpCount = -1;
        private static int fpVersion = -1;
        private static int version;
        // Keys are only needed while a drag is going on, so they are worked out on first use.
        private static bool lastKeysValid;
        private static bool rectKeysValid;

        private struct Fingerprint
        {
            private Type type;
            private Texture icon;
            private float order;
            private string label;

            public Fingerprint(Gizmo gizmo)
            {
                Command command = gizmo as Command;
                type = gizmo.GetType();
                icon = command?.icon;
                order = gizmo.Order;
                label = command?.defaultLabel;
            }

            public bool Matches(Gizmo gizmo)
            {
                Command command = gizmo as Command;
                return gizmo.GetType() == type && command?.icon == icon && gizmo.Order == order && command?.defaultLabel == label;
            }
        }

        public static bool Active { get; private set; }

        public static void Invalidate() => version++;

        [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor))]
        [HarmonyPrefix]
        private static void DrawGizmoGridFor_Prefix()
        {
            // Vanilla draws no buttons in screenshot mode; forget the last frame's so they cannot be hit.
            if (Find.ScreenshotModeHandler.Active)
            {
                Forget();
                return;
            }
            if (Event.current.type == EventType.Layout)
                return;
            Active = true;
            if (Drag.HandleEvent(HitTest, out string source, out string target) && source != target)
            {
                EnsureLastKeys();
                if (LastKeys.Contains(source))
                {
                    List<string> keys = DragUtil.Merge(LastKeys, VanillaTuneUpMod.Settings.dragReorder.inspectGizmos, keepAbsent: true);
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
            int source = RectIndexOf(dragging);
            int target = HitIndex(Event.current.mousePosition);
            Rect? sourceRect = source >= 0 ? Rects[source].rect : (Rect?)null;
            Rect? targetRect = target >= 0 && RectKeys[target] != dragging ? Rects[target].rect : (Rect?)null;
            string label = source >= 0 ? (Rects[source].gizmo as Command)?.LabelCap ?? "" : "";
            DragUtil.DrawFeedback(sourceRect, targetRect, label);
        }

        [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor))]
        [HarmonyFinalizer]
        private static void DrawGizmoGridFor_Finalizer() => Active = false;

        /// <summary>The last frame's buttons hold the old game's pawns and map; let them go on exit.</summary>
        [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
        [HarmonyPostfix]
        private static void ClearAllMapsAndWorld_Postfix() => Forget();

        private static void Forget()
        {
            Rects.Clear();
            RectKeys.Clear();
            LastOrder.Clear();
            LastKeys.Clear();
            lastKeysValid = false;
            rectKeysValid = false;
            Drag.Cancel();
        }

        /// <summary>Called from the redirected sort in DrawGizmoGrid after the vanilla sort.</summary>
        public static void Sort(IList<Gizmo> gizmos)
        {
            Rects.Clear();
            LastOrder.Clear();
            lastKeysValid = false;
            rectKeysValid = false;
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
                if (!fingerprint[i].Matches(gizmos[i]))
                    return false;
            }
            return true;
        }

        private static void BuildPermutation(IList<Gizmo> gizmos, List<string> saved)
        {
            int n = gizmos.Count;
            if (fingerprint.Length < n)
            {
                int size = Mathf.NextPowerOfTwo(n);
                fingerprint = new Fingerprint[size];
                perm = new int[size];
                ranks = new int[size];
            }
            ComputeKeys(gizmos, TmpKeys);
            if (MigrateLegacyKeys(TmpKeys, saved))
                VanillaTuneUpMod.Save();
            List<string> merged = DragUtil.Merge(TmpKeys, saved);
            for (int i = 0; i < n; i++)
            {
                ranks[i] = merged.IndexOf(TmpKeys[i]);
                fingerprint[i] = new Fingerprint(gizmos[i]);
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

        /// <summary>Renames saved keys from the old lambda format to the current one, in place.</summary>
        private static bool MigrateLegacyKeys(List<string> keys, List<string> saved)
        {
            bool changed = false;
            foreach (string key in keys)
            {
                if (!LegacyKeys.TryGetValue(key, out string legacy) || saved.Contains(key))
                    continue;
                int at = saved.IndexOf(legacy);
                if (at >= 0)
                {
                    saved[at] = key;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// One key per button, in list order. A button that shares its base key with an earlier one gets
        /// that button's key when the two are grouped into one on screen, and a numbered key otherwise.
        /// GroupsWith is only asked when the base keys match, so the extra cost stays small.
        /// </summary>
        private static void ComputeKeys(IList<Gizmo> gizmos, List<string> keys)
        {
            keys.Clear();
            TmpBaseKeys.Clear();
            TmpKeyCounts.Clear();
            for (int i = 0; i < gizmos.Count; i++)
            {
                Gizmo gizmo = gizmos[i];
                string baseKey = KeyOf(gizmo);
                string key = null;
                for (int j = 0; j < i && key == null; j++)
                {
                    if (TmpBaseKeys[j] == baseKey && gizmos[j].GroupsWith(gizmo))
                        key = keys[j];
                }
                if (key == null)
                {
                    TmpKeyCounts.TryGetValue(baseKey, out int count);
                    TmpKeyCounts[baseKey] = count + 1;
                    key = count == 0 ? baseKey : baseKey + "#" + (count + 1);
                }
                TmpBaseKeys.Add(baseKey);
                keys.Add(key);
            }
            TmpBaseKeys.Clear();
            TmpKeyCounts.Clear();
        }

        private static void EnsureLastKeys()
        {
            if (lastKeysValid)
                return;
            ComputeKeys(LastOrder, LastKeys);
            lastKeysValid = true;
        }

        /// <summary>The key of every drawn button, matched by reference against the last sorted list.</summary>
        private static void EnsureRectKeys()
        {
            if (rectKeysValid)
                return;
            EnsureLastKeys();
            RectKeys.Clear();
            foreach (var (gizmo, _) in Rects)
            {
                string key = null;
                for (int i = 0; i < LastOrder.Count; i++)
                {
                    if (LastOrder[i] == gizmo)
                    {
                        key = LastKeys[i];
                        break;
                    }
                }
                RectKeys.Add(key);
            }
            rectKeysValid = true;
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
                string legacy = "method:" + method.DeclaringType?.FullName + "." + method.Name;
                key = LambdaKey(method) ?? legacy;
                if (key != legacy && !LegacyKeys.ContainsKey(key))
                    LegacyKeys[key] = legacy;
                MethodKeys[method] = key;
            }
            // Every reverse designator (haul, uninstall, ...) shares one lambda; tell them apart by designator.
            Designator designator = ClosureDesignator(del.Target);
            return designator != null ? TypeKey(designator.GetType()) : key;
        }

        /// <summary>
        /// Key for a compiler-generated lambda or local function: the user type, the method it was written
        /// in, and its ordinal within that method (or the local function's name). The compiler also numbers
        /// methods per type ("b__15_3", "DisplayClass15_0"); that part shifts whenever a method is added
        /// earlier in the type, so it is left out. Returns null for an ordinary named method.
        /// </summary>
        private static string LambdaKey(MethodInfo method)
        {
            // "<GetGizmos>b__15_3", "<GetGizmos>b__3" or "<GetGizmos>g__Local|15_0".
            string name = method.Name;
            int close = name.IndexOf('>');
            if (!name.StartsWith("<") || close < 0)
                return null;
            int sep = name.IndexOf("__", close, StringComparison.Ordinal);
            if (sep < 0)
                return null;
            string outer = name.Substring(1, close - 1);
            string rest = name.Substring(sep + 2);
            int bar = rest.IndexOf('|');
            string id = bar >= 0 ? rest.Substring(0, bar) : rest.Substring(rest.LastIndexOf('_') + 1);
            Type root = method.DeclaringType;
            while (root?.DeclaringType != null && root.Name.StartsWith("<"))
                root = root.DeclaringType;
            return "lambda:" + root?.FullName + "." + outer + "." + id;
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
            int index = HitIndex(pos);
            return index >= 0 ? RectKeys[index] : null;
        }

        private static int HitIndex(Vector2 pos)
        {
            EnsureRectKeys();
            for (int i = 0; i < Rects.Count; i++)
            {
                if (Rects[i].rect.Contains(pos))
                    return i;
            }
            return -1;
        }

        private static int RectIndexOf(string key)
        {
            EnsureRectKeys();
            return RectKeys.IndexOf(key);
        }
    }
}
