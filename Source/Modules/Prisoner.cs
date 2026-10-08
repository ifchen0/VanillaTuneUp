using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla drops the food policy whenever the eater is in a mental state, regardless of who is fetching the food.
    /// That is meant for a breaking pawn feeding itself, but it also lets a warden deliver restricted food to a
    /// prisoner who is mid-break. Keep the policy when someone else is the getter; self-feeding stays vanilla.
    /// </summary>
    [HarmonyPatchCategory("PrisonerFoodPolicy")]
    [HarmonyPatch(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.GetCurrentRespectedRestriction))]
    public static class Patch_GetCurrentRespectedRestriction
    {
        public static void Postfix(Pawn_FoodRestrictionTracker __instance, Pawn getter, ref FoodPolicy __result)
        {
            if (__result != null || getter == null)
                return;
            Pawn pawn = __instance.pawn;
            if (getter == pawn || !pawn.InMentalState || !__instance.Configurable)
                return;
            // Same faction gate vanilla applies before its mental state check.
            if (pawn.Faction != Faction.OfPlayer && getter.Faction != Faction.OfPlayer)
                return;
            __result = __instance.CurrentFoodPolicy;
        }
    }

    /// <summary>
    /// Freshly captured prisoners are often both hungry and bleeding. Warden feeding can be picked before doctor
    /// tending, letting the prisoner bleed out while being spoon-fed. Skip non-forced feeding while the prisoner is
    /// still bleeding so the pawn moves on to other work such as tending; bleeding kills far faster than hunger.
    /// </summary>
    [HarmonyPatchCategory("PrisonerBleeding")]
    [HarmonyPatch]
    public static class Patch_WardenFeed_SkipWhileBleeding
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorkGiver_Warden_Feed), nameof(WorkGiver_Warden_Feed.JobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_Warden_DeliverFood), nameof(WorkGiver_Warden_DeliverFood.JobOnThing));
        }

        public static bool Prefix(Thing t, bool forced, ref Job __result)
        {
            if (forced || !(t is Pawn prisoner) || prisoner.health.hediffSet.BleedRateTotal <= 0f)
                return true;
            __result = null;
            return false;
        }
    }
}
