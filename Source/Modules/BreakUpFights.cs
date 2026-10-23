using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    [DefOf]
    public static class BreakUpFightDefOf
    {
        public static JobDef VTU_BreakUpFight;
        public static InteractionDef VTU_BreakUpFightTalk;
        public static InteractionDef VTU_JoinFight;
        public static ThoughtDef VTU_FightBrokenUp;
        public static ThoughtDef VTU_FightMeddled;
        public static ThoughtDef VTU_FightInterrupted;
        public static ThoughtDef VTU_BrokeUpFight;

        static BreakUpFightDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(BreakUpFightDefOf));
    }

    /// <summary>
    /// Colonists who see a social fight nearby may step in: they walk up to one fighter, talk for a moment,
    /// and either end the fight or, now and then, get pulled into a new one with that fighter.
    /// Rivals of a fighter and Bloodlust pawns may pile in on the fighter they like least instead.
    /// </summary>
    public static class BreakUpFights
    {
        public const int ScanIntervalTicks = 120;
        private const int MinFightAge = 120;
        private const float Radius = 10f;
        // Rolled by each bystander on every scan, so watching for a while makes stepping in likely.
        // A failed mediator goes back to their work and rolls again only while still nearby.
        private const float StepInChancePerScan = 0.35f;
        // Each mediator rolls on their own; more of them adds no bonus, only more rolls either way.
        private const int MaxMediators = 3;
        private const float SocialXp = 20f;
        // Vanilla shows "rival" from -20 down; a rival of a fighter joins the fight instead of breaking it up.
        private const float RivalOpinion = -20f;
        // Pawns piling onto the fighters of one fight at a time.
        private const int MaxJoiners = 2;
        // Interruptible in vanilla, but walking off to a fight makes no sense.
        private static readonly HashSet<string> NoStepInJobs = new HashSet<string> { "Flee", "FleeAndCower", "FleeAndCowerShort", "Hunt" };
        // Mediators carry their load along and set it down when done, except food on its way to someone else.
        private static readonly HashSet<string> KeepCarryingJobs = new HashSet<string>
        {
            "FeedPatient", "DeliverFood", "BottleFeedBaby", "Breastfeed", "BreastfeedCarryToMom"
        };

        public static MentalState_SocialFighting FightOf(Pawn pawn) =>
            pawn != null && pawn.Spawned ? pawn.MentalState as MentalState_SocialFighting : null;

        public static void TrySendMediator(MentalState_SocialFighting fight)
        {
            Pawn a = fight.pawn;
            Pawn b = fight.otherPawn;
            // Both fighters tick the same fight; only the one with the lower ID scans.
            if (b == null || a.thingIDNumber > b.thingIDNumber || FightOf(b)?.otherPawn != a)
                return;
            if (fight.Age < MinFightAge || !a.Spawned || b.Map != a.Map)
                return;

            Pawn mediator = null, joiner = null, joinTarget = null;
            float bestDist = float.MaxValue, bestJoinDist = float.MaxValue;
            int mediators = 0, joiners = 0;
            foreach (Pawn p in a.Map.mapPawns.AllHumanlikeSpawned)
            {
                if (p == a || p == b || !IsInGroup(p, a, b))
                    continue;
                Job cur = p.CurJob;
                if (cur != null && cur.def == BreakUpFightDefOf.VTU_BreakUpFight && (cur.targetA.Thing == a || cur.targetA.Thing == b))
                {
                    mediators++;
                    continue;
                }
                Pawn piledOn = FightOf(p)?.otherPawn;
                if (piledOn == a || piledOn == b)
                {
                    joiners++;
                    continue;
                }
                string reason = RejectReason(p, a, b, out float dist);
                Pawn hated = null;
                if (reason == null && WantsToJoin(p, a, b, out hated))
                {
                    // Vanilla social fight chance against the disliked fighter: opinion, Bloodlust, drink and so on.
                    if (!CanFight(p, hated) || !Rand.Chance(p.interactions.SocialFightChance(BreakUpFightDefOf.VTU_JoinFight, hated)))
                        reason = "join roll failed";
                    else if (dist >= bestJoinDist)
                        reason = "someone closer";
                    else
                    {
                        joiner = p;
                        joinTarget = hated;
                        bestJoinDist = dist;
                        continue;
                    }
                }
                else
                {
                    reason ??= MediatorRejectReason(p);
                    if (reason == null && !Rand.Chance(StepInChancePerScan))
                        reason = "roll failed";
                    if (reason == null && dist >= bestDist)
                        reason = "someone closer";
                    if (reason == null)
                    {
                        mediator = p;
                        bestDist = dist;
                        continue;
                    }
                }
            }
            if (joiners >= MaxJoiners)
                joiner = null;
            if (mediators >= MaxMediators)
                mediator = null;
            if (joiner != null)
                Join(joiner, joinTarget);
            if (mediator == null)
                return;

            Pawn target = mediator.Position.DistanceToSquared(a.Position) <= mediator.Position.DistanceToSquared(b.Position) ? a : b;
            Job job = JobMaker.MakeJob(BreakUpFightDefOf.VTU_BreakUpFight, target);
            // The load comes along and is dropped by the fight when the job ends (the job def does not keep it);
            // regular work picks it up later. A job that was carrying it cannot pick up where it left off.
            bool carrying = mediator.carryTracker.CarriedThing != null;
            bool resume = !carrying && mediator.CurJob != null && mediator.CurJob.def.suspendable;
            mediator.jobs.StartJob(job, JobCondition.InterruptForced, resumeCurJobAfterwards: resume, keepCarryingThingOverride: true);
        }

        // Anyone hostile to neither fighter may step in, whatever their standing. Who may actually fight whom
        // (prisoners and slaves only their own kind) is left to the vanilla checks in CanFight and TryDragIn,
        // and prison doors keep prisoners in their cell. Wild and non-human pawns stay out.
        private static bool IsInGroup(Pawn p, Pawn a, Pawn b) =>
            p.Faction != null && !p.IsSubhuman && !p.HostileTo(a) && !p.HostileTo(b);

        private static float DistanceTo(Pawn p, Pawn a, Pawn b) =>
            Mathf.Min(p.Position.DistanceToSquared(a.Position), p.Position.DistanceToSquared(b.Position));

        // A rival of either fighter, or anyone with Bloodlust, would rather pile in on the fighter they like least.
        private static bool WantsToJoin(Pawn p, Pawn a, Pawn b, out Pawn hated)
        {
            float opA = p.relations.OpinionOf(a);
            float opB = p.relations.OpinionOf(b);
            hated = opA <= opB ? a : b;
            return Mathf.Min(opA, opB) <= RivalOpinion || p.story?.traits?.HasTrait(TraitDefOf.Bloodlust) == true;
        }

        // Vanilla rules from both sides, so prisoners and slaves still only fight their own kind.
        private static bool CanFight(Pawn p, Pawn other) =>
            p.interactions.SocialFightPossible(other) && other.interactions.SocialFightPossible(p);

        // The joiner fights the disliked fighter, who keeps fighting their own opponent (see Patch_SocialFighting_ShouldStop).
        private static void Join(Pawn joiner, Pawn target)
        {
            if (!joiner.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.SocialFighting, null, false, false, false, target))
                return;
            if (PawnUtility.ShouldSendNotificationAbout(joiner) || PawnUtility.ShouldSendNotificationAbout(target))
            {
                Messages.Message("VTU_BF_JoinedIn".Translate(joiner.LabelShort, target.LabelShort, joiner.Named("PAWN1"), target.Named("PAWN2")),
                    joiner, MessageTypeDefOf.ThreatSmall);
            }
            TaleRecorder.RecordTale(TaleDefOf.SocialFight, joiner, target);
        }

        private static string MediatorRejectReason(Pawn p)
        {
            if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                return "cannot talk";
            if (p.skills == null || p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
                return "incapable of social";
            return null;
        }

        // Null when the pawn could step in or join; otherwise why not.
        private static string RejectReason(Pawn p, Pawn a, Pawn b, out float dist)
        {
            dist = DistanceTo(p, a, b);
            if (dist > Radius * Radius)
                return "too far";
            if (p.Downed || p.Drafted || p.InMentalState || !p.Awake() || p.IsBurning())
                return "downed, drafted, asleep or in a mental state";
            // Visitors and raiders always belong to a lord; for the colony's own pawns it means a party or ritual.
            if (p.Faction != null && p.Faction.IsPlayer && p.GetLord() != null)
                return "in a group activity";
            Job cur = p.CurJob;
            Thing carried = p.carryTracker.CarriedThing;
            if (carried is Pawn || (carried != null && cur != null && KeepCarryingJobs.Contains(cur.def.defName)))
                return "carrying " + carried.LabelShort;
            if (cur != null && cur.playerForced)
                return "player-ordered job " + cur.def.defName;
            if (cur != null && (!cur.def.casualInterruptible || NoStepInJobs.Contains(cur.def.defName)))
                return "uninterruptible job " + cur.def.defName;
            if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                return "cannot move";
            if (!GenSight.LineOfSight(p.Position, a.Position, p.Map) && !GenSight.LineOfSight(p.Position, b.Position, p.Map))
                return "no line of sight";
            if (!p.CanReach(a, PathEndMode.Touch, Danger.Some) && !p.CanReach(b, PathEndMode.Touch, Danger.Some))
                return "cannot reach";
            return null;
        }

        public static void Talk(Pawn mediator, Pawn fighter)
        {
            InteractionDef def = BreakUpFightDefOf.VTU_BreakUpFightTalk;
            MoteMaker.MakeInteractionBubble(mediator, fighter, def.interactionMote, def.GetSymbol(mediator.Faction, mediator.Ideo), def.GetSymbolColor(mediator.Faction));
            Find.PlayLog.Add(new PlayLogEntry_Interaction(def, mediator, fighter, null));
        }

        public static float SuccessChance(Pawn mediator, Pawn fighter, Pawn other)
        {
            // Social 0: 20%, 10: 50%, 20: 80% before the other factors.
            float chance = 0.2f + 0.03f * mediator.skills.GetSkill(SkillDefOf.Social).Level;
            float opinion = (fighter.relations.OpinionOf(mediator) + other.relations.OpinionOf(mediator)) / 2f;
            chance *= GenMath.LerpDouble(-100f, 100f, 0.5f, 1.5f, opinion);
            if (mediator.story.traits.HasTrait(TraitDefOf.Kind))
                chance *= 1.25f;
            if (mediator.story.traits.HasTrait(TraitDefOf.Abrasive))
                chance *= 0.6f;
            return Mathf.Clamp(chance, 0.05f, 0.95f);
        }

        public static void Resolve(Pawn mediator, Pawn fighter, MentalState_SocialFighting fight)
        {
            Map map = mediator.Map;
            Pawn other = fight.otherPawn;
            mediator.skills.Learn(SkillDefOf.Social, SocialXp);
            if (Rand.Chance(SuccessChance(mediator, fighter, other)))
            {
                MoteMaker.ThrowText(mediator.DrawPos, map, "VTU_BF_Calmed".Translate());
                // Ending one side ends the other too (MentalState_SocialFighting.PostEnd).
                fight.RecoverFromState();
                ReactToBreakUp(fighter, mediator);
                // In a brawl, calming the one who piled in leaves the main fight going.
                if (FightOf(other) == null)
                    ReactToBreakUp(other, mediator);
                int stage = mediator.story.traits.HasTrait(TraitDefOf.Kind) ? 1 : 0;
                mediator.needs?.mood?.thoughts.memories.TryGainMemory(ThoughtMaker.MakeThought(BreakUpFightDefOf.VTU_BrokeUpFight, stage));
                return;
            }
            // Vanilla social fight chance from the fighter's side, with the talk def's own base chance.
            if (fighter.interactions.SocialFightPossible(mediator) && mediator.interactions.SocialFightPossible(fighter)
                && Rand.Chance(fighter.interactions.SocialFightChance(BreakUpFightDefOf.VTU_BreakUpFightTalk, mediator))
                && TryDragIn(mediator, fighter, fight))
                return;
            MoteMaker.ThrowText(mediator.DrawPos, map, "VTU_BF_Ignored".Translate());
            fighter.needs?.mood?.thoughts.memories.TryGainMemory(BreakUpFightDefOf.VTU_FightInterrupted, mediator);
        }

        // The fighter turns on the mediator while their old opponent keeps hitting them (see
        // Patch_SocialFighting_ShouldStop), so it becomes a brawl that ends when the fighter stops.
        private static bool TryDragIn(Pawn mediator, Pawn fighter, MentalState_SocialFighting fight)
        {
            Pawn old = fight.otherPawn;
            fight.otherPawn = mediator;
            if (!mediator.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.SocialFighting, null, false, false, false, fighter))
            {
                fight.otherPawn = old;
                return false;
            }
            fighter.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (PawnUtility.ShouldSendNotificationAbout(fighter) || PawnUtility.ShouldSendNotificationAbout(mediator))
            {
                Messages.Message("VTU_BF_DraggedIn".Translate(fighter.LabelShort, mediator.LabelShort, fighter.Named("PAWN1"), mediator.Named("PAWN2")),
                    fighter, MessageTypeDefOf.ThreatSmall);
            }
            TaleRecorder.RecordTale(TaleDefOf.SocialFight, fighter, mediator);
            return true;
        }

        // Most fighters are grateful; those who wanted the fight, or dislike the mediator, resent it.
        private static void ReactToBreakUp(Pawn fighter, Pawn mediator)
        {
            float meddled = 0.15f;
            if (fighter.story?.traits?.HasTrait(TraitDefOf.Bloodlust) == true)
                meddled += 0.45f;
            if (fighter.story?.traits?.HasTrait(TraitDefOf.Abrasive) == true)
                meddled += 0.2f;
            if (fighter.relations.OpinionOf(mediator) < 0)
                meddled += 0.2f;
            ThoughtDef def = Rand.Chance(meddled) ? BreakUpFightDefOf.VTU_FightMeddled : BreakUpFightDefOf.VTU_FightBrokenUp;
            fighter.needs?.mood?.thoughts.memories.TryGainMemory(def, mediator);
        }
    }

    [HarmonyPatch(typeof(MentalState_SocialFighting), nameof(MentalState_SocialFighting.MentalStateTick))]
    [HarmonyPatchCategory("BreakUpFights")]
    public static class Patch_SocialFighting_MentalStateTick
    {
        public static void Postfix(MentalState_SocialFighting __instance, int delta)
        {
            Pawn pawn = __instance.pawn;
            if (pawn.MentalState == __instance && pawn.IsHashIntervalTick(BreakUpFights.ScanIntervalTicks, delta))
                BreakUpFights.TrySendMediator(__instance);
        }
    }

    /// <summary>
    /// Vanilla stops a fighter as soon as their opponent fights someone else. Keep going while the opponent
    /// is still in any social fight, so a fighter who turned on a mediator gets ganged up on instead.
    /// Normal one-on-one fights are always mutual, so they are unaffected.
    /// </summary>
    [HarmonyPatch(typeof(MentalState_SocialFighting), "ShouldStop", MethodType.Getter)]
    [HarmonyPatchCategory("BreakUpFights")]
    public static class Patch_SocialFighting_ShouldStop
    {
        public static void Postfix(MentalState_SocialFighting __instance, ref bool __result)
        {
            Pawn other = __instance.otherPawn;
            if (__result && other != null && other.Spawned && !other.Dead && !other.Downed && BreakUpFights.FightOf(other) != null)
                __result = false;
        }
    }

    /// <summary>
    /// Leaves the talk entries out of saves: vanilla keeps a log entry whose def is gone and throws whenever
    /// the Social tab draws it, so a save made with them would break once the mod is removed.
    /// </summary>
    [HarmonyPatch(typeof(PlayLog), nameof(PlayLog.ExposeData))]
    [HarmonyPatchCategory("BreakUpFights")]
    public static class Patch_PlayLog_ExposeData
    {
        private static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, InteractionDef> IntDef =
            AccessTools.FieldRefAccess<PlayLogEntry_Interaction, InteractionDef>("intDef");

        public static void Prefix(List<LogEntry> ___entries, out List<LogEntry> __state)
        {
            __state = null;
            if (Scribe.mode != LoadSaveMode.Saving)
                return;
            __state = new List<LogEntry>(___entries);
            ___entries.RemoveAll(e => e is PlayLogEntry_Interaction i && IntDef(i) == BreakUpFightDefOf.VTU_BreakUpFightTalk);
        }

        public static void Postfix(List<LogEntry> ___entries, List<LogEntry> __state)
        {
            if (__state == null)
                return;
            ___entries.Clear();
            ___entries.AddRange(__state);
        }
    }

    /// <summary>Follows one fighter until adjacent, talks for a moment, then rolls the outcome.</summary>
    public class JobDriver_BreakUpFight : JobDriver
    {
        private const int TalkTicks = 120;
        private const int GiveUpTicks = 1500;

        private int talkStartTick = -1;

        private Pawn Fighter => job.GetTarget(TargetIndex.A).Pawn;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref talkStartTick, "talkStartTick", -1);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            Toil toil = ToilMaker.MakeToil("BreakUpFight");
            toil.tickAction = () =>
            {
                Pawn fighter = Fighter;
                MentalState_SocialFighting fight = BreakUpFights.FightOf(fighter);
                int now = Find.TickManager.TicksGame;
                if (fight == null || now - startTick > GiveUpTicks)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (!pawn.CanReachImmediate(fighter, PathEndMode.Touch))
                {
                    // Already following the fighter returns early; the path follower re-paths as they move.
                    pawn.pather.StartPath(fighter, PathEndMode.Touch);
                    return;
                }
                if (pawn.pather.Moving)
                    pawn.pather.StopDead();
                pawn.rotationTracker.FaceTarget(fighter);
                if (talkStartTick < 0)
                {
                    talkStartTick = now;
                    BreakUpFights.Talk(pawn, fighter);
                }
                // Keep the vanilla minimum fight length before anyone can stop it.
                if (now - talkStartTick >= TalkTicks && fight.Age >= fight.def.minTicksBeforeRecovery)
                {
                    BreakUpFights.Resolve(pawn, fighter, fight);
                    // A mediator pulled into the fight has already left this job.
                    if (pawn.CurJob == job)
                        EndJobWith(JobCondition.Succeeded);
                }
            };
            toil.defaultCompleteMode = ToilCompleteMode.Never;
            toil.socialMode = RandomSocialMode.Off;
            yield return toil;
        }
    }
}
