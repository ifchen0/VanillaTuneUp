using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace VanillaTuneUp
{
    /// <summary>
    /// A camera jump to a pawn on the current map (next/previous colonist keys, double-clicking a colonist bar
    /// portrait, alerts, letters, messages, the quest window) keeps the camera on that pawn while it moves. Following
    /// stops as soon as the camera is moved by hand (keys, middle-mouse drag, screen edge), the pawn is no longer the
    /// only thing selected, or it leaves the map. Jumps that do not end with the pawn selected (scripted quest jumps)
    /// therefore stop on the next frame. Zooming keeps following. With InspectTabCamera on, an open inspect tab shifts
    /// the followed pawn into the uncovered part of the screen, like that module does for the jump itself.
    /// </summary>
    public static class CameraFollow
    {
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPos =
            AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        private static readonly AccessTools.FieldRef<CameraDriver, Vector2> DesiredDollyRaw =
            AccessTools.FieldRefAccess<CameraDriver, Vector2>("desiredDollyRaw");

        private static readonly AccessTools.FieldRef<CameraDriver, CameraPanner> Panner =
            AccessTools.FieldRefAccess<CameraDriver, CameraPanner>("panner");

        private static readonly System.Func<CameraDriver, Vector2> InputDolly =
            AccessTools.MethodDelegate<System.Func<CameraDriver, Vector2>>(
                AccessTools.Method(typeof(CameraDriver), "CalculateCurInputDollyVect"));

        // How quickly the camera closes the gap to the pawn: about 1 - e^(-Tightness * seconds) of it.
        private const float Tightness = 10f;

        private static Pawn followed;

        private static void Stop() => followed = null;

        [HarmonyPatchCategory("CameraFollow")]
        [HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJump),
            new[] { typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode) })]
        public static class Patch_TryJump
        {
            public static void Postfix(GlobalTargetInfo target)
            {
                followed = null;
                if (!target.IsValid || Current.ProgramState != ProgramState.Playing || WorldRendererUtility.WorldSelected)
                    return;
                // Same adjustment vanilla used for the jump: a carried pawn gives its carrier.
                GlobalTargetInfo adjusted = CameraJumper.GetAdjustedTarget(target);
                if (adjusted.Thing is Pawn pawn && pawn.Spawned && pawn.Map == Find.CurrentMap)
                    followed = pawn;
            }
        }

        [HarmonyPatchCategory("CameraFollow")]
        [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
        public static class Patch_Update
        {
            public static void Prefix(CameraDriver __instance)
            {
                Pawn pawn = followed;
                if (pawn == null)
                    return;
                if (LongEventHandler.ShouldWaitForEvent || Find.CurrentMap == null)
                    return;
                if (!pawn.Spawned || pawn.Map != Find.CurrentMap || WorldRendererUtility.WorldSelected
                    || Find.Selector.SingleSelectedThing != pawn)
                {
                    Stop();
                    return;
                }
                if (DesiredDollyRaw(__instance) != Vector2.zero || InputDolly(__instance) != Vector2.zero)
                {
                    Stop();
                    return;
                }
                // Let the jump's own pan finish first; it ends where the pawn was when the jump started.
                if (Panner(__instance).Moving)
                    return;

                Vector3 target = pawn.DrawPos;
                if (Modules.IsActive("InspectTabCamera"))
                    target.x -= InspectTabCamera.CurrentTabOffset();
                ref Vector3 root = ref RootPos(__instance);
                float t = 1f - Mathf.Exp(-Tightness * Mathf.Min(Time.deltaTime, CameraDriver.MaxDeltaTime));
                root.x = Mathf.Lerp(root.x, target.x, t);
                root.z = Mathf.Lerp(root.z, target.z, t);
            }
        }

        [HarmonyPatchCategory("CameraFollow")]
        [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
        public static class Patch_ClearAllMapsAndWorld
        {
            // Don't keep the pawn of a game that is gone.
            public static void Postfix() => Stop();
        }
    }
}
