using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla drops a pawn's bed on a guest status change only for a prisoner bed of a non-prisoner, a non-prisoner
    /// bed of a prisoner, or a colonist bed of a pawn without a host faction. A slave bed of a pawn that is no longer
    /// a slave is missing, and banishing goes through exactly that: no release, so no UnclaimAll, and the exit-map
    /// cleanup only covers released or hosted pawns. The banished slave keeps the bed off the map and other slaves
    /// skip it as full. Drop it like the other cases, and clear such leftovers from earlier saves on load.
    /// Babies are left alone: vanilla lets them be assigned to slave beds.
    /// </summary>
    public static class SlaveBedOwnership
    {
        public static bool Stale(Pawn owner) => !owner.IsSlave && !owner.DevelopmentalStage.Baby();
    }

    [HarmonyPatchCategory("SlaveBedOwnership")]
    [HarmonyPatch(typeof(Pawn_Ownership), nameof(Pawn_Ownership.Notify_ChangedGuestStatus))]
    public static class SlaveBedOwnership_GuestStatus
    {
        public static void Postfix(Pawn_Ownership __instance, Pawn ___pawn)
        {
            Building_Bed bed = __instance.OwnedBed;
            if (bed != null && bed.ForSlaves && SlaveBedOwnership.Stale(___pawn))
                __instance.UnclaimBed();
        }
    }

    [HarmonyPatchCategory("SlaveBedOwnership")]
    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class SlaveBedOwnership_Load
    {
        public static void Postfix()
        {
            int cleared = 0;
            foreach (Map map in Find.Maps)
            {
                foreach (Building_Bed bed in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>())
                {
                    if (!bed.ForSlaves)
                        continue;
                    foreach (Pawn owner in bed.OwnersForReading.ToList())
                    {
                        if (!SlaveBedOwnership.Stale(owner))
                            continue;
                        if (owner.ownership?.OwnedBed == bed)
                            owner.ownership.UnclaimBed();
                        else
                            bed.CompAssignableToPawn.ForceRemovePawn(owner);
                        cleared++;
                    }
                }
            }
            if (cleared > 0)
                Log.Message($"[Vanilla Tune-Up] SlaveBedOwnership: freed {cleared} slave bed slot(s) still assigned to pawns that are no longer slaves.");
        }
    }
}
