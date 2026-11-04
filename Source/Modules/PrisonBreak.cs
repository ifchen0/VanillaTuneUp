using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

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
    ///   rebellion. Each one now joins with chance (initiator's interval / own interval), so pawns less inclined to
    ///   break out than the one who started it (recreation, a recent break, high suppression) often stay put.
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
            joined = initiatorMtb <= 0f || mtb <= 0f || Rand.Chance(initiatorMtb / mtb);
            joins[pawn] = joined;
            return joined;
        }

        public static float PrisonBreakMtb(Pawn pawn) => PrisonBreakUtility.InitiatePrisonBreakMtbDays(pawn, null, ignoreAsleep: true);
        public static float RebellionMtb(Pawn pawn) => SlaveRebellionUtility.InitiateSlaveRebellionMtbDays(pawn);
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
    /// Vanilla applies the days-since-prison-break factor without listing it in the prisoner tab tooltip; recreation
    /// now drives it too, so show it. Display only, the interval is unchanged.
    /// </summary>
    [HarmonyPatchCategory("PrisonBreak")]
    [HarmonyPatch(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.InitiatePrisonBreakMtbDays))]
    public static class PrisonBreak_CalmTooltip
    {
        public static void Postfix(Pawn pawn, StringBuilder sb, float __result)
        {
            if (sb == null || __result <= 0f)
                return;
            float factor = PrisonBreak.CalmFactor(pawn);
            if (Mathf.Approximately(factor, 1f))
                return;
            sb.AppendLineIfNotEmpty();
            sb.Append("  - " + "VTU_PrisonCalmedDown".Translate() + ": " + factor.ToStringPercent());
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
