using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla doctors only rescue downed pawns of their own faction on their own (WorkGiver_RescueDowned skips the
    /// map unless one of theirs is down, and HealthAIUtility.CanRescueNow requires the same faction unless ordered).
    /// This lets colonists also rescue downed outsiders, depending on who they are:
    /// - bloodlust or psychopath: only their own people (vanilla);
    /// - kind, or an ideoligion holding charity essential or important: allies, neutral factions and the factionless;
    /// - anyone else: allies only.
    /// A trait decides before the ideoligion. Everything else stays vanilla: Doctor work, enemies within 25 cells,
    /// forbidden pawns, a free bed or sleeping spot. Rescued outsiders become guests, as when ordered by hand.
    /// </summary>
    public static class RescueOutsiders
    {
        private static PreceptDef charityEssential;
        private static PreceptDef charityImportant;
        private static bool charityLooked;

        /// <summary>Whether the colonist would rescue this downed outsider on their own.</summary>
        public static bool Allows(Pawn rescuer, Pawn patient)
        {
            if (rescuer.Faction != Faction.OfPlayer || !rescuer.RaceProps.Humanlike || !patient.RaceProps.Humanlike)
                return false;
            if (patient.Faction == rescuer.Faction || patient.IsPrisoner || patient.IsSlave)
                return false;
            Faction faction = patient.Faction;
            if (faction != null && faction.HostileTo(Faction.OfPlayer))
                return false;
            if (patient.mindState.WillJoinColonyIfRescued || QuestUtility.IsReservedByQuestOrQuestBeingGenerated(patient))
                return false;
            switch (Outlook(rescuer))
            {
                case Reach.OwnOnly:
                    return false;
                case Reach.NeutralAndAllies:
                    return true;
                default:
                    return faction != null && faction.RelationKindWith(Faction.OfPlayer) == FactionRelationKind.Ally;
            }
        }

        public enum Reach { OwnOnly, Allies, NeutralAndAllies }

        public static Reach Outlook(Pawn p)
        {
            TraitSet traits = p.story?.traits;
            if (traits != null)
            {
                if (traits.HasTrait(TraitDefOf.Bloodlust) || traits.HasTrait(TraitDefOf.Psychopath))
                    return Reach.OwnOnly;
                if (traits.HasTrait(TraitDefOf.Kind))
                    return Reach.NeutralAndAllies;
            }
            if (ModsConfig.IdeologyActive && p.Ideo != null)
            {
                if (!charityLooked)
                {
                    charityEssential = DefDatabase<PreceptDef>.GetNamedSilentFail("Charity_Essential");
                    charityImportant = DefDatabase<PreceptDef>.GetNamedSilentFail("Charity_Important");
                    charityLooked = true;
                }
                if ((charityEssential != null && p.Ideo.HasPrecept(charityEssential))
                    || (charityImportant != null && p.Ideo.HasPrecept(charityImportant)))
                    return Reach.NeutralAndAllies;
            }
            return Reach.Allies;
        }
    }

    // The work giver only looks at the map while one of the doctor's own people is down; outsiders count too now.
    [HarmonyPatchCategory("RescueOutsiders")]
    [HarmonyPatch(typeof(WorkGiver_RescueDowned), nameof(WorkGiver_RescueDowned.ShouldSkip))]
    public static class RescueOutsiders_ShouldSkip
    {
        public static void Postfix(ref bool __result, Pawn pawn)
        {
            if (!__result || pawn.Map == null)
                return;
            foreach (Pawn p in pawn.Map.mapPawns.SpawnedDownedPawns)
            {
                if (!p.InBed() && RescueOutsiders.Allows(pawn, p))
                {
                    __result = false;
                    return;
                }
            }
        }
    }

    // Unforced rescues need the same faction in vanilla; for allowed outsiders run vanilla's remaining checks.
    [HarmonyPatchCategory("RescueOutsiders")]
    [HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.CanRescueNow))]
    public static class RescueOutsiders_CanRescueNow
    {
        public static void Postfix(ref bool __result, Pawn rescuer, Pawn patient, bool forced)
        {
            if (__result || forced || patient.Faction == rescuer.Faction || !RescueOutsiders.Allows(rescuer, patient))
                return;
            __result = HealthAIUtility.WantsToBeRescued(patient)
                && !patient.IsForbidden(rescuer)
                && !GenAI.EnemyIsNear(patient, 25f)
                && rescuer.CanReserveAndReach(patient, PathEndMode.OnCell, Danger.Deadly);
        }
    }
}
