using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// "Continue" button at the top of the main menu's option list that loads the newest save, autosaves included.
    /// Enter (VTU_Continue key binding) does the same. Loading goes through the same version and mod list checks as
    /// the Load game dialog. Part of the ModUpdateNotes module, which groups the main menu additions.
    /// </summary>
    public static class MainMenuContinue
    {
        // The save folder is read at most this often; the menu is drawn every frame.
        private const float RefreshSeconds = 2f;

        // The hotkey is ignored this long after the menu reappears (a dialog closed, back from a game), so an Enter
        // held or repeated from the dialog that just closed does not load a save.
        private const float HotkeyDelaySeconds = 0.5f;

        // Gap between two menu draws that counts as the menu having been hidden.
        private const float HiddenGapSeconds = 0.2f;

        private static FileInfo latest;
        private static float lastRefresh = -RefreshSeconds;
        private static KeyBindingDef keyDef;
        private static float lastDrawn = -1f;
        private static float shownSince;

        // Set while DoMainMenuControls runs until its first option list (the left column) is drawn.
        public static bool PendingInsert;

        public static FileInfo Latest
        {
            get
            {
                if (Time.realtimeSinceStartup - lastRefresh >= RefreshSeconds)
                {
                    lastRefresh = Time.realtimeSinceStartup;
                    latest = GenFilePaths.AllSavedGameFiles.FirstOrDefault();
                }
                return latest;
            }
        }

        public static void Load(FileInfo file)
        {
            lastRefresh = -RefreshSeconds;
            file.Refresh();
            if (!file.Exists)
                return;
            GameDataSaveLoader.CheckVersionAndLoadGame(Path.GetFileNameWithoutExtension(file.Name));
        }

        /// <summary>
        /// VTU_Continue key binding (Enter by default) on the start screen. Vanilla draws the menu only while no
        /// dialog is open, so the key does nothing behind the Load game dialog or a message box.
        /// </summary>
        public static void HandleHotkey()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastDrawn > HiddenGapSeconds)
                shownSince = now;
            lastDrawn = now;
            keyDef ??= DefDatabase<KeyBindingDef>.GetNamedSilentFail("VTU_Continue");
            if (keyDef == null || !keyDef.KeyDownEvent)
                return;
            Event.current.Use();
            FileInfo file = Latest;
            if (now - shownSince >= HotkeyDelaySeconds && file != null)
                Load(file);
        }
    }

    public class ListableOption_Continue : ListableOption
    {
        private readonly FileInfo file;

        public ListableOption_Continue(FileInfo file)
            : base("VTU_MM_Continue".Translate(), null)
        {
            this.file = file;
            action = () => MainMenuContinue.Load(file);
        }

        public override float DrawOption(Vector2 pos, float width)
        {
            float height = base.DrawOption(pos, width);
            TooltipHandler.TipRegion(new Rect(pos.x, pos.y, width, height), "VTU_MM_ContinueTip".Translate(
                Path.GetFileNameWithoutExtension(file.Name), file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
            return height;
        }
    }

    [HarmonyPatchCategory("ModUpdateNotes")]
    [HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
    public static class Patch_MainMenuDrawer_DoMainMenuControls
    {
        // Only the start screen; the in-game Esc menu is drawn by the same method.
        public static void Prefix()
        {
            MainMenuContinue.PendingInsert = Current.ProgramState == ProgramState.Entry;
            if (MainMenuContinue.PendingInsert)
                MainMenuContinue.HandleHotkey();
        }

        public static void Finalizer() => MainMenuContinue.PendingInsert = false;
    }

    [HarmonyPatchCategory("ModUpdateNotes")]
    [HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
    public static class Patch_OptionListingUtility_DrawOptionListing
    {
        public static void Prefix(List<ListableOption> optList)
        {
            if (!MainMenuContinue.PendingInsert)
                return;
            MainMenuContinue.PendingInsert = false;
            FileInfo file = MainMenuContinue.Latest;
            if (file != null && !optList.Any(o => o is ListableOption_Continue))
                optList.Insert(0, new ListableOption_Continue(file));
        }
    }
}
