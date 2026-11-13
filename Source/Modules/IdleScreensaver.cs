using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace VanillaTuneUp
{
    /// <summary>
    /// Screensaver: after a stretch without key, mouse button or scroll wheel input (mouse movement does not count) while the game is running (not paused), the UI is
    /// hidden as in screenshot mode (F11) and the camera follows a random awake colonist on the current map, switching
    /// to another one every few seconds. Any such input, or the game becoming paused (a threat letter pauses it), ends it:
    /// the UI, camera position and selection go back to how they were. The input that ends it is swallowed so the
    /// waking click or key does nothing else.
    /// </summary>
    public static class IdleScreensaver
    {
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPos =
            AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        private static readonly AccessTools.FieldRef<CameraDriver, float> RootSizeRef =
            AccessTools.FieldRefAccess<CameraDriver, float>("rootSize");

        private static readonly AccessTools.FieldRef<CameraDriver, float> DesiredSize =
            AccessTools.FieldRefAccess<CameraDriver, float>("desiredSize");

        // Each colonist gets a random zoom from the closest zoom allowed (vanilla 11; camera mods such as SimpleCameraSetting
        // or Camera+ lower it) up to this, close enough to see what they are doing (vanilla default is 24).
        private const float ZoomFar = 16f;
        // Same follow speed as CameraFollow.
        private const float Tightness = 10f;

        private static float lastInputTime;

        private static bool active;
        private static Map map;
        private static Vector3 savedCameraPos;
        private static float savedCameraSize;
        private static bool savedScreenshotMode;
        private static readonly List<object> savedSelection = new List<object>();
        private static Pawn followed;
        private static float followedSince;
        // The followed pawn came from the next/previous colonist keys, not from a random pick.
        private static bool pickedByPlayer;

        // After waking, input events are swallowed until every key and button is released.
        private static bool swallowInput;
        private static int releasedFrame = -1;

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.Label("VTU_IdleScreensaverDelay".Translate(settings.idleScreensaverMinutes));
            settings.idleScreensaverMinutes = Mathf.RoundToInt(listing.Slider(settings.idleScreensaverMinutes, 1f, 30f));
            listing.Label("VTU_IdleScreensaverSwitch".Translate(settings.idleScreensaverSwitchSeconds));
            settings.idleScreensaverSwitchSeconds =
                Mathf.RoundToInt(listing.Slider(settings.idleScreensaverSwitchSeconds, 10f, 120f) / 5f) * 5;
        }

        /// <summary>
        /// A key press, mouse click or scroll in the focused game window. Mouse movement alone is not input, so moving
        /// across the window to work in another program keeps it idle; keys held with Alt or the Windows key are
        /// system shortcuts for switching windows (Alt+Tab, Win+D) and do not count either.
        /// </summary>
        private static bool AnyInput()
        {
            if (!Application.isFocused)
                return false;
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2))
                return true;
            // While running, zooming and the next/previous colonist keys work as usual and keep it running.
            if (!active && Input.mouseScrollDelta != Vector2.zero)
                return true;
            if (!Input.anyKey)
                return false;
            if (active && (KeyBindingDefOf.NextColonist.IsDown || KeyBindingDefOf.PreviousColonist.IsDown))
                return false;
            return !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)
                || Input.GetKey(KeyCode.LeftWindows) || Input.GetKey(KeyCode.RightWindows));
        }

        /// <summary>Input events that pass through while running: vanilla zoom and next/previous colonist.</summary>
        private static bool PassesThrough(Event e)
        {
            if (!active)
                return false;
            if (e.type == EventType.ScrollWheel)
                return true;
            return e.type == EventType.KeyDown
                && (KeyBindingDefOf.NextColonist.KeyDownEvent || KeyBindingDefOf.PreviousColonist.KeyDownEvent);
        }

        private static void FrameUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (AnyInput())
            {
                lastInputTime = now;
                if (active)
                {
                    Exit(true);
                    swallowInput = true;
                    releasedFrame = -1;
                }
            }
            UpdateSwallow();

            if (Current.ProgramState != ProgramState.Playing || LongEventHandler.ShouldWaitForEvent)
            {
                if (active)
                    Exit(false);
                // Idle time only counts on the map screen: loading a save starts the count from zero.
                lastInputTime = now;
                return;
            }

            if (active)
            {
                if (Find.CurrentMap != map || WorldRendererUtility.WorldSelected || Find.TickManager.Paused)
                {
                    Exit(Find.CurrentMap == map);
                    return;
                }
                UpdateFollowed(now);
                return;
            }

            if (now - lastInputTime < VanillaTuneUpMod.Settings.idleScreensaverMinutes * 60f)
                return;
            if (Find.CurrentMap == null || WorldRendererUtility.WorldSelected || Find.TickManager.Paused)
                return;
            Pawn first = PickPawn(Find.CurrentMap, null);
            if (first != null)
                Enter(first, now);
        }

        private static void UpdateSwallow()
        {
            if (!swallowInput || Input.anyKey)
            {
                releasedFrame = -1;
                return;
            }
            // Keep swallowing for one more frame so the release events of this frame are eaten too.
            if (releasedFrame < 0)
                releasedFrame = Time.frameCount;
            else if (Time.frameCount > releasedFrame)
                swallowInput = false;
        }

        private static bool Watchable(Pawn pawn) =>
            pawn != null && pawn.Spawned && pawn.Map == map && !pawn.Downed && pawn.Awake();

        /// <summary>A random awake free colonist other than <paramref name="except"/>; any colonist if none is awake.</summary>
        private static Pawn PickPawn(Map onMap, Pawn except)
        {
            List<Pawn> colonists = onMap.mapPawns.FreeColonistsSpawned;
            List<Pawn> awake = colonists.Where(p => p != except && !p.Downed && p.Awake()).ToList();
            if (awake.Count > 0)
                return awake.RandomElement();
            if (except != null && colonists.Contains(except))
                return except;
            return colonists.Where(p => p != except).RandomElementWithFallback();
        }

        private static void Enter(Pawn first, float now)
        {
            map = Find.CurrentMap;
            CameraDriver driver = Find.CameraDriver;
            savedCameraPos = RootPos(driver);
            savedCameraSize = driver.RootSize;
            savedScreenshotMode = Find.UIRoot.screenshotMode.Active;
            savedSelection.Clear();
            savedSelection.AddRange(Find.Selector.SelectedObjects);
            // Selection brackets would show in the footage, and CameraFollow lets go of a pawn that is no longer selected.
            Find.Selector.ClearSelection();
            Find.UIRoot.screenshotMode.Active = true;
            active = true;
            SwitchTo(first, now);
        }

        // Sets the fields directly: the RootSize setter zooms toward the mouse, which would pull the camera off the pawn.
        private static void SetSize(CameraDriver driver, float size)
        {
            RootSizeRef(driver) = size;
            DesiredSize(driver) = size;
        }

        private static void SwitchTo(Pawn pawn, float now)
        {
            followed = pawn;
            followedSince = now;
            pickedByPlayer = false;
            if (pawn == null)
                return;
            // A colonist picked with the next/previous keys is selected, and CameraFollow follows it; let go of it.
            if (Find.Selector.NumSelected > 0)
                Find.Selector.ClearSelection();
            CameraDriver driver = Find.CameraDriver;
            driver.JumpToCurrentMapLoc(pawn.DrawPos);
            float near = driver.config.sizeRange.min;
            float far = Mathf.Clamp(ZoomFar, near, driver.config.sizeRange.max);
            SetSize(driver, Rand.Range(near, far));
        }

        private static void UpdateFollowed(float now)
        {
            bool due = now - followedSince >= VanillaTuneUpMod.Settings.idleScreensaverSwitchSeconds;
            if (!due && Watchable(followed))
                return;
            // A colonist the player picked stays on screen until the next switch, even asleep.
            if (!due && pickedByPlayer && followed != null && followed.Spawned && followed.Map == map)
                return;
            if (!due && followed != null && followed.Spawned && followed.Map == map
                && !map.mapPawns.FreeColonistsSpawned.Any(p => p != followed && !p.Downed && p.Awake()))
                return; // Fell asleep or down, but nobody else is awake to switch to: stay until the next switch.
            Pawn next = PickPawn(map, followed);
            if (next == null)
            {
                Exit(true);
                return;
            }
            SwitchTo(next, now);
        }

        /// <param name="restore">Put the camera and selection back; false when the map is gone or changed.</param>
        private static void Exit(bool restore)
        {
            if (!active)
                return;
            active = false;
            followed = null;
            if (Find.UIRoot != null)
                Find.UIRoot.screenshotMode.Active = savedScreenshotMode;
            if (restore && Find.CurrentMap == map && map != null)
            {
                Find.CameraDriver.JumpToCurrentMapLoc(savedCameraPos);
                SetSize(Find.CameraDriver, savedCameraSize);
                // Drop a colonist picked with the next/previous keys before the old selection comes back.
                Find.Selector.ClearSelection();
                foreach (object obj in savedSelection)
                {
                    if (obj is Thing thing ? thing.Spawned && thing.Map == map
                        : obj is Zone zone && map.zoneManager.AllZones.Contains(zone))
                        Find.Selector.Select(obj, playSound: false, forceDesignatorDeselect: false);
                }
            }
            savedSelection.Clear();
            map = null;
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
        public static class Patch_RootUpdate
        {
            public static void Postfix() => FrameUpdate();
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
        public static class Patch_UIRootOnGUI
        {
            public static void Prefix()
            {
                if (!active && !swallowInput)
                    return;
                Event e = Event.current;
                if ((e.isMouse || e.isKey || e.type == EventType.ScrollWheel) && !PassesThrough(e))
                    e.Use();
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJump),
            new[] { typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode) })]
        public static class Patch_TryJump
        {
            // The next/previous colonist keys jump the vanilla way; follow the pawn they landed on.
            public static void Postfix(GlobalTargetInfo target)
            {
                if (!active || !target.IsValid)
                    return;
                GlobalTargetInfo adjusted = CameraJumper.GetAdjustedTarget(target);
                if (adjusted.Thing is Pawn pawn && pawn.Spawned && pawn.Map == map)
                {
                    followed = pawn;
                    followedSince = Time.realtimeSinceStartup;
                    pickedByPlayer = true;
                }
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
        public static class Patch_CameraUpdate
        {
            public static void Prefix(CameraDriver __instance)
            {
                Pawn pawn = followed;
                if (!active || pawn == null || !pawn.Spawned || pawn.Map != Find.CurrentMap || LongEventHandler.ShouldWaitForEvent)
                    return;
                Vector3 target = pawn.DrawPos;
                ref Vector3 root = ref RootPos(__instance);
                float t = 1f - Mathf.Exp(-Tightness * Mathf.Min(Time.deltaTime, CameraDriver.MaxDeltaTime));
                root.x = Mathf.Lerp(root.x, target.x, t);
                root.z = Mathf.Lerp(root.z, target.z, t);
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
        public static class Patch_ClearAllMapsAndWorld
        {
            // Leaving the game: drop the state without touching a camera or selection that is going away.
            public static void Prefix() => Exit(false);
        }
    }
}
