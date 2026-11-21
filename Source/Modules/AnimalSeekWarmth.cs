using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Profile;

namespace VanillaTuneUp
{
    /// <summary>
    /// A colony animal only heads for a warm (or cool) room once its hypothermia or heatstroke reaches the Serious stage
    /// (severity 0.35). Frostbite starts at 0.37 and deals half of the part's base hit points regardless of body size,
    /// so a baby animal (duckling, chick) can lose its head to the first frostbite before it gets home. Colony animals
    /// now start looking for a safe temperature at the Minor stage (0.2), the same search vanilla runs at Serious.
    /// </summary>
    [HarmonyPatchCategory("AnimalSeekWarmth")]
    [HarmonyPatch(typeof(JobGiver_SeekSafeTemperature), "TryGiveJob")]
    public static class AnimalSeekWarmth
    {
        private static readonly AccessTools.FieldRef<JobGiver_SeekSafeTemperature, bool> RequiresInjury =
            AccessTools.FieldRefAccess<JobGiver_SeekSafeTemperature, bool>("requiresInjury");

        // Think nodes are shared between pawns, so the flag is put back by the finalizer right after this call.
        public static void Prefix(JobGiver_SeekSafeTemperature __instance, Pawn pawn, out bool __state)
        {
            __state = false;
            if (!RequiresInjury(__instance) || !IsColonyAnimal(pawn) || !HasMinorTemperatureInjury(pawn))
                return;
            RequiresInjury(__instance) = false;
            __state = true;
        }

        public static void Finalizer(JobGiver_SeekSafeTemperature __instance, bool __state)
        {
            if (__state)
                RequiresInjury(__instance) = true;
        }

        internal static bool IsColonyAnimal(Pawn pawn) =>
            pawn != null && pawn.IsAnimal && pawn.Faction == Faction.OfPlayer;

        internal static bool HasMinorTemperatureInjury(Pawn pawn) =>
            pawn.health.hediffSet.HasTemperatureInjury(TemperatureInjuryStage.Minor);
    }

    /// <summary>
    /// A sleeping pawn never reaches the temperature check: ThinkNode_ConditionalMustKeepLyingDown keeps it lying down
    /// until it is rested, and the ground sleep spot ignores temperature, so an animal that dozes off in the snow sleeps
    /// on while it freezes. A colony animal with minor or worse hypothermia or heatstroke, lying somewhere outside its
    /// comfortable range, is released from the keep-lying node when the same region search vanilla uses finds a safe
    /// place it may go to. With nowhere safe to go it keeps sleeping as in vanilla, and is not checked again for a while.
    /// </summary>
    [HarmonyPatchCategory("AnimalSeekWarmth")]
    [HarmonyPatch(typeof(ThinkNode_ConditionalMustKeepLyingDown), "Satisfied")]
    public static class AnimalSeekWarmthWakeUp
    {
        // The region search can cover the whole map, so a sleeper with nowhere safe to go waits before the next search.
        private const int RetryCooldownTicks = 2500;

        private static readonly Dictionary<int, int> lastFailedSearch = new Dictionary<int, int>();

        public static void Postfix(Pawn pawn, ref bool __result)
        {
            // Medical rest is skipped: JobGiver_PatientGoToBed sits above the temperature check and sends it back to bed.
            if (!__result || !pawn.Spawned || pawn.Downed || pawn.CurJob.restUntilHealed
                || !AnimalSeekWarmth.IsColonyAnimal(pawn) || !AnimalSeekWarmth.HasMinorTemperatureInjury(pawn))
                return;
            FloatRange tempRange = pawn.ComfortableTemperatureRange();
            if (tempRange.Includes(pawn.AmbientTemperature))
                return;
            int now = Find.TickManager.TicksGame;
            if (lastFailedSearch.TryGetValue(pawn.thingIDNumber, out int last) && now >= last && now - last < RetryCooldownTicks)
                return;
            Region safe = JobGiver_SeekSafeTemperature.ClosestRegionWithinTemperatureRange(
                pawn.Position, pawn.Map, pawn, tempRange, TraverseParms.For(pawn));
            if (safe == null)
            {
                lastFailedSearch[pawn.thingIDNumber] = now;
                return;
            }
            lastFailedSearch.Remove(pawn.thingIDNumber);
            __result = false;
        }

        public static void Forget() => lastFailedSearch.Clear();
    }

    [HarmonyPatchCategory("AnimalSeekWarmth")]
    [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
    public static class Patch_MemoryUtility_ClearAllMapsAndWorld_AnimalSeekWarmth
    {
        public static void Postfix() => AnimalSeekWarmthWakeUp.Forget();
    }
}
