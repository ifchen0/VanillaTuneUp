using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla labels every child of two different heritable xenotypes "Hybrid", even when the inherited germline
    /// genes are almost exactly one xenotype's. A hybrid whose genes (germline and xenogenes alike, since a xenotype
    /// def does not split them) match one xenotype by more than 80% is given that xenotype and is no longer a hybrid;
    /// equal best matches are picked at random. The match is shared / combined genes, each weighted by its complexity
    /// (at least 1, so cosmetic genes still count), with skin color, hair color and Inbred ignored like vanilla's own
    /// same-xenotype check. Genes are never changed; the hybrid flag only affects the label and children's labels.
    /// Optionally (off by default) a Baseliner with a custom xenotype name, such as one implanted with a xenogerm, is
    /// matched the same way when it still has the default faceless icon; mutants keep their name.
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

        private static List<KeyValuePair<XenotypeDef, HashSet<GeneDef>>> Candidates =>
            candidates ??= DefDatabase<XenotypeDef>.AllDefs
                .Select(x => new KeyValuePair<XenotypeDef, HashSet<GeneDef>>(
                    x, new HashSet<GeneDef>(x.genes.Where(Comparable))))
                .ToList();

        /// <summary>
        /// A Baseliner shown with the default faceless icon, when the option is on. Hybrids and xenogerms without a
        /// chosen icon show it, a plain Baseliner shows its own icon, and a custom xenotype given its own icon is skipped.
        /// </summary>
        private static bool IsNamedBaseliner(Pawn pawn)
        {
            Pawn_GeneTracker genes = pawn.genes;
            return VanillaTuneUpMod.Settings.hybridIncludeNamed && !pawn.IsMutant
                && genes.Xenotype == XenotypeDefOf.Baseliner
                && genes.XenotypeIcon == XenotypeIconDefOf.Basic.Icon;
        }

        public static void DrawSettings(Listing_Standard listing)
        {
            // The settings window writes the settings when it closes.
            listing.CheckboxLabeled("VTU_HybridIncludeNamed".Translate(),
                ref VanillaTuneUpMod.Settings.hybridIncludeNamed, "VTU_HybridIncludeNamed_Desc".Translate());
        }

        /// <summary>Gives a qualifying hybrid its best-matching xenotype. Returns that xenotype, or null.</summary>
        public static XenotypeDef TryResolve(Pawn pawn)
        {
            Pawn_GeneTracker genes = pawn?.genes;
            if (genes == null || !(genes.hybrid || IsNamedBaseliner(pawn)))
                return null;

            tmpGenes.Clear();
            int pawnWeight = 0;
            foreach (Gene gene in genes.GenesListForReading)
            {
                if (Comparable(gene.def) && tmpGenes.Add(gene.def))
                    pawnWeight += Weight(gene.def);
            }

            float bestScore = 0f;
            tmpBest.Clear();
            foreach (var candidate in Candidates)
            {
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
            XenotypeDef best = bestScore > MatchThreshold ? tmpBest.RandomElement() : null;
            tmpBest.Clear();
            if (best == null)
                return null;

            genes.hybrid = false;
            genes.iconDef = null;
            genes.SetXenotypeDirect(best);
            return best;
        }
    }

    /// <summary>Checks a hybrid right after a xenogerm is implanted, instead of waiting for the next load.</summary>
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
