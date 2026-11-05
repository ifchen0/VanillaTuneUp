using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Prison breaks and slave rebellions with fewer all-at-once riots:
    /// - Escaping prisoners have no keys: they cannot open doors and have to break through them.
    /// - Prisoners and slaves use recreation buildings in the room of their own bed (vanilla gives them no
    ///   recreation need, so they never do), and it moves the vanilla values the break and rebellion intervals
    ///   already read: for a prisoner the "days since last prison break" (as if it had broken out 5 days ago,
    ///   x1.5 at first and back to x1 by day 10, never pulling a real recent break back), for a slave suppression
    ///   (about +10% per full session). The prisoner tab tooltip gains the line vanilla leaves out for that factor.
    /// - Vanilla pulls every prisoner of a joining cell into a break, and every slave on the map or nearby into a
    ///   rebellion. Each one now joins with chance (initiator's vanilla interval / own vanilla interval) x mood
    ///   (prisoners) x pain x traits, so pawns less inclined to break out than the one who started it (recreation, a
    ///   recent break, high suppression, a good mood, pain, Kind / Wimp / Nervous) often stay put.
    /// - The same mood, pain and trait factors divide each pawn's own break and rebellion interval, so a content,
    ///   hurting or timid pawn also starts one less often (Bloodlust, Brawler and Volatile more often).
    /// - Wardens arrest a prisoner walking off the map on its own (not part of a break), and an undrafted colonist
    ///   can be ordered to arrest a prisoner that got out of its cell (vanilla needs a drafted colonist for both).
    /// </summary>
    public static class PrisonBreak
    {
        // Vanilla's days-since-prison-break curve is 0 d x20, 5 d x1.5, 10 d x1. Day 5 is the start of the gentle part:
        // any earlier point sits on the steep part and loses a quarter of its effect within hours of the session.
        public const float CalmedDays = 5f;
        // Outside recreation hours a prisoner plays again once its break record is this old (about x1.3 left),
        // so roughly every 2 days, and a slave only once suppression is below vanilla's 30% mark.
        public const float PrisonerRetryDays = 7f;
        public const float SlaveRetrySuppression = 0.3f;
        // Suppression gained per tick of recreation: +10% over a 4000-tick session (chess, poker, television).
        public const float SuppressionPerTick = 0.1f / 4000f;

        private static readonly SimpleCurve DaysSinceBreakCurve =
            AccessTools.Field(typeof(PrisonBreakUtility), "PrisonBreakMTBFactorForDaysSincePrisonBreak")?.GetValue(null) as SimpleCurve;

        public static bool UsesRules(Pawn pawn) =>
            pawn != null && pawn.needs?.joy == null && (pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony);

        /// <summary>One recreation tick (or several, for delta): calms a prisoner or raises a slave's suppression.</summary>
        public static void NoteRecreation(Pawn pawn, int delta)
        {
            int now = Find.TickManager.TicksGame;
            if (pawn.IsPrisonerOfColony)
            {
                int calmedTick = now - Mathf.RoundToInt(CalmedDays * GenDate.TicksPerDay);
                Pawn_GuestTracker guest = pawn.guest;
                // Only ever later: a real break a day ago (x20) must not be eased by a game of chess.
                if (!guest.everParticipatedInPrisonBreak || guest.lastPrisonBreakTicks < calmedTick)
                {
                    guest.everParticipatedInPrisonBreak = true;
                    guest.lastPrisonBreakTicks = calmedTick;
                }
            }
            else if (pawn.needs.TryGetNeed(out Need_Suppression suppression))
                suppression.CurLevel += SuppressionPerTick * delta;
        }

        /// <summary>Days since the prisoner's break record (a real break or recreation), or infinity without one.</summary>
        private static float DaysSinceCalmed(Pawn pawn)
        {
            Pawn_GuestTracker guest = pawn.guest;
            if (guest == null || !guest.everParticipatedInPrisonBreak)
                return float.PositiveInfinity;
            return (Find.TickManager.TicksGame - guest.lastPrisonBreakTicks) / (float)GenDate.TicksPerDay;
        }

        /// <summary>Vanilla's days-since-prison-break factor for this prisoner, 1 when it has none.</summary>
        public static float CalmFactor(Pawn pawn)
        {
            float days = DaysSinceCalmed(pawn);
            return float.IsInfinity(days) || DaysSinceBreakCurve == null ? 1f : DaysSinceBreakCurve.Evaluate(days);
        }

        // ---------- Recreation ----------

        /// <summary>The room a prisoner or slave takes recreation in: its own bed's room, or a prisoner's current cell.</summary>
        private static Room RecreationRoom(Pawn pawn)
        {
            Building_Bed bed = pawn.ownership?.OwnedBed;
            if (bed != null && bed.Spawned && bed.Map == pawn.Map)
                return bed.GetRoom();
            return pawn.IsPrisonerOfColony ? pawn.GetRoom() : null;
        }

        private static readonly System.Func<JoyGiver_InteractBuilding, Pawn, Thing, Job> TryGivePlayJob =
            AccessTools.MethodDelegate<System.Func<JoyGiver_InteractBuilding, Pawn, Thing, Job>>(
                AccessTools.Method(typeof(JoyGiver_InteractBuilding), "TryGivePlayJob"));
        private static readonly List<Thing> tmpBuildings = new List<Thing>();
        private static Dictionary<ThingDef, List<JoyGiverDef>> giversByDef;

        // The building-use joy givers for each building def, built on first use (defs are fixed by then).
        private static List<JoyGiverDef> GiversFor(ThingDef def)
        {
            if (giversByDef == null)
            {
                giversByDef = new Dictionary<ThingDef, List<JoyGiverDef>>();
                foreach (JoyGiverDef giver in DefDatabase<JoyGiverDef>.AllDefsListForReading)
                {
                    if (!(giver.Worker is JoyGiver_InteractBuilding) || giver.thingDefs == null)
                        continue;
                    foreach (ThingDef thingDef in giver.thingDefs)
                    {
                        if (!giversByDef.TryGetValue(thingDef, out List<JoyGiverDef> list))
                            giversByDef[thingDef] = list = new List<JoyGiverDef>();
                        list.Add(giver);
                    }
                }
            }
            return giversByDef.TryGetValue(def, out List<JoyGiverDef> givers) ? givers : null;
        }

        /// <summary>
        /// A job using a recreation building in the pawn's room, picked like vanilla picks one (same joy givers, same
        /// checks) except that the room stands in for social properness: prison cell buildings are improper for slaves.
        /// </summary>
        public static Job TryGiveRecreationJob(Pawn pawn)
        {
            Room room = RecreationRoom(pawn);
            if (room == null || room.PsychologicallyOutdoors)
                return null;
            tmpBuildings.Clear();
            foreach (Thing thing in room.ContainedAndAdjacentThings)
            {
                if (thing.def.building?.joyKind != null && thing.GetRoom() == room && GiversFor(thing.def) != null && CanUse(pawn, thing))
                    tmpBuildings.Add(thing);
            }
            tmpBuildings.Shuffle();
            try
            {
                foreach (Thing building in tmpBuildings)
                {
                    foreach (JoyGiverDef giver in GiversFor(building.def))
                    {
                        var worker = (JoyGiver_InteractBuilding)giver.Worker;
                        if (!worker.CanBeGivenTo(pawn) || !CanUseWith(pawn, building, giver))
                            continue;
                        Job job = TryGivePlayJob(worker, pawn, building);
                        if (job != null)
                            return job;
                    }
                }
                return null;
            }
            finally
            {
                tmpBuildings.Clear();
            }
        }

        // Vanilla JoyGiver_InteractBuilding.CanInteractWith without the social properness check (mirror it on game
        // updates), split into the building's own checks and the ones that depend on the joy giver.
        private static bool CanUse(Pawn pawn, Thing building)
        {
            if (building.IsForbidden(pawn) || building.Fogged() || !building.IsPoliticallyProper(pawn) || building.VacuumConcernTo(pawn))
                return false;
            CompPowerTrader power = building.TryGetComp<CompPowerTrader>();
            return power == null || power.PowerOn;
        }

        private static bool CanUseWith(Pawn pawn, Thing building, JoyGiverDef giver)
        {
            if (giver.unroofedOnly && building.Position.Roofed(building.Map))
                return false;
            return pawn.CanReserveAndReach(building, PathEndMode.OnCell, Danger.Some, giver.jobDef.joyMaxParticipants);
        }

        /// <summary>
        /// Recreation priority for pawns without a recreation need: during recreation hours always; otherwise only
        /// when the value recreation pushes has run down (a prisoner's break record, a slave's suppression), so
        /// the buildings are not tied up all day and a poorly suppressed slave stops working before it rebels.
        /// Never during a mental state: binges and the like keep their own jobs.
        /// </summary>
        public static float RecreationPriority(Pawn pawn)
        {
            if (Find.TickManager.TicksGame < 5000 || pawn.InMentalState)
                return 0f;
            TimeAssignmentDef assignment = pawn.timetable?.CurrentAssignment ?? TimeAssignmentDefOf.Anything;
            if ((assignment != TimeAssignmentDefOf.Joy && assignment != TimeAssignmentDefOf.Anything) || JoyUtility.LordPreventsGettingJoy(pawn))
                return 0f;
            if (assignment == TimeAssignmentDefOf.Joy)
                return 7f;
            if (pawn.IsPrisonerOfColony)
                return DaysSinceCalmed(pawn) >= PrisonerRetryDays ? 6f : 0f;
            return pawn.needs.TryGetNeed(out Need_Suppression suppression) && suppression.CurLevelPercentage < SlaveRetrySuppression ? 6f : 0f;
        }

        // ---------- Joining a break ----------

        private static Pawn initiator;
        private static float initiatorMtb;
        private static System.Func<Pawn, float> mtbOf;
        private static bool computing;
        private static readonly Dictionary<Pawn, bool> joins = new Dictionary<Pawn, bool>();

        /// <summary>Starts a break or rebellion; mtbOf gives each pawn's interval for the join rolls.</summary>
        public static void BeginBreak(Pawn starter, System.Func<Pawn, float> intervalOf)
        {
            // Worked out before the starter is set, so the participation checks inside it pass as vanilla.
            initiatorMtb = intervalOf(starter);
            mtbOf = intervalOf;
            initiator = starter;
            joins.Clear();
        }

        public static void EndBreak()
        {
            initiator = null;
            joins.Clear();
        }

        /// <summary>Whether a pawn that vanilla would pull into the current break joins it; rolled once per break.</summary>
        public static bool Joins(Pawn pawn)
        {
            if (initiator == null || computing || pawn == initiator)
                return true;
            if (joins.TryGetValue(pawn, out bool joined))
                return joined;
            float mtb;
            computing = true;
            try { mtb = mtbOf(pawn); }
            finally { computing = false; }
            // No interval to compare (not in a cell, starter unknown): vanilla, join.
            joined = initiatorMtb <= 0f || mtb <= 0f || Rand.Chance(initiatorMtb / mtb * Inclination(pawn));
            joins[pawn] = joined;
            return joined;
        }

        // ---------- Personal inclination ----------

        // Mood, pain and traits scale both rolls: how often a pawn starts a break (its interval is divided by this)
        // and whether it joins one. The join roll compares vanilla intervals without them, so each roll counts them
        // once. Mood counts for prisoners only: vanilla's slave rebellion interval already includes it. Prisoners
        // are rarely happy, so the curve bites from the middle down. Pain is in neither vanilla interval (movement is,
        // so it is not counted again here).
        private static readonly SimpleCurve MoodCurve = new SimpleCurve
        {
            new CurvePoint(0f, 1f),
            new CurvePoint(0.5f, 0.3f),
            new CurvePoint(0.8f, 0.1f)
        };

        private static readonly SimpleCurve PainCurve = new SimpleCurve
        {
            new CurvePoint(0f, 1f),
            new CurvePoint(0.4f, 0.4f)
        };

        private static List<(TraitDef def, int degree, float factor)> traitFactors;

        private static List<(TraitDef def, int degree, float factor)> TraitFactors()
        {
            if (traitFactors == null)
            {
                TraitDef nerves = DefDatabase<TraitDef>.GetNamedSilentFail("Nerves");
                traitFactors = new List<(TraitDef, int, float)>
                {
                    (TraitDefOf.Wimp, 0, 0.5f),
                    (nerves, -1, 0.7f),     // Nervous
                    (TraitDefOf.Kind, 0, 0.7f),
                    (TraitDefOf.Bloodlust, 0, 1.5f),
                    (TraitDefOf.Brawler, 0, 1.3f),
                    (nerves, -2, 1.3f),     // Volatile
                };
                traitFactors.RemoveAll(t => t.def == null);
            }
            return traitFactors;
        }

        public static float MoodFactor(Pawn pawn) =>
            pawn.IsPrisoner && pawn.needs?.mood != null ? MoodCurve.Evaluate(pawn.needs.mood.CurLevelPercentage) : 1f;

        public static float PainFactor(Pawn pawn) => PainCurve.Evaluate(pawn.health.hediffSet.PainTotal);

        /// <summary>Product of the listed traits' factors; labels gets their names when given.</summary>
        public static float TraitFactor(Pawn pawn, List<string> labels = null)
        {
            float factor = 1f;
            TraitSet traits = pawn.story?.traits;
            if (traits == null)
                return factor;
            foreach ((TraitDef def, int degree, float traitFactor) in TraitFactors())
            {
                Trait trait = traits.GetTrait(def, degree);
                if (trait == null)
                    continue;
                factor *= traitFactor;
                labels?.Add(trait.LabelCap);
            }
            return factor;
        }

        /// <summary>Factor on a pawn's break chances from its mood (prisoners only), pain and traits.</summary>
        public static float Inclination(Pawn pawn) => MoodFactor(pawn) * PainFactor(pawn) * TraitFactor(pawn);

        /// <summary>Set while the vanilla intervals are wanted, without the inclination.</summary>
        public static bool VanillaInterval;

        private static float WithoutInclination(System.Func<float> interval)
        {
            bool was = VanillaInterval;
            VanillaInterval = true;
            try { return interval(); }
            finally { VanillaInterval = was; }
        }

        public static float PrisonBreakMtb(Pawn pawn) =>
            WithoutInclination(() => PrisonBreakUtility.InitiatePrisonBreakMtbDays(pawn, null, ignoreAsleep: true));
        public static float RebellionMtb(Pawn pawn) =>
            WithoutInclination(() => SlaveRebellionUtility.InitiateSlaveRebellionMtbDays(pawn));

        /// <summary>Appends the inclination factors that differ from 1 as interval factors, each "prefix label: x%".</summary>
        public static void AppendInclination(StringBuilder sb, Pawn pawn, string prefix, string valuePrefix = "")
        {
            void Line(string label, float factor)
            {
                if (Mathf.Approximately(factor, 1f))
                    return;
                sb.AppendLineIfNotEmpty();
                sb.Append(prefix + label + ": " + valuePrefix + (1f / factor).ToStringPercent());
            }
            Line("VTU_PrisonMood".Translate(), MoodFactor(pawn));
            Line("VTU_PrisonPain".Translate(), PainFactor(pawn));
            var labels = new List<string>();
            float traits = TraitFactor(pawn, labels);
            if (labels.Count > 0)
                Line("VTU_PrisonTraits".Translate() + " (" + string.Join(", ", labels) + ")", traits);
        }
    }

    // ---------- No keys ----------

    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]
    public static class PrisonBreak_NoKeys
    {
        public static void Postfix(Pawn p, ref bool __result)
        {
            if (__result && p.IsPrisonerOfColony && PrisonBreakUtility.IsPrisonBreaking(p))
                __result = false;
        }
    }

    // ---------- Recapture ----------

    /// <summary>
    /// Wardens arrest a prisoner walking off the map on its own (woke up outside after field tending, found an open
    /// door): vanilla counts such a prisoner as not secure, so no warden work touches it until someone downs it.
    /// Prisoners in a break (any lord) are left to drafted colonists, since they fight back. The arrest job is
    /// vanilla's, resistance roll (berserk on failure) included.
    /// </summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(WorkGiver_Warden_TakeToBed), nameof(WorkGiver_Warden_TakeToBed.JobOnThing))]
    public static class PrisonBreak_RecaptureEscapee
    {
        public static bool EscapingAlone(Pawn prisoner) =>
            prisoner.IsPrisonerOfColony && prisoner.Spawned && !prisoner.Downed && !prisoner.guest.Released
            && !prisoner.InAggroMentalState && prisoner.CurJob != null && prisoner.CurJob.exitMapOnArrival
            && prisoner.GetLord() == null;

        public static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            if (__result != null || !(t is Pawn prisoner) || !EscapingAlone(prisoner)
                || prisoner.IsForbidden(pawn) || !prisoner.CanBeArrestedBy(pawn)
                || !pawn.CanReserveAndReach(prisoner, PathEndMode.OnCell, pawn.NormalMaxDanger(), 1, -1, null, forced))
                return;
            Building_Bed bed = RestUtility.FindBedFor(prisoner, pawn, checkSocialProperness: false, ignoreOtherReservations: false, GuestStatus.Prisoner);
            if (bed == null)
            {
                JobFailReason.Is("NoPrisonerBedShort".Translate());
                return;
            }
            Job job = JobMaker.MakeJob(JobDefOf.Arrest, prisoner, bed);
            job.count = 1;
            __result = job;
        }
    }

    /// <summary>
    /// The right-click arrest option for an undrafted colonist also covers the colony's own prisoners (vanilla
    /// offers it undrafted for wild men only). Prisoners still inside a prison cell are excluded by vanilla's
    /// CanBeArrestedBy, so this only reaches ones that got out.
    /// </summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(FloatMenuOptionProvider_Arrest), "GetSingleOptionFor", typeof(Pawn), typeof(FloatMenuContext))]
    public static class PrisonBreak_ArrestUndrafted
    {
        public static bool DraftedOrOwnPrisoner(Pawn arrester, Pawn clicked) =>
            arrester.Drafted || clicked.IsPrisonerOfColony;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            System.Reflection.MethodInfo drafted = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Drafted));
            bool patched = false;
            foreach (CodeInstruction code in instructions)
            {
                if (!patched && code.Calls(drafted))
                {
                    // Stack holds the arrester; clickedPawn is argument 1 (the closure copy leaves it untouched).
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_1).WithLabels(code.ExtractLabels());
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call,
                        AccessTools.Method(typeof(PrisonBreak_ArrestUndrafted), nameof(DraftedOrOwnPrisoner)));
                    patched = true;
                    continue;
                }
                yield return code;
            }
            if (!patched)
                Log.Warning("[Vanilla Tune-Up] PrisonBreak: drafted check not found in the arrest menu option; escaped prisoners still need a drafted colonist to arrest them.");
        }
    }

    // ---------- Recreation ----------

    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(ThinkNode_Priority_GetJoy), nameof(ThinkNode_Priority_GetJoy.GetPriority))]
    public static class PrisonBreak_JoyPriority
    {
        public static void Postfix(Pawn pawn, ref float __result)
        {
            if (__result <= 0f && PrisonBreak.UsesRules(pawn))
                __result = PrisonBreak.RecreationPriority(pawn);
        }
    }

    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(JobGiver_GetJoy), "TryGiveJob")]
    public static class PrisonBreak_GiveJoyJob
    {
        public static bool Prefix(JobGiver_GetJoy __instance, Pawn pawn, ref Job __result)
        {
            if (!PrisonBreak.UsesRules(pawn))
                return true;
            // The in-bed variant would only ever pick from bed; these pawns get up for their room's buildings instead.
            __result = __instance is JobGiver_GetJoyInBed || (pawn.InBed() && HealthAIUtility.ShouldSeekMedicalRest(pawn))
                ? null
                : PrisonBreak.TryGiveRecreationJob(pawn);
            return false;
        }
    }

    /// <summary>
    /// Vanilla ends a recreation job at once when the pawn has no recreation need. Keep it running for prisoners
    /// and slaves, note the session, and still end it when recreation hours are over.
    /// </summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(JoyUtility), nameof(JoyUtility.JoyTickCheckEnd))]
    public static class PrisonBreak_JoyTick
    {
        public static bool Prefix(Pawn pawn, int delta, ref bool __result)
        {
            Job job = pawn.CurJob;
            if (job?.def.joyKind == null || job.doUntilGatheringEnded || !PrisonBreak.UsesRules(pawn))
                return true;
            PrisonBreak.NoteRecreation(pawn, delta);
            if (job.def.joySkill != null)
                pawn.skills?.GetSkill(job.def.joySkill).Learn(job.def.joyXpPerTick * delta);
            // The rest mirrors vanilla's tail: end the job once recreation hours are over.
            __result = !job.ignoreJoyTimeAssignment && !pawn.GetTimeAssignment().allowJoy;
            if (__result)
                pawn.jobs.curDriver.EndJobWith(JobCondition.InterruptForced);
            return false;
        }
    }

    /// <summary>
    /// Divides the prisoner's break interval by its inclination (mood, pain, traits) and lists those factors in the
    /// prisoner tab tooltip, together with the days-since-prison-break factor vanilla applies without listing it
    /// (recreation now drives it too).
    /// </summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.InitiatePrisonBreakMtbDays))]
    public static class PrisonBreak_PrisonerInterval
    {
        public static void Postfix(Pawn pawn, StringBuilder sb, ref float __result)
        {
            if (__result <= 0f || PrisonBreak.VanillaInterval)
                return;
            if (sb != null)
            {
                float calm = PrisonBreak.CalmFactor(pawn);
                if (!Mathf.Approximately(calm, 1f))
                {
                    sb.AppendLineIfNotEmpty();
                    sb.Append("  - " + "VTU_PrisonCalmedDown".Translate() + ": " + calm.ToStringPercent());
                }
                PrisonBreak.AppendInclination(sb, pawn, "  - ");
            }
            __result /= PrisonBreak.Inclination(pawn);
        }
    }

    /// <summary>The slave's rebellion interval divided by its pain and traits (vanilla already counts its mood).</summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(SlaveRebellionUtility), nameof(SlaveRebellionUtility.InitiateSlaveRebellionMtbDays))]
    public static class PrisonBreak_SlaveInterval
    {
        public static void Postfix(Pawn pawn, ref float __result)
        {
            if (__result > 0f && !PrisonBreak.VanillaInterval)
                __result /= PrisonBreak.Inclination(pawn);
        }
    }

    /// <summary>Lists the slave's pain and trait factors above the final interval in the suppression tooltip.</summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(SlaveRebellionUtility), nameof(SlaveRebellionUtility.GetSlaveRebellionMtbCalculationExplanation))]
    public static class PrisonBreak_SlaveTooltip
    {
        public static void Postfix(Pawn pawn, ref string __result)
        {
            int last = __result.LastIndexOf('\n');
            if (last < 0)
                return;
            var sb = new StringBuilder();
            PrisonBreak.AppendInclination(sb, pawn, "", "x");
            if (sb.Length == 0)
                return;
            // Vanilla writes these lines as "label: x150%".
            __result = __result.Substring(0, last + 1) + sb + "\n" + __result.Substring(last + 1);
        }
    }

    // ---------- Joining a break ----------

    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.StartPrisonBreak),
        new[] { typeof(Pawn), typeof(string), typeof(string), typeof(LetterDef), typeof(List<Pawn>) },
        new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out })]
    public static class PrisonBreak_StartPrisonBreak
    {
        public static void Prefix(Pawn initiator) => PrisonBreak.BeginBreak(initiator, PrisonBreak.PrisonBreakMtb);
        public static void Finalizer() => PrisonBreak.EndBreak();
    }

    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.CanParticipateInPrisonBreak))]
    public static class PrisonBreak_CanParticipate
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result)
                __result = PrisonBreak.Joins(pawn);
        }
    }

    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(SlaveRebellionUtility), nameof(SlaveRebellionUtility.StartSlaveRebellion),
        new[] { typeof(Pawn), typeof(string), typeof(string), typeof(LetterDef), typeof(LookTargets), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out, ArgumentType.Normal })]
    public static class PrisonBreak_StartRebellion
    {
        public static void Prefix(Pawn initiator) => PrisonBreak.BeginBreak(initiator, PrisonBreak.RebellionMtb);
        public static void Finalizer() => PrisonBreak.EndBreak();
    }

    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(SlaveRebellionUtility), nameof(SlaveRebellionUtility.CanParticipateInSlaveRebellion))]
    public static class PrisonBreak_CanRebel
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result)
                __result = PrisonBreak.Joins(pawn);
        }
    }
}

