using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace VanillaTuneUp
{
    /// <summary>
    /// Background music on/off button in the bottom-right play settings row (map and world view), plus the
    /// VTU_ToggleMusic key binding (unbound by default). Muting zeroes MusicManagerPlay's volume instead of writing
    /// Prefs.VolumeMusic, so the options slider and the prefs file are left alone (loading a save with music volume 0
    /// and raising it later has been seen to overlap two songs on 1.7). The flag is not saved: music is back on after
    /// a restart. Songs keep running silently, so turning music back on resumes mid-song. The mouse wheel over the
    /// icon does change Prefs.VolumeMusic (see HandleScroll), the same value as the options slider.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MusicToggle
    {
        public static bool MusicOn = true;

        // Vanilla's song preview note (UI/Buttons/PreviewSound_NotPlaying) darkened to the grey of the other
        // play settings icons; the original is pure white and stands out in that row.
        private static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("UI/VanillaTuneUp/MusicToggle");
        private static KeyBindingDef keyDef;
        private static bool prefsDirty;

        private const int TipId = 0x56545530; // "VTU0"
        private static readonly System.Func<string> TipGetter = BuildTip;

        private static string BuildTip()
        {
            string tip = "VTU_MusicToggleTip".Translate();
            if (MusicOn)
                tip += "\n\n" + "VTU_MusicToggleVolume".Translate(Prefs.VolumeMusic.ToStringPercent());
            if (keyDef != null && keyDef.MainKey != KeyCode.None)
                tip = $"{"HotKeyTip".Translate()}: {keyDef.MainKeyLabel}\n\n{tip}";
            return tip;
        }

        private const float IconSize = 24f;
        private const float VolumeStep = 0.05f;
        private static readonly AccessTools.FieldRef<WidgetRow, UIDirection> GrowDirection =
            AccessTools.FieldRefAccess<WidgetRow, UIDirection>("growDirection");

        public static void DoButton(WidgetRow row)
        {
            // Music is silent anyway at 0, so the button (and its key) would do nothing; a check mark would only
            // suggest music is playing. Any mute state is kept and shows again once the slider is raised.
            if (Prefs.VolumeMusic <= 0f)
                return;
            keyDef ??= DefDatabase<KeyBindingDef>.GetNamedSilentFail("VTU_ToggleMusic");
            row.ToggleableIcon(ref MusicOn, Icon, null, SoundDefOf.Mouseover_ButtonToggle);
            Rect rect = IconRect(row);
            // A plain string tip is keyed by its text, so every volume step would close and reopen it; a fixed id
            // with a text getter keeps the same tip open and just refreshes the number.
            TooltipHandler.TipRegion(rect, new TipSignal(TipGetter, TipId));
            HandleScroll(rect);

            if (keyDef != null && keyDef.KeyDownEvent)
            {
                MusicOn = !MusicOn;
                (MusicOn ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
                Event.current.Use();
            }
        }

        /// <summary>
        /// Rect of the icon ToggleableIcon just drew, rebuilt from where the row ended up. The play settings row
        /// grows to the left; a wrap to a new line happens before the icon is placed, so FinalY is its line.
        /// </summary>
        private static Rect IconRect(WidgetRow row)
        {
            bool growsLeft = GrowDirection(row) is UIDirection.LeftThenUp or UIDirection.LeftThenDown;
            float x = growsLeft ? row.FinalX + row.CellGap : row.FinalX - IconSize - row.CellGap;
            return new Rect(x, row.FinalY, IconSize, IconSize);
        }

        /// <summary>
        /// Mouse wheel over the icon changes the options' music volume in 5% steps. It stops at 5% because the
        /// button hides at 0, which would leave nothing to scroll back up on. The event is used up so the camera
        /// does not zoom (UIRoot runs before the camera drivers in Root_Play.OnGUI). Prefs are written once the
        /// mouse leaves the icon instead of on every step.
        /// </summary>
        private static void HandleScroll(Rect rect)
        {
            bool over = Mouse.IsOver(rect);
            // While muted the wheel is left to the camera: a volume change could not be heard, and would only
            // surprise the player on the next unmute.
            if (MusicOn && over && Event.current.type == EventType.ScrollWheel)
            {
                float delta = Event.current.delta.y;
                if (delta != 0f)
                {
                    float steps = Mathf.Round(Prefs.VolumeMusic / VolumeStep) - Mathf.Sign(delta);
                    Prefs.VolumeMusic = Mathf.Clamp(steps * VolumeStep, VolumeStep, 1f);
                    prefsDirty = true;
                }
                Event.current.Use();
            }
            else if (prefsDirty && !over && Event.current.type == EventType.Repaint)
            {
                prefsDirty = false;
                Prefs.Save();
            }
        }
    }

    [HarmonyPatchCategory("MusicToggle")]
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class Patch_PlaySettings_MusicToggle
    {
        public static void Postfix(WidgetRow row) => MusicToggle.DoButton(row);
    }

    [HarmonyPatchCategory("MusicToggle")]
    [HarmonyPatch(typeof(MusicManagerPlay), "CurVolume", MethodType.Getter)]
    public static class Patch_MusicManagerPlay_CurVolume
    {
        public static void Postfix(ref float __result)
        {
            if (!MusicToggle.MusicOn)
                __result = 0f;
        }
    }
}
