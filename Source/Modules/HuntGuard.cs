using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Verse.Profile;

namespace VanillaTuneUp
{
    /// <summary>
    /// Hunting an animal that can turn on the hunter at all calls the nearest free armed colonists as guards, more of
    /// them for a tougher victim or one whose packmates would join its revenge. The guards follow and defend the hunter
    /// with the same vanilla AI that obedient animals use for their master, and the hunter holds fire at the cast
    /// position until every guard is close or a time limit runs out. In strict mode a hunter who cannot get every
    /// guard it needs before the first shot gives the hunt up and leaves that animal alone for a while instead.
    /// Assignments live in memory only: nothing is saved, so a game loaded mid-hunt simply goes on without guards.
    /// </summary>
    public static class HuntGuard
    {
        // The hunter shoots anyway once it has waited this long (30 s) for the guards.
        private const int MaxWaitTicks = 1800;
        // Guard counts as arrived within this many cells of the hunter.
        private const float ReadyDistance = 8f;
        // One guard per this much health scale on the victim's side (a grizzly bear is 2.5, a megasloth 3.6, a
        // thrumbo 8). Revenge chance only decides whether guards come: every hit rolls it again, so even a small
        // chance adds up over the many hits a tough animal takes.
        private const float HealthPerGuard = 3f;
        // Same radius vanilla uses to pull packmates into an animal's revenge (Pawn_MindState.GetPackmates).
        private const float PackRadius = 24f;
        // Strict mode: a hunter that gave a hunt up leaves that animal alone this long (one in-game hour).
        private const int GiveUpCooldownTicks = 2500;

        private sealed class Escort
        {
            public Pawn Hunter;
            public readonly List<Pawn> Guards = new List<Pawn>();
            public Pawn Victim;
            public int Needed;
            public int WaitStartTick = -1;
            // Set once the guard has arrived or the wait timed out, so the hunter never waits twice.
            public bool WaitOver;
        }

        private static readonly Dictionary<Pawn, Escort> ByHunter = new Dictionary<Pawn, Escort>();
        private static readonly Dictionary<Pawn, Escort> ByGuard = new Dictionary<Pawn, Escort>();
        // Strict mode: hunter and animal of each hunt given up, with the tick until which it is not taken again.
        private static readonly Dictionary<(Pawn Hunter, Pawn Victim), int> GivenUp = new Dictionary<(Pawn, Pawn), int>();

        private static bool Strict => VanillaTuneUpMod.Settings.huntGuardStrict;

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.CheckboxLabeled("VTU_HuntGuardStrict".Translate(), ref settings.huntGuardStrict, "VTU_HuntGuardStrict_Desc".Translate());
        }

        /// <summary>The hunter this pawn is guarding right now, or null.</summary>
        public static Pawn HunterGuardedBy(Pawn guard)
        {
            if (!ByGuard.TryGetValue(guard, out Escort escort))
                return null;
            if (!Refresh(escort))
                return null;
            return escort.Guards.Contains(guard) ? escort.Hunter : null;
        }

        /// <summary>
        /// Called when a hunt job starts: picks guards if the victim is dangerous enough. False when strict mode
        /// gives the hunt up because not enough guards are free.
        /// </summary>
        public static bool Request(Pawn hunter, Pawn victim)
        {
            // A downed victim only needs slaughtering, so no guard for that.
            if (hunter?.Map == null || victim == null || victim.Dead || victim.Downed || !hunter.IsColonistPlayerControlled
                || hunter.CurJob == null || hunter.CurJob.playerForced)
                return true;
            PruneInvalid();
            if (ByHunter.ContainsKey(hunter))
                return true;
            float range = hunter.equipment?.PrimaryEq?.PrimaryVerb?.EffectiveRange ?? 1f;
            float chance = PawnUtility.GetManhunterOnDamageChance(victim, hunter, range * JobDriver_Hunt.MaxRangeFactor);
            if (chance <= 0f)
                return true;
            int needed = GuardsNeeded(victim);
            List<Pawn> guards = FindGuards(hunter, needed);
            if (Strict && guards.Count < needed)
            {
                GiveUp(hunter, victim);
                return false;
            }
            if (guards.Count == 0)
                return true;
            var escort = new Escort { Hunter = hunter, Victim = victim, Needed = needed };
            escort.Guards.AddRange(guards);
            ByHunter[hunter] = escort;
            foreach (Pawn guard in guards)
            {
                ByGuard[guard] = escort;
                // Start following directly: ending the current job and letting the think tree choose is not enough,
                // since hauling mods chain the next haul as soon as a haul job ends.
                guard.jobs.StartJob(JobGiver_HuntGuardFollow.MakeFollowJob(hunter), JobCondition.InterruptForced);
            }
            return true;
        }

