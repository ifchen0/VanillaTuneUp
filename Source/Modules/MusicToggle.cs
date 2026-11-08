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
    /// a restart. Songs keep running silently, so turning music back on resumes mid-song.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MusicToggle
    {
        public static bool MusicOn = true;

        // Vanilla's song preview note (UI/Buttons/PreviewSound_NotPlaying) darkened to the grey of the other
        // play settings icons; the original is pure white and stands out in that row.
        private static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("UI/VanillaTuneUp/MusicToggle");
        private static KeyBindingDef keyDef;

        public static void DoButton(WidgetRow row)
        {
            // Music is silent anyway at 0, so the button (and its key) would do nothing; a check mark would only
            // suggest music is playing. Any mute state is kept and shows again once the slider is raised.
            if (Prefs.VolumeMusic <= 0f)
                return;
            keyDef ??= DefDatabase<KeyBindingDef>.GetNamedSilentFail("VTU_ToggleMusic");
            string tip = "VTU_MusicToggleTip".Translate();
            if (keyDef != null && keyDef.MainKey != KeyCode.None)
                tip = $"{"HotKeyTip".Translate()}: {keyDef.MainKeyLabel}\n\n{tip}";
            row.ToggleableIcon(ref MusicOn, Icon, tip, SoundDefOf.Mouseover_ButtonToggle);

            if (keyDef != null && keyDef.KeyDownEvent)
            {
                MusicOn = !MusicOn;
                (MusicOn ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
                Event.current.Use();
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
