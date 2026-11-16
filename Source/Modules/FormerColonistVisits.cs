using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla only brings existing faction members back into play by redressing them into a pawn kind, which needs the
    /// kind's race and one of the xenotypes that faction/kind generates, and never takes creepjoiners. Members who fail
    /// those checks (a Miho living with a human tribe, a sanguophage among pirates, a creepjoiner) sit frozen among the
    /// world pawns until they die; former colonists are only picked at random among hundreds of world pawns.
    /// Such members ("outsiders": structurally unredressable members plus every former colonist) now come along as they
    /// are, same kind, gear, relations and memories, in three places:
    /// - visitors, trader caravans and travelers of a non-hostile faction: one healthy outsider, 25% per group;
    /// - raids: one healthy outsider able to fight, 25% per raid, joining the raid's lord;
    /// - a faction settlement map when it is generated: every outsider rolls 25%, downed ones included (they can be
    ///   rescued or captured), joining a defender's lord.
    /// The pick is weighted: former colonists by days spent as a colonist (1 to 60), others 1.
    /// Other mods can call <see cref="TryPickFighter"/> to apply the raid rule to their own battles (Dynamic Diplomacy
    /// Fixes &amp; Tweaks does so for faction battles).
    /// </summary>
    public static class FormerColonistVisits
    {
        public enum Use { Visit, Fight, Home }

        // Chance per group (visit/raid) or per member (home); a field so tests can force it.
        public static float ChancePerGroup = 0.25f;

        public const float MaxWeightDays = 60f;

        // Set when the module's patches are applied; other mods read it through TryPickFighter.
        public static bool Active;

        public static void Activate() => Active = true;

        public static float DaysAsColonist(Pawn p) => p.records.GetValue(RecordDefOf.TimeAsColonistOrColonyAnimal) / GenDate.TicksPerDay;

        public static float Weight(Pawn p) =>
            PawnUtility.EverBeenColonistOrTameAnimal(p) ? Mathf.Clamp(DaysAsColonist(p), 1f, MaxWeightDays) : 1f;

        /// <summary>A member vanilla can never redress into any of the faction's pawn kinds, or a former colonist.</summary>
        public static bool IsOutsider(Pawn p, Faction faction)
        {
            if (PawnUtility.EverBeenColonistOrTameAnimal(p))
                return true;
            if (p.IsCreepJoiner)
                return true;
            // Redressable when some kind of the faction has this race and generates this xenotype (the xenotype the
            // group maker picks per slot is passed on as a forced xenotype).
            foreach (PawnKindDef kind in FactionKinds(faction.def))
            {
                if (kind.race != p.def)
                    continue;
                if (!ModsConfig.BiotechActive || p.genes == null || PawnGenerator.XenotypesAvailableFor(kind, null, faction).ContainsKey(p.genes.Xenotype))
                    return false;
            }
            return true;
        }

        private static IEnumerable<PawnKindDef> FactionKinds(FactionDef def)
        {
            if (def.basicMemberKind != null)
                yield return def.basicMemberKind;
            if (def.pawnGroupMakers == null)
                yield break;
            foreach (PawnGroupMaker maker in def.pawnGroupMakers)
            {
                foreach (List<PawnGenOption> options in new[] { maker.options, maker.traders, maker.carriers, maker.guards })
                {
                    if (options == null)
                        continue;
                    foreach (PawnGenOption option in options)
                        if (option.kind != null)
                            yield return option.kind;
                }
            }
        }

        // In good shape for travelling or fighting: walking, seeing to things, wounds healed (scars and lost parts are fine).
        public static bool Healthy(Pawn p)
        {
            if (p.Downed)
                return false;
            PawnCapacitiesHandler c = p.health.capacities;
            if (c.GetLevel(PawnCapacityDefOf.Consciousness) < 0.8f || c.GetLevel(PawnCapacityDefOf.Moving) < 0.8f
                || c.GetLevel(PawnCapacityDefOf.Manipulation) < 0.5f)
                return false;
            foreach (Hediff h in p.health.hediffSet.hediffs)
                if (h is Hediff_Injury && !h.IsPermanent())
                    return false;
            return true;
        }

        public static bool CanFight(Pawn p) =>
            p.DevelopmentalStage.Adult()
            && !p.WorkTagIsDisabled(WorkTags.Violent)
            && (!ModsConfig.BiotechActive || p.genes?.Xenotype == null || p.genes.Xenotype.canGenerateAsCombatant)
            && (!ModsConfig.BiotechActive || !p.health.hediffSet.HasHediff(HediffDefOf.PregnantHuman));

        public static bool Eligible(Pawn p, Faction faction, Use use)
        {
            if (p.Faction != faction || !p.RaceProps.Humanlike
                || p.Dead || p.Destroyed || p.Spawned
                || p.IsMutant || p.IsPrisoner || p.IsSlave
                || p.health.hediffSet.GetBrain() == null
                || p.health.hediffSet.BleedRateTotal > 0.001f
                || Find.WorldPawns.GetSituation(p) != WorldPawnSituation.Free)
                return false;
            if (use != Use.Home && !Healthy(p))
                return false;
            if (use == Use.Fight && !CanFight(p))
                return false;
            return IsOutsider(p, faction);
        }

        public static List<Pawn> Candidates(Faction faction, Use use) =>
            faction == null || faction.IsPlayer
                ? new List<Pawn>()
                : Find.WorldPawns.AllPawnsAlive.Where(p => Eligible(p, faction, use)).ToList();

        /// <summary>One outsider for a group (visit or fight), after the per-group chance; null when none.</summary>
        public static Pawn TryPickOne(Faction faction, Use use)
        {
            List<Pawn> candidates = Candidates(faction, use);
            if (candidates.Count == 0 || !Rand.Chance(ChancePerGroup))
                return null;
            return candidates.TryRandomElementByWeight(Weight, out Pawn pick) ? pick : null;
        }

        /// <summary>
        /// For other mods' battles: one outsider of <paramref name="faction"/> able to fight, after the 25% roll, or null
        /// (also when this module is off). Still a world pawn: the caller removes it and spawns it with its group, or
        /// <see cref="Spawn"/> does both.
        /// </summary>
        public static Pawn TryPickFighter(Faction faction) => Active ? TryPickOne(faction, Use.Fight) : null;

        /// <summary>Takes the pawn out of the world pawns and spawns it near <paramref name="center"/>.</summary>
        public static void Spawn(Pawn p, IntVec3 center, Map map)
        {
            Find.WorldPawns.RemovePawn(p);
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(center, map, 5);
            GenSpawn.Spawn(p, cell, map);
            if (p.needs?.food != null)
                p.needs.food.CurLevel = p.needs.food.MaxLevel;
        }

        // Former colonists get a message; other outsiders are just members of the group.
        public static void Announce(Pawn p, Faction faction, MessageTypeDef type)
        {
            if (!PawnUtility.EverBeenColonistOrTameAnimal(p))
                return;
            Messages.Message("VTU_FormerColonistVisits_Message".Translate(p.Named("PAWN"), faction.NameColored.Named("FACTION"),
                    DaysAsColonist(p).ToString("0").Named("DAYS")).AdjustedFor(p),
                p, type);
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
            if (parms.faction == null || parms.faction.HostileTo(Faction.OfPlayer))
                return;
            Pawn visitor = FormerColonistVisits.TryPickOne(parms.faction, FormerColonistVisits.Use.Visit);
            if (visitor == null)
                return;
            FormerColonistVisits.Spawn(visitor, parms.spawnCenter, map);
            __result.Add(visitor);
            parms.storeGeneratedNeutralPawns?.Add(visitor);
            FormerColonistVisits.Announce(visitor, parms.faction, MessageTypeDefOf.PositiveEvent);
        }
    }

    /// <summary>
    /// Raids: after the raiders are generated and have arrived, but before the letter and the lord, one outsider joins
    /// them, so the letter lists them and they follow the raid's strategy.
    /// </summary>
    [HarmonyPatchCategory("FormerColonistVisits")]
    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    public static class FormerColonistVisits_Raid
    {
        public static void Postfix(IncidentParms parms, List<Pawn> pawns, bool debugTest, bool __result)
        {
            if (!__result || debugTest || pawns == null || pawns.Count == 0 || !(parms.target is Map map))
                return;
            if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer))
                return;
            Pawn raider = FormerColonistVisits.TryPickOne(parms.faction, FormerColonistVisits.Use.Fight);
            if (raider == null)
                return;
            Pawn anchor = pawns.Where(p => p.Spawned && p.Map == map).RandomElementWithFallback();
            if (anchor != null)
            {
                FormerColonistVisits.Spawn(raider, anchor.Position, map);
            }
            else if (parms.spawnCenter.IsValid && parms.spawnCenter.InBounds(map))
            {
                // Drop-pod raids: the raiders are still in their pods; come down beside them.
                Find.WorldPawns.RemovePawn(raider);
                DropPodUtility.DropThingsNear(parms.spawnCenter, map, new Thing[] { raider }, canRoofPunch: false, forbid: false, faction: parms.faction);
                anchor = pawns[0];
            }
            else
            {
                return;
            }
            // Walk-in-groups raids split the lord by group: stay with the raider we arrived beside.
            if (parms.pawnGroups != null && parms.pawnGroups.TryGetValue(anchor, out int group))
                parms.pawnGroups[raider] = group;
            pawns.Add(raider);
            FormerColonistVisits.Announce(raider, parms.faction, MessageTypeDefOf.ThreatSmall);
        }
    }

    /// <summary>
    /// A faction settlement's map, once generated (vanilla or a base-generation mod alike): each outsider rolls the
    /// chance and, if picked, is placed beside a random defender and joins that defender's lord. Downed members may
    /// appear too and lie where they are, so they can be rescued or captured.
    /// </summary>
    [HarmonyPatchCategory("FormerColonistVisits")]
    [HarmonyPatch(typeof(Settlement), nameof(Settlement.PostMapGenerate))]
    public static class FormerColonistVisits_Settlement
    {
        public static void Postfix(Settlement __instance)
        {
            Map map = __instance.Map;
            Faction faction = __instance.Faction;
            if (map == null || faction == null || faction.IsPlayer)
                return;
            List<Pawn> picks = FormerColonistVisits.Candidates(faction, FormerColonistVisits.Use.Home)
                .Where(_ => Rand.Chance(FormerColonistVisits.ChancePerGroup)).ToList();
            if (picks.Count == 0)
                return;
            List<Pawn> defenders = map.mapPawns.SpawnedPawnsInFaction(faction).Where(p => p.RaceProps.Humanlike).ToList();
            if (defenders.Count == 0)
                return;
            foreach (Pawn p in picks)
            {
                Pawn anchor = defenders.RandomElement();
                FormerColonistVisits.Spawn(p, anchor.Position, map);
                anchor.GetLord()?.AddPawn(p);
                FormerColonistVisits.Announce(p, faction, MessageTypeDefOf.NeutralEvent);
            }
        }
    }
}
