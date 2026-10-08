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
    /// Shrinks every colonist bar entry to two thirds of its height while the mouse is away from the bar, and
    /// restores it while the mouse is over the bar's area (all entries plus the group frame margin). The rows move
    /// closer together too, so the whole bar takes less screen space. Each row opens on its own: the row under the
    /// mouse and every row below it open, rows above stay collapsed until the mouse reaches them, so the entry under
    /// the mouse never moves away from it. Collapsed entries keep the portrait where it was and cut it off at the
    /// shortened edges, so the head stays visible. Weapon icons are hidden while collapsed and names and status icons
    /// shown, each changeable in the settings.
    /// Optionally also gives the bar taller entries with a closer camera, so each portrait shows the whole pawn
    /// inside its entry (left to [NL] Custom Portraits when that mod is active).
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CompactColonistBar
    {
        private const float CollapsedHeightFactor = 2f / 3f;
        // Vertical gap between rows: vanilla leaves room for the name under each entry.
        private const float RowGap = 32f;
        private const float CollapsedRowGap = 10f;
        private const float MarginTop = 21f;
        // With taller portraits the weapon icon is sized from the entry width (36 at scale 1, as vanilla at 48x48)
        // and placed under the name; the row gap grows to fit it.
        private const float WeaponIconWidthFactor = 0.75f;
        private const float WeaponRowGap = 54f;
        private const float AnimationSeconds = 0.12f;
        private const float CollapseDelaySeconds = 0.3f;
        // Same margin the vanilla group frame draws around its entries.
        private const float HoverMargin = 12f;

        private const string CustomPortraitsPackageId = "Nals.CustomPortraits";
        private static readonly Vector2 VanillaBaseSize = ColonistBar.BaseSize;
        private static readonly Vector2 VanillaPawnTextureSize = ColonistBarColonistDrawer.PawnTextureSize;
        private static readonly FieldInfo BaseSizeField = AccessTools.Field(typeof(ColonistBar), nameof(ColonistBar.BaseSize));
        private static readonly FieldInfo PawnTextureSizeField =
            AccessTools.Field(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.PawnTextureSize));

        public static readonly bool CustomPortraitsModActive =
            ModLister.GetActiveModWithIdentifier(CustomPortraitsPackageId, ignorePostfix: true) != null;
        private static bool portraitSizeDirty = true;

        private static float lastHoverTime = float.NegativeInfinity;
        // Topmost row the mouse reached since it entered the bar; this row and all rows below it are open.
        private static int anchorRow = int.MaxValue;

        // Vanilla draw locations of the last recache with the row of each entry; the bar's own list holds the
        // locations moved for the current row openness.
        private static readonly List<Vector2> baseDrawLocs = new List<Vector2>();
        private static readonly List<int> entryRows = new List<int>();
        private static List<Vector2> barDrawLocs;
        private static float barScale = 1f;

        // Per row: openness (0 collapsed, 1 open), top edge, entry height and gap above in the current layout.
        private static readonly List<float> rowOpenness = new List<float>();
        private static readonly List<float> rowTops = new List<float>();
        private static readonly List<float> rowHeights = new List<float>();
        private static readonly List<float> rowGapsAbove = new List<float>();

        // Openness of the entry being drawn, set when its portrait is drawn and used for its name, icons and weapon.
        private static float drawingEntryOpenness = 1f;

        private static readonly Action<ColonistBarColonistDrawer, Rect, Pawn> DrawIconsOriginal =
            AccessTools.MethodDelegate<Action<ColonistBarColonistDrawer, Rect, Pawn>>(
                AccessTools.Method(typeof(ColonistBarColonistDrawer), "DrawIcons"));

        private static bool DrawingEntryOpen => drawingEntryOpenness >= 1f;

        /// <summary>Gap under a row with the given openness, in unscaled units; it holds the name and weapon.</summary>
        private static float RowGapFor(float openness)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            bool weapons = Prefs.ShowWeaponsUnderPortraitMode != ShowWeaponsUnderPortraitMode.Never;
            float weaponGap = UseCustomPortraits ? WeaponRowGap : RowGap;
            float expandedGap = weapons ? weaponGap : RowGap;
            float collapsedGap = weapons && settings.compactBarShowWeapons ? weaponGap
                : settings.compactBarShowLabels ? RowGap : CollapsedRowGap;
            return Mathf.Lerp(collapsedGap, expandedGap, openness);
        }

        /// <summary>Remembers freshly calculated vanilla draw locations and moves them for the current openness.</summary>
        public static void Notify_DrawLocsCalculated(List<Vector2> drawLocs, float scale)
        {
            barDrawLocs = drawLocs;
            barScale = scale;
            baseDrawLocs.Clear();
            baseDrawLocs.AddRange(drawLocs);
            // Vanilla puts every row at the same height in all groups, so rows are the distinct heights in order.
            var rowYs = new List<float>();
            foreach (Vector2 loc in drawLocs)
            {
                if (!rowYs.Exists(y => Mathf.Abs(y - loc.y) < 0.5f))
                    rowYs.Add(loc.y);
            }
            rowYs.Sort();
            entryRows.Clear();
            foreach (Vector2 loc in drawLocs)
                entryRows.Add(rowYs.FindIndex(y => Mathf.Abs(y - loc.y) < 0.5f));
            while (rowOpenness.Count < rowYs.Count)
                rowOpenness.Add(rowOpenness.Count > 0 ? rowOpenness[rowOpenness.Count - 1] : 0f);
            if (rowOpenness.Count > rowYs.Count)
                rowOpenness.RemoveRange(rowYs.Count, rowOpenness.Count - rowYs.Count);
            ApplyLayout();
        }

        private static void ApplyLayout()
        {
            rowTops.Clear();
            rowHeights.Clear();
            rowGapsAbove.Clear();
            float top = MarginTop;
            for (int row = 0; row < rowOpenness.Count; row++)
            {
                float openness = rowOpenness[row];
                float height = ColonistBar.BaseSize.y * Mathf.Lerp(CollapsedHeightFactor, 1f, openness) * barScale;
                rowTops.Add(top);
                rowHeights.Add(height);
                // The first row has the screen edge above it; it is clipped like the rows below for a uniform look.
                rowGapsAbove.Add(RowGapFor(row > 0 ? rowOpenness[row - 1] : openness));
                top += height + RowGapFor(openness) * barScale;
            }
            if (barDrawLocs == null || barDrawLocs.Count != baseDrawLocs.Count)
                return;
            for (int i = 0; i < baseDrawLocs.Count; i++)
                barDrawLocs[i] = new Vector2(baseDrawLocs[i].x, rowTops[entryRows[i]]);
        }

        /// <summary>Row whose entries start at <paramref name="y"/> in the current layout, or -1.</summary>
        private static int RowAtTop(float y)
        {
            for (int row = 0; row < rowTops.Count; row++)
            {
                if (Mathf.Abs(rowTops[row] - y) < 0.01f)
                    return row;
            }
            return -1;
        }

        /// <summary>Entry rect for the entry at (x, y): the height comes from the entry's row.</summary>
        public static Rect EntryRect(float x, float y, float width, float height)
        {
            int row = RowAtTop(y);
            return new Rect(x, y, width, row >= 0 ? rowHeights[row] : height);
        }

        public static void InitEntryRect(ref Rect rect, float x, float y, float width, float height) =>
            rect = EntryRect(x, y, width, height);

        /// <summary>Bottom edge of a group's entries in the current layout, for its frame.</summary>
        public static float GroupBottom(int group)
        {
            List<ColonistBar.Entry> entries = Find.ColonistBar.Entries;
            float bottom = 0f;
            for (int i = 0; i < entries.Count && i < entryRows.Count && i < barDrawLocs.Count; i++)
            {
                if (entries[i].group == group)
                    bottom = Mathf.Max(bottom, barDrawLocs[i].y + rowHeights[entryRows[i]]);
            }
            return bottom;
        }

        private static bool UseCustomPortraits => VanillaTuneUpMod.Settings.compactBarCustomPortraits && !CustomPortraitsModActive;

        /// <summary>Sets the entry and portrait sizes for the current settings once they changed.</summary>
        public static void ApplyPortraitSizeIfDirty()
        {
            if (!portraitSizeDirty || CustomPortraitsModActive)
                return;
            portraitSizeDirty = false;
            // Both fields are static readonly, so they are set by reflection, like [NL] Custom Portraits does.
            Vector2 baseSize = UseCustomPortraits
                ? new Vector2(VanillaBaseSize.x, VanillaTuneUpMod.Settings.compactBarPortraitHeight)
                : VanillaBaseSize;
            Vector2 textureSize = UseCustomPortraits ? baseSize - new Vector2(2f, 2f) : VanillaPawnTextureSize;
            BaseSizeField.SetValue(null, baseSize);
            PawnTextureSizeField.SetValue(null, textureSize);
            PortraitsCache.Clear();
            Find.ColonistBar?.MarkColonistsDirty();
        }

        /// <summary>Moves the camera closer for colonist bar portraits and renders them at double resolution.</summary>
        public static void AdjustPortraitCamera(Pawn pawn, ref Vector2 size, ref Vector3 cameraOffset, ref float cameraZoom)
        {
            if (!UseCustomPortraits || cameraOffset != ColonistBarColonistDrawer.PawnTextureCameraOffset)
                return;
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            cameraOffset.z += settings.compactBarPortraitOffset;
            cameraZoom = settings.compactBarPortraitZoom * settings.RaceZoom(pawn?.def?.defName);
            size *= 2f;
        }

        /// <summary>Updates the open/closed state of every row once per frame from the mouse position.</summary>
        public static void Update(ColonistBar bar)
        {
            List<ColonistBar.Entry> entries = bar.Entries;
            if (barDrawLocs == null || entries.Count == 0 || entryRows.Count != entries.Count)
                return;
            float now = Time.realtimeSinceStartup;
            Vector2 mouse = UI.MousePositionOnUIInverted;
            if (HoverArea().Contains(mouse))
            {
                lastHoverTime = now;
                // The gap under a row belongs to that row: it holds the row's names and weapons.
                int row = 0;
                while (row + 1 < rowTops.Count && mouse.y >= rowTops[row + 1])
                    row++;
                anchorRow = Mathf.Min(anchorRow, row);
            }
            else if (now - lastHoverTime > CollapseDelaySeconds)
                anchorRow = int.MaxValue;
            float step = Time.unscaledDeltaTime / AnimationSeconds;
            for (int row = 0; row < rowOpenness.Count; row++)
                rowOpenness[row] = Mathf.MoveTowards(rowOpenness[row], row >= anchorRow ? 1f : 0f, step);
            // Rewritten every frame: cheap, and also picks up settings changes that alter the gaps.
            ApplyLayout();
        }

        private static Rect HoverArea()
        {
            float xMin = float.MaxValue, xMax = float.MinValue, yMax = 0f;
            float width = ColonistBar.BaseSize.x * barScale;
            for (int i = 0; i < barDrawLocs.Count; i++)
            {
                xMin = Mathf.Min(xMin, barDrawLocs[i].x);
                xMax = Mathf.Max(xMax, barDrawLocs[i].x + width);
                yMax = Mathf.Max(yMax, barDrawLocs[i].y + rowHeights[entryRows[i]]);
            }
            return Rect.MinMaxRect(xMin, 0f, xMax, yMax).ExpandedBy(HoverMargin * barScale);
        }

        /// <summary>
        /// Draws the portrait at its full-height position, cut off at the bottom of a shortened entry and at the
        /// top of the narrowed row gap, so it does not cover the row above.
        /// </summary>
        public static void DrawPortrait(Rect textureRect, Texture texture, Rect entryRect)
        {
            int row = RowAtTop(entryRect.y);
            drawingEntryOpenness = row >= 0 ? rowOpenness[row] : 1f;
            float gapAbove = row >= 0 ? rowGapsAbove[row] : RowGap;
            float bottom = Mathf.Min(textureRect.yMax, entryRect.yMax - 1f);
            float top = Mathf.Max(textureRect.y, entryRect.y - (gapAbove - 2f) * Find.ColonistBar.Scale);
            if (bottom >= textureRect.yMax && top <= textureRect.y)
            {
                GUI.DrawTexture(textureRect, texture);
                return;
            }
            if (bottom <= top)
                return;
            // Texture coordinates start at the bottom edge.
            float vMin = (textureRect.yMax - bottom) / textureRect.height;
            float vMax = (textureRect.yMax - top) / textureRect.height;
            GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(textureRect.x, top, textureRect.xMax, bottom),
                texture, new Rect(0f, vMin, 1f, vMax - vMin));
        }

        public static void DrawLabel(Pawn pawn, Vector2 pos, float alpha, float truncateToWidth,
            Dictionary<string, string> truncatedLabelsCache, GameFont font, bool alwaysDrawBg, bool alignCenter)
        {
            if (DrawingEntryOpen || VanillaTuneUpMod.Settings.compactBarShowLabels)
                GenMapUI.DrawPawnLabel(pawn, pos, alpha, truncateToWidth, truncatedLabelsCache, font, alwaysDrawBg, alignCenter);
        }

        public static void DrawIcons(ColonistBarColonistDrawer drawer, Rect rect, Pawn colonist)
        {
            if (DrawingEntryOpen || VanillaTuneUpMod.Settings.compactBarShowIcons)
                DrawIconsOriginal(drawer, rect, colonist);
        }

        /// <summary>
        /// Vanilla sizes the weapon icon from the entry height and puts it under the entry, so with taller entries
        /// it grows and lands on the next row. With taller portraits it keeps the vanilla 48x48 size and sits right
        /// under the name instead.
        /// </summary>
        public static Rect WeaponRect(Rect vanillaRect)
        {
            if (!UseCustomPortraits)
                return vanillaRect;
            // Undo ScaledBy(0.75f) of new Rect(x, y + h * 1.05f, w, h) to get the entry back.
            float width = vanillaRect.width / 0.75f, height = vanillaRect.height / 0.75f;
            float x = vanillaRect.center.x - width / 2f;
            float yMax = vanillaRect.center.y - height / 2f - height * 0.05f;
            // The name starts 4 (scaled) above the entry's bottom and is 12 or 16 high (unscaled).
            float labelBottom = yMax - 4f * Find.ColonistBar.Scale + (Prefs.DisableTinyText ? 16f : 12f);
            float size = width * WeaponIconWidthFactor;
            return new Rect(x + (width - size) / 2f, labelBottom, size, size);
        }

        public static ShowWeaponsUnderPortraitMode WeaponMode()
        {
            if (DrawingEntryOpen || VanillaTuneUpMod.Settings.compactBarShowWeapons)
                return Prefs.ShowWeaponsUnderPortraitMode;
            return ShowWeaponsUnderPortraitMode.Never;
        }

        public static void DrawSettings(Listing_Standard listing)
        {
            // The settings window writes the settings when it closes.
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.Label("VTU_CB_WhileCollapsed".Translate());
            listing.CheckboxLabeled("VTU_CB_ShowLabels".Translate(), ref settings.compactBarShowLabels);
            listing.CheckboxLabeled("VTU_CB_ShowIcons".Translate(), ref settings.compactBarShowIcons);
            listing.CheckboxLabeled("VTU_CB_ShowWeapons".Translate(), ref settings.compactBarShowWeapons);
            listing.Gap(6f);
            if (CustomPortraitsModActive)
            {
                GUI.color = Color.gray;
                listing.Label("VTU_CB_PortraitsByCustomPortraits".Translate());
                GUI.color = Color.white;
                return;
            }
            bool custom = settings.compactBarCustomPortraits;
            float height = settings.compactBarPortraitHeight, zoom = settings.compactBarPortraitZoom, offset = settings.compactBarPortraitOffset;
            listing.CheckboxLabeled("VTU_CB_CustomPortraits".Translate(), ref settings.compactBarCustomPortraits,
                "VTU_CB_CustomPortraits_Desc".Translate());
            if (settings.compactBarCustomPortraits)
            {
                settings.compactBarPortraitHeight = Mathf.Round(listing.SliderLabeled(
                    "VTU_CB_PortraitHeight".Translate(settings.compactBarPortraitHeight.ToString("0")),
                    settings.compactBarPortraitHeight, 48f, 128f) / 2f) * 2f;
                settings.compactBarPortraitZoom = Mathf.Round(listing.SliderLabeled(
                    "VTU_CB_PortraitZoom".Translate(settings.compactBarPortraitZoom.ToString("0.00")),
                    settings.compactBarPortraitZoom, 1f, 4f) * 20f) / 20f;
                settings.compactBarPortraitOffset = Mathf.Round(listing.SliderLabeled(
                    "VTU_CB_PortraitOffset".Translate(settings.compactBarPortraitOffset.ToString("0.00")),
                    settings.compactBarPortraitOffset, -0.5f, 0.5f) * 100f) / 100f;
                if (listing.ButtonText("VTU_CB_PortraitReset".Translate()))
                    settings.ResetCompactBarPortraits();
            }
            bool raceChanged = settings.compactBarCustomPortraits && DrawRaceZoom(listing, settings);
            if (custom != settings.compactBarCustomPortraits || height != settings.compactBarPortraitHeight)
                portraitSizeDirty = true;
            else if (raceChanged || zoom != settings.compactBarPortraitZoom || offset != settings.compactBarPortraitOffset)
                PortraitsCache.Clear();
        }

        /// <summary>One zoom slider per race on the colonist bar, plus races with a saved or default value.</summary>
        private static bool DrawRaceZoom(Listing_Standard listing, TuneUpSettings settings)
        {
            var races = new List<ThingDef>();
            void Add(ThingDef def)
            {
                if (def != null && !races.Contains(def))
                    races.Add(def);
            }
            if (Current.ProgramState == ProgramState.Playing)
            {
                foreach (ColonistBar.Entry entry in Find.ColonistBar.Entries)
                    Add(entry.pawn?.def);
            }
            foreach (string defName in settings.RaceZoomDefNames())
                Add(DefDatabase<ThingDef>.GetNamedSilentFail(defName));
            if (races.Count == 0)
                return false;
            listing.Label("VTU_CB_RaceZoom".Translate());
            bool changed = false;
            foreach (ThingDef race in races)
            {
                float before = settings.RaceZoom(race.defName);
                float after = Mathf.Round(listing.SliderLabeled(
                    "VTU_CB_RaceZoomEntry".Translate(race.LabelCap, before.ToString("0.00")), before, 0.5f, 2f) * 100f) / 100f;
                if (after != before)
                {
                    settings.SetRaceZoom(race.defName, after);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// Replaces the first Rect construction in the method, the entry rect built from a draw location and
        /// ColonistBar.Size, with EntryRect so each entry gets the height of its row.
        /// </summary>
        public static IEnumerable<CodeInstruction> ReplaceEntryRect(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            bool done = false;
            foreach (CodeInstruction code in instructions)
            {
                if (!done && code.operand is ConstructorInfo ctor && ctor.DeclaringType == typeof(Rect) && ctor.GetParameters().Length == 4)
                {
                    // newobj leaves the Rect on the stack; call initializes the Rect at an address below the arguments.
                    string method = code.opcode == System.Reflection.Emit.OpCodes.Newobj ? nameof(EntryRect) : nameof(InitEntryRect);
                    code.opcode = System.Reflection.Emit.OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(CompactColonistBar), method);
                    done = true;
                }
                yield return code;
            }
            if (!done)
                throw new Exception($"Entry rect not found in {original.DeclaringType.Name}.{original.Name}");
        }

        /// <summary>Replaces the first call to <paramref name="target"/> with a static method taking the same stack.</summary>
        public static List<CodeInstruction> ReplaceCall(List<CodeInstruction> codes, MethodInfo target, MethodInfo replacement)
        {
            int index = codes.FindIndex(c => c.Calls(target));
            if (index < 0)
                throw new Exception($"Call to {target.DeclaringType.Name}.{target.Name} not found");
            codes[index] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, replacement).MoveLabelsFrom(codes[index]).MoveBlocksFrom(codes[index]);
            return codes;
        }
    }

    [HarmonyPatchCategory("CompactColonistBar")]
    [HarmonyPatch(typeof(ColonistBar), nameof(ColonistBar.ColonistBarOnGUI))]
    public static class Patch_ColonistBar_ColonistBarOnGUI
    {
        public static void Prefix(ColonistBar __instance)
        {
            CompactColonistBar.ApplyPortraitSizeIfDirty();
            if (Event.current.type == EventType.Repaint)
                CompactColonistBar.Update(__instance);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo weaponMode = AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.ShowWeaponsUnderPortraitMode));
            MethodInfo replacement = AccessTools.Method(typeof(CompactColonistBar), nameof(CompactColonistBar.WeaponMode));
            // The weapon icon rect is the only ScaledBy in the method.
            MethodInfo scaledBy = AccessTools.Method(typeof(GenUI), nameof(GenUI.ScaledBy));
            bool weaponRect = false;
            foreach (CodeInstruction code in CompactColonistBar.ReplaceEntryRect(instructions, original))
            {
                if (code.Calls(weaponMode))
                    code.operand = replacement;
                yield return code;
                if (code.Calls(scaledBy) && !weaponRect)
                {
                    yield return CodeInstruction.Call(typeof(CompactColonistBar), nameof(CompactColonistBar.WeaponRect));
                    weaponRect = true;
                }
            }
            if (!weaponRect)
                Log.Warning("[Vanilla Tune-Up] Weapon icon rect not found in ColonistBar.ColonistBarOnGUI; weapons keep their vanilla position.");
        }
    }

    [HarmonyPatchCategory("CompactColonistBar")]
    [HarmonyPatch(typeof(PortraitsCache), nameof(PortraitsCache.Get))]
    public static class Patch_PortraitsCache_Get
    {
        public static void Prefix(Pawn pawn, ref Vector2 size, ref Vector3 cameraOffset, ref float cameraZoom) =>
            CompactColonistBar.AdjustPortraitCamera(pawn, ref size, ref cameraOffset, ref cameraZoom);
    }

    [HarmonyPatchCategory("CompactColonistBar")]
    [HarmonyPatch(typeof(ColonistBarDrawLocsFinder), nameof(ColonistBarDrawLocsFinder.CalculateDrawLocs),
        new[] { typeof(List<Vector2>), typeof(float), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
    public static class Patch_ColonistBarDrawLocsFinder_CalculateDrawLocs
    {
        public static void Postfix(List<Vector2> outDrawLocs, ref float scale) => CompactColonistBar.Notify_DrawLocsCalculated(outDrawLocs, scale);
    }

    [HarmonyPatchCategory("CompactColonistBar")]
    [HarmonyPatch]
    public static class Patch_ColonistBar_HitTesting
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ColonistBar), nameof(ColonistBar.TryGetEntryAt));
            yield return AccessTools.Method(typeof(ColonistBar), nameof(ColonistBar.ColonistsOrCorpsesInScreenRect));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            CompactColonistBar.ReplaceEntryRect(instructions, original);
    }

    [HarmonyPatchCategory("CompactColonistBar")]
    [HarmonyPatch(typeof(ColonistBarColonistDrawer), "GroupFrameRect")]
    public static class Patch_ColonistBarColonistDrawer_GroupFrameRect
    {
        // Vanilla adds ColonistBar.Size.y to every entry; rows can have different heights here.
        public static void Postfix(int group, ref Rect __result) =>
            __result.yMax = CompactColonistBar.GroupBottom(group) + 12f * Find.ColonistBar.Scale;
    }

    [HarmonyPatchCategory("CompactColonistBar")]
    [HarmonyPatch(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.DrawColonist))]
    public static class Patch_ColonistBarColonistDrawer_DrawColonist
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            Type type = typeof(CompactColonistBar);
            // The portrait is the only Rect + Texture draw right after PortraitsCache.Get.
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo portraitsGet = AccessTools.Method(typeof(PortraitsCache), nameof(PortraitsCache.Get));
            MethodInfo drawTexture = AccessTools.Method(typeof(GUI), nameof(GUI.DrawTexture), new[] { typeof(Rect), typeof(Texture) });
            int get = codes.FindIndex(c => c.Calls(portraitsGet));
            if (get < 0 || get + 1 >= codes.Count || !codes[get + 1].Calls(drawTexture))
                throw new Exception("Portrait draw not found in ColonistBarColonistDrawer.DrawColonist");
            codes[get + 1].operand = AccessTools.Method(type, nameof(CompactColonistBar.DrawPortrait));
            codes.Insert(get + 1, new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_1));

            MethodInfo drawLabel = AccessTools.Method(typeof(GenMapUI), nameof(GenMapUI.DrawPawnLabel), new[]
            {
                typeof(Pawn), typeof(Vector2), typeof(float), typeof(float), typeof(Dictionary<string, string>),
                typeof(GameFont), typeof(bool), typeof(bool)
            });
            codes = CompactColonistBar.ReplaceCall(codes, drawLabel, AccessTools.Method(type, nameof(CompactColonistBar.DrawLabel)));
            codes = CompactColonistBar.ReplaceCall(codes, AccessTools.Method(typeof(ColonistBarColonistDrawer), "DrawIcons"),
                AccessTools.Method(type, nameof(CompactColonistBar.DrawIcons)));
            return codes;
        }
    }
}