        /// <summary>True while the hunter should hold fire because a guard is still on the way.</summary>
        public static bool ShouldWait(Pawn hunter)
        {
            if (!ByHunter.TryGetValue(hunter, out Escort escort))
                return false;
            if (!Refresh(escort))
                return false;
            if (escort.WaitOver)
                return false;
            if (escort.Guards.TrueForAll(g => g.Position.InHorDistOf(hunter.Position, ReadyDistance)))
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
            if (Strict)
            {
                GiveUp(hunter, escort.Victim);
                Release(escort);
            }
            return false;
        }

        /// <summary>Strict mode: true when this hunter gave up hunting this animal and should end the job.</summary>
        public static bool ShouldGiveUp(Pawn hunter, Pawn victim) =>
            hunter.CurJob != null && !hunter.CurJob.playerForced && IsGivenUp(hunter, victim);

        /// <summary>Strict mode: true while the hunter leaves this animal alone after giving its hunt up.</summary>
        public static bool IsGivenUp(Pawn hunter, Pawn victim) =>
            Strict && victim != null && GivenUp.TryGetValue((hunter, victim), out int until) && Find.TickManager.TicksGame < until;

        private static void GiveUp(Pawn hunter, Pawn victim)
        {
            int now = Find.TickManager.TicksGame;
            if (GivenUp.Count > 0)
            {
                foreach (var key in new List<(Pawn, Pawn)>(GivenUp.Keys))
                {
                    if (GivenUp[key] <= now)
                        GivenUp.Remove(key);
                }
            }
            GivenUp[(hunter, victim)] = now + GiveUpCooldownTicks;
        }

        // Worst case: the victim's revenge pulls in every packmate vanilla would, which needs big threats allowed.
        private static int GuardsNeeded(Pawn victim)
        {
            float threat = Health(victim);
            if (victim.Spawned && Find.Storyteller.difficulty.allowBigThreats)
            {
                District district = victim.GetDistrict();
                List<ThingDef> crossAggro = victim.RaceProps.crossAggroWith;
                foreach (Pawn p in victim.Map.mapPawns.AllPawnsSpawned)
                {
                    if (p == victim || p.Dead || p.Downed || p.Faction != victim.Faction)
                        continue;
                    if (p.def != victim.def && (crossAggro == null || !crossAggro.Contains(p.def)))
                        continue;
                    if (p.Position.InHorDistOf(victim.Position, PackRadius) && p.GetDistrict() == district)
                        threat += Health(p);
                }
            }
            // No upper limit: a whole herd calls out as many guards as it takes, like a hunting party.
            return Mathf.Max(1, Mathf.CeilToInt(threat / HealthPerGuard));
        }

        // Health left to shoot through: the race and life stage's health scale, less what wounds took already.
        private static float Health(Pawn p) => p.HealthScale * p.health.summaryHealth.SummaryHealthPercent;

