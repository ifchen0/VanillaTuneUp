using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// The window close button (and Alt+F4) during a game goes through the same path as the in-game menu's quit
    /// button: the vanilla "Really quit?" confirmation when there is unsaved progress, or save and quit in
    /// commitment mode. Elsewhere (main menu, loading) the window closes as before.
    /// 1.7 (Waylith) never asks Application.wantsToQuit for a window close, so there the main window's close
    /// handler is patched; on Unity, wantsToQuit is refused instead.
    /// </summary>
    [HarmonyPatchCategory("ConfirmCloseWindow")]
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    public static class ConfirmCloseWindow
    {
        private static bool pending;
        // Set once the game itself quits (menu button, the confirmation), so that quit is let through.
        internal static bool quitting;
        private static Window dialog;

        public static void Hook()
        {
            // On 1.7 the close button is handled by Patch_MainWindowHandler_OnCloseRequested instead.
            if (!Patch_MainWindowHandler_OnCloseRequested.Prepare())
                Application.wantsToQuit += WantsToQuit;
        }

        private static bool ShouldAsk => !quitting && Current.ProgramState == ProgramState.Playing && Current.Game != null;

        private static bool WantsToQuit()
        {
            if (!ShouldAsk)
                return true;
            pending = true;
            return false;
        }

        // Returns true when the window close is taken over by the confirmation.
        internal static bool InterceptWindowClose()
        {
            if (!ShouldAsk)
                return false;
            pending = true;
            return true;
        }

        public static void Postfix()
        {
            if (!pending || LongEventHandler.AnyEventNowOrWaiting)
                return;
            pending = false;
            if (dialog != null && Find.WindowStack.IsOpen(dialog))
                return;
            // Same choices as the in-game menu (MainMenuDrawer.DoMainMenuControls).
            if (Current.Game.Info.permadeathMode)
            {
                LongEventHandler.QueueLongEvent(() =>
                {
                    GameDataSaveLoader.SaveGame(Current.Game.Info.permadeathModeUniqueName);
                    LongEventHandler.ExecuteWhenFinished(Root.Shutdown);
                }, "SavingLongEvent", doAsynchronously: false, null, showExtraUIInfo: false);
            }
            else if (GameDataSaveLoader.CurrentGameStateIsValuable)
            {
                dialog = Dialog_MessageBox.CreateConfirmation("ConfirmQuit".Translate(), Root.Shutdown, destructive: true, null, WindowLayer.Super);
                Find.WindowStack.Add(dialog);
            }
            else
            {
                Root.Shutdown();
            }
        }
    }

    [HarmonyPatchCategory("ConfirmCloseWindow")]
    [HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
    public static class Patch_Root_Shutdown
    {
        public static void Prefix() => ConfirmCloseWindow.quitting = true;
    }

    /// <summary>
    /// 1.7 (Waylith): the main window's close handler requests an engine shutdown right away, so it is skipped
    /// (and the close refused) while the confirmation takes over.
    /// </summary>
    [HarmonyPatchCategory("ConfirmCloseWindow")]
    public static class Patch_MainWindowHandler_OnCloseRequested
    {
        private static readonly MethodInfo requestFocus =
            AccessTools.Method(AccessTools.TypeByName("Waylith.Windowing.IWindow"), "RequestFocus");

        public static bool Prepare() => Type.GetType("Waylith.EngineApplication, Waylith") != null;

        public static MethodBase TargetMethod()
        {
            // Throw when Waylith is there but the handler moved, so the module is reported and turned off.
            Type handler = AccessTools.TypeByName("Waylith.Input.MainWindowHandler")
                ?? throw new MissingMemberException("Waylith.Input.MainWindowHandler");
            return AccessTools.Method(handler, "Waylith.Windowing.IWindowHandler.OnCloseRequested")
                ?? throw new MissingMethodException("Waylith.Input.MainWindowHandler", "OnCloseRequested");
        }

        public static bool Prefix(object[] __args, ref bool __result)
        {
            if (!ConfirmCloseWindow.InterceptWindowClose())
                return true;
            // Closing from the taskbar leaves the window unfocused; bring it back so the dialog is seen.
            try { requestFocus?.Invoke(__args[0], null); } catch { }
            __result = false;
            return false;
        }
    }
}
