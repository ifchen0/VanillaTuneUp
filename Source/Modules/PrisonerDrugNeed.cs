using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// In the prisoner think tree, medical rest (JobGiver_PatientGoToBed) comes before the needs node holding
    /// JobGiver_SatisfyChemicalNeed and JobGiver_SatifyChemicalDependency, and the tree is ordered, not priority
    /// sorted. A wounded prisoner who can still walk is sent back to bed on every think, never reaches the drug job,
    /// and dies of a deadly addiction or genetic chemical dependency with the drug sitting in the cell. Colonists are
    /// not affected: their drug needs sit in a priority sorter at 9.25.
    ///
    /// When the medical rest node would send a prisoner to bed while a deadly addiction is at desire or worse, or a
    /// deadly chemical dependency wants a dose (vanilla thresholds), the prisoner takes the drug instead, found the
    /// same way as vanilla. Deadly = lethal severity, or any stage that is life-threatening or has a death MTB, so
    /// modded drugs and genes count too. The bed job checks for overrides every 211 ticks, so a
    /// prisoner already lying down gets up for it. Downed prisoners are left alone, as in vanilla.
    /// </summary>
    public static class PrisonerDrugNeed
    {
        private static readonly Dictionary<HediffDef, bool> deadly = new Dictionary<HediffDef, bool>();
        private static readonly JobGiver_SatisfyChemicalNeed giver = new JobGiver_SatisfyChemicalNeed();
        private static readonly JobGiver_SatifyChemicalDependency dependencyGiver = new JobGiver_SatifyChemicalDependency();
        private static Func<JobGiver_SatisfyChemicalNeed, Pawn, Need_Chemical, Thing> findDrugFor;
        private static bool resolved;

        private static bool IsDeadly(HediffDef def)
        {
            if (deadly.TryGetValue(def, out bool result))
                return result;
            result = def.lethalSeverity > 0f;
            if (!result && def.stages != null)
            {
                foreach (HediffStage stage in def.stages)
                {
                    if (stage.lifeThreatening || stage.deathMtbDays > 0f)
                    {
                        result = true;
                        break;
                    }
                }
            }
            deadly[def] = result;
            return result;
        }

        private static bool Resolve()
        {
            if (!resolved)
            {
                resolved = true;
                var method = AccessTools.Method(typeof(JobGiver_SatisfyChemicalNeed), "FindDrugFor");
                if (method != null)
                    findDrugFor = AccessTools.MethodDelegate<Func<JobGiver_SatisfyChemicalNeed, Pawn, Need_Chemical, Thing>>(method);
                else
                    Log.Warning("[Vanilla Tune-Up] PrisonerDrugNeed: JobGiver_SatisfyChemicalNeed.FindDrugFor not found, module inactive.");
            }
            return findDrugFor != null;
        }

        public static Job TryDrugJob(Pawn pawn)
        {
            if (!pawn.IsPrisonerOfColony || !pawn.Spawned || pawn.Downed || pawn.needs == null)
                return null;
            return TryAddictionJob(pawn) ?? TryDependencyJob(pawn);
        }

        private static Job TryDependencyJob(Pawn pawn)
        {
            if (!ModsConfig.BiotechActive)
                return null;
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i] is Hediff_ChemicalDependency dependency && dependency.ShouldSatify && IsDeadly(dependency.def))
                    return dependencyGiver.TryIssueJobPackage(pawn, default).Job;
            }
            return null;
        }

        private static Job TryAddictionJob(Pawn pawn)
        {
            List<Need> needs = pawn.needs.AllNeeds;
            Need_Chemical lowest = null;
            for (int i = 0; i < needs.Count; i++)
            {
                if (!(needs[i] is Need_Chemical need) || need.CurCategory > DrugDesireCategory.Desire)
                    continue;
                Hediff_Addiction addiction = need.AddictionHediff;
                if (addiction == null || !IsDeadly(addiction.def))
                    continue;
                if (lowest == null || need.CurLevel < lowest.CurLevel)
                    lowest = need;
            }
            if (lowest == null || !Resolve())
                return null;
            Thing drug = findDrugFor(giver, pawn, lowest);
            if (drug == null)
                return null;
            Pawn holder = (drug.ParentHolder as Pawn_InventoryTracker)?.pawn;
            if (holder != null && holder != pawn)
            {
                Job take = JobMaker.MakeJob(JobDefOf.TakeFromOtherInventory, drug, holder);
                take.count = 1;
                return take;
            }
            return DrugAIUtility.IngestAndTakeToInventoryJob(drug, pawn, 1);
        }
    }

    [HarmonyPatchCategory("PrisonerDrugNeed")]
    [HarmonyPatch(typeof(JobGiver_PatientGoToBed), "TryGiveJob")]
    public static class PrisonerDrugNeed_PatientGoToBed
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null)
                return;
            Job job = PrisonerDrugNeed.TryDrugJob(pawn);
            if (job != null)
                __result = job;
        }
    }
}
