using System.Collections.Generic;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Caravan incidents (another caravan meets yours, a hostile caravan demands goods, a mod's caravan event such as
    /// Real Ruins finding ruins) jump the camera to the caravan on the world map before opening their dialog, pulling
    /// the player out of the colony map every time. While a map is being viewed and an incident targeting a caravan
    /// runs, skip camera jumps to world objects; the dialog still opens. Jumps to pawns or cells on a map (ambush maps)
    /// are kept, and so are jumps made later by dialog options or letters, and the world view still pans as before.
    /// </summary>
    [HarmonyPatchCategory("CaravanEncounterNoJump")]
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class Patch_IncidentWorker_TryExecute
    {
        public static int Depth;

        public static void Prefix(IncidentParms parms, out bool __state)
        {
            __state = parms?.target is Caravan;
            if (__state)
                Depth++;
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
                Depth--;
        }

        public static bool AllowJump(GlobalTargetInfo target)
        {
            return Depth == 0 || !target.HasWorldObject || WorldRendererUtility.WorldSelected;
        }
    }

    public static class CaravanEncounterNoJumpDebug
    {
        // Vanilla "Do incident" only targets a caravan from the world view, where the jump is kept on purpose.
        [DebugAction("Vanilla Tune-Up", "Caravan incident on first caravan...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> CaravanIncident()
        {
            List<DebugActionNode> list = new List<DebugActionNode>();
            Caravan caravan = Find.WorldObjects.Caravans.Find(c => c.IsPlayerControlled);
            if (caravan == null)
                return list;
            foreach (IncidentDef def in DefDatabase<IncidentDef>.AllDefs)
            {
                if (!def.TargetAllowed(caravan))
                    continue;
                IncidentDef localDef = def;
                list.Add(new DebugActionNode(def.defName, DebugActionType.Action, delegate
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(localDef.category, caravan);
                    parms.forced = true;
                    localDef.Worker.TryExecute(parms);
                }));
            }
            list.SortBy(n => n.label);
            return list;
        }
    }

    [HarmonyPatchCategory("CaravanEncounterNoJump")]
    [HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJumpAndSelect))]
    public static class Patch_CameraJumper_TryJumpAndSelect
    {
        public static bool Prefix(GlobalTargetInfo target) => Patch_IncidentWorker_TryExecute.AllowJump(target);
    }

    [HarmonyPatchCategory("CaravanEncounterNoJump")]
    [HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJump), typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode))]
    public static class Patch_CameraJumper_TryJump
    {
        public static bool Prefix(GlobalTargetInfo target) => Patch_IncidentWorker_TryExecute.AllowJump(target);
    }
}
