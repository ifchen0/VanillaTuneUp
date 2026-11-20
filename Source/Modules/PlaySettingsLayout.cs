using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Lays out the bottom-right play settings row (map and world view) on a grid of its own so any button in it,
    /// other mods' included, can be hidden or moved. Every WidgetRow button call made while
    /// PlaySettings.DoPlaySettingsGlobalControls runs (vanilla, other mods' prefixes and postfixes) is keyed by its icon
    /// and the assembly that drew it. The row is moved to that key's grid slot right before the call, so the call
    /// itself is untouched and its click result still goes straight back to the caller. Hidden buttons are skipped and
    /// take no slot, Gap calls are ignored, and the row's final height comes from the slots in use, which also drops
    /// the empty line that vanilla wrapping can leave. Slots are worked out from the previous pass (the row is drawn on
    /// every GUI event); a button seen for the first time goes after the others until the next pass.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PlaySettingsLayout
    {
        public sealed class ButtonInfo
        {
            public Texture2D Icon;
            public string Label;
            public string Source;
        }

        private sealed class ViewState
        {
            public readonly List<string> LastPass = new List<string>();
            public readonly List<string> CurrentPass = new List<string>();
            public readonly Dictionary<string, int> Slots = new Dictionary<string, int>();
            public readonly Dictionary<Texture2D, string> KeyByTexture = new Dictionary<Texture2D, string>();
            public int SlotCount;
            public bool Dirty = true;
        }

        private const float SettingsRowHeight = 28f;
        private const float OffScreenY = -10000f;

        private static readonly ViewState[] Views = { new ViewState(), new ViewState() };
        private static readonly Dictionary<string, ButtonInfo> Known = new Dictionary<string, ButtonInfo>();
        private static HashSet<string> hidden;
        private static readonly int[] ReorderGroups = { -1, -1 };

        private static readonly AccessTools.FieldRef<WidgetRow, float> StartXRef = AccessTools.FieldRefAccess<WidgetRow, float>("startX");
        private static readonly AccessTools.FieldRef<WidgetRow, float> CurXRef = AccessTools.FieldRefAccess<WidgetRow, float>("curX");
        private static readonly AccessTools.FieldRef<WidgetRow, float> CurYRef = AccessTools.FieldRefAccess<WidgetRow, float>("curY");
        private static readonly AccessTools.FieldRef<WidgetRow, float> MaxWidthRef = AccessTools.FieldRefAccess<WidgetRow, float>("maxWidth");
        private static readonly AccessTools.FieldRef<WidgetRow, float> GapRef = AccessTools.FieldRefAccess<WidgetRow, float>("gap");
        private static readonly AccessTools.FieldRef<WidgetRow, UIDirection> GrowDirectionRef =
            AccessTools.FieldRefAccess<WidgetRow, UIDirection>("growDirection");

        // State of the DoPlaySettingsGlobalControls call in progress; row is null outside it.
        private static WidgetRow row;
        private static ViewState view;
        private static int viewIndex;
        private static int depth;
        private static int extraSlots;
        private static int maxSlot;
        private static float startX, startY, step;
        private static int perRow;
        private static bool growsLeft, growsUp;

        private static HashSet<string> Hidden => hidden ??= new HashSet<string>(VanillaTuneUpMod.Settings.playSettingsHidden);

        public static void Begin(WidgetRow r, bool worldView)
        {
            if (r == null || row != null)
                return;
            row = r;
            viewIndex = worldView ? 1 : 0;
            view = Views[viewIndex];
            if (view.Dirty)
                Rebuild(view);
            startX = StartXRef(r);
            startY = CurYRef(r);
            float gap = GapRef(r);
            step = WidgetRow.IconSize + gap;
            perRow = Mathf.Max(1, Mathf.FloorToInt((MaxWidthRef(r) + gap) / step));
            UIDirection dir = GrowDirectionRef(r);
            growsLeft = dir is UIDirection.LeftThenUp or UIDirection.LeftThenDown;
            growsUp = dir is UIDirection.LeftThenUp or UIDirection.RightThenUp;
            view.CurrentPass.Clear();
            extraSlots = 0;
            maxSlot = -1;
            depth = 0;
            PlaceForNext();
        }

        public static void End()
        {
            if (row == null)
                return;
            if (!SameKeys(view.CurrentPass, view.LastPass))
            {
                view.LastPass.Clear();
                view.LastPass.AddRange(view.CurrentPass);
                view.Dirty = true;
            }
            // Leave the row where a wrapped vanilla row would end, minus any line nothing was drawn on.
            int lines = maxSlot < 0 ? 0 : maxSlot / perRow;
            int cols = maxSlot < 0 ? 0 : maxSlot % perRow + 1;
            CurXRef(row) = growsLeft ? startX - cols * step : startX + cols * step;
            CurYRef(row) = growsUp ? startY - lines * step : startY + lines * step;
            row = null;
            view = null;
        }

        /// <summary>Called before a button draws on any WidgetRow; false skips the button.</summary>
        public static bool BeforeButton(WidgetRow r, Texture2D tex, string tooltip, out bool entered)
        {
            entered = false;
            if (r != row || depth > 0 || tex == null)
                return true;
            string key = KeyFor(tex, tooltip);
            view.CurrentPass.Add(key);
            if (Hidden.Contains(key))
            {
                // Callers that work out their icon's rect from the row afterwards (tooltip, mouse wheel area) get one
                // off screen instead of a neighbour's. Only X is checked for wrapping, so this cannot start a new line.
                CurXRef(row) = startX;
                CurYRef(row) = OffScreenY;
                return false;
            }
            if (!view.Slots.TryGetValue(key, out int slot))
                slot = view.SlotCount + extraSlots++;
            if (slot > maxSlot)
                maxSlot = slot;
            MoveTo(slot);
            depth++;
            entered = true;
            return true;
        }

        public static void AfterButton(bool entered)
        {
            if (!entered || row == null)
                return;
            depth--;
            PlaceForNext();
        }

        /// <summary>Spacing between buttons comes from the grid, so gaps other code adds are dropped.</summary>
        public static bool AllowGap(WidgetRow r) => r != row || depth > 0;

        /// <summary>
        /// Puts the row where the next button is expected, so code that reads the row's position before drawing its
        /// button (Better Trees' right-click area, Hauler's Dream's highlight rect) gets that button's slot.
        /// </summary>
        private static void PlaceForNext()
        {
            int i = view.CurrentPass.Count;
            if (i >= view.LastPass.Count || !view.Slots.TryGetValue(view.LastPass[i], out int slot))
                slot = view.SlotCount + extraSlots;
            MoveTo(slot);
        }

        private static void MoveTo(int slot)
        {
            int col = slot % perRow;
            int line = slot / perRow;
            CurXRef(row) = growsLeft ? startX - col * step : startX + col * step;
            CurYRef(row) = growsUp ? startY - line * step : startY + line * step;
        }

        private static bool SameKeys(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!ReferenceEquals(a[i], b[i]) && a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static string KeyFor(Texture2D tex, string tooltip)
        {
            if (!view.KeyByTexture.TryGetValue(tex, out string key))
            {
                Assembly source = FindSource();
                string asmName = source?.GetName().Name ?? "Unknown";
                string texName = tex.name.NullOrEmpty() ? "icon" : tex.name;
                key = (viewIndex == 1 ? "world/" : "map/") + asmName + ":" + texName;
                view.KeyByTexture[tex] = key;
                if (!Known.ContainsKey(key))
                {
                    Known[key] = new ButtonInfo
                    {
                        Icon = tex,
                        Label = LabelFor(tooltip, texName, source),
                        Source = ModName(source)
                    };
                }
            }
            // Same icon twice in one pass (rare): number the later ones so each keeps a slot of its own.
            if (view.CurrentPass.Contains(key))
            {
                int n = 2;
                while (view.CurrentPass.Contains(key + "#" + n))
                    n++;
                key = key + "#" + n;
                if (!Known.ContainsKey(key) && Known.TryGetValue(view.KeyByTexture[tex], out ButtonInfo first))
                    Known[key] = new ButtonInfo { Icon = first.Icon, Label = first.Label + " #" + n, Source = first.Source };
            }
            return key;
        }

        /// <summary>
        /// The assembly of the first caller outside WidgetRow and this module. Run once per icon. Patched methods
        /// are resolved to their originals, so vanilla buttons drawn from a patched PlaySettings method still count
        /// as vanilla.
        /// </summary>
        private static Assembly FindSource()
        {
            try
            {
                var trace = new StackTrace(1, false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    StackFrame frame = trace.GetFrame(i);
                    MethodBase method = Harmony.GetOriginalMethodFromStackframe(frame) ?? frame.GetMethod();
                    Type type = method?.DeclaringType;
                    if (type == null || type == typeof(WidgetRow) || type == typeof(PlaySettingsLayout)
                        || type == typeof(Patch_PlaySettingsLayout_Toggle) || type == typeof(Patch_PlaySettingsLayout_Button)
                        || type == typeof(Patch_PlaySettings_IconGap) || type.Assembly.IsDynamic
                        || type.Assembly == typeof(Harmony).Assembly)
                        continue;
                    return type.Assembly;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static string LabelFor(string tooltip, string texName, Assembly source)
        {
            if (!tooltip.NullOrEmpty())
            {
                // Vanilla puts "Hotkey: X" and a blank line in front of some tips.
                string hotKey = "HotKeyTip".Translate();
                foreach (string line in tooltip.Split('\n'))
                {
                    string text = line.Trim();
                    if (text.Length > 0 && !text.StartsWith(hotKey))
                        return text.StripTags();
                }
            }
            // This mod's own buttons draw their tips separately; their label key is named after the icon.
            if (source == typeof(PlaySettingsLayout).Assembly && ("VTU_" + texName).CanTranslate())
                return ("VTU_" + texName).Translate();
            return texName;
        }

        private static string ModName(Assembly asm)
        {
            if (asm == null)
                return "?";
            if (asm == typeof(PlaySettings).Assembly)
                return "RimWorld";
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies.loadedAssemblies.Contains(asm))
                    return mod.Name;
            }
            return asm.GetName().Name;
        }

        /// <summary>Keys seen in the last pass of a view, in the player's order; new ones last, in drawing order.</summary>
        private static List<string> Ordered(ViewState state)
        {
            var present = new HashSet<string>(state.LastPass);
            var result = new List<string>(state.LastPass.Count);
            foreach (string key in VanillaTuneUpMod.Settings.playSettingsOrder)
            {
                if (present.Remove(key))
                    result.Add(key);
            }
            foreach (string key in state.LastPass)
            {
                if (present.Remove(key))
                    result.Add(key);
            }
            return result;
        }

        private static void Rebuild(ViewState state)
        {
            state.Slots.Clear();
            int slot = 0;
            foreach (string key in Ordered(state))
            {
                if (!Hidden.Contains(key))
                    state.Slots[key] = slot++;
            }
            state.SlotCount = slot;
            state.Dirty = false;
        }

        private static void SettingsChanged()
        {
            hidden = null;
            foreach (ViewState state in Views)
                state.Dirty = true;
        }

        public static void DrawSettings(Listing_Standard listing)
        {
            GUI.color = Color.gray;
            listing.Label("VTU_PlaySettingsLayout_Help".Translate());
            GUI.color = Color.white;
            DrawView(listing, 0, "VTU_PlaySettingsLayout_Map");
            DrawView(listing, 1, "VTU_PlaySettingsLayout_World");
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            if ((settings.playSettingsOrder.Count > 0 || settings.playSettingsHidden.Count > 0)
                && listing.ButtonText("VTU_PlaySettingsLayout_Reset".Translate(), widthPct: 0.3f))
            {
                settings.playSettingsOrder.Clear();
                settings.playSettingsHidden.Clear();
                SettingsChanged();
            }
        }

        private static void DrawView(Listing_Standard listing, int index, string headerKey)
        {
            listing.Label(headerKey.Translate());
            List<string> keys = Ordered(Views[index]);
            if (keys.Count == 0)
            {
                GUI.color = Color.gray;
                listing.Label("VTU_PlaySettingsLayout_NotSeen".Translate());
                GUI.color = Color.white;
                return;
            }
            Rect all = listing.GetRect(keys.Count * SettingsRowHeight);
            // NewGroup only hands out ids on Repaint (-1 otherwise); the mouse-down that starts a drag comes in another
            // event, so the id from the last Repaint is kept for those.
            if (Event.current.type == EventType.Repaint)
                ReorderGroups[index] = ReorderableWidget.NewGroup((from, to) => Reorder(keys, from, to), ReorderableDirection.Vertical, all, 2f);
            int group = ReorderGroups[index];
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                Rect rect = new Rect(all.x, all.y + i * SettingsRowHeight, all.width, SettingsRowHeight);
                ReorderableWidget.Reorderable(group, rect);
                if (Mouse.IsOver(rect))
                    Widgets.DrawHighlight(rect);
                Known.TryGetValue(key, out ButtonInfo info);
                bool shown = !Hidden.Contains(key);
                bool before = shown;
                Widgets.Checkbox(rect.x, rect.y + 2f, ref shown, 24f);
                if (shown != before)
                {
                    if (shown)
                        VanillaTuneUpMod.Settings.playSettingsHidden.Remove(key);
                    else
                        VanillaTuneUpMod.Settings.playSettingsHidden.Add(key);
                    SettingsChanged();
                }
                Rect iconRect = new Rect(rect.x + 32f, rect.y + 2f, 24f, 24f);
                if (info?.Icon != null)
                {
                    GUI.color = shown ? Color.white : Color.gray;
                    GUI.DrawTexture(iconRect, info.Icon);
                }
                float labelX = iconRect.xMax + 8f;
                float sourceWidth = Mathf.Min(rect.xMax - labelX, rect.width * 0.35f);
                Rect sourceRect = new Rect(rect.xMax - sourceWidth, rect.y, sourceWidth, rect.height);
                Rect labelRect = new Rect(labelX, rect.y, sourceRect.x - labelX - 8f, rect.height);
                using (new TextBlock(TextAnchor.MiddleLeft))
                {
                    GUI.color = shown ? Color.white : Color.gray;
                    Widgets.Label(labelRect, (info?.Label ?? key).Truncate(labelRect.width));
                    GUI.color = Color.gray;
                    Widgets.Label(sourceRect, (info?.Source ?? "?").Truncate(sourceRect.width));
                }
                GUI.color = Color.white;
                TooltipHandler.TipRegion(rect, "VTU_PlaySettingsLayout_RowTip".Translate());
            }
            listing.Gap(6f);
        }

        private static void Reorder(List<string> keys, int from, int to)
        {
            if (from == to || from < 0 || from >= keys.Count)
                return;
            var order = new List<string>(keys);
            string moved = order[from];
            order.Insert(to, moved);
            order.RemoveAt(from < to ? from : from + 1);
            // Keep the other view's order, and buttons not drawn right now, after this view's keys.
            var inView = new HashSet<string>(order);
            foreach (string key in VanillaTuneUpMod.Settings.playSettingsOrder)
            {
                if (!inView.Contains(key))
                    order.Add(key);
            }
            VanillaTuneUpMod.Settings.playSettingsOrder = order;
            SettingsChanged();
        }
    }

    [HarmonyPatchCategory("PlaySettingsLayout")]
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class Patch_PlaySettingsLayout_Scope
    {
        // First, so buttons other mods draw in their own prefixes are placed too.
        [HarmonyPriority(Priority.First)]
        public static void Prefix(WidgetRow row, bool worldView) => PlaySettingsLayout.Begin(row, worldView);

        // Runs after every postfix, so buttons added there are counted before the row height is set.
        public static void Finalizer() => PlaySettingsLayout.End();
    }

    [HarmonyPatchCategory("PlaySettingsLayout")]
    [HarmonyPatch(typeof(WidgetRow), nameof(WidgetRow.ToggleableIcon))]
    public static class Patch_PlaySettingsLayout_Toggle
    {
        public static bool Prefix(WidgetRow __instance, Texture2D tex, string tooltip, out bool __state) =>
            PlaySettingsLayout.BeforeButton(__instance, tex, tooltip, out __state);

        public static void Postfix(bool __state) => PlaySettingsLayout.AfterButton(__state);
    }

    [HarmonyPatchCategory("PlaySettingsLayout")]
    [HarmonyPatch(typeof(WidgetRow), nameof(WidgetRow.ButtonIcon))]
    public static class Patch_PlaySettingsLayout_Button
    {
        public static bool Prefix(WidgetRow __instance, Texture2D tex, string tooltip, ref bool __result, out bool __state)
        {
            if (PlaySettingsLayout.BeforeButton(__instance, tex, tooltip, out __state))
                return true;
            __result = false;
            return false;
        }

        public static void Postfix(bool __state) => PlaySettingsLayout.AfterButton(__state);
    }

    [HarmonyPatchCategory("PlaySettingsLayout")]
    [HarmonyPatch(typeof(WidgetRow), nameof(WidgetRow.Gap))]
    public static class Patch_PlaySettingsLayout_Gap
    {
        public static bool Prefix(WidgetRow __instance) => PlaySettingsLayout.AllowGap(__instance);
    }
}
