using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// A pawn downed while walking to bed keeps its LayDown job: MakeDowned sets the Down state before ClearMind, so
    /// IsLayingForJobCleanup sees a laying posture and StopAll leaves the job alone, while the pather is stopped. When
    /// the pawn gets up again, MakeUndowned stops the pather once more and the job carries on in the GotoBed toil, which
    /// only ends on arrival, so the pawn stands still until it collapses from exhaustion. Short downings (earthquakes,
    /// stuns from mods) hit this most. Restart the walk whenever the toil finds the pather stopped on a pawn that is up;
    /// toils are rebuilt on load, so pawns already stuck in a save recover too.
    /// </summary>
    [HarmonyPatchCategory("BedWalkAfterDowned")]
    [HarmonyPatch(typeof(Toils_Bed), nameof(Toils_Bed.GotoBed))]
    public static class Patch_Toils_Bed_GotoBed
    {
        public static void Postfix(Toil __result, TargetIndex bedIndex)
        {
            Toil toil = __result;
            System.Action<int> vanilla = toil.tickIntervalAction;
            toil.tickIntervalAction = delta =>
            {
                vanilla?.Invoke(delta);
                Pawn actor = toil.actor;
                if (actor.pather.Moving || actor.Downed || actor.CurJob == null)
                    return;
                if (actor.CurJob.GetTarget(bedIndex).Thing is Building_Bed bed)
                    actor.pather.StartPath(RestUtility.GetBedSleepingSlotPosFor(actor, bed), PathEndMode.OnCell);
            };
        }
    }
}
