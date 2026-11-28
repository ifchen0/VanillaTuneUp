using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Trims the pawn menu a biosculpter pod cycle button opens. Vanilla lists every free colonist on the map;
    /// this hides colonists the cycle would do nothing for, tags each remaining name and adds a tooltip:
    /// - medic / bioregeneration: hidden when nothing would be healed; tag = heal count ("5", or "5+1/3" when
    ///   the cycle also cures one random condition out of 3); tooltip = vanilla's "will heal" list.
    /// - age reversal: hidden when already at the adult minimum age; tag = current age; tooltip = age before and after.
    /// - pleasure: nobody hidden; sorted by mood, lowest first; tag = current mood.
    /// Cycles from other mods keep vanilla's list. Disabled entries (no path, too young) stay: FloatMenu itself
    /// puts disabled options last. A biotuned pod skips the menu in vanilla and is left alone.
    /// </summary>
    public static class BiosculpterPawnMenu
    {
        private const float TicksPerYear = 3600000f;

        private static readonly AccessTools.FieldRef<CompBiosculpterPod, Pawn> BiotunedTo =
            AccessTools.FieldRefAccess<CompBiosculpterPod, Pawn>("biotunedTo");

        private static readonly Func<CompBiosculpterPod_HealingCycle, Pawn, Hediff, bool> WillHeal =
            AccessTools.MethodDelegate<Func<CompBiosculpterPod_HealingCycle, Pawn, Hediff, bool>>(
                AccessTools.Method(typeof(CompBiosculpterPod_HealingCycle), "WillHeal"));
        private static readonly Func<CompBiosculpterPod_HealingCycle, Pawn, Hediff, bool> CanPotentiallyHeal =
            AccessTools.MethodDelegate<Func<CompBiosculpterPod_HealingCycle, Pawn, Hediff, bool>>(
                AccessTools.Method(typeof(CompBiosculpterPod_HealingCycle), "CanPotentiallyHeal"));

        /// <summary>
        /// Heal count tag, or null when nothing would be healed. Mirrors CycleCompleted: every WillHeal condition is
        /// cured, then one random condition out of the remaining CanPotentiallyHeal ones (sure when it is the only one).
        /// </summary>
        private static string HealTag(CompBiosculpterPod_HealingCycle cycle, Pawn pawn)
        {
            int sure = 0, candidates = 0;
            foreach (Hediff h in pawn.health.hediffSet.hediffs)
            {
                if (WillHeal(cycle, pawn, h))
                    sure++;
                else if (CanPotentiallyHeal(cycle, pawn, h))
                    candidates++;
            }
            if (candidates == 1)
            {
                sure++;
                candidates = 0;
            }
            if (candidates == 0)
                return sure == 0 ? null : sure.ToString();
            return sure == 0 ? "1/" + candidates : sure + "+1/" + candidates;
        }

        private struct Entry
        {
            public FloatMenuOption Option;
            public float SortKey;
        }

        public static void Filter(CompBiosculpterPod pod, CompBiosculpterPod_Cycle cycle, List<FloatMenuOption> options)
        {
            if (BiotunedTo(pod) != null || options == null)
                return;
            bool healing = cycle is CompBiosculpterPod_HealingCycle;
            bool ageReversal = cycle is CompBiosculpterPod_AgeReversalCycle;
            bool pleasure = cycle is CompBiosculpterPod_PleasureCycle;
            if (!healing && !ageReversal && !pleasure)
                return;

            // Vanilla adds exactly one option per pawn, in this order; leave the list alone if that ever differs.
            var pawns = new List<Pawn>();
            foreach (Pawn p in pod.parent.Map.mapPawns.FreeColonistsSpawned)
            {
                if (!p.IsQuestLodger())
                    pawns.Add(p);
            }
            if (pawns.Count != options.Count)
                return;

            var kept = new List<Entry>(pawns.Count);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                FloatMenuOption option = options[i];
                float sortKey = 0f;
                string tip, tag;
                if (healing)
                {
                    var healingCycle = (CompBiosculpterPod_HealingCycle)cycle;
                    tag = HealTag(healingCycle, pawn);
                    if (tag == null)
                        continue;
                    tip = healingCycle.GetHealingDescriptionForPawn(pawn);
                }
                else if (ageReversal)
                {
                    Pawn_AgeTracker age = pawn.ageTracker;
                    long minTicks = (long)(TicksPerYear * age.AdultMinAge);
                    if (age.Adult && age.AgeBiologicalTicks <= minTicks)
                        continue;
                    tag = "VTU_BiosculpterPawnMenu_AgeTag".Translate(age.AgeBiologicalYears);
                    long afterTicks = System.Math.Max(minTicks,
                        age.AgeBiologicalTicks - (long)(TicksPerYear * age.AdultAgingMultiplier));
                    tip = "VTU_BiosculpterPawnMenu_Age".Translate(
                        (age.AgeBiologicalTicks / TicksPerYear).ToString("0.#"),
                        (afterTicks / TicksPerYear).ToString("0.#"));
                }
                else
                {
                    Need_Mood mood = pawn.needs?.mood;
                    sortKey = mood?.CurLevelPercentage ?? 1f;
                    tag = mood == null ? null : (string)"VTU_BiosculpterPawnMenu_Mood".Translate(sortKey.ToStringPercent());
                    tip = null;
                }
                // Vanilla's label is the pawn label, then ": reason" when disabled; put the tag right after the name.
                if (tag != null && option.Label.StartsWith(pawn.Label))
                    option.Label = pawn.Label + " (" + tag + ")" + option.Label.Substring(pawn.Label.Length);
                if (!tip.NullOrEmpty())
                    option.tooltip = new TipSignal(tip);
                kept.Add(new Entry { Option = option, SortKey = sortKey });
            }

            if (pleasure)
                kept = kept.OrderBy(e => e.SortKey).ToList(); // stable: equal moods keep vanilla's order

            options.Clear();
            foreach (Entry e in kept)
                options.Add(e.Option);
            // An empty FloatMenu logs an error and closes; say why instead.
            if (options.Count == 0)
                options.Add(new FloatMenuOption("VTU_BiosculpterPawnMenu_NoneNeed".Translate(), null));
        }
    }

    [HarmonyPatchCategory("BiosculpterPawnMenu")]
    [HarmonyPatch(typeof(CompBiosculpterPod), "SelectPawnsForCycleOptions")]
    public static class BiosculpterPawnMenu_SelectPawnsForCycleOptions
    {
        // The short-circuit call only decides whether the button is enabled; filter only the menu build.
        public static void Postfix(CompBiosculpterPod __instance, CompBiosculpterPod_Cycle cycle,
            ref List<FloatMenuOption> options, bool shortCircuit)
        {
            if (!shortCircuit)
                BiosculpterPawnMenu.Filter(__instance, cycle, options);
        }
    }
}
