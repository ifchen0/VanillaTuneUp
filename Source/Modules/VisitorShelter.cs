using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Trader caravans and visitor groups leave early as soon as any member, pack animals included, gets mild
    /// hypothermia or heatstroke: they wait at a spot just outside the colony and their Defend duty never seeks a safe
    /// temperature. Every few in-game minutes while waiting, if the temperature at that spot is outside the safe range
    /// of the most sensitive member or anything hostile to the group is active on the map, the waiting spot moves into
    /// a room: a barn when the group has animals, otherwise a dining room, with a storeroom as the fallback for both.
    /// The room must itself be within the safe range and reachable by every member. The group goes back to its original
    /// spot once the threat is gone and the temperature there is well inside the range again.
    /// </summary>
    [HarmonyPatchCategory("VisitorShelter")]
    [HarmonyPatch(typeof(LordToil), nameof(LordToil.LordToilTick))]
    public static class VisitorShelter
    {
        // Often enough to get inside before a raid arrives; the check itself is a few pawns and the threat cache.
        private const int CheckInterval = 250;
        // Shelter a little before the safe range is left; go back out only when well inside it, to avoid flip-flopping.
        private const float EnterMargin = 2f;
        private const float LeaveMargin = 6f;
        internal const int CellTries = 10;

        private static readonly Type DefendTraderCaravanType = AccessTools.TypeByName("RimWorld.LordToil_DefendTraderCaravan");
        private static readonly AccessTools.FieldRef<LordJob_TradeWithColony, IntVec3> TradeChillSpot =
            AccessTools.FieldRefAccess<LordJob_TradeWithColony, IntVec3>("chillSpot");
        private static readonly AccessTools.FieldRef<LordJob_VisitColony, IntVec3> VisitChillSpot =
            AccessTools.FieldRefAccess<LordJob_VisitColony, IntVec3>("chillSpot");

        public static void Postfix(LordToil __instance)
        {
            if (__instance is LordToil_DefendPoint toil && toil.lord.ticksInToil % CheckInterval == 0
                && TryGetHome(toil, out IntVec3 home))
                Check(toil, home);
        }

        /// <summary>The original waiting spot, if this toil is the waiting toil of a trader caravan or visitor group.</summary>
        private static bool TryGetHome(LordToil_DefendPoint toil, out IntVec3 home)
        {
            Lord lord = toil.lord;
            if (lord.LordJob is LordJob_TradeWithColony trade)
            {
                // The toil defending the trader after an attack is the same class; only the waiting one (added last).
                home = TradeChillSpot(trade);
                return toil.GetType() == DefendTraderCaravanType
                    && lord.Graph.lordToils.LastOrDefault(t => t.GetType() == DefendTraderCaravanType) == toil;
            }
            if (lord.LordJob is LordJob_VisitColony visit && toil.GetType() == typeof(LordToil_DefendPoint))
            {
                home = VisitChillSpot(visit);
                return true;
            }
            home = IntVec3.Invalid;
            return false;
        }

        /// <summary>The room a sheltering group is waiting in, or null when it waits at its original spot.</summary>
        public static Room ShelterOf(Pawn pawn)
        {
            if (!(pawn.GetLord()?.CurLordToil is LordToil_DefendPoint toil) || !TryGetHome(toil, out IntVec3 home)
                || toil.FlagLoc == home || !toil.FlagLoc.IsValid)
                return null;
            Room room = toil.FlagLoc.GetRoom(pawn.Map);
            return room != null && !room.PsychologicallyOutdoors ? room : null;
        }

        private static void Check(LordToil_DefendPoint toil, IntVec3 home)
        {
            Lord lord = toil.lord;
            Map map = lord.Map;
            if (!home.IsValid || !home.InBounds(map))
                return;

            FloatRange safe = new FloatRange(float.MinValue, float.MaxValue);
            bool hasAnimals = false;
            foreach (Pawn pawn in lord.ownedPawns)
            {
                if (!pawn.Spawned || pawn.Dead)
                    continue;
                FloatRange range = pawn.SafeTemperatureRange();
                safe.min = Math.Max(safe.min, range.min);
                safe.max = Math.Min(safe.max, range.max);
                hasAnimals |= pawn.RaceProps.Animal;
            }

            float outside = home.GetTemperature(map);
            bool threat = GenHostility.AnyHostileActiveThreatTo(map, lord.faction);
            IntVec3 current = toil.FlagLoc;
            if (current == home)
            {
                if (!threat && outside >= safe.min + EnterMargin && outside <= safe.max - EnterMargin)
                    return;
                if (TryFindShelter(lord, safe, hasAnimals, out IntVec3 cell, out Room room))
                {
                    Move(toil, cell);
                    Pawn trader = TraderCaravanUtility.FindTrader(lord);
                    Pawn look = trader ?? lord.ownedPawns.FirstOrDefault(p => p.Spawned);
                    string group = (trader != null ? "VTU_VisitorShelter_Traders" : "VTU_VisitorShelter_Visitors").Translate(lord.faction.Name);
                    string reason = threat ? "VTU_VisitorShelter_ReasonEnemies"
                        : outside < safe.min + EnterMargin ? "VTU_VisitorShelter_ReasonCold" : "VTU_VisitorShelter_ReasonHeat";
                    Messages.Message("VTU_VisitorShelter_Moved".Translate(group, reason.Translate(), room.GetRoomRoleLabel()).CapitalizeFirst(),
                        look != null ? new LookTargets(look) : new LookTargets(cell, map), MessageTypeDefOf.NeutralEvent);
                }
                return;
            }

            if (!threat && outside >= safe.min + LeaveMargin && outside <= safe.max - LeaveMargin)
            {
                Move(toil, home);
                return;
            }
            // Still unsafe outside: stay unless the room became unusable (deconstructed, door left open, heater off).
            Room currentRoom = current.GetRoom(map);
            if (currentRoom != null && !currentRoom.PsychologicallyOutdoors && safe.Includes(currentRoom.Temperature))
                return;
            Move(toil, TryFindShelter(lord, safe, hasAnimals, out IntVec3 other, out _) ? other : home);
        }

        private static void Move(LordToil_DefendPoint toil, IntVec3 cell)
        {
            toil.SetDefendPoint(cell);
            toil.UpdateAllDuties();
        }

        internal static IEnumerable<IntVec3> WaitCells(Room room, Map map) =>
            room.Cells.Where(c => c.Standable(map) && c.GetDoor(map) == null);

        private static bool TryFindShelter(Lord lord, FloatRange safe, bool hasAnimals, out IntVec3 cell, out Room shelter)
        {
            Map map = lord.Map;
            string[] roles = hasAnimals ? new[] { "Barn", "Storeroom" } : new[] { "DiningRoom", "Storeroom" };
            foreach (string roleName in roles)
            {
                RoomRoleDef role = DefDatabase<RoomRoleDef>.GetNamedSilentFail(roleName);
                if (role == null)
                    continue;
                IEnumerable<Room> rooms = map.regionGrid.AllRooms
                    .Where(r => r.Role == role && !r.PsychologicallyOutdoors && !r.Fogged && safe.Includes(r.Temperature))
                    .OrderByDescending(r => r.CellCount);
                foreach (Room room in rooms)
                {
                    foreach (IntVec3 c in WaitCells(room, map).InRandomOrder().Take(CellTries))
                    {
                        if (lord.ownedPawns.All(p => !p.Spawned || p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)))
                        {
                            cell = c;
                            shelter = room;
                            return true;
                        }
                    }
                }
            }
            cell = IntVec3.Invalid;
            shelter = null;
            return false;
        }
    }

    /// <summary>
    /// Moving the waiting spot is not enough: guards and other members keep wandering up to 7 cells around it, out the
    /// door into the cold. While a group shelters, its wandering stays inside the shelter room.
    /// </summary>
    [HarmonyPatchCategory("VisitorShelter")]
    [HarmonyPatch(typeof(JobGiver_Wander), "GetExactWanderDest")]
    public static class Patch_JobGiver_Wander_GetExactWanderDest
    {
        public static bool Prefix(JobGiver_Wander __instance, Pawn pawn, ref IntVec3 __result)
        {
            if (!(__instance is JobGiver_WanderNearDutyLocation))
                return true;
            Room room = VisitorShelter.ShelterOf(pawn);
            if (room == null)
                return true;
            IntVec3 cell = VisitorShelter.WaitCells(room, pawn.Map).InRandomOrder().Take(VisitorShelter.CellTries)
                .FirstOrDefault(c => pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly));
            if (!cell.IsValid || cell == default(IntVec3))
                return true;
            __result = cell;
            return false;
        }
    }
}
