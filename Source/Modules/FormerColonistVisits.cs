using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// A former colonist who now lives with a friendly or neutral faction sits frozen among the world pawns until they
    /// die: vanilla only brings faction members back by redressing them into a pawn kind, which skips other races and
    /// xenotypes and never takes creepjoiners. When such a faction's visitors, trader caravan or travelers arrive,
    /// one of its former colonists (weighted by days spent as a colonist) may come along as they are: same kind, gear,
    /// relations and memories.
    /// </summary>
    public static class FormerColonistVisits
    {
        // Chance that a group with an eligible former colonist brings one (a field so tests can force it).
        public static float ChancePerGroup = 0.25f;

        public static bool CanVisit(Pawn p, Faction faction) =>
            p.Faction == faction
            && p.RaceProps.Humanlike
            && !p.Dead && !p.Destroyed && !p.Spawned
            && !p.Downed
            && !p.IsMutant
            && !p.IsPrisoner && !p.IsSlave
            && p.health.hediffSet.BleedRateTotal <= 0.001f
            && PawnUtility.EverBeenColonistOrTameAnimal(p)
            && Find.WorldPawns.GetSituation(p) == WorldPawnSituation.Free;

        public static float DaysAsColonist(Pawn p) => p.records.GetValue(RecordDefOf.TimeAsColonistOrColonyAnimal) / GenDate.TicksPerDay;

        public static Pawn TryPick(Faction faction)
        {
            if (faction == null || faction.IsPlayer || faction.HostileTo(Faction.OfPlayer))
                return null;
            List<Pawn> candidates = Find.WorldPawns.AllPawnsAlive.Where(p => CanVisit(p, faction)).ToList();
            if (candidates.Count == 0 || !Rand.Chance(ChancePerGroup))
                return null;
            return candidates.TryRandomElementByWeight(DaysAsColonist, out Pawn pick) ? pick : null;
        }
    }

    [HarmonyPatchCategory("FormerColonistVisits")]
    [HarmonyPatch(typeof(IncidentWorker_NeutralGroup), "SpawnPawns")]
    public static class FormerColonistVisits_SpawnPawns
    {
        public static void Postfix(IncidentParms parms, List<Pawn> __result)
        {
            if (__result == null || __result.Count == 0 || !(parms.target is Map map))
                return;
            Pawn visitor = FormerColonistVisits.TryPick(parms.faction);
            if (visitor == null)
                return;
            Find.WorldPawns.RemovePawn(visitor);
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(parms.spawnCenter, map, 5);
            GenSpawn.Spawn(visitor, cell, map);
            if (visitor.needs?.food != null)
                visitor.needs.food.CurLevel = visitor.needs.food.MaxLevel;
            __result.Add(visitor);
            parms.storeGeneratedNeutralPawns?.Add(visitor);
            Messages.Message("VTU_FormerColonistVisits_Message".Translate(visitor.Named("PAWN"), parms.faction.NameColored.Named("FACTION"),
                    FormerColonistVisits.DaysAsColonist(visitor).ToString("0").Named("DAYS")).AdjustedFor(visitor),
                visitor, MessageTypeDefOf.PositiveEvent);
        }
    }
}
