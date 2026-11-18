using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla visitor groups carry off downed guests of their own faction (LordJob_VisitColony, TakeWoundedGuest),
    /// but a given faction only visits now and then (about 4 visitor groups a year, shared by every friendly faction),
    /// so a guest who cannot recover may lie in the colony for good. Once a guest has been downed on a player home map
    /// for the vanilla visitor interval (15 days), this sends a visitor group of the guest's faction; vanilla does the
    /// rest. Factions that cannot send visitors (hostile, hidden, no peaceful group) and factionless guests are left
    /// as they are.
    /// </summary>
    public static class WoundedGuestPickup
    {
        public const int WaitTicks = 15 * GenDate.TicksPerDay;
        private const int CheckInterval = GenDate.TicksPerHour;

        // Downed guest -> tick since which it has been downed on a home map. Rebuilt on every check, saved with the
        // guest tracker, so it survives save and load.
        public static Dictionary<Pawn, int> downedSince = new Dictionary<Pawn, int>();

        public static bool IsWaiting(Pawn p)
        {
            return p.RaceProps.Humanlike && p.guest != null && p.HostFaction == Faction.OfPlayer && !p.IsPrisoner
                && !p.IsSlave && p.Faction != null && p.Faction != Faction.OfPlayer && p.Downed && !p.Dead
                && !QuestUtility.IsReservedByQuestOrQuestBeingGenerated(p);
        }

        public static void Tick()
        {
            if (Find.TickManager.TicksGame % CheckInterval == 0)
                Check();
        }

        public static void Check()
        {
            int now = Find.TickManager.TicksGame;
            var next = new Dictionary<Pawn, int>();
            var due = new List<(Map map, Faction faction)>();
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome)
                    continue;
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (!IsWaiting(p))
                        continue;
                    int since = downedSince.TryGetValue(p, out int t) ? t : now;
                    next[p] = since;
                    if (now - since >= WaitTicks && !due.Contains((map, p.Faction)))
                        due.Add((map, p.Faction));
                }
            }
            downedSince = next;
            foreach ((Map map, Faction faction) in due)
            {
                if (VisitorsPresent(map, faction))
                    continue;
                if (!TrySendVisitors(map, faction))
                    continue;
                // Next try only after another full wait, should these visitors leave without them.
                foreach (Pawn p in downedSince.Keys.ToList())
                {
                    if (p.Map == map && p.Faction == faction)
                        downedSince[p] = now;
                }
            }
        }

        private static bool VisitorsPresent(Map map, Faction faction)
        {
            return map.lordManager.lords.Any(l => l.faction == faction && l.LordJob is LordJob_VisitColony);
        }

        public static bool TrySendVisitors(Map map, Faction faction)
        {
            IncidentDef def = IncidentDefOf.VisitorGroup;
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.faction = faction;
            parms.forced = true;
            if (def.Worker is IncidentWorker_PawnsArrive worker && !worker.FactionCanBeGroupSource(faction, parms))
                return false;
            return def.Worker.TryExecute(parms);
        }
    }

    [HarmonyPatchCategory("WoundedGuestPickup")]
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class WoundedGuestPickup_Tick
    {
        public static void Postfix()
        {
            WoundedGuestPickup.Tick();
        }
    }

    // The downed-since tick rides along in the guest tracker; a save loaded without the mod just ignores the node.
    [HarmonyPatchCategory("WoundedGuestPickup")]
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.ExposeData))]
    public static class WoundedGuestPickup_ExposeData
    {
        public static void Postfix(Pawn ___pawn)
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (WoundedGuestPickup.downedSince.TryGetValue(___pawn, out int since))
                    Scribe_Values.Look(ref since, "vtuDownedSince", int.MinValue);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                int since = int.MinValue;
                Scribe_Values.Look(ref since, "vtuDownedSince", int.MinValue);
                if (since != int.MinValue)
                    WoundedGuestPickup.downedSince[___pawn] = since;
            }
        }
    }
}
