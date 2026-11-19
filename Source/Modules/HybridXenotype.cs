using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla labels every child of two different heritable xenotypes "Hybrid", even when the inherited germline
    /// genes are almost exactly one xenotype's. A hybrid with no xenogenes whose germline genes match one heritable
    /// xenotype by more than 80% is given that xenotype and is no longer a hybrid; equal best matches are picked at
    /// random. The match is shared / combined genes, each weighted by its complexity (at least 1, so cosmetic genes
    /// still count), with skin color, hair color and Inbred ignored like vanilla's own same-xenotype check. Genes are
    /// never changed; the hybrid flag only affects the label and children's labels.
    /// An option (off by default) also takes modified pawns into account: all genes (germline and xenogenes alike,
    /// since a xenotype def does not split them) are matched against every xenotype, for hybrids with xenogenes and
    /// for Baseliners still shown with the default faceless icon, such as xenogerm implants. Mutants keep their name.
    /// With Humanoid Alien Races loaded the body comes first: a race that lists its own xenotypes (such as Miho or
    /// Weazel) always takes the best-matching one of those, with no threshold, since its look and meat come from the
    /// race, not the genes. Other bodies (humans) are matched by genes only against xenotypes their race may use, so
    /// they are never named after another race's xenotype.
    /// </summary>
    public static class HybridXenotype
    {
        private const float MatchThreshold = 0.8f;

        private static List<KeyValuePair<XenotypeDef, HashSet<GeneDef>>> candidates;
        private static readonly HashSet<GeneDef> tmpGenes = new HashSet<GeneDef>();
        private static readonly List<XenotypeDef> tmpBest = new List<XenotypeDef>();

        private static bool Comparable(GeneDef gene) =>
            gene != GeneDefOf.Inbred
            && gene.endogeneCategory != EndogeneCategory.Melanin
            && gene.endogeneCategory != EndogeneCategory.HairColor;

        private static int Weight(GeneDef gene) => gene.biostatCpx < 1 ? 1 : gene.biostatCpx;

        // Humanoid Alien Races restricts some xenotypes to their own race (body, head and meat come from the race
        // ThingDef, not the xenotype). Looked up by name so HAR stays optional; null when HAR is not loaded.
        private static readonly System.Func<XenotypeDef, ThingDef, bool> harCanUseXenotype = LookupHarCanUseXenotype();

        private static System.Func<XenotypeDef, ThingDef, bool> LookupHarCanUseXenotype()
        {
            System.Reflection.MethodInfo method =
                AccessTools.TypeByName("AlienRace.RaceRestrictionSettings") is System.Type type
                    ? AccessTools.Method(type, "CanUseXenotype", new[] { typeof(XenotypeDef), typeof(ThingDef) })
                    : null;
            return method != null && method.ReturnType == typeof(bool)
                ? AccessTools.MethodDelegate<System.Func<XenotypeDef, ThingDef, bool>>(method)
                : null;
        }

        private static bool RaceCanUse(XenotypeDef xenotype, Pawn pawn) =>
            harCanUseXenotype == null || harCanUseXenotype(xenotype, pawn.def);

        private static readonly Dictionary<ThingDef, HashSet<XenotypeDef>> raceOwnXenotypes =
            new Dictionary<ThingDef, HashSet<XenotypeDef>>();

        /// <summary>
        /// The xenotypes a Humanoid Alien Races race lists as its own (alienRace.raceRestriction.xenotypeList).
        /// Empty for humans, for races without such a list, and when HAR is not loaded.
        /// </summary>
        public static HashSet<XenotypeDef> RaceOwnXenotypes(ThingDef race)
        {
            if (!raceOwnXenotypes.TryGetValue(race, out HashSet<XenotypeDef> own))
            {
                var list = Traverse.Create(race).Field("alienRace").Field("raceRestriction").Field("xenotypeList")
                    .GetValue() as IEnumerable<XenotypeDef>;
                own = list == null ? new HashSet<XenotypeDef>() : new HashSet<XenotypeDef>(list.Where(x => x != null));
                raceOwnXenotypes[race] = own;
            }
            return own;
        }

        private static List<KeyValuePair<XenotypeDef, HashSet<GeneDef>>> Candidates =>
            candidates ??= DefDatabase<XenotypeDef>.AllDefs
                .Select(x => new KeyValuePair<XenotypeDef, HashSet<GeneDef>>(
                    x, new HashSet<GeneDef>(x.genes.Where(Comparable))))
                .ToList();

        /// <summary>
        /// A Baseliner shown with the default faceless icon. Hybrids and xenogerms without a chosen icon show it, a
        /// plain Baseliner shows its own icon, and a custom xenotype given its own icon is skipped.
        /// Mirrors Pawn_GeneTracker.XenotypeIcon by def and path: FinalizeInit runs off the main thread during loading,
        /// where loading the textures fails and every icon compares equal as null.
        /// </summary>
        private static bool IsFacelessBaseliner(Pawn pawn)
        {
            Pawn_GeneTracker genes = pawn.genes;
            if (pawn.IsMutant || genes.Xenotype != XenotypeDefOf.Baseliner)
                return false;
            if (genes.iconDef != null)
                return genes.iconDef == XenotypeIconDefOf.Basic;
            return genes.UniqueXenotype || genes.Xenotype.iconPath == XenotypeIconDefOf.Basic.texPath;
        }

        public static void DrawSettings(Listing_Standard listing)
        {
            // The settings window writes the settings when it closes.
            listing.CheckboxLabeled("VTU_HybridIncludeXenogenes".Translate(),
                ref VanillaTuneUpMod.Settings.hybridIncludeXenogenes, "VTU_HybridIncludeXenogenes_Desc".Translate());
        }

        /// <summary>Gives a qualifying hybrid its best-matching xenotype. Returns that xenotype, or null.</summary>
        public static XenotypeDef TryResolve(Pawn pawn)
        {
            Pawn_GeneTracker genes = pawn?.genes;
            if (genes == null)
                return null;
            bool includeXenogenes = VanillaTuneUpMod.Settings.hybridIncludeXenogenes;
            if (includeXenogenes ? !(genes.hybrid || IsFacelessBaseliner(pawn)) : !genes.hybrid || genes.Xenogenes.Count > 0)
                return null;

            XenotypeDef best = Pick(pawn, includeXenogenes);
            if (best == null)
                return null;

            genes.hybrid = false;
            genes.iconDef = null;
            genes.SetXenotypeDirect(best);
            return best;
        }

        /// <summary>
        /// The xenotype a hybrid with this pawn's body and genes would be given, or null; the pawn is not changed.
        /// Equal best matches are picked at random.
        /// </summary>
        public static XenotypeDef Pick(Pawn pawn, bool includeXenogenes)
        {
            Pawn_GeneTracker genes = pawn.genes;
            tmpGenes.Clear();
            int pawnWeight = 0;
            foreach (Gene gene in includeXenogenes ? genes.GenesListForReading : genes.Endogenes)
            {
                if (Comparable(gene.def) && tmpGenes.Add(gene.def))
                    pawnWeight += Weight(gene.def);
            }

            // The body decides first: an alien race with its own xenotypes only ever takes one of them, chosen by genes
            // with no threshold. Only bodies without their own xenotypes (humans) are classified by genes alone.
            HashSet<XenotypeDef> own = RaceOwnXenotypes(pawn.def);
            bool byRace = own.Count > 0;

            float bestScore = 0f;
            tmpBest.Clear();
            foreach (var candidate in Candidates)
            {
                if (!includeXenogenes && !candidate.Key.inheritable
                    || (byRace ? !own.Contains(candidate.Key) : !RaceCanUse(candidate.Key, pawn)))
                    continue;
                int shared = 0, xenotypeWeight = 0;
                foreach (GeneDef gene in candidate.Value)
                {
                    int weight = Weight(gene);
                    xenotypeWeight += weight;
                    if (tmpGenes.Contains(gene))
                        shared += weight;
                }
                int combined = pawnWeight + xenotypeWeight - shared;
                float score = combined == 0 ? 1f : (float)shared / combined;
                if (score > bestScore + 0.0001f)
                {
                    bestScore = score;
                    tmpBest.Clear();
                    tmpBest.Add(candidate.Key);
                }
                else if (score > bestScore - 0.0001f)
                {
                    tmpBest.Add(candidate.Key);
                }
            }
            tmpGenes.Clear();

            // Equal best matches are picked at random; once resolved the pawn is no longer a hybrid, so this happens once.
            XenotypeDef best = tmpBest.Count > 0 && (byRace || bestScore > MatchThreshold) ? tmpBest.RandomElement() : null;
            tmpBest.Clear();
            return best;
        }
    }

    /// <summary>
    /// With the option on, checks a pawn right after a xenogerm is implanted instead of waiting for the next load.
    /// With it off the pawn now has xenogenes, so it is skipped.
    /// </summary>
    [HarmonyPatchCategory("HybridXenotype")]
    [HarmonyPatch(typeof(GeneUtility), nameof(GeneUtility.ImplantXenogermItem))]
    public static class Patch_GeneUtility_ImplantXenogermItem
    {
        public static void Postfix(Pawn pawn) => HybridXenotype.TryResolve(pawn);
    }

    [HarmonyPatchCategory("HybridXenotype")]
    [HarmonyPatch(typeof(PregnancyUtility), nameof(PregnancyUtility.ApplyBirthOutcome))]
    public static class Patch_PregnancyUtility_ApplyBirthOutcome
    {
        public static void Postfix(Thing __result)
        {
            Pawn baby = __result as Pawn ?? (__result as Corpse)?.InnerPawn;
            HybridXenotype.TryResolve(baby);
        }
    }

    /// <summary>Re-checks every hybrid on each load, so hybrids born before the module was enabled are covered too.</summary>
    [HarmonyPatchCategory("HybridXenotype")]
    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class Patch_Game_FinalizeInit
    {
        public static void Postfix()
        {
            var resolved = new List<string>();
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
            {
                XenotypeDef xenotype = HybridXenotype.TryResolve(pawn);
                if (xenotype != null)
                    resolved.Add($"{pawn.LabelShort} ({xenotype.defName})");
            }
            if (resolved.Count > 0)
                Log.Message($"[Vanilla Tune-Up] Gave {resolved.Count} pawn(s) their matching xenotype: {string.Join(", ", resolved)}");
        }
    }
}