namespace VanillaTuneUp
{
    /// <summary>
    /// Prisoner tab break interval, display only: long intervals show as "10 years or more" (right after a break the
    /// multiplied factors give thousands of days that keep jumping as wounds heal and the cooldown wears off), and
    /// the tooltip keeps a fixed id. Vanilla's id is the text's hash, so every change of the listed factors closed
    /// the tooltip and reopened it after the hover delay; with a fixed id it stays open and updates in place.
    /// </summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(ITab_Pawn_Visitor), "DoPrisonerTab")]
    public static class PrisonBreak_LongIntervalText
    {
        public const int LongIntervalDays = 600;    // 10 years

        private static readonly System.Reflection.MethodInfo TranslateNamed = AccessTools.Method(
            typeof(TranslatorFormattedStringExtensions), nameof(TranslatorFormattedStringExtensions.Translate),
            new[] { typeof(string), typeof(NamedArgument) });

        private static readonly System.Reflection.MethodInfo TipFromString =
            AccessTools.Method(typeof(TipSignal), "op_Implicit", new[] { typeof(string) });
        private static readonly int TipId = "VTU_PrisonBreakIntervalTip".GetHashCode();

        public static TaggedString IntervalText(string key, NamedArgument days) =>
            days.arg is int value && value >= LongIntervalDays ? "VTU_PrisonBreakLongInterval".Translate() : key.Translate(days);

