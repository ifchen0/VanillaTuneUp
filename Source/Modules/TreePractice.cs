using System.Collections.Generic;
using HarmonyLib;
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
    /// Pawns with a passion for Shooting or Melee may pass their recreation time practicing on a tree outside the home
    /// area: shooting at it from a few cells away, or hitting it. Practice does no damage to its target, so strong late
    /// game weapons do not end it at the first hit. Mostly useful on maps away from the colony, where nothing else but
    /// meditative recreation is available.
    /// </summary>
    public static class TreePractice
    {
        public const float MinShootRange = 3f;
        public const float MaxShootRange = 6f;
        private const float MaxSearchDistance = 40f;
        // Vanilla wild misses land within 2 cells of the target at practice range for any ordinary shooter (aim chance
        // 22% or more), plus rounding to a cell and a building's edge. Only a crippled shooter misses wider; stray shots
        // never hit pawns, and the odd stray hitting something along the line of fire out here does not matter.
        public const float ClearRadius = 4f;
        // Full clearance checks per job search; the cheap checks come first.
        private const int MaxClearChecks = 15;
        // How many of the nearest fitting targets the practice target is picked from at random.
        private const int TargetChoices = 6;

        private static TuneUpModule module;

        /// <summary>
        /// Whether the module was switched on at startup, as its no-damage patch was. The defs are always loaded (XML
        /// caches such as Gagarin's ignore mod settings), so the recreation itself checks this.
        /// </summary>
        public static bool Enabled => (module ??= Modules.Get("TreePractice")).IsActive;

        /// <summary>Whether the damage is a practice hit on the attacker's practice target.</summary>
        public static bool IsPracticeHit(Thing target, DamageInfo dinfo) =>
            dinfo.Instigator is Pawn pawn && pawn.CurJobDef == TreePracticeDefOf.VTU_TreePractice
            && pawn.CurJob.targetA.Thing == target;

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
            // Brawlers dislike guns, so they only practice with a melee weapon.
            bool brawler = pawn.story?.traits?.HasTrait(TraitDefOf.Brawler) ?? false;
            float shoot = SafeGun(pawn) != null && !brawler ? PassionFactor(pawn, SkillDefOf.Shooting) : 0f;
            // Melee practice needs a melee weapon in hand: no rifle butts, fists or natural weapons.
            float melee = pawn.equipment?.Primary?.def.IsMeleeWeapon == true ? PassionFactor(pawn, SkillDefOf.Melee) : 0f;
            ranged = shoot > 0f && shoot >= melee;
            return ranged ? shoot : melee;
        }

        /// <summary>Psychopaths and bloodlust pawns also practice on corpses and do not mind what stray shots hit.</summary>
        public static bool Callous(Pawn pawn)
        {
            TraitSet traits = pawn.story?.traits;
            return traits != null && (traits.HasTrait(TraitDefOf.Psychopath) || traits.HasTrait(TraitDefOf.Bloodlust));
        }

        // Out in the wild: nothing in the home area or a growing zone.
        private static bool Outside(Thing t, Pawn pawn)
        {
            Map map = t.Map;
            IntVec3 c = t.Position;
            return !map.areaManager.Home[c] && !(map.zoneManager.ZoneAt(c) is Zone_Growing) && !c.Fogged(map)
                && !c.IsForbidden(pawn) && !t.IsBurning();
        }

        // Wild trees only: no special trees (anima, Gauranlen and the like all carry comps).
        private static bool IsWildTree(Thing t, Pawn pawn) =>
            t is Plant plant && plant.def.plant.IsTree && plant.def.comps.NullOrEmpty() && Outside(t, pawn);

        // Enemies' and animals' corpses, never the colony's own people.
        private static bool IsStrangerCorpse(Thing t, Pawn pawn) =>
            t is Corpse corpse && corpse.InnerPawn != null && corpse.InnerPawn.Faction != Faction.OfPlayer && Outside(t, pawn);

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

        /// <summary>
        /// A random pick among the few nearest fitting targets, so practice does not always go to the same tree.
        /// </summary>
        private static Thing FindTarget(Pawn pawn, bool ranged, ThingRequestGroup group, System.Func<Thing, bool> fits)
        {
            Map map = pawn.Map;
            bool checkArea = ranged && !Callous(pawn);
            int clearChecks = 0;
            List<Thing> candidates = new List<Thing>(TargetChoices);
            // The validator collects candidates and only stops the search once there are enough of them.
            GenClosest.ClosestThingReachable(pawn.Position, map, ThingRequest.ForGroup(group),
                PathEndMode.Touch, TraverseParms.For(pawn, Danger.None), MaxSearchDistance, t =>
                {
                    if (!fits(t))
                        return false;
                    if (checkArea && (clearChecks++ >= MaxClearChecks || !AreaClear(t.Position, map)))
                        return false;
                    candidates.Add(t);
                    return candidates.Count >= TargetChoices;
                });
            return candidates.Count > 0 ? candidates.RandomElement() : null;
        }

        public static Job TryMakeJob(Pawn pawn)
        {
            if (PracticeFactor(pawn, out bool ranged) <= 0f || !JoyUtility.EnjoyableOutsideNow(pawn))
                return null;
            Verb gun = ranged ? SafeGun(pawn) : null;
            // A nearby tree, living or dead (practice does no damage); the callous look for a corpse first.
            Thing tree = (Callous(pawn) ? FindTarget(pawn, ranged, ThingRequestGroup.Corpse, t => IsStrangerCorpse(t, pawn)) : null)
                ?? FindTarget(pawn, ranged, ThingRequestGroup.Plant, t => IsWildTree(t, pawn));
            if (tree == null)
                return null;
            if (!ranged)
                return JobMaker.MakeJob(TreePracticeDefOf.VTU_TreePractice, tree);
            if (!TryFindShootCell(pawn, tree, gun, out IntVec3 cell))
                return null;
            return JobMaker.MakeJob(TreePracticeDefOf.VTU_TreePractice, tree, cell);
        }
    }

    /// <summary>
    /// Practice hits, bullets and melee alike, leave the target untouched; skill and recreation come from the attack
    /// itself. A last bullet still in the air when practice ends may land as a normal hit.
    /// </summary>
    [HarmonyPatchCategory("TreePractice")]
    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    public static class Patch_Thing_TakeDamage_TreePractice
    {
        public static bool Prefix(Thing __instance, DamageInfo dinfo, ref DamageWorker.DamageResult __result)
        {
            if (!TreePractice.IsPracticeHit(__instance, dinfo))
                return true;
            __result = new DamageWorker.DamageResult();
            return false;
        }
    }

    public class JoyGiver_TreePractice : JoyGiver
    {
        public override float GetChance(Pawn pawn) => def.baseChance * TreePractice.PracticeFactor(pawn, out _);

        public override bool CanBeGivenTo(Pawn pawn) =>
            TreePractice.Enabled && base.CanBeGivenTo(pawn) && TreePractice.PracticeFactor(pawn, out _) > 0f;

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
