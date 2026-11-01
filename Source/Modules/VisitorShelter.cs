using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
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
    /// temperature. Every few in-game minutes while waiting, if the temperature at that spot is near the safe limit of
    /// the most sensitive member or anything hostile to the group is active on the map, the waiting spot moves into a
    /// room: a barn when the group has animals, otherwise a dining room, with a storeroom as the fallback for both.
    /// The room must be comfortably inside the safe range, reachable by every member and not already taken by another
    /// sheltering group (first come, first served). The group goes back to its original spot once the threat is gone
    /// and the temperature there is well inside the range again. Without a usable room, vanilla behaviour stays.
    /// </summary>
    [HarmonyPatchCategory("VisitorShelter")]
    [HarmonyPatch(typeof(LordToil), nameof(LordToil.LordToilTick))]
    public static class VisitorShelter
    {
        // Often enough to get inside before a raid arrives; the check itself is a few pawns and the threat cache.
        private const int CheckInterval = 250;
        // After a failed room search, wait an in-game hour before scanning the map's rooms again.
        private const int RetryInterval = GenDate.TicksPerHour;
        // Shelter a little before the safe range is left; go back out only when well inside it, to avoid flip-flopping.
        private const float EnterMargin = 2f;
        private const float LeaveMargin = 6f;
        private const string ArrivedMemo = "TravelArrived";

        private static readonly AccessTools.FieldRef<LordJob_TradeWithColony, IntVec3> TradeChillSpot =
            AccessTools.FieldRefAccess<LordJob_TradeWithColony, IntVec3>("chillSpot");
        private static readonly AccessTools.FieldRef<LordJob_VisitColony, IntVec3> VisitChillSpot =
            AccessTools.FieldRefAccess<LordJob_VisitColony, IntVec3>("chillSpot");
        private static readonly AccessTools.FieldRef<Trigger_Memo, string> MemoOf =
            AccessTools.FieldRefAccess<Trigger_Memo, string>("memo");

        // Both lord jobs hold other LordToil_DefendPoint toils of the same class (defending after an attack, on the way
        // in or out); the waiting one is the toil entered when the group arrives at its chill spot. The visitors' exit
        // subgraph has a TravelArrived transition of its own (to LordToil_ExitMap), hence the target type check.
        private static readonly ConditionalWeakTable<StateGraph, LordToil> WaitingToils = new ConditionalWeakTable<StateGraph, LordToil>();
        private static readonly ConditionalWeakTable<Lord, StrongBox<int>> NextSearch = new ConditionalWeakTable<Lord, StrongBox<int>>();

        // Set on the first unexpected exception (another mod reshaping lord graphs, say): one error, then both patches
        // step aside for the rest of the session and groups behave as in vanilla.
        public static bool Failed { get; private set; }

        public static void Postfix(LordToil __instance)
        {
            if (Failed)
                return;
            try
            {
                if (__instance is LordToil_DefendPoint toil && toil.lord.ticksInToil % CheckInterval == 0
                    && TryGetHome(toil, out IntVec3 home))
                    Check(toil, home);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        internal static void Fail(Exception e)
        {
            Failed = true;
            Log.Error($"[Vanilla Tune-Up] VisitorShelter: unexpected error, turned off for this session: {e}");
        }

        /// <summary>The original waiting spot, if this toil is the waiting toil of a trader caravan or visitor group.</summary>
        private static bool TryGetHome(LordToil_DefendPoint toil, out IntVec3 home)
        {
            Lord lord = toil.lord;
            home = IntVec3.Invalid;
            if (lord.faction == null)
                return false;
            if (lord.LordJob is LordJob_TradeWithColony trade)
                home = TradeChillSpot(trade);
            else if (lord.LordJob is LordJob_VisitColony visit)
                home = VisitChillSpot(visit);
            else
                return false;
            return WaitingToil(lord.Graph) == toil;
        }

        private static LordToil WaitingToil(StateGraph graph) =>
            WaitingToils.GetValue(graph, g => g.transitions.FirstOrDefault(t => t.target is LordToil_DefendPoint
                && t.triggers.Any(tr => tr is Trigger_Memo memo && MemoOf(memo) == ArrivedMemo))?.target);

        /// <summary>The room a group is sheltering in, or null when it is not sheltering.</summary>
        public static Room ShelterRoomOf(Lord lord)
        {
            if (!(lord?.CurLordToil is LordToil_DefendPoint toil) || !TryGetHome(toil, out IntVec3 home)
                || toil.FlagLoc == home || !toil.FlagLoc.IsValid)
                return null;
            Room room = toil.FlagLoc.GetRoom(lord.Map);
            return room != null && !room.PsychologicallyOutdoors ? room : null;
        }

        /// <summary>Whether a food source is in the room; a dispenser counts by the cell its users stand on.</summary>
        internal static bool InRoom(Thing t, Room room)
        {
            Map map = t.MapHeld;
            if (map == null)
                return false;
            IntVec3 cell = t.Spawned && t.def.hasInteractionCell ? t.InteractionCell : t.PositionHeld;
            return cell.GetRoom(map) == room;
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
            // No temperature suits every member at once, so no room can help.
            if (safe.min + 2 * EnterMargin > safe.max)
                return;

            float outside = home.GetTemperature(map);
            bool threat = GenHostility.AnyHostileActiveThreatTo(map, lord.faction);
            IntVec3 current = toil.FlagLoc;
            if (current == home)
            {
                if (!threat && Within(outside, safe, EnterMargin))
                    return;
                Shelter(toil, safe, hasAnimals, threat, outside, home);
                return;
            }

            if (!threat && Within(outside, safe, LeaveMargin))
            {
                Move(toil, home, interrupt: false);
                return;
            }
            // Still unsafe outside: stay while the room is safe or at least better than outside (a heater may just be
            // catching up), otherwise look for another room, and go back to vanilla behaviour without one.
            Room room = current.GetRoom(map);
            float roomDistance = room == null || room.PsychologicallyOutdoors ? float.MaxValue : Distance(room.Temperature, safe);
            if (roomDistance == 0f || roomDistance < Distance(outside, safe))
                return;
            Shelter(toil, safe, hasAnimals, threat, outside, home);
        }

        private static void Shelter(LordToil_DefendPoint toil, FloatRange safe, bool hasAnimals, bool threat, float outside, IntVec3 home)
        {
            Lord lord = toil.lord;
            StrongBox<int> next = NextSearch.GetOrCreateValue(lord);
            if (Find.TickManager.TicksGame < next.Value)
            {
                if (toil.FlagLoc != home)
                    Move(toil, home, interrupt: false);
                return;
            }
            if (!TryFindShelter(lord, safe, hasAnimals, out IntVec3 cell, out Room room))
            {
                next.Value = Find.TickManager.TicksGame + RetryInterval;
                if (toil.FlagLoc != home)
                    Move(toil, home, interrupt: false);
                return;
            }
            Move(toil, cell, interrupt: true);
            Pawn trader = TraderCaravanUtility.FindTrader(lord);
            Pawn look = trader ?? lord.ownedPawns.FirstOrDefault(p => p.Spawned);
            string group = (trader != null ? "VTU_VisitorShelter_Traders" : "VTU_VisitorShelter_Visitors").Translate(lord.faction.Name);
            string reason = threat ? "VTU_VisitorShelter_ReasonEnemies"
                : outside < safe.min + EnterMargin ? "VTU_VisitorShelter_ReasonCold" : "VTU_VisitorShelter_ReasonHeat";
            Messages.Message("VTU_VisitorShelter_Moved".Translate(group, reason.Translate(), room.GetRoomRoleLabel()).CapitalizeFirst(),
                look != null ? new LookTargets(look) : new LookTargets(cell, lord.Map), MessageTypeDefOf.NeutralEvent);
        }

        private static bool Within(float temperature, FloatRange safe, float margin) =>
            temperature >= safe.min + margin && temperature <= safe.max - margin;

        private static float Distance(float temperature, FloatRange safe) =>
            temperature < safe.min ? safe.min - temperature : temperature > safe.max ? temperature - safe.max : 0f;

        // Going inside interrupts current jobs and wakes sleepers, as vanilla lord transitions do; otherwise a member
        // asleep outside would sleep on in the cold until rested. Going back out lets them finish what they are doing.
        private static void Move(LordToil_DefendPoint toil, IntVec3 cell, bool interrupt)
        {
            toil.SetDefendPoint(cell);
            toil.UpdateAllDuties();
            if (!interrupt)
                return;
            foreach (Pawn pawn in toil.lord.ownedPawns)
            {
                if (!pawn.Spawned)
                    continue;
                if (!pawn.Awake())
                    RestUtility.WakeUp(pawn);
                if (pawn.jobs?.curJob != null)
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
        }

        private static bool TryFindShelter(Lord lord, FloatRange safe, bool hasAnimals, out IntVec3 cell, out Room shelter)
        {
            Map map = lord.Map;
            // First come, first served: a room another group already shelters in is not shared.
            var taken = new HashSet<Room>();
            foreach (Lord other in map.lordManager.lords)
            {
                Room room = other == lord ? null : ShelterRoomOf(other);
                if (room != null)
                    taken.Add(room);
            }
            string[] roles = hasAnimals ? new[] { "Barn", "Storeroom" } : new[] { "DiningRoom", "Storeroom" };
            foreach (string roleName in roles)
            {
                RoomRoleDef role = DefDatabase<RoomRoleDef>.GetNamedSilentFail(roleName);
                if (role == null)
                    continue;
                IEnumerable<Room> rooms = map.regionGrid.AllRooms
                    .Where(r => r.Role == role && !r.PsychologicallyOutdoors && !r.Fogged && !taken.Contains(r)
                        && Within(r.Temperature, safe, EnterMargin))
                    .OrderByDescending(r => r.CellCount);
                foreach (Room room in rooms)
                {
                    // Reachability is decided per region, so one cell per room tells whether everyone can get in.
                    if (room.Cells.Where(c => c.Standable(map)).TryRandomElement(out IntVec3 c)
                        && lord.ownedPawns.All(p => !p.Spawned || p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)))
                    {
                        cell = c;
                        shelter = room;
                        return true;
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
    /// door into the cold. While a group shelters, vanilla wandering is kept but only accepts cells in the shelter room.
    /// </summary>
    [HarmonyPatchCategory("VisitorShelter")]
    [HarmonyPatch(typeof(JobGiver_Wander), "GetExactWanderDest")]
    public static class Patch_JobGiver_Wander_GetExactWanderDest
    {
        private static readonly AccessTools.FieldRef<JobGiver_Wander, float> WanderRadius =
            AccessTools.FieldRefAccess<JobGiver_Wander, float>("wanderRadius");
        private static readonly AccessTools.FieldRef<JobGiver_Wander, Func<Pawn, IntVec3, IntVec3, bool>> Validator =
            AccessTools.FieldRefAccess<JobGiver_Wander, Func<Pawn, IntVec3, IntVec3, bool>>("wanderDestValidator");
        private static readonly AccessTools.FieldRef<JobGiver_Wander, Danger> MaxDanger =
            AccessTools.FieldRefAccess<JobGiver_Wander, Danger>("maxDanger");
        private static readonly AccessTools.FieldRef<JobGiver_Wander, bool> CanBashDoors =
            AccessTools.FieldRefAccess<JobGiver_Wander, bool>("canBashDoors");

        public static bool Prefix(JobGiver_Wander __instance, Pawn pawn, ref IntVec3 __result)
        {
            if (VisitorShelter.Failed || !(__instance is JobGiver_WanderNearDutyLocation))
                return true;
            try
            {
                Lord lord = pawn.GetLord();
                Room room = VisitorShelter.ShelterRoomOf(lord);
                if (room == null)
                    return true;
                // Wander around the shelter spot rather than the duty focus: carriers follow the trader, who may be outside.
                IntVec3 root = lord.CurLordToil.FlagLoc;
                float radius = pawn.mindState.duty?.wanderRadius ?? WanderRadius(__instance);
                Func<Pawn, IntVec3, IntVec3, bool> validator = Validator(__instance);
                __result = RCellFinder.RandomWanderDestFor(pawn, root, radius,
                    (p, c, r) => c.GetRoom(p.Map) == room && (validator == null || validator(p, c, r)),
                    PawnUtility.ResolveMaxDanger(pawn, MaxDanger(__instance)), CanBashDoors(__instance));
                return false;
            }
            catch (Exception e)
            {
                VisitorShelter.Fail(e);
                return true;
            }
        }
    }

    /// <summary>
    /// Food lying around the colony draws a sheltering member out of the room just like wandering would. While the
    /// group shelters, the food search on the map only accepts food in the shelter room, until the eater gets urgently
    /// hungry; food the member carries is not affected.
    /// </summary>
    [HarmonyPatchCategory("VisitorShelter")]
    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.BestFoodSourceOnMap))]
    public static class Patch_FoodUtility_BestFoodSourceOnMap
    {
        private const HungerCategory LeaveRoomHunger = HungerCategory.UrgentlyHungry;

        /// <summary>Wraps the food validator where vanilla stores it, like VisitorFood does; the two wrappers stack.</summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo wrap = AccessTools.Method(typeof(Patch_FoodUtility_BestFoodSourceOnMap), nameof(Wrap));
            bool wrapped = false;
            foreach (CodeInstruction code in instructions)
            {
                if (!wrapped && (code.opcode == OpCodes.Stfld || code.opcode == OpCodes.Stloc || code.opcode == OpCodes.Stloc_S)
                    && VisitorFood.IsFoodValidator(code.operand))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, wrap);
                    wrapped = true;
                }
                yield return code;
            }
            if (!wrapped)
                Log.Warning("[Vanilla Tune-Up] VisitorShelter: could not find the food validator; sheltering visitors may leave the room for food.");
        }

        public static Predicate<Thing> Wrap(Predicate<Thing> validator, Pawn getter, Pawn eater)
        {
            if (VisitorShelter.Failed || getter != eater)
                return validator;
            try
            {
                Room room = VisitorShelter.ShelterRoomOf(eater?.GetLord());
                if (room == null || eater.needs?.food == null || eater.needs.food.CurCategory >= LeaveRoomHunger)
                    return validator;
                return t => VisitorShelter.InRoom(t, room) && validator(t);
            }
            catch (Exception e)
            {
                VisitorShelter.Fail(e);
                return validator;
            }
        }
    }

    /// <summary>
    /// After picking up food, vanilla walks to the nearest free chair at a table within 32 cells, wherever it is, and
    /// only avoids rooms at an unsafe temperature: enemies do not count, and the group shelters a little before the
    /// outside gets unsafe. While a group shelters, its members sit at a table in the shelter room, otherwise eat
    /// standing in the room.
    /// </summary>
    [HarmonyPatchCategory("VisitorShelter")]
    [HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.TryFindChairOrSpot))]
    public static class Patch_Toils_Ingest_TryFindChairOrSpot
    {
        public static void Postfix(Pawn pawn, Thing ingestible, ref IntVec3 cell, ref bool __result)
        {
            if (VisitorShelter.Failed)
                return;
            try
            {
                Lord lord = pawn.GetLord();
                Room room = VisitorShelter.ShelterRoomOf(lord);
                Map map = pawn.Map;
                if (room == null || __result && cell.GetRoom(map) == room)
                    return;
                if (!(UsesChair(ingestible) && TryFindChairInRoom(pawn, room, out cell)))
                {
                    cell = RCellFinder.SpotToChewStandingNear(pawn, ingestible,
                        c => c.GetRoom(map) == room && pawn.CanReserveSittableOrSpot(c));
                    // Still outside the room (on the way in): eat at the shelter spot.
                    if (!cell.IsValid || cell.GetRoom(map) != room)
                        cell = pawn.GetRoom() == room ? pawn.Position : lord.CurLordToil.FlagLoc;
                }
                __result = true;
            }
            catch (Exception e)
            {
                VisitorShelter.Fail(e);
            }
        }

        // Vanilla skips chairs for food whose chairSearchRadius is 0.
        private static bool UsesChair(Thing ingestible)
        {
            ThingDef def = ingestible.def.ingestible != null ? ingestible.def
                : (ingestible as Building_NutrientPasteDispenser)?.DispensableDef;
            return def?.ingestible != null && def.ingestible.chairSearchRadius > 0f;
        }

        /// <summary>The closest chair in the room that vanilla would take, minus the distance limit.</summary>
        private static bool TryFindChairInRoom(Pawn pawn, Room room, out IntVec3 cell)
        {
            Map map = pawn.Map;
            cell = IntVec3.Invalid;
            float best = float.MaxValue;
            foreach (Thing t in room.ContainedAndAdjacentThings)
            {
                if (t.def.building == null || !t.def.building.isSittable || t.GetRoom() != room || t.IsForbidden(pawn)
                    || !t.IsSociallyProper(pawn) || t.IsBurning() || t.HostileTo(pawn) || !pawn.CanReserve(t)
                    || !Toils_Ingest.TryFindFreeSittingSpotOnThing(t, pawn, out IntVec3 spot) || !NextToTable(spot, map))
                    continue;
                float distance = (spot - pawn.Position).LengthHorizontalSquared;
                if (distance < best && pawn.CanReach(spot, PathEndMode.OnCell, Danger.Deadly))
                {
                    best = distance;
                    cell = spot;
                }
            }
            return cell.IsValid;
        }

        private static bool NextToTable(IntVec3 spot, Map map)
        {
            for (int i = 0; i < 4; i++)
            {
                Building edifice = (spot + GenAdj.CardinalDirections[i]).GetEdifice(map);
                if (edifice != null && edifice.def.surfaceType == SurfaceType.Eat)
                    return true;
            }
            return false;
        }
    }
}
