using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Faction Customizer reads the raw right mouse button every frame on the world map and selects whatever is
    /// on the tile under the cursor, skipping only when a window is there. World command buttons are not drawn in
    /// a window, so a right-press (or right-drag) on one changed the selection and the buttons vanished.
    /// Skip its handler while the mouse is over a world command button.
    /// </summary>
    [HarmonyPatchCategory("DragReorder")]
    [HarmonyPatch]
    public static class FactionCustomizerCompat
    {
        private static MethodBase Target()
        {
            if (!ModsConfig.IsActive("azravos.factioncustomizer"))
                return null;
            return AccessTools.Method(AccessTools.TypeByName("FactionCustomizer.WorldInterfaceUpdate"), "HandleFloatMenuFunctionality");
        }

        private static bool Prepare() => Target() != null;

        private static MethodBase TargetMethod() => Target();

        private static bool Prefix() => WorldGizmoUtility.LastMouseOverGizmo == null;
    }
}
