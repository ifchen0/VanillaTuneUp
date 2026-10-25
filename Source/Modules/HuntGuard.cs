using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Hunting an animal likely to turn on the hunter calls the nearest free armed colonist as a guard. The guard
    /// follows and defends the hunter with the same vanilla AI that obedient animals use for their master, and the
    /// hunter holds fire at the cast position until the guard is close or a time limit runs out. Assignments live in
    /// memory only: nothing is saved, so a game loaded mid-hunt simply goes on without a guard.
    /// </summary>
    public static class HuntGuard
    {
        public const float DefaultMinRevengeChance = 0.2f;
        // The hunter shoots anyway once it has waited this long (30 s) for the guard.
        private const int MaxWaitTicks = 1800;
        // Guard counts as arrived within this many cells of the hunter.
        private const float ReadyDistance = 8f;

        private sealed class Escort
        {
            public Pawn Hunter;
            public Pawn Guard;
            public Pawn Victim;
            public int WaitStartTick = -1;
            // Set once the guard has arrived or the wait timed out, so the hunter never waits twice.
            public bool WaitOver;
        }

        private static readonly Dictionary<Pawn, Escort> ByHunter = new Dictionary<Pawn, Escort>();
        private static readonly Dictionary<Pawn, Escort> ByGuard = new Dictionary<Pawn, Escort>();

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.Label("VTU_HuntGuardMinChance".Translate((settings.huntGuardMinRevengeChance * 100f).ToString("0")),
                tooltip: "VTU_HuntGuardMinChance_Desc".Translate());
            settings.huntGuardMinRevengeChance = Mathf.Round(listing.Slider(settings.huntGuardMinRevengeChance, 0.05f, 1f) * 20f) / 20f;
        }

        /// <summary>The hunter this pawn is guarding right now, or null.</summary>
        public static Pawn HunterGuardedBy(Pawn guard)
        {
            if (!ByGuard.TryGetValue(guard, out Escort escort))
                return null;
            if (!IsValid(escort))
            {
                Release(escort);
                return null;
            }
            return escort.Hunter;
        }

        /// <summary>Called when a hunt job starts: picks a guard if the victim is dangerous enough.</summary>
        public static void Request(Pawn hunter, Pawn victim)
        {
            // A downed victim only needs slaughtering, so no guard for that.
            if (hunter?.Map == null || victim == null || victim.Dead || victim.Downed || !hunter.IsColonistPlayerControlled
                || hunter.CurJob == null || hunter.CurJob.playerForced)
                return;
            PruneInvalid();
            if (ByHunter.ContainsKey(hunter))
                return;
            float range = hunter.equipment?.PrimaryEq?.PrimaryVerb?.EffectiveRange ?? 1f;
            float chance = PawnUtility.GetManhunterOnDamageChance(victim, hunter, range * JobDriver_Hunt.MaxRangeFactor);
            if (chance < VanillaTuneUpMod.Settings.huntGuardMinRevengeChance)
                return;
            Pawn guard = FindGuard(hunter);
            if (guard == null)
                return;
            var escort = new Escort { Hunter = hunter, Guard = guard, Victim = victim };
            ByHunter[hunter] = escort;
            ByGuard[guard] = escort;
            // Start following directly: ending the current job and letting the think tree choose is not enough,
            // since hauling mods chain the next haul as soon as a haul job ends.
            guard.jobs.StartJob(JobGiver_HuntGuardFollow.MakeFollowJob(hunter), JobCondition.InterruptForced);
        }

        /// <summary>True while the hunter should hold fire because its guard is still on the way.</summary>
        public static bool ShouldWait(Pawn hunter)
        {
            if (!ByHunter.TryGetValue(hunter, out Escort escort))
                return false;
            if (!IsValid(escort))
            {
                Release(escort);
                return false;
            }
            if (escort.WaitOver)
                return false;
            if (escort.Guard.Position.InHorDistOf(hunter.Position, ReadyDistance))
            {
                escort.WaitOver = true;
                return false;
            }
            int now = Find.TickManager.TicksGame;
            if (escort.WaitStartTick < 0)
                escort.WaitStartTick = now;
            if (now - escort.WaitStartTick < MaxWaitTicks)
                return true;
            escort.WaitOver = true;
            return false;
        }

        private static Pawn FindGuard(Pawn hunter)
        {
            Pawn best = null;
            float bestDist = float.MaxValue;
            foreach (Pawn p in hunter.Map.mapPawns.FreeColonistsSpawned)
            {
                if (p == hunter || ByGuard.ContainsKey(p) || ByHunter.ContainsKey(p) || !CanGuard(p))
                    continue;
                if (p.CurJob != null && (p.CurJob.playerForced || p.CurJob.def == JobDefOf.Hunt || !p.jobs.IsCurrentJobPlayerInterruptible()))
                    continue;
                float dist = p.Position.DistanceToSquared(hunter.Position);
                if (dist < bestDist && p.CanReach(hunter, PathEndMode.OnCell, Danger.Deadly))
                {
                    best = p;
                    bestDist = dist;
                }
            }
            return best;
        }

        // Also checked while guarding, so a guard who gets very hungry, very tired or hurt goes back to normal life.
        // Picking needs a fed and rested pawn; once guarding, plain hunger or tiredness is not enough to leave.
        private static bool CanGuard(Pawn p, bool guarding = false)
        {
            if (p.Downed || p.Drafted || p.InMentalState || !p.Awake() || p.GetLord() != null)
                return false;
            // Fighting jobs give nothing to colonists set to flee or ignore, so only those set to attack can guard.
            if (p.WorkTagIsDisabled(WorkTags.Violent) || p.playerSettings?.hostilityResponse != HostilityResponseMode.Attack)
                return false;
            if (p.equipment?.Primary == null || !p.equipment.Primary.def.IsRangedWeapon)
                return false;
            HungerCategory maxHunger = guarding ? HungerCategory.Hungry : HungerCategory.Fed;
            if (p.needs?.food != null && p.needs.food.CurCategory > maxHunger)
                return false;
            RestCategory maxTired = guarding ? RestCategory.Tired : RestCategory.Rested;
            if (p.needs?.rest != null && p.needs.rest.CurCategory > maxTired)
                return false;
            return !HealthAIUtility.ShouldSeekMedicalRest(p);
        }

        private static bool IsValid(Escort escort)
        {
            Pawn hunter = escort.Hunter;
            Pawn victim = escort.Victim;
            // Once the victim is down or dead the hunter only slaughters and hauls, so the danger is over.
            if (!hunter.Spawned || hunter.Downed || victim.Dead || victim.Downed)
                return false;
            if (!StillEngaged(hunter, victim))
                return false;
            return escort.Guard.Spawned && escort.Guard.Map == hunter.Map && CanGuard(escort.Guard, guarding: true);
        }

        // When the victim turns on the hunter, the hunter drops the hunt job to fight back or flee; that is exactly
        // when the guard is needed, so any job aimed at the victim or an enraged victim keeps the escort going.
        private static bool StillEngaged(Pawn hunter, Pawn victim)
        {
            if (victim.InAggroMentalState && victim.Map == hunter.Map)
                return true;
            Job job = hunter.CurJob;
            return job != null && job.targetA.Thing == victim;
        }

        private static void Release(Escort escort)
        {
            ByHunter.Remove(escort.Hunter);
            ByGuard.Remove(escort.Guard);
        }

        private static void PruneInvalid()
        {
            List<Escort> stale = null;
            foreach (Escort escort in ByHunter.Values)
            {
                if (!IsValid(escort))
                    (stale ??= new List<Escort>()).Add(escort);
            }
            if (stale != null)
                stale.ForEach(Release);
        }
    }

    /// <summary>
    /// Adds the guard call at the start of the hunt and a wait before the first shot. The wait toil goes after
    /// every vanilla toil, so the saved toil index of a hunt in progress means the same with or without the module.
    /// </summary>
    [HarmonyPatchCategory("HuntGuard")]
    [HarmonyPatch(typeof(JobDriver_Hunt), "MakeNewToils")]
    public static class Patch_JobDriver_Hunt_MakeNewToils
    {
        private static readonly AccessTools.FieldRef<JobDriver_Hunt, int> JobStartTick =
            AccessTools.FieldRefAccess<JobDriver_Hunt, int>("jobStartTick");

        public static IEnumerable<Toil> Postfix(IEnumerable<Toil> toils, JobDriver_Hunt __instance)
        {
            JobDriver_Hunt driver = __instance;
            Toil gotoCastPos = null;
            Toil wait = ToilMaker.MakeToil("VTU_WaitForHuntGuard");
            int waitBegan = -1;
            wait.initAction = () =>
            {
                waitBegan = Find.TickManager.TicksGame;
                driver.pawn.pather.StopDead();
            };
            wait.tickAction = () =>
            {
                if (driver.Victim != null)
                    driver.pawn.rotationTracker.FaceTarget(driver.Victim);
                if (HuntGuard.ShouldWait(driver.pawn))
                    return;
                // Vanilla gives up on a hunt after 5000 ticks; time spent waiting should not count.
                if (waitBegan >= 0)
                    JobStartTick(driver) += Find.TickManager.TicksGame - waitBegan;
                driver.JumpToToil(gotoCastPos);
            };
            wait.defaultCompleteMode = ToilCompleteMode.Never;
            wait.handlingFacing = true;

            bool first = true;
            foreach (Toil toil in toils)
            {
                if (first)
                {
                    first = false;
                    System.Action start = toil.initAction;
                    toil.initAction = () =>
                    {
                        start?.Invoke();
                        HuntGuard.Request(driver.pawn, driver.Victim);
                    };
                }
                else if (toil.debugName == "GotoCastPosition" && gotoCastPos == null)
                {
                    gotoCastPos = toil;
                }
                else if (toil.debugName == "JumpIfTargetNotHittable" && gotoCastPos != null)
                {
                    System.Action check = toil.initAction;
                    toil.initAction = () =>
                    {
                        if (HuntGuard.ShouldWait(driver.pawn))
                            driver.JumpToToil(wait);
                        else
                            check?.Invoke();
                    };
                }
                yield return toil;
            }
            if (gotoCastPos != null)
                yield return wait;
        }
    }

    public class ThinkNode_ConditionalHuntGuard : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn)
        {
            Pawn hunter = HuntGuard.HunterGuardedBy(pawn);
            return hunter != null && pawn.CanReach(hunter, PathEndMode.OnCell, Danger.Deadly);
        }
    }

    public class JobGiver_HuntGuardDefend : JobGiver_AIDefendPawn
    {
        // Threats within this many cells of the hunter are engaged.
        private const float DefendRadius = 20f;

        protected override Pawn GetDefendee(Pawn pawn) => HuntGuard.HunterGuardedBy(pawn);

        protected override float GetFlagRadius(Pawn pawn) => DefendRadius;
    }

    public class JobGiver_HuntGuardFollow : JobGiver_AIFollowPawn
    {
        private const float FollowRadius = 4f;
        private const int FollowExpireTicks = 60;
        private const int StandByTicks = 60;

        protected override int FollowJobExpireInterval => FollowExpireTicks;

        public static Job MakeFollowJob(Pawn hunter)
        {
            Job job = JobMaker.MakeJob(JobDefOf.FollowClose, hunter);
            job.expiryInterval = FollowExpireTicks;
            job.checkOverrideOnExpire = true;
            job.followRadius = FollowRadius;
            return job;
        }

        protected override Pawn GetFollowee(Pawn pawn) => HuntGuard.HunterGuardedBy(pawn);

        protected override float GetRadius(Pawn pawn) => FollowRadius;

        // Close enough already: stand by with the weapon ready instead of going back to work.
        protected override Job TryGiveJob(Pawn pawn) =>
            base.TryGiveJob(pawn) ?? JobMaker.MakeJob(JobDefOf.Wait_Combat, StandByTicks, checkOverrideOnExpiry: true);
    }
}
