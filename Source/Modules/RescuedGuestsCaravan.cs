using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Downed pawns of other factions that the player rescued are guests of the player, and vanilla only lets them
    /// leave with a caravan as prisoners (the "Capture" section), which angers their faction. This module lets them
    /// travel as guests instead: they are listed in a "Rescued guests" section of the caravan dialogs, are not
    /// captured when they join the caravan, and keep their guest status until they recover at the destination and
    /// walk out (vanilla guest think tree), which is when their faction's goodwill reward is given.
    ///
    /// Only maps that are not a player home list them: at home they recover in bed and leave, as in vanilla.
    ///
    /// Vanilla side effects that are suppressed while they travel:
    /// - Pawn.ExitMap calls Faction.Notify_MemberExitedMap even for caravan members, which would pay the exit
    ///   goodwill reward when the caravan leaves and reset the tend counter.
    /// - Pawn_GuestTracker.Notify_PawnUndowned (the rescuee join roll) needs a spawned pawn; recovering inside the
    ///   caravan would skip the roll for good. The roll is deferred until the caravan enters a map.
    /// - Settlements buy every caravan member that is not an owner, so guests could be sold or gifted.
    /// </summary>
    public static class RescuedGuestsCaravan
    {
        /// <summary>A non-prisoner pawn of another faction (or factionless) hosted as a guest by the player.</summary>
        public static bool IsPlayerGuest(Pawn p)
        {
            return p != null && p.RaceProps.Humanlike && p.guest != null && p.HostFaction == Faction.OfPlayer
                && !p.IsPrisoner && !p.IsSlave && p.Faction != Faction.OfPlayer;
        }

        /// <summary>A rescued guest that may be added to a caravan formed on its map.</summary>
        public static bool CanJoinCaravan(Pawn p)
        {
            if (!IsPlayerGuest(p) || !p.Spawned || !p.Downed || p.Dead)
                return false;
            if (p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer))
                return false;
            if (p.guest.leftAfterRescue || !p.RaceProps.allowedOnCaravan)
                return false;
            if (p.IsQuestHelper() || p.IsQuestLodger() || QuestUtility.IsReservedByQuestOrQuestBeingGenerated(p))
                return false;
            Lord lord = p.GetLord();
            return lord == null || lord.LordJob is LordJob_VoluntarilyJoinable || lord.LordJob.IsCaravanSendable;
        }

        /// <summary>Guests in the caravan whose rescuee join roll is still pending (recovered while travelling).</summary>
        public static bool PendingJoinRoll(Pawn p)
        {
            return IsPlayerGuest(p) && !p.Dead && !p.Downed && !p.guest.leftAfterRescue;
        }

        private static readonly Action<Pawn_GuestTracker> notifyPawnUndowned =
            AccessTools.MethodDelegate<Action<Pawn_GuestTracker>>(
                AccessTools.Method(typeof(Pawn_GuestTracker), "Notify_PawnUndowned"));

        public static void RunJoinRoll(Pawn p)
        {
            notifyPawnUndowned(p.guest);
        }
    }

    // Rescued guests show up in the form/reform caravan dialog (and the "Add to caravan" gizmo) away from home.
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.AllSendablePawns))]
    public static class RescuedGuestsCaravan_AllSendablePawns
    {
        public static void Postfix(List<Pawn> __result, Map map, bool allowEvenIfDowned)
        {
            if (!allowEvenIfDowned || map.IsPlayerHome)
                return;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (RescuedGuestsCaravan.CanJoinCaravan(p) && !__result.Contains(p))
                    __result.Add(p);
            }
        }
    }

    // A player guest joining a player caravan stays a guest instead of being captured.
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(CaravanUtility), nameof(CaravanUtility.ShouldAutoCapture))]
    public static class RescuedGuestsCaravan_ShouldAutoCapture
    {
        public static void Postfix(ref bool __result, Pawn p, Faction caravanFaction)
        {
            if (__result && caravanFaction == Faction.OfPlayer && RescuedGuestsCaravan.IsPlayerGuest(p))
                __result = false;
        }
    }

    // Own section in the form, reform and split caravan dialogs; otherwise guests fit no vanilla section and are hidden.
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(CaravanUIUtility), nameof(CaravanUIUtility.AddPawnsSections))]
    public static class RescuedGuestsCaravan_AddPawnsSections
    {
        public static void Postfix(TransferableOneWayWidget widget, List<TransferableOneWay> transferables)
        {
            widget.AddSection("VTU_RescuedGuestsCaravan_Section".Translate(),
                transferables.Where(x => x.AnyThing is Pawn p && RescuedGuestsCaravan.IsPlayerGuest(p)));
        }
    }

    // Guests are not caravan owners, so settlements would buy them; they are not ours to sell or give away.
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.PlayerSellableNow))]
    public static class RescuedGuestsCaravan_PlayerSellableNow
    {
        public static void Postfix(ref bool __result, Thing t)
        {
            if (__result && t is Pawn p && RescuedGuestsCaravan.IsPlayerGuest(p))
                __result = false;
        }
    }

    // No exit goodwill while leaving with the caravan; it is paid when they walk off a map after recovering.
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(Faction), nameof(Faction.Notify_MemberExitedMap))]
    public static class RescuedGuestsCaravan_MemberExitedMap
    {
        public static bool Prefix(Pawn member, bool freed)
        {
            return freed || !member.IsCaravanMember() || !RescuedGuestsCaravan.IsPlayerGuest(member);
        }
    }

    // ExitMap clears the mind (Pawn_MindState.Reset), which also zeroes the tend count the exit goodwill is based on.
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
    public static class RescuedGuestsCaravan_ExitMap
    {
        public static void Prefix(Pawn __instance, out int __state)
        {
            __state = RescuedGuestsCaravan.IsPlayerGuest(__instance) ? __instance.mindState.timesGuestTendedToByPlayer : -1;
        }

        public static void Postfix(Pawn __instance, int __state)
        {
            if (__state > 0 && __instance.IsCaravanMember() && RescuedGuestsCaravan.IsPlayerGuest(__instance))
                __instance.mindState.timesGuestTendedToByPlayer = __state;
        }
    }

    // Defer the rescuee join roll while the guest is inside a caravan (vanilla would skip it for good).
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(Pawn_GuestTracker), "Notify_PawnUndowned")]
    public static class RescuedGuestsCaravan_PawnUndowned
    {
        public static bool Prefix(Pawn ___pawn)
        {
            return !(___pawn.IsCaravanMember() && RescuedGuestsCaravan.IsPlayerGuest(___pawn));
        }
    }

    // Run the deferred join roll once the caravan's guests are spawned on a map.
    [HarmonyPatchCategory("RescuedGuestsCaravan")]
    [HarmonyPatch(typeof(CaravanEnterMapUtility), nameof(CaravanEnterMapUtility.Enter),
        new[] { typeof(Caravan), typeof(Map), typeof(Func<Pawn, IntVec3>), typeof(CaravanDropInventoryMode), typeof(bool) })]
    public static class RescuedGuestsCaravan_Enter
    {
        public static void Prefix(Caravan caravan, out List<Pawn> __state)
        {
            __state = caravan.PawnsListForReading.Where(RescuedGuestsCaravan.PendingJoinRoll).ToList();
        }

        public static void Postfix(List<Pawn> __state)
        {
            foreach (Pawn p in __state)
            {
                if (p.Spawned && RescuedGuestsCaravan.PendingJoinRoll(p))
                    RescuedGuestsCaravan.RunJoinRoll(p);
            }
        }
    }
}
