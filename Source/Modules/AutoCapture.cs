using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Wardens capture downed enemies on their own, as long as a prisoner bed is free: the number of free prisoner
    /// beds is the only control. When there are fewer free beds than downed enemies, the most valuable ones (pawn
    /// market value: skills, traits, age, health) are taken first. Only humanlike members of hostile factions that
    /// vanilla lets you capture are taken; the usual rescue checks apply (no enemies within 25 cells, allowed area,
    /// reachable). Uses vanilla's capture job, so it is the same as ordering it by hand.
    /// </summary>
    public static class AutoCapture
    {
        private static TuneUpModule module;
        public static bool Enabled => (module ??= Modules.Get("AutoCapture")).IsActive;

        private static Map cachedMap;
        private static int cachedTick = -1;
        private static readonly HashSet<Pawn> cachedChosen = new HashSet<Pawn>();

        public static bool IsTarget(Pawn p)
        {
            return p.RaceProps.Humanlike && p.Spawned && p.Downed && !p.Dead && !p.InBed()
                && p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer)
                && !p.IsPrisonerOfColony && p.CanBeCaptured();
        }

        /// <summary>
        /// Free sleeping slots in prisoner beds that no one has reserved. Like vanilla, only beds inside a prison
        /// cell count; a prisoner bed in the open is never offered to a captive.
        /// </summary>
        public static int FreePrisonerSlots(Map map)
        {
            int free = 0;
            foreach (Building_Bed bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
            {
                if (!bed.ForPrisoners || bed.Medical || !bed.Spawned || bed.GetRoom()?.IsPrisonCell != true)
                    continue;
                if (map.reservationManager.IsReservedByAnyoneOf(bed, Faction.OfPlayer))
                    continue;
                int used = System.Math.Max(bed.OwnersForReading.Count, bed.CurOccupants.Count());
                free += System.Math.Max(0, bed.SleepingSlotsCount - used);
            }
            return free;
        }

        /// <summary>
        /// Downed enemies that get a bed, most valuable first, as many as there are free prisoner slots.
        /// Recomputed at most once per tick per map.
        /// </summary>
        public static bool IsChosen(Pawn p)
        {
            Map map = p.Map;
            int now = Find.TickManager.TicksGame;
            if (map != cachedMap || now != cachedTick)
            {
                cachedMap = map;
                cachedTick = now;
                cachedChosen.Clear();
                int slots = FreePrisonerSlots(map);
                if (slots > 0)
                {
                    foreach (Pawn t in map.mapPawns.SpawnedDownedPawns.Where(IsTarget)
                                 .OrderByDescending(t => t.MarketValue).Take(slots))
                        cachedChosen.Add(t);
                }
            }
            return cachedChosen.Contains(p);
        }

        /// <summary>
        /// Whether something that threatens the warden is within the radius of the target. GenAI.EnemyIsNear looks
        /// from the given pawn's side, which for an enemy target would count the colonists themselves.
        /// </summary>
        public static bool ThreatNear(Pawn warden, Pawn target, float radius)
        {
            Map map = target.Map;
            bool fogged = target.Position.Fogged(map);
            foreach (IAttackTarget threat in map.attackTargetsCache.GetPotentialTargetsFor(warden))
            {
                if (threat.ThreatDisabled(warden) || (!fogged && threat.Thing.Position.Fogged(map)))
                    continue;
                if (target.Position.InHorDistOf(threat.Thing.Position, radius))
                    return true;
            }
            return false;
        }

        public static bool AnyTarget(Map map)
        {
            foreach (Pawn p in map.mapPawns.SpawnedDownedPawns)
            {
                if (IsTarget(p))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Warden work: capture a chosen downed enemy into a free prisoner bed.</summary>
    public class WorkGiver_AutoCapture : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Pawn);

        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => pawn.Map.mapPawns.SpawnedDownedPawns;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !AutoCapture.Enabled || pawn.Faction != Faction.OfPlayer || !AutoCapture.AnyTarget(pawn.Map);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Pawn target) || !AutoCapture.IsTarget(target) || !AutoCapture.IsChosen(target))
                return false;
            if (target.IsForbidden(pawn) || AutoCapture.ThreatNear(pawn, target, 25f))
                return false;
            if (!pawn.CanReserveAndReach(target, PathEndMode.OnCell, Danger.Deadly, 1, -1, null, forced))
                return false;
            Building_Bed bed = FindBed(pawn, target);
            return bed != null && target.CanReserve(bed);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Pawn target = (Pawn)t;
            Building_Bed bed = FindBed(pawn, target);
            if (bed == null)
                return null;
            Job job = JobMaker.MakeJob(JobDefOf.Capture, target, bed);
            job.count = 1;
            return job;
        }

        private static Building_Bed FindBed(Pawn warden, Pawn target) =>
            RestUtility.FindBedFor(target, warden, checkSocialProperness: false, ignoreOtherReservations: false, GuestStatus.Prisoner);
    }
}
