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
    /// <para>
    /// Doctors also stop the bleeding of downed non-hostile outsiders where they lie, without medicine, so the ones
    /// nobody rescues do not bleed to death. Any doctor but a bloodlust or psychopath one does this; the tend ends
    /// once the bleeding stops and the rest is vanilla (they recover or not where they lie).
    /// </para>
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

        /// <summary>
        /// A downed, bleeding, non-hostile outsider lying outside a bed that nobody looks after: not a guest, prisoner
        /// or slave (HostFaction null), not a wild man. Vanilla never tends them (no player settings, no host).
        /// Cheap checks first: tend scans ask this for every pawn with any hediff, for every doctor.
        /// </summary>
        public static bool BleedingOutsider(Pawn patient)
        {
            if (!patient.Spawned || !patient.Downed || !patient.RaceProps.Humanlike)
                return false;
            if (patient.Faction == Faction.OfPlayer || patient.HostFaction != null || patient.IsSlave || patient.IsWildMan())
                return false;
            if (patient.health.hediffSet.BleedRateTotal <= 0f || patient.InBed())
                return false;
            Faction faction = patient.Faction;
            if (faction != null && faction.HostileTo(Faction.OfPlayer))
                return false;
            return !patient.IsMutant && HealthAIUtility.ShouldEverReceiveMedicalCareFromPlayer(patient)
                && patient.health.HasHediffsNeedingTend();
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

    // Bleeding outsiders count as needing the player's tending. This also keeps vanilla's tend job going until the
    // bleeding stops (its end condition asks the same question) and lets the urgent tend work giver pick them.
    [HarmonyPatchCategory("RescueOutsiders")]
    [HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.ShouldBeTendedNowByPlayer))]
    public static class RescueOutsiders_ShouldBeTendedNowByPlayer
    {
        public static void Postfix(ref bool __result, Pawn pawn)
        {
            if (!__result && RescueOutsiders.BleedingOutsider(pawn))
                __result = true;
        }
    }

    // Vanilla's tend scan also wants a humanlike patient in bed. For a bleeding outsider: any doctor but a bloodlust
    // or psychopath one, not while a threat to the doctor is within 25 cells, not while the patient burns.
    [HarmonyPatchCategory("RescueOutsiders")]
    [HarmonyPatch(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.HasJobOnThing))]
    public static class RescueOutsiders_TendHasJob
    {
        public static void Postfix(ref bool __result, Pawn pawn, Thing t, bool forced)
        {
            if (!(t is Pawn patient) || patient == pawn || !RescueOutsiders.BleedingOutsider(patient))
                return;
            __result = pawn.Faction == Faction.OfPlayer
                && RescueOutsiders.Outlook(pawn) != RescueOutsiders.Reach.OwnOnly
                && !patient.IsBurning()
                && !(patient.InAggroMentalState && !patient.health.hediffSet.HasHediff(HediffDefOf.Scaria))
                && pawn.CanReserve(patient, 1, -1, null, forced)
                && !AutoCapture.ThreatNear(pawn, patient, 25f);
        }
    }

    // No medicine for them: vanilla hands over any carried medicine to a patient without player settings.
    // A tend ordered by hand keeps vanilla's choice.
    [HarmonyPatchCategory("RescueOutsiders")]
    [HarmonyPatch(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.JobOnThing))]
    public static class RescueOutsiders_TendJob
    {
        public static void Postfix(ref Job __result, Thing t, bool forced)
        {
            if (forced || __result == null || __result.targetB.Thing == null || !(t is Pawn patient)
                || !RescueOutsiders.BleedingOutsider(patient))
                return;
            __result = JobMaker.MakeJob(JobDefOf.TendPatient, patient);
        }
    }
}
