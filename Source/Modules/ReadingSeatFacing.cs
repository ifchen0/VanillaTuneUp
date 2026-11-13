using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// After sitting down to read, vanilla turns the reader towards any adjacent table, then towards any adjacent
    /// item surface (dresser, end table, shelf, workbench), and only faces the seat's own direction when none is
    /// found. The surface has no effect on reading, so readers seated next to a dresser turn their back on the way
    /// the chair faces. When the reader sits on something sittable, skip the surface search so vanilla's own
    /// fallback (face the seat's direction) is used. Readers standing on an open cell keep the vanilla behavior.
    /// </summary>
    [HarmonyPatchCategory("ReadingSeatFacing")]
    [HarmonyPatch(typeof(JobDriver_Reading), "TryFaceClosestSurface")]
    public static class Patch_JobDriver_Reading_TryFaceClosestSurface
    {
        public static bool Prefix(IntVec3 pos, Map map, ref bool __result)
        {
            // Same seat lookup as vanilla's FindAdjacentReadingSurface.
            Building seat = pos.GetFirstThing<Building>(map);
            if (seat?.def.building == null || !seat.def.building.isSittable)
                return true;
            __result = false;
            return false;
        }
    }
}
