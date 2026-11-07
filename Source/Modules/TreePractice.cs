using System.Collections.Generic;
using RimWorld;
using RimWorld.Utility;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    [DefOf]
    public static class TreePracticeDefOf
    {
        public static JobDef VTU_TreePractice;

        static TreePracticeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(TreePracticeDefOf));
    }

    /// <summary>
    /// Pawns with a passion for Shooting or Melee may pass their recreation time practicing on a stump or dead tree,
    /// or a living tree when none is near, outside the home area: shooting at it from a few cells away, or hitting it.
    /// Mostly useful on maps away from the colony, where nothing else but meditative recreation is available.
    /// </summary>
    public static class TreePractice
    {
        public const float MinShootRange = 3f;
        public const float MaxShootRange = 6f;
        private const float MaxSearchDistance = 40f;
        // Wild misses land up to about 10 cells from the target for the worst shots.
        private const float ClearRadius = 10f;
        // Full clearance checks per job search; the cheap checks come first.
        private const int MaxClearChecks = 15;

        /// <summary>
        /// The primary weapon's verb when it is a plain gun: no explosives, fire, single-use launchers or
        /// charges. Other verbs (mod weapons included) are left out to be safe.
        /// </summary>
        public static Verb SafeGun(Pawn pawn)
        {
            ThingWithComps weapon = pawn.equipment?.Primary;
            Verb verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (weapon == null || verb == null || verb.GetType() != typeof(Verb_Shoot) || verb.verbProps.ForcedMissRadius > 0f)
                return null;
            foreach (ThingComp comp in weapon.AllComps)
            {
                if (comp is ICompWithCharges)
                    return null;
            }
            ProjectileProperties proj = ((Verb_Shoot)verb).Projectile?.projectile;
            if (proj == null || proj.explosionRadius > 0f || proj.ai_IsIncendiary || proj.damageDef == null
                || proj.damageDef.isExplosive || proj.damageDef == DamageDefOf.Flame)
                return null;
            return verb;
        }

        private static float PassionFactor(Pawn pawn, SkillDef skill)
        {
            SkillRecord rec = pawn.skills?.GetSkill(skill);
            if (rec == null || rec.TotallyDisabled)
                return 0f;
            switch (rec.passion)
            {
                case Passion.Major: return 1.5f;
                case Passion.Minor: return 1f;
                default: return 0f;
            }
        }

        /// <summary>Chance factor from the stronger usable passion; 0 when the pawn cannot practice.</summary>
        public static float PracticeFactor(Pawn pawn, out bool ranged)
        {
            ranged = false;
            if (pawn.skills == null || pawn.WorkTagIsDisabled(WorkTags.Violent))
                return 0f;
            float shoot = SafeGun(pawn) != null ? PassionFactor(pawn, SkillDefOf.Shooting) : 0f;
            float melee = PassionFactor(pawn, SkillDefOf.Melee);
            ranged = shoot > 0f && shoot >= melee;
            return ranged ? shoot : melee;
        }

        // Wild trees only: no special trees (anima, Gauranlen and the like all carry comps), nothing in the
        // home area or a growing zone.
        private static bool IsWildTree(Thing t, Pawn pawn)
        {
            if (!(t is Plant plant) || !plant.def.plant.IsTree || !plant.def.comps.NullOrEmpty())
                return false;
            Map map = plant.Map;
            IntVec3 c = plant.Position;
            return !map.areaManager.Home[c] && !(map.zoneManager.ZoneAt(c) is Zone_Growing) && !c.Fogged(map)
                && !c.IsForbidden(pawn) && !plant.IsBurning();
        }

        // Stray shots never hit pawns (canHitNonTargetPawns off), but they can still hit buildings and items,
        // such as an ancient cryptosleep casket that opens when damaged. Natural rock and chunks are fine.
        private static bool AreaClear(IntVec3 center, Map map)
        {
            foreach (IntVec3 c in GenRadial.RadialCellsAround(center, ClearRadius, useCenter: true))
            {
                if (!c.InBounds(map))
                    continue;
                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    ThingDef def = things[i].def;
                    if (def.category == ThingCategory.Building && !def.mineable)
                        return false;
                    if (def.category == ThingCategory.Item && !def.IsWithinCategory(ThingCategoryDefOf.Chunks))
                        return false;
                }
            }
            return true;
        }

        /// <summary>A reachable cell 3-6 cells from the tree to shoot from, nearest to the pawn first.</summary>
        private static bool TryFindShootCell(Pawn pawn, Thing tree, Verb verb, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            Map map = pawn.Map;
            List<IntVec3> cells = new List<IntVec3>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(tree.Position, MaxShootRange, useCenter: false))
            {
                if (c.InBounds(map) && c.DistanceTo(tree.Position) >= MinShootRange && c.Standable(map) && !c.Fogged(map) && !c.IsForbidden(pawn))
                    cells.Add(c);
            }
            cells.SortBy(c => c.DistanceToSquared(pawn.Position));
            foreach (IntVec3 c in cells)
            {
                if (verb.CanHitTargetFrom(c, tree) && pawn.CanReach(c, PathEndMode.OnCell, Danger.None))
                {
                    result = c;
                    return true;
                }
            }
            return false;
        }

        private static Thing FindTarget(Pawn pawn, bool ranged, bool dead)
        {
            Map map = pawn.Map;
            int clearChecks = 0;
            return GenClosest.ClosestThingReachable(pawn.Position, map, ThingRequest.ForGroup(ThingRequestGroup.Plant),
                PathEndMode.Touch, TraverseParms.For(pawn, Danger.None), MaxSearchDistance, t =>
                {
                    if (t is DeadPlant != dead || !IsWildTree(t, pawn))
                        return false;
                    if (!ranged)
                        return true;
                    return clearChecks++ < MaxClearChecks && AreaClear(t.Position, map);
                });
        }

        // Killing a living tree counts toward the "trees destroyed" mood of the Trees: desired precept
        // (stumps do not), so such colonies leave living trees alone.
        private static bool TreesDesired()
        {
            PreceptDef desired = DefDatabase<PreceptDef>.GetNamedSilentFail("Trees_Desired");
            if (desired == null || !ModsConfig.IdeologyActive)
                return false;
            foreach (Ideo ideo in Faction.OfPlayer.ideos.AllIdeos)
            {
                if (ideo.HasPrecept(desired))
                    return true;
            }
            return false;
        }

        public static Job TryMakeJob(Pawn pawn)
        {
            if (PracticeFactor(pawn, out bool ranged) <= 0f || !JoyUtility.EnjoyableOutsideNow(pawn))
                return null;
            Verb gun = ranged ? SafeGun(pawn) : null;
            // Dead trees (stumps, burned trees) first; living ones only when no colony ideoligion wants trees kept.
            Thing tree = FindTarget(pawn, ranged, dead: true) ?? (TreesDesired() ? null : FindTarget(pawn, ranged, dead: false));
            if (tree == null)
                return null;
            if (!ranged)
                return JobMaker.MakeJob(TreePracticeDefOf.VTU_TreePractice, tree);
            if (!TryFindShootCell(pawn, tree, gun, out IntVec3 cell))
                return null;
            return JobMaker.MakeJob(TreePracticeDefOf.VTU_TreePractice, tree, cell);
        }
    }

    public class JoyGiver_TreePractice : JoyGiver
    {
        public override float GetChance(Pawn pawn) => def.baseChance * TreePractice.PracticeFactor(pawn, out _);

        public override bool CanBeGivenTo(Pawn pawn) => base.CanBeGivenTo(pawn) && TreePractice.PracticeFactor(pawn, out _) > 0f;

        public override Job TryGiveJob(Pawn pawn) => TreePractice.TryMakeJob(pawn);
    }

    /// <summary>Walks to the tree (or to a shooting cell when B is set) and attacks it until recreation is full or time runs out.</summary>
    public class JobDriver_TreePractice : JobDriver
    {
        private Thing Tree => job.GetTarget(TargetIndex.A).Thing;
        private bool Ranged => job.GetTarget(TargetIndex.B).IsValid;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Tree.IsBurning());
            if (Ranged)
                yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            else
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            Toil practice = ToilMaker.MakeToil("TreePractice");
            practice.tickIntervalAction = delta =>
            {
                Thing tree = Tree;
                if (!pawn.stances.FullBodyBusy)
                {
                    if (Ranged)
                    {
                        Verb gun = TreePractice.SafeGun(pawn);
                        // Weapon swapped or the line blocked since the job started.
                        if (gun == null || !gun.CanHitTargetFrom(pawn.Position, tree))
                        {
                            EndJobWith(JobCondition.Incompletable);
                            return;
                        }
                        gun.TryStartCastOn(tree, surpriseAttack: false, canHitNonTargetPawns: false);
                    }
                    else
                        pawn.meleeVerbs.TryMeleeAttack(tree);
                }
                JoyUtility.JoyTickCheckEnd(pawn, delta, JoyTickFullJoyAction.EndJob);
            };
            practice.handlingFacing = true;
            practice.tickAction = () => pawn.rotationTracker.FaceTarget(Tree);
            practice.defaultCompleteMode = ToilCompleteMode.Delay;
            practice.defaultDuration = job.def.joyDuration;
            practice.socialMode = RandomSocialMode.Off;
            yield return practice;
        }
    }
}
