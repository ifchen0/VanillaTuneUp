using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla keeps slaves out of prisoner beds, so a colony with both needs separate slave bedrooms and a slave
    /// hospital. Let slaves of the colony use prisoner beds, sleeping and medical, under the prisoner rules.
    /// </summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.CanUseBedNow))]
    public static class SlavePrisonerBeds
    {
        public static bool Prefix(Thing bedThing, Pawn sleeper, bool allowMedBedEvenIfSetToNoCare,
            GuestStatus? guestStatusOverride, ref bool __result)
        {
            if (!(bedThing is Building_Bed bed) || !bed.ForPrisoners || sleeper == null
                || (guestStatusOverride ?? sleeper.GuestStatus) != GuestStatus.Slave || !sleeper.IsSlaveOfColony)
                return true;
            // Vanilla skips the forbidden check for prisoners only; slaves still respect it.
            if (!sleeper.Downed && sleeper.CurJob?.ignoreForbidden != true && bed.IsForbidden(sleeper))
            {
                __result = false;
                return false;
            }
            // Prisoner social properness means "bed in the room the pawn is in now", which a slave walking in from
            // elsewhere never meets; a prisoner bed is always in a prison cell, and that is the point here.
            __result = RestUtility.CanUseBedNow(bedThing, sleeper, checkSocialProperness: false,
                allowMedBedEvenIfSetToNoCare, GuestStatus.Prisoner);
            return false;
        }
    }

    /// <summary>
    /// Rescue and take-to-bed jobs fail at once when the bed's prisoner flag differs from the carried pawn's
    /// (a compiler-generated FailOn lambda in MakeNewToils), so a downed slave could be given a prisoner medical bed
    /// but never carried there. Let that check pass for slaves of the colony.
    /// </summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch]
    public static class SlavePrisonerBeds_TakeToBed
    {
        private static MethodBase target;

        public static bool Prepare()
        {
            MethodInfo forPrisoners = AccessTools.PropertyGetter(typeof(Building_Bed), nameof(Building_Bed.ForPrisoners));
            MethodInfo isPrisoner = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsPrisoner));
            target = AccessTools.GetDeclaredMethods(typeof(JobDriver_TakeToBed)).FirstOrDefault(m =>
            {
                if (!m.Name.Contains("MakeNewToils") || m.ReturnType != typeof(bool) || m.GetParameters().Length != 0)
                    return false;
                var code = PatchProcessor.GetOriginalInstructions(m);
                return code.Any(i => i.Calls(forPrisoners)) && code.Any(i => i.Calls(isPrisoner));
            });
            if (target == null)
                Log.Warning("[Vanilla Tune-Up] SlavePrisonerBeds: take-to-bed bed check not found; downed slaves will not be carried to prisoner beds.");
            return target != null;
        }

        public static MethodBase TargetMethod() => target;

        public static bool Prefix(JobDriver_TakeToBed __instance, ref bool __result)
        {
            Job job = __instance.job;
            if (job.def.makeTargetPrisoner || !(job.targetA.Thing is Pawn takee) || !takee.IsSlaveOfColony
                || !(job.targetB.Thing is Building_Bed bed) || !bed.ForPrisoners)
                return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Enslaving changes the pawn's faction, and Pawn.SetFaction first calls SetGuestStatus(null): for that nested
    /// call the pawn is neither prisoner nor slave, so Pawn_Ownership.Notify_ChangedGuestStatus drops its prisoner bed
    /// (the role the pawn ends up with is not known yet at that point). Give the bed back once it is a slave.
    /// </summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    public static class SlavePrisonerBeds_KeepBedOnEnslave
    {
        public static void Prefix(Pawn ___pawn, GuestStatus guestStatus, out Building_Bed __state)
        {
            __state = null;
            Building_Bed bed = ___pawn?.ownership?.OwnedBed;
            if (guestStatus == GuestStatus.Slave && bed != null && bed.ForPrisoners && ___pawn.IsPrisonerOfColony)
                __state = bed;
        }

        public static void Postfix(Pawn ___pawn, Building_Bed __state)
        {
            if (__state == null || !__state.Spawned || !__state.ForPrisoners || !___pawn.IsSlaveOfColony
                || ___pawn.ownership.OwnedBed != null || !__state.AnyUnownedSleepingSlot)
                return;
            ___pawn.ownership.ClaimBedIfNonMedical(__state);
        }
    }
}
