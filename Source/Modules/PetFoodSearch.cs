using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Profile;

namespace VanillaTuneUp
{
    /// <summary>
    /// A colony animal only searches the nearest 100 regions for food (FoodUtility.GetMaxRegionsToScan), even when it
    /// is starving. In a large base full of doors and fences those 100 regions cover a small area, so a pet that wanders
    /// away from the food stock can starve next to plenty of kibble. When the normal search finds nothing and the animal
    /// is urgently hungry, search once more over the whole map, the same way vanilla gives a starving visitor one last
    /// chance before it leaves. Penned animals (roamers) keep the vanilla pen-limited search.
    /// </summary>
    [HarmonyPatchCategory("PetFoodSearch")]
    [HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
    public static class PetFoodSearch
    {
        // A failed whole-map search is expensive, so an animal that still finds nothing waits before trying again.
        private const int RetryCooldownTicks = 2500;

        private static readonly Func<JobGiver_GetFood, Pawn, Job> TryGiveJob =
            AccessTools.MethodDelegate<Func<JobGiver_GetFood, Pawn, Job>>(
                AccessTools.Method(typeof(JobGiver_GetFood), "TryGiveJob"));

        private static readonly AccessTools.FieldRef<JobGiver_GetFood, bool> ForceScanWholeMap =
            AccessTools.FieldRefAccess<JobGiver_GetFood, bool>("forceScanWholeMap");

        private static readonly Dictionary<int, int> lastWholeMapSearch = new Dictionary<int, int>();
        private static bool searching;

        public static void Postfix(JobGiver_GetFood __instance, Pawn pawn, ref Job __result)
        {
            if (__result != null || searching || ForceScanWholeMap(__instance) || !ShouldSearchWholeMap(pawn))
                return;
            int now = Find.TickManager.TicksGame;
            if (lastWholeMapSearch.TryGetValue(pawn.thingIDNumber, out int last) && now >= last && now - last < RetryCooldownTicks)
                return;
            lastWholeMapSearch[pawn.thingIDNumber] = now;
            // Think nodes are shared between pawns, so the flag is restored right after this one call.
            searching = true;
            ForceScanWholeMap(__instance) = true;
            try
            {
                __result = TryGiveJob(__instance, pawn);
            }
            finally
            {
                ForceScanWholeMap(__instance) = false;
                searching = false;
            }
            if (__result != null)
                lastWholeMapSearch.Remove(pawn.thingIDNumber);
        }

        private static bool ShouldSearchWholeMap(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || !pawn.IsAnimal || pawn.Faction != Faction.OfPlayer || pawn.Roamer)
                return false;
            Need_Food food = pawn.needs?.food;
            return food != null && food.CurCategory >= HungerCategory.UrgentlyHungry;
        }

        public static void Forget() => lastWholeMapSearch.Clear();
    }

    [HarmonyPatchCategory("PetFoodSearch")]
    [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
    public static class Patch_MemoryUtility_ClearAllMapsAndWorld_PetFoodSearch
    {
        public static void Postfix() => PetFoodSearch.Forget();
    }
}
