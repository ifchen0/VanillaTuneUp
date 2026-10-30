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
    /// Quicksave hotkey (VTU_Quicksave key binding, F5 by default). Saves rotate through "Quicksave-1..N" the same
    /// way vanilla autosaves rotate: the first free slot, else the oldest file. Commitment mode saves to its single
    /// save file, like vanilla autosave does.
    /// </summary>
    [HarmonyPatchCategory("Quicksave")]
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    public static class Quicksave
    {
        // Same default and range as vanilla's autosave count.
        public const int DefaultCount = 5;
        public const int MaxCount = 25;
        private const string Prefix = "Quicksave-";

        // Key repeat while F5 is held, or a double tap, lands inside this window and is ignored.
        private const float CooldownSeconds = 1f;

        private static KeyBindingDef keyDef;
        private static float lastRealTime = -CooldownSeconds;
        private static string lastFileName;
        private static int lastTick = -1;
        private static Game lastGame;

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.Label("VTU_QuicksaveCount".Translate(settings.quicksaveCount));
            settings.quicksaveCount = Mathf.RoundToInt(listing.Slider(settings.quicksaveCount, 1f, MaxCount));
        }

        public static void Postfix()
        {
            keyDef ??= DefDatabase<KeyBindingDef>.GetNamedSilentFail("VTU_Quicksave");
            if (keyDef == null || !keyDef.KeyDownEvent || Current.ProgramState != ProgramState.Playing)
                return;
            Event.current.Use();
            if (Time.realtimeSinceStartup - lastRealTime < CooldownSeconds)
                return;
            if (LongEventHandler.AnyEventNowOrWaiting || GameDataSaveLoader.SavingIsTemporarilyDisabled)
            {
                Messages.Message("VTU_QuicksaveUnavailable".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            string fileName = Current.Game.Info.permadeathMode ? Current.Game.Info.permadeathModeUniqueName : NextFileName();
            lastRealTime = Time.realtimeSinceStartup;
            lastFileName = fileName;
            lastTick = Find.TickManager.TicksGame;
            lastGame = Current.Game;
            LongEventHandler.QueueLongEvent(() => GameDataSaveLoader.SaveGame(fileName), "SavingLongEvent", doAsynchronously: false, null);
            Messages.Message("SavedAs".Translate(fileName), MessageTypeDefOf.SilentInput, historical: false);
            PlayerKnowledgeDatabase.Save();
        }

        private static string NextFileName()
        {
            // Nothing happened since the last quicksave (e.g. pressed again while paused): overwrite that one
            // instead of rotating, so repeated presses do not push older quicksaves out.
            if (lastGame == Current.Game && lastTick == Find.TickManager.TicksGame && lastFileName.StartsWith(Prefix)
                && SaveGameFilesUtility.SavedGameNamedExists(lastFileName))
                return lastFileName;
            List<string> names = Enumerable.Range(1, VanillaTuneUpMod.Settings.quicksaveCount).Select(i => Prefix + i).ToList();
            return names.FirstOrDefault(name => !SaveGameFilesUtility.SavedGameNamedExists(name))
                ?? names.MinBy(name => new FileInfo(GenFilePaths.FilePathForSavedGame(name)).LastWriteTime);
        }
    }
}