        public static TipSignal StableTip(string text) => new TipSignal(text ?? "", TipId);

        public static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
        {
            bool afterKey = false, patched = false, afterTip = false, tipPatched = false;
            foreach (CodeInstruction code in instructions)
            {
                if (code.opcode == System.Reflection.Emit.OpCodes.Ldstr && code.operand as string == "PeriodDays")
                    afterKey = true;
                else if (code.opcode == System.Reflection.Emit.OpCodes.Ldstr && code.operand as string == "PrisonBreakMTBDaysDescription")
                    afterTip = true;
                else if (afterKey && !patched && code.Calls(TranslateNamed))
                {
                    code.opcode = System.Reflection.Emit.OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(PrisonBreak_LongIntervalText), nameof(IntervalText));
                    patched = true;
                }
                else if (afterTip && !tipPatched && code.Calls(TipFromString))
                {
                    code.operand = AccessTools.Method(typeof(PrisonBreak_LongIntervalText), nameof(StableTip));
                    tipPatched = true;
                }
                yield return code;
            }
            if (!patched)
                Log.Warning("[Vanilla Tune-Up] PrisonBreak: break interval text not found in ITab_Pawn_Visitor.DoPrisonerTab; long intervals show in days.");
            if (!tipPatched)
                Log.Warning("[Vanilla Tune-Up] PrisonBreak: break interval tooltip not found in ITab_Pawn_Visitor.DoPrisonerTab; it may flicker while its factors change.");
        }
    }
}
