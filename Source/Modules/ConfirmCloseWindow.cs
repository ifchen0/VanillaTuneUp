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
    /// 1.7 (Waylith/GLFW) never asks Application.wantsToQuit for a window close, so there the GLFW close callback
    /// is used and the close flag reset; on Unity, wantsToQuit is refused instead.
    /// </summary>
    [HarmonyPatchCategory("ConfirmCloseWindow")]
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    public static class ConfirmCloseWindow
    {
        private static bool pending;
        // Set once the game itself quits (menu button, the confirmation), so that quit is let through.
        internal static bool quitting;
        private static Window dialog;

        private static PropertyInfo shouldCloseProperty;
        private static MethodInfo focusMethod;

        public static void Hook()
        {
            Type app = Type.GetType("Waylith.EngineApplication, Waylith");
            if (app == null)
            {
                Application.wantsToQuit += WantsToQuit;
                return;
            }
            object window = AccessTools.Property(app, "Window").GetValue(AccessTools.Property(app, "Instance").GetValue(null));
            Type windowType = window.GetType();
            shouldCloseProperty = AccessTools.Property(windowType, "ShouldClose");
            focusMethod = AccessTools.Method(windowType, "Focus");
            EventInfo onClose = windowType.GetEvent("OnClose");
            // The handler takes object; the MethodInfo overload allows parameter contravariance, the by-name one does not.
            onClose.AddEventHandler(window, Delegate.CreateDelegate(onClose.EventHandlerType, AccessTools.Method(typeof(ConfirmCloseWindow), nameof(OnWindowClose))));
        }

        private static bool ShouldAsk => !quitting && Current.ProgramState == ProgramState.Playing && Current.Game != null;

        private static bool WantsToQuit()
        {
            if (!ShouldAsk)
                return true;
            pending = true;
            return false;
        }

        private static void OnWindowClose(object window)
        {
            if (!ShouldAsk)
                return;
            shouldCloseProperty.SetValue(window, false);
            // Closing from the taskbar leaves the window unfocused, and an unfocused window does not run the game
            // loop, so the dialog would not show until the player clicks back in.
            focusMethod?.Invoke(window, null);
            pending = true;
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
}
