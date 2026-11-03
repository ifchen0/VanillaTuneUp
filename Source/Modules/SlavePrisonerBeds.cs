using HarmonyLib;
using RimWorld;
using Verse;

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
            // Prisoner social properness means "bed in the room the pawn is in now", which a slave walking in from
            // elsewhere never meets; a prisoner bed is always in a prison cell, and that is the point here.
            __result = RestUtility.CanUseBedNow(bedThing, sleeper, checkSocialProperness: false,
                allowMedBedEvenIfSetToNoCare, GuestStatus.Prisoner);
            // Vanilla skips the forbidden check for prisoners only; slaves still respect it.
            if (__result && !sleeper.Downed && sleeper.CurJob?.ignoreForbidden != true && bed.IsForbidden(sleeper))
                __result = false;
            return false;
        }
    }

    /// <summary>
    /// Enslaving changes the pawn's faction first, which briefly leaves it neither prisoner nor slave, and vanilla
    /// drops its prisoner bed at that moment. Give the bed back once the pawn is a slave of the colony.
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