        private static List<Pawn> FindGuards(Pawn hunter, int count)
        {
            var candidates = new List<Pawn>();
            foreach (Pawn p in hunter.Map.mapPawns.FreeColonistsSpawned)
            {
                if (p == hunter || ByGuard.ContainsKey(p) || ByHunter.ContainsKey(p) || !CanGuard(p))
                    continue;
                if (p.CurJob != null && (p.CurJob.playerForced || p.CurJob.def == JobDefOf.Hunt || !p.jobs.IsCurrentJobPlayerInterruptible()))
                    continue;
                // Pulling away a pawn mid-carry would drop the rescued pawn or the meal where it stands.
                if (p.carryTracker?.CarriedThing != null)
                    continue;
                candidates.Add(p);
            }
            candidates.SortBy(p => p.Position.DistanceToSquared(hunter.Position));
            var guards = new List<Pawn>();
            foreach (Pawn p in candidates)
            {
                if (guards.Count >= count)
                    break;
                if (p.CanReach(hunter, PathEndMode.OnCell, Danger.Deadly))
                    guards.Add(p);
            }
            return guards;
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

        // Drops guards that can no longer guard, and the whole escort once the hunt is over or no guard is left.
        // Returns whether the escort still stands.
        private static bool Refresh(Escort escort)
        {
            if (!HuntGoingOn(escort))
            {
                Release(escort);
                return false;
            }
            for (int i = escort.Guards.Count - 1; i >= 0; i--)
            {
                Pawn guard = escort.Guards[i];
                if (guard.Spawned && guard.Map == escort.Hunter.Map && CanGuard(guard, guarding: true))
                    continue;
                escort.Guards.RemoveAt(i);
                ByGuard.Remove(guard);
            }
            // Strict mode: losing a guard before the first shot means the hunt cannot go ahead as planned.
            if (Strict && !escort.WaitOver && escort.Guards.Count < escort.Needed)
                GiveUp(escort.Hunter, escort.Victim);
            else if (escort.Guards.Count > 0)
                return true;
            Release(escort);
            return false;
        }

        private static bool HuntGoingOn(Escort escort)
        {
            Pawn hunter = escort.Hunter;
            Pawn victim = escort.Victim;
            // Once the victim is down or dead the hunter only slaughters and hauls, so the danger is over.
            if (!hunter.Spawned || hunter.Downed || victim.Dead || victim.Downed)
                return false;
            return StillEngaged(hunter, victim);
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
            foreach (Pawn guard in escort.Guards)
                ByGuard.Remove(guard);
            escort.Guards.Clear();
        }

        /// <summary>Drops every assignment, so the old game's pawns and map are not kept after leaving it.</summary>
        public static void Forget()
        {
            ByHunter.Clear();
            ByGuard.Clear();
            GivenUp.Clear();
        }

        private static void PruneInvalid()
        {
            if (ByHunter.Count == 0)
                return;
            foreach (Escort escort in new List<Escort>(ByHunter.Values))
                Refresh(escort);
        }
    }

    /// <summary>
    /// Adds the guard call at the start of the hunt and a wait before the first shot. The wait toil goes after
    /// every vanilla toil, behind a toil that ends the job, so it is only reached by jumping to it and the saved
    /// toil index of a hunt in progress means the same with or without the module.
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
                if (HuntGuard.ShouldGiveUp(driver.pawn, driver.Victim))
                {
                    driver.EndJobWith(JobCondition.Incompletable);
                    return;
                }
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
                        if (!HuntGuard.Request(driver.pawn, driver.Victim))
                            driver.EndJobWith(JobCondition.Incompletable);
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
                        else if (HuntGuard.ShouldGiveUp(driver.pawn, driver.Victim))
                            driver.EndJobWith(JobCondition.Incompletable);
                        else
                            check?.Invoke();
                    };
                }
                yield return toil;
            }
            if (gotoCastPos != null)
            {
                // Vanilla ends the hunt by running out of toils after the corpse is stored. Without this stop the
                // driver would walk on into the wait toil and from there back to shooting, now at the corpse.
                Toil end = ToilMaker.MakeToil("VTU_EndHunt");
                end.initAction = () => driver.EndJobWith(JobCondition.Succeeded);
                yield return end;
                yield return wait;
            }
        }
    }

    /// <summary>Strict mode: a hunter that gave a hunt up does not pick the same animal again until the cooldown ends.</summary>
    [HarmonyPatchCategory("HuntGuard")]
    [HarmonyPatch(typeof(WorkGiver_HunterHunt), nameof(WorkGiver_HunterHunt.HasJobOnThing))]
    public static class Patch_WorkGiver_HunterHunt_HasJobOnThing
    {
        public static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
        {
            if (__result && !forced && t is Pawn victim && HuntGuard.IsGivenUp(pawn, victim))
                __result = false;
        }
    }

    [HarmonyPatchCategory("HuntGuard")]
    [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
    public static class Patch_MemoryUtility_ClearAllMapsAndWorld_HuntGuard
    {
        public static void Postfix() => HuntGuard.Forget();
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
