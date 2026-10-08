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
    /// closer together too, so the whole bar takes less screen space. Collapsed entries keep the portrait where it
    /// was and cut it off at the shortened edges, so the head stays visible. Names, status icons and weapon icons
    /// are hidden while collapsed unless turned back on in the settings.
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

        private static float openness;
        private static float lastHoverTime = float.NegativeInfinity;

        // Vanilla draw locations of the last recache; the bar's own list holds them moved for the current openness.
        private static readonly List<Vector2> baseDrawLocs = new List<Vector2>();
        private static List<Vector2> barDrawLocs;

        private static readonly Action<ColonistBarColonistDrawer, Rect, Pawn> DrawIconsOriginal =
            AccessTools.MethodDelegate<Action<ColonistBarColonistDrawer, Rect, Pawn>>(
                AccessTools.Method(typeof(ColonistBarColonistDrawer), "DrawIcons"));

        private static bool FullyOpen => openness >= 1f;

        /// <summary>Entry size used for drawing and hit testing in place of ColonistBar.Size.</summary>
        public static Vector2 EntrySize(ColonistBar bar)
        {
            Vector2 size = ColonistBar.BaseSize * bar.Scale;
            size.y *= Mathf.Lerp(CollapsedHeightFactor, 1f, openness);
            return size;
        }

        /// <summary>Gap above each entry in unscaled units; the portrait may extend into it.</summary>
        private static float CurrentRowGap
        {
            get
            {
                TuneUpSettings settings = VanillaTuneUpMod.Settings;
                bool weapons = Prefs.ShowWeaponsUnderPortraitMode != ShowWeaponsUnderPortraitMode.Never;
                float weaponGap = UseCustomPortraits ? WeaponRowGap : RowGap;
                float expandedGap = weapons ? weaponGap : RowGap;
                float collapsedGap = weapons && settings.compactBarShowWeapons ? weaponGap
                    : settings.compactBarShowLabels ? RowGap : CollapsedRowGap;
                return Mathf.Lerp(collapsedGap, expandedGap, openness);
            }
        }

        /// <summary>Remembers freshly calculated vanilla draw locations and moves them for the current openness.</summary>
        public static void Notify_DrawLocsCalculated(List<Vector2> drawLocs)
        {
            barDrawLocs = drawLocs;
            baseDrawLocs.Clear();
            baseDrawLocs.AddRange(drawLocs);
            ApplyRowSpacing();
        }

        private static void ApplyRowSpacing()
        {
            if (barDrawLocs == null || barDrawLocs.Count != baseDrawLocs.Count)
                return;
            float rowStep = ColonistBar.BaseSize.y + RowGap;
            float ratio = (ColonistBar.BaseSize.y * Mathf.Lerp(CollapsedHeightFactor, 1f, openness) + CurrentRowGap) / rowStep;
            for (int i = 0; i < baseDrawLocs.Count; i++)
            {
                Vector2 loc = baseDrawLocs[i];
                barDrawLocs[i] = new Vector2(loc.x, MarginTop + (loc.y - MarginTop) * ratio);
            }
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

        /// <summary>Updates the open/closed state once per frame from the mouse position.</summary>
        public static void Update(ColonistBar bar)
        {
            List<ColonistBar.Entry> entries = bar.Entries;
            List<Vector2> drawLocs = bar.DrawLocs;
            float now = Time.realtimeSinceStartup;
            if (entries.Count > 0 && drawLocs.Count == entries.Count && HoverArea(bar, drawLocs).Contains(UI.MousePositionOnUIInverted))
                lastHoverTime = now;
            float target = now - lastHoverTime <= CollapseDelaySeconds ? 1f : 0f;
            openness = Mathf.MoveTowards(openness, target, Time.unscaledDeltaTime / AnimationSeconds);
            // Rewritten every frame: cheap, and also picks up settings changes that alter the collapsed gap.
            ApplyRowSpacing();
        }

        private static Rect HoverArea(ColonistBar bar, List<Vector2> drawLocs)
        {
            Vector2 size = EntrySize(bar);
            float xMin = float.MaxValue, xMax = float.MinValue, yMax = 0f;
            for (int i = 0; i < drawLocs.Count; i++)
            {
                xMin = Mathf.Min(xMin, drawLocs[i].x);
                xMax = Mathf.Max(xMax, drawLocs[i].x + size.x);
                yMax = Mathf.Max(yMax, drawLocs[i].y + size.y);
            }
            return Rect.MinMaxRect(xMin, 0f, xMax, yMax).ExpandedBy(HoverMargin * bar.Scale);
        }

        /// <summary>
        /// Draws the portrait at its full-height position, cut off at the bottom of a shortened entry and at the
        /// top of the narrowed row gap, so it does not cover the row above.
        /// </summary>
        public static void DrawPortrait(Rect textureRect, Texture texture, Rect entryRect)
        {
            float bottom = Mathf.Min(textureRect.yMax, entryRect.yMax - 1f);
            float top = Mathf.Max(textureRect.y, entryRect.y - (CurrentRowGap - 2f) * Find.ColonistBar.Scale);
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
            if (FullyOpen || VanillaTuneUpMod.Settings.compactBarShowLabels)
                GenMapUI.DrawPawnLabel(pawn, pos, alpha, truncateToWidth, truncatedLabelsCache, font, alwaysDrawBg, alignCenter);
        }

        public static void DrawIcons(ColonistBarColonistDrawer drawer, Rect rect, Pawn colonist)
        {
            if (FullyOpen || VanillaTuneUpMod.Settings.compactBarShowIcons)
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
            if (FullyOpen || VanillaTuneUpMod.Settings.compactBarShowWeapons)
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

        /// <summary>Replaces every ColonistBar.Size read in the method with EntrySize.</summary>
        public static IEnumerable<CodeInstruction> ReplaceSize(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(ColonistBar), nameof(ColonistBar.Size));
            MethodInfo replacement = AccessTools.Method(typeof(CompactColonistBar), nameof(EntrySize));
            int count = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(getter))
                {
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, replacement).WithLabels(code.labels).WithBlocks(code.blocks);
                    count++;
                }
                else
                    yield return code;
            }
            if (count == 0)
                throw new Exception($"ColonistBar.Size not found in {original.DeclaringType.Name}.{original.Name}");
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
            foreach (CodeInstruction code in CompactColonistBar.ReplaceSize(instructions, original))
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
        public static void Postfix(List<Vector2> outDrawLocs) => CompactColonistBar.Notify_DrawLocsCalculated(outDrawLocs);
    }

    [HarmonyPatchCategory("CompactColonistBar")]
    [HarmonyPatch]
    public static class Patch_ColonistBar_HitTesting
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ColonistBar), nameof(ColonistBar.TryGetEntryAt));
            yield return AccessTools.Method(typeof(ColonistBar), nameof(ColonistBar.ColonistsOrCorpsesInScreenRect));
            yield return AccessTools.Method(typeof(ColonistBarColonistDrawer), "GroupFrameRect");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            CompactColonistBar.ReplaceSize(instructions, original);
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
