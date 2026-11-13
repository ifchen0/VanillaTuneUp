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
    /// Screensaver: after a stretch without key, mouse button or scroll wheel input (mouse movement does not count)
    /// while the game is running (not paused), the UI is hidden as in screenshot mode (F11) and the camera follows a
    /// random awake colonist on the current map, switching to another one every few seconds. Camera work, each on by
    /// default and optional: a slow zoom in or out during each shot and a fade to black between colonists. An event or
    /// threat letter that does not pause the game cuts to what it points at for one shot, and so does a corner message about a threat, a
    /// death or a health problem; one event shot holds off others for a few seconds. Any other input, or the game
    /// becoming paused (a threat letter pauses it), ends it: the UI and selection go back to how they were, and the
    /// camera stays where it is unless set to go back too. The input that ends it is swallowed so the waking click or key does nothing else.
    /// </summary>
    public static class IdleScreensaver
    {
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPos =
            AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        private static readonly AccessTools.FieldRef<CameraDriver, float> RootSizeRef =
            AccessTools.FieldRefAccess<CameraDriver, float>("rootSize");

        private static readonly AccessTools.FieldRef<CameraDriver, float> DesiredSize =
            AccessTools.FieldRefAccess<CameraDriver, float>("desiredSize");

        // Each shot gets a random zoom from the closest zoom allowed (vanilla 11; camera mods such as SimpleCameraSetting
        // or Camera+ lower it) up to this, close enough to see what the colonist is doing (vanilla default is 24).
        private const float ZoomFar = 16f;
        // Same follow speed as CameraFollow.
        private const float Tightness = 10f;
        private const float FadeSeconds = 0.5f;
        // After an event shot, later events within this many seconds are ignored.
        private const float EventShotBuffer = 5f;
        private static float lastEventShot = -EventShotBuffer;

        private static float lastInputTime;

        private static bool active;
        private static Map map;
        private static Vector3 savedCameraPos;
        private static float savedCameraSize;
        private static bool savedScreenshotMode;
        private static readonly List<object> savedSelection = new List<object>();

        // What the current shot shows: a pawn to follow, or else a letter's thing or cell.
        private static Pawn followed;
        private static LocalTargetInfo spot = LocalTargetInfo.Invalid;
        private static Vector3 lastSpotPos;
        private static float shotSince;
        // The shot came from the next/previous colonist keys or a letter: it stays until the next switch.
        private static bool held;

        // Slow zoom during the shot; stops for the shot once the player zooms.
        private static bool drifting;
        private static float zoomFrom;
        private static float zoomTo;
        private static float lastSetSize;

        // Fade to black before the next random colonist.
        private static bool fadePending;
        private static float fadeAt;
        // Our own black overlay, alpha moving from overlayFrom to overlayTo between the two times. Vanilla's ScreenFader
        // is not used: while it fades, vanilla takes control away from the player, and that also blocks the automatic
        // pause a threat letter asks for.
        private static float overlayFrom;
        private static float overlayTo;
        private static float overlayStart;
        private static float overlayEnd;
        // Fading out before the screensaver starts; the interface is still up.
        private static bool entering;
        private static Map enteringMap;

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
            listing.CheckboxLabeled("VTU_IdleScreensaverDrift".Translate(), ref settings.idleScreensaverZoomDrift);
            listing.CheckboxLabeled("VTU_IdleScreensaverFade".Translate(), ref settings.idleScreensaverFade);
            listing.CheckboxLabeled("VTU_IdleScreensaverStay".Translate(), ref settings.idleScreensaverStayOnExit);
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
                if (active || entering)
                {
                    AbortEntering();
                    Exit(true);
                    swallowInput = true;
                    releasedFrame = -1;
                }
            }
            UpdateSwallow();

            if (Current.ProgramState != ProgramState.Playing || LongEventHandler.ShouldWaitForEvent)
            {
                AbortEntering();
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
                UpdateShot(now);
                return;
            }

            if (entering)
            {
                if (Find.CurrentMap != enteringMap || WorldRendererUtility.WorldSelected || Find.TickManager.Paused)
                {
                    AbortEntering();
                    return;
                }
                if (now < fadeAt)
                    return;
                entering = false;
                Pawn faded = PickPawn(enteringMap, null);
                if (faded == null)
                {
                    CancelFade();
                    return;
                }
                Enter(faded, afterFade: true);
                FadeIn(now);
                return;
            }

            if (now - lastInputTime < VanillaTuneUpMod.Settings.idleScreensaverMinutes * 60f)
                return;
            if (Find.CurrentMap == null || WorldRendererUtility.WorldSelected || Find.TickManager.Paused)
                return;
            Pawn first = PickPawn(Find.CurrentMap, null);
            if (first == null)
                return;
            if (VanillaTuneUpMod.Settings.idleScreensaverFade)
            {
                // Fade out with the interface still up; the screensaver starts once the screen is black.
                FadeOut(now);
                entering = true;
                enteringMap = Find.CurrentMap;
                return;
            }
            Enter(first);
        }

        /// <summary>Stops a fade-out that was leading into the screensaver; the screen clears at once.</summary>
        private static void AbortEntering()
        {
            if (!entering)
                return;
            CancelFade();
            entering = false;
            enteringMap = null;
            // Whatever stopped it (a pause, a map change), the idle time starts over.
            lastInputTime = Time.realtimeSinceStartup;
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

        private static bool OnMap(Pawn pawn) => pawn != null && pawn.Spawned && pawn.Map == map;

        private static bool Watchable(Pawn pawn) => OnMap(pawn) && !pawn.Downed && pawn.Awake();

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

        private static void Enter(Pawn first, bool afterFade = false)
        {
            enteringMap = null;
            map = Find.CurrentMap;
            CameraDriver driver = Find.CameraDriver;
            savedCameraPos = RootPos(driver);
            savedCameraSize = driver.RootSize;
            savedScreenshotMode = Find.UIRoot.screenshotMode.Active;
            savedSelection.Clear();
            savedSelection.AddRange(Find.Selector.SelectedObjects);
            Find.UIRoot.screenshotMode.Active = true;
            active = true;
            StartShot(first, false, afterFade);
        }

        // Sets the fields directly: the RootSize setter zooms toward the mouse, which would pull the camera off the pawn.
        private static void SetSize(CameraDriver driver, float size)
        {
            RootSizeRef(driver) = size;
            DesiredSize(driver) = size;
            lastSetSize = size;
        }

        /// <summary>Cuts to <paramref name="target"/> (a pawn is followed) with a new zoom.</summary>
        private static void StartShot(LocalTargetInfo target, bool hold, bool afterFade = false)
        {
            // A cut in the middle of a fade (letter) clears the screen; the end of our own fade-out keeps it black.
            if (!afterFade)
                CancelFade();
            followed = target.Thing as Pawn;
            spot = followed == null ? target : LocalTargetInfo.Invalid;
            lastSpotPos = target.CenterVector3;
            shotSince = Time.realtimeSinceStartup;
            held = hold;
            // Selection brackets would show in the footage, and CameraFollow follows a selected pawn it jumped to.
            if (Find.Selector.NumSelected > 0)
                Find.Selector.ClearSelection();
            CameraDriver driver = Find.CameraDriver;
            driver.JumpToCurrentMapLoc(target.CenterVector3);
            float near = driver.config.sizeRange.min;
            float far = Mathf.Clamp(ZoomFar, near, driver.config.sizeRange.max);
            float mid = (near + far) / 2f;
            zoomFrom = Rand.Range(near, far);
            // Drift toward the other half of the range so every shot moves a noticeable amount, in or out.
            zoomTo = zoomFrom < mid ? Rand.Range(mid, far) : Rand.Range(near, mid);
            drifting = VanillaTuneUpMod.Settings.idleScreensaverZoomDrift;
            SetSize(driver, zoomFrom);
        }

        private static void UpdateShot(float now)
        {
            if (fadePending)
            {
                if (now < fadeAt)
                    return;
                fadePending = false;
                Pawn faded = PickPawn(map, followed);
                if (faded == null)
                {
                    Exit(true);
                    return;
                }
                StartShot(faded, false, afterFade: true);
                FadeIn(now);
                return;
            }

            bool due = now - shotSince >= VanillaTuneUpMod.Settings.idleScreensaverSwitchSeconds;
            if (!due)
            {
                // A picked colonist or a letter's target stays on screen until the next switch, even asleep.
                if (held && (followed == null || OnMap(followed)))
                    return;
                if (Watchable(followed))
                    return;
                if (OnMap(followed) && !map.mapPawns.FreeColonistsSpawned.Any(p => p != followed && !p.Downed && p.Awake()))
                    return; // Fell asleep or down, but nobody else is awake to switch to: stay until the next switch.
            }
            Pawn next = PickPawn(map, followed);
            if (next == null)
            {
                Exit(true);
                return;
            }
            if (VanillaTuneUpMod.Settings.idleScreensaverFade)
            {
                // The colonist is picked once the screen is black, so the pick is fresh.
                FadeOut(now);
                fadePending = true;
                return;
            }
            StartShot(next, false);
        }

        private static float OverlayAlpha(float now) => overlayEnd <= overlayStart
            ? overlayTo
            : Mathf.Lerp(overlayFrom, overlayTo, Mathf.InverseLerp(overlayStart, overlayEnd, now));

        private static void FadeOverlay(float now, float to)
        {
            overlayFrom = OverlayAlpha(now);
            overlayTo = to;
            overlayStart = now;
            overlayEnd = now + FadeSeconds;
        }

        private static void FadeOut(float now)
        {
            FadeOverlay(now, 1f);
            fadeAt = now + FadeSeconds;
        }

        private static void FadeIn(float now) => FadeOverlay(now, 0f);

        /// <summary>Drops a pending fade switch and clears the overlay right away.</summary>
        private static void CancelFade()
        {
            fadePending = false;
            overlayFrom = overlayTo = 0f;
            overlayStart = overlayEnd = 0f;
        }

        /// <param name="restore">Put the camera and selection back; false when the map is gone or changed.</param>
        private static void Exit(bool restore)
        {
            if (!active)
                return;
            active = false;
            // Whatever ended it (a pause, a jump to another map), the idle time starts over.
            lastInputTime = Time.realtimeSinceStartup;
            followed = null;
            spot = LocalTargetInfo.Invalid;
            drifting = false;
            CancelFade();
            if (Find.UIRoot != null)
                Find.UIRoot.screenshotMode.Active = savedScreenshotMode;
            if (restore && Find.CurrentMap == map && map != null)
            {
                if (!VanillaTuneUpMod.Settings.idleScreensaverStayOnExit)
                {
                    Find.CameraDriver.JumpToCurrentMapLoc(savedCameraPos);
                    SetSize(Find.CameraDriver, savedCameraSize);
                }
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

        /// <summary>
        /// Cuts to the first of <paramref name="lookTargets"/> on the screensaver's map for one round. A letter and a
        /// message about the same event often arrive together, so the first event shot shuts out others for a while.
        /// </summary>
        private static void EventShot(LookTargets lookTargets)
        {
            if (!active || Find.TickManager.Paused)
                return;
            float now = Time.realtimeSinceStartup;
            if (now - lastEventShot < EventShotBuffer)
                return;
            LocalTargetInfo target = EventTarget(lookTargets);
            if (!target.IsValid)
                return;
            lastEventShot = now;
            StartShot(target, true);
        }

        /// <summary>The first target on the screensaver's map, or Invalid.</summary>
        private static LocalTargetInfo EventTarget(LookTargets lookTargets)
        {
            List<GlobalTargetInfo> targets = lookTargets?.targets;
            if (targets == null)
                return LocalTargetInfo.Invalid;
            foreach (GlobalTargetInfo target in targets)
            {
                if (!target.IsValid || target.Map != map)
                    continue;
                if (target.HasThing)
                {
                    if (target.Thing.SpawnedOrAnyParentSpawned)
                        return CameraJumper.GetAdjustedTarget(target).Thing is Thing adjusted && adjusted.Spawned
                            ? new LocalTargetInfo(adjusted)
                            : new LocalTargetInfo(target.Thing.PositionHeld);
                }
                else if (target.Cell.IsValid)
                    return target.Cell;
            }
            return LocalTargetInfo.Invalid;
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
            // Drawn after the whole interface, so the fade covers it too while the screensaver is starting.
            public static void Postfix()
            {
                if (Event.current.type != EventType.Repaint)
                    return;
                float alpha = OverlayAlpha(Time.realtimeSinceStartup);
                if (alpha <= 0f)
                    return;
                GUI.color = new Color(0f, 0f, 0f, alpha);
                GUI.DrawTexture(new Rect(0f, 0f, UI.screenWidth, UI.screenHeight), BaseContent.WhiteTex);
                GUI.color = Color.white;
            }

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
        [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
            new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
        public static class Patch_ReceiveLetter
        {
            // An event or threat letter that leaves the game running gets one shot of what it points at on this map.
            // A letter that pauses the game ends the screensaver on the next frame instead.
            public static void Postfix(LetterStack __instance, Letter let)
            {
                if (!active || !__instance.LettersListForReading.Contains(let))
                    return;
                if (let.def == LetterDefOf.NegativeEvent || let.def == LetterDefOf.ThreatSmall || let.def == LetterDefOf.ThreatBig
                    || let.def == LetterDefOf.NeutralEvent || let.def == LetterDefOf.PositiveEvent)
                    EventShot(let.lookTargets);
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(Messages), nameof(Messages.Message), new[] { typeof(Message), typeof(bool) })]
        public static class Patch_Message
        {
            private static readonly System.Reflection.FieldInfo LiveMessagesField =
                AccessTools.Field(typeof(Messages), "liveMessages");

            private static List<Message> LiveMessages() => (List<Message>)LiveMessagesField.GetValue(null);

            // Corner messages about threats, deaths and health (a social fight is a small threat) get a shot too;
            // the many everyday ones (events, finished work, input feedback) do not.
            public static void Postfix(Message msg)
            {
                // Vanilla drops duplicates and messages it does not accept; only a message on screen counts.
                if (!active || !LiveMessages().Contains(msg))
                    return;
                MessageTypeDef def = msg.def;
                if (def == MessageTypeDefOf.ThreatBig || def == MessageTypeDefOf.ThreatSmall
                    || def == MessageTypeDefOf.PawnDeath || def == MessageTypeDefOf.NegativeHealthEvent)
                    EventShot(msg.lookTargets);
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJump),
            new[] { typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode) })]
        public static class Patch_TryJump
        {
            // The next/previous colonist keys jump the vanilla way; follow the pawn they landed on at the player's zoom.
            public static void Postfix(GlobalTargetInfo target)
            {
                if (!active || !target.IsValid)
                    return;
                GlobalTargetInfo adjusted = CameraJumper.GetAdjustedTarget(target);
                if (adjusted.Thing is Pawn pawn && OnMap(pawn))
                {
                    CancelFade();
                    followed = pawn;
                    spot = LocalTargetInfo.Invalid;
                    shotSince = Time.realtimeSinceStartup;
                    held = true;
                    drifting = false;
                }
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
        public static class Patch_CameraUpdate
        {
            public static void Prefix(CameraDriver __instance)
            {
                if (!active || Find.CurrentMap != map || LongEventHandler.ShouldWaitForEvent)
                    return;
                Vector3 target;
                if (OnMap(followed))
                    target = followed.DrawPos;
                else if (spot.IsValid)
                {
                    // A letter's thing that is gone leaves the camera where it last was.
                    if (!spot.HasThing || spot.Thing.SpawnedOrAnyParentSpawned)
                        lastSpotPos = spot.CenterVector3;
                    target = lastSpotPos;
                }
                else
                    return;
                float dt = Mathf.Min(Time.deltaTime, CameraDriver.MaxDeltaTime);
                ref Vector3 root = ref RootPos(__instance);
                float t = 1f - Mathf.Exp(-Tightness * dt);
                root.x = Mathf.Lerp(root.x, target.x, t);
                root.z = Mathf.Lerp(root.z, target.z, t);

                if (!drifting)
                    return;
                // The scroll wheel changed the zoom: keep the player's zoom for the rest of the shot.
                if (Mathf.Abs(DesiredSize(__instance) - lastSetSize) > 0.01f)
                {
                    drifting = false;
                    return;
                }
                float progress = Mathf.Clamp01((Time.realtimeSinceStartup - shotSince)
                    / VanillaTuneUpMod.Settings.idleScreensaverSwitchSeconds);
                SetSize(__instance, Mathf.Lerp(zoomFrom, zoomTo, Mathf.SmoothStep(0f, 1f, progress)));
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(CameraDriver), "CalculateCurInputDollyVect")]
        public static class Patch_InputDolly
        {
            // Mouse movement does not end the screensaver, so a cursor left at the screen edge would scroll the
            // camera against the follow. Keys and dragging end it before they matter.
            public static void Postfix(ref Vector2 __result)
            {
                if (active)
                    __result = Vector2.zero;
            }
        }

        [HarmonyPatchCategory("IdleScreensaver")]
        [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
        public static class Patch_ClearAllMapsAndWorld
        {
            // Leaving the game: drop the state without touching a camera or selection that is going away.
            public static void Prefix()
            {
                AbortEntering();
                Exit(false);
            }
        }
    }
}
