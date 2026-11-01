using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// Field tending and cheaper medicine:
    /// - A downed humanlike lying outside a bed can be tended where they lie when they would bleed to death within
    ///   6 hours, or when no bed is free for them (vanilla tends humanlikes only in bed, so they waited untended).
    /// - Tending a fast bleeder where they lie uses only medicine the doctor carries, or none, instead of walking
    ///   to a stockpile first.
    /// - Patients whose wounds needing tend are all non-bleeding injuries get the cheapest allowed medicine instead
    ///   of the best one. Bleeding wounds and diseases (infections, flu, plague, ...) still get the best.
    /// </summary>
    public static class FieldTending
    {
        private const int FastBleedTicks = 6 * GenDate.TicksPerHour;
        private const int BedCheckIntervalTicks = 250;

        private static readonly ConditionalWeakTable<Pawn, BedCheck> bedChecks = new ConditionalWeakTable<Pawn, BedCheck>();

        private class BedCheck
        {
            public int Tick = -BedCheckIntervalTicks;
            public bool HasBed;
        }

        public static bool BleedingFast(Pawn patient) =>
            HealthUtility.TicksUntilDeathDueToBloodLoss(patient) < FastBleedTicks;

        public static bool LyingOutsideBed(Pawn patient) =>
            patient.Spawned && patient.Downed && patient.RaceProps.Humanlike && !patient.InBed();

        /// <summary>
        /// Whether a bed is free for the patient, by the same lookup vanilla rescue uses. Cached per patient for
        /// 250 ticks, since work scans ask for every doctor and every tend work giver.
        /// </summary>
        private static bool HasBed(Pawn patient, Pawn doctor)
        {
            BedCheck check = bedChecks.GetOrCreateValue(patient);
            int now = Find.TickManager.TicksGame;
            if (now - check.Tick >= BedCheckIntervalTicks)
            {
                check.Tick = now;
                check.HasBed = RestUtility.FindBedFor(patient, doctor, checkSocialProperness: false, ignoreOtherReservations: false, patient.GuestStatus) != null;
            }
            return check.HasBed;
        }

        public static bool CanTendInPlace(Pawn patient, Pawn doctor) =>
            patient != doctor && LyingOutsideBed(patient) && (BleedingFast(patient) || !HasBed(patient, doctor));

        /// <summary>Every wound needing tend is an injury that is not bleeding: no disease, no bleeding.</summary>
        public static bool OnlyMinorWounds(Pawn patient)
        {
            HediffSet set = patient.health.hediffSet;
            if (set.BleedRateTotal > 0f)
                return false;
            List<Hediff> hediffs = set.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i].TendableNow() && !(hediffs[i] is Hediff_Injury))
                    return false;
            }
            return true;
        }

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.CheckboxLabeled("VTU_FieldTendBeforeCapture".Translate(), ref settings.fieldTendBeforeCapture,
                "VTU_FieldTendBeforeCapture_Desc".Translate());
        }

        /// <summary>Set while FindBestMedicine runs for a patient with only minor wounds.</summary>
        public static bool PreferCheapest;

        /// <summary>Replaces vanilla's medicine priority (potency, higher is better) inside FindBestMedicine.</summary>
        public static float MedicinePriority(Thing t)
        {
            float potency = t.def.GetStatValueAbstract(StatDefOf.MedicalPotency);
            return PreferCheapest ? -potency : potency;
        }
    }

    [HarmonyPatchCategory("FieldTending")]
    [HarmonyPatch(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.GoodLayingStatusForTend))]
    public static class Patch_WorkGiver_Tend_GoodLayingStatusForTend
    {
        public static void Postfix(Pawn patient, Pawn doctor, ref bool __result)
        {
            if (!__result && FieldTending.CanTendInPlace(patient, doctor))
                __result = true;
        }
    }

    [HarmonyPatchCategory("FieldTending")]
    [HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.FindBestMedicine))]
    public static class Patch_HealthAIUtility_FindBestMedicine
    {
        public static void Prefix(Pawn healer, Pawn patient, ref bool onlyUseInventory)
        {
            // Fast bleeder on the ground: no detour to the stockpile, use carried medicine or none.
            if (!onlyUseInventory && patient != healer && patient.playerSettings != null
                && FieldTending.LyingOutsideBed(patient) && FieldTending.BleedingFast(patient))
                onlyUseInventory = true;
            FieldTending.PreferCheapest = FieldTending.OnlyMinorWounds(patient);
        }

        public static void Finalizer() => FieldTending.PreferCheapest = false;
    }

    /// <summary>
    /// Optional: a colonist told to capture or arrest a downed pawn who would bleed to death within 6 hours tends
    /// them where they lie first (carried medicine or none), then carries them to the bed as ordered. The target
    /// is taken prisoner as the tend begins, so recovering mid-tend leaves a prisoner rather than an enemy on its
    /// feet. Each target is tried once per in-game hour, so a tend that cannot happen does not keep replacing the
    /// capture.
    /// </summary>
    [HarmonyPatchCategory("FieldTending")]
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_Pawn_JobTracker_StartJob
    {
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> jobPawn =
            AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

        private static readonly ConditionalWeakTable<Pawn, StrongBox<int>> lastTry = new ConditionalWeakTable<Pawn, StrongBox<int>>();

        /// <summary>Tend jobs started in place of a capture or arrest; their target is taken prisoner on arrival.</summary>
        private static readonly ConditionalWeakTable<Job, object> captureTends = new ConditionalWeakTable<Job, object>();

        public static bool IsCaptureTend(Job job) => captureTends.TryGetValue(job, out _);

        public static void Prefix(Pawn_JobTracker __instance, ref Job newJob, JobTag? tag)
        {
            if (!VanillaTuneUpMod.Settings.fieldTendBeforeCapture || newJob == null
                || (newJob.def != JobDefOf.Capture && newJob.def != JobDefOf.Arrest))
                return;
            Pawn doctor = jobPawn(__instance);
            if (!(newJob.targetA.Thing is Pawn target) || doctor == null || !doctor.IsColonistPlayerControlled
                || !target.Spawned || !target.Downed || !FieldTending.BleedingFast(target)
                || doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) || !doctor.CanReserve(target))
                return;
            StrongBox<int> last = lastTry.GetValue(target, _ => new StrongBox<int>(-GenDate.TicksPerHour));
            int now = Find.TickManager.TicksGame;
            if (now - last.Value < GenDate.TicksPerHour)
                return;
            last.Value = now;

            Job tend = JobMaker.MakeJob(JobDefOf.TendPatient, target, HealthAIUtility.FindBestMedicine(doctor, target, onlyUseInventory: true));
            tend.count = 1;
            // Not forced, the tend job would end at once: the target is not a patient the colony tends on its own.
            tend.playerForced = true;
            captureTends.Add(tend, null);
            __instance.jobQueue.EnqueueFirst(newJob, tag);
            newJob = tend;
        }

        /// <summary>
        /// What JobDriver_TakeToBed does when it reaches the target (arrest-resistance toil, then CheckMakeTakeePrisoner),
        /// done before the tend. The capture job queued after the tend then finds a prisoner and only carries them
        /// to the bed. A downed target never resists, so the arrest-resistance roll is left out.
        /// </summary>
        public static void MakePrisoner(Pawn target, Pawn capturer)
        {
            if (target.IsPrisonerOfColony)
                return;
            target.GetLord()?.Notify_PawnAttemptArrested(target);
            GenClamor.DoClamor(target, 10f, ClamorDefOf.Harm);
            if (!target.IsSlave)
            {
                bool lodger = target.IsQuestLodger();
                Faction homeFaction = target.HomeFaction;
                QuestUtility.SendQuestTargetSignals(target.questTags, "Arrested", target.Named("SUBJECT"));
                if (target.Faction != null)
                    QuestUtility.SendQuestTargetSignals(target.Faction.questTags, "FactionMemberArrested", target.Faction.Named("FACTION"));
                if (lodger)
                    target.SetFaction(homeFaction);
            }
            target.guest.CapturedBy(Faction.OfPlayer, capturer);
            if (target.playerSettings == null)
                target.playerSettings = new Pawn_PlayerSettings(target);
        }
    }

    /// <summary>
    /// Takes the target of a capture-replacing tend prisoner as the tend itself begins: a pre-init action on the
    /// toil just before FinalizeTend (vanilla's tend wait), so the doctor has arrived, fetched any medicine and is
    /// about to treat. Runs again on each repeat tend, where MakePrisoner returns at once.
    /// </summary>
    [HarmonyPatchCategory("FieldTending")]
    [HarmonyPatch(typeof(JobDriver_TendPatient), "MakeNewToils")]
    public static class Patch_JobDriver_TendPatient_MakeNewToils
    {
        public static void Postfix(JobDriver_TendPatient __instance, ref IEnumerable<Toil> __result)
        {
            if (__instance.job != null && Patch_Pawn_JobTracker_StartJob.IsCaptureTend(__instance.job))
                __result = CaptureOnTendStart(__result, __instance);
        }

        private static IEnumerable<Toil> CaptureOnTendStart(IEnumerable<Toil> toils, JobDriver_TendPatient driver)
        {
            Toil previous = null;
            bool hooked = false;
            foreach (Toil toil in toils)
            {
                if (!hooked && previous != null && toil.debugName == "FinalizeTend")
                {
                    previous.AddPreInitAction(() =>
                    {
                        if (driver.job.targetA.Thing is Pawn target && target.Spawned)
                            Patch_Pawn_JobTracker_StartJob.MakePrisoner(target, driver.pawn);
                    });
                    hooked = true;
                }
                previous = toil;
                yield return toil;
            }
            if (!hooked)
                Log.WarningOnce("[Vanilla Tune-Up] Could not find the tend step in JobDriver_TendPatient; capture targets turn prisoner when carried, as in vanilla.", 0x5654544E);
        }
    }

    /// <summary>
    /// Points FindBestMedicine and its inventory helper at FieldTending.MedicinePriority. The calls are redirected
    /// instead of patching vanilla's tiny PriorityOf local function, which the JIT may inline.
    /// </summary>
    [HarmonyPatchCategory("FieldTending")]
    [HarmonyPatch]
    public static class Patch_HealthAIUtility_MedicinePriority
    {
        private static bool IsPriorityOf(object operand) =>
            operand is MethodInfo m && m.DeclaringType == typeof(HealthAIUtility) && m.Name.Contains("FindBestMedicine>g__PriorityOf");

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(HealthAIUtility), nameof(HealthAIUtility.FindBestMedicine));
            // The inventory helper captures the validator, so it may live on a compiler-generated nested class.
            foreach (System.Type type in typeof(HealthAIUtility).GetNestedTypes(AccessTools.all).Prepend(typeof(HealthAIUtility)))
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                    if (m.Name.Contains("FindBestMedicine>g__GetBestMedInInventory"))
                        yield return m;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodInfo replacement = AccessTools.Method(typeof(FieldTending), nameof(FieldTending.MedicinePriority));
            int replaced = 0;
            foreach (CodeInstruction code in instructions)
            {
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Ldftn) && IsPriorityOf(code.operand))
                {
                    code.operand = replacement;
                    replaced++;
                }
                yield return code;
            }
            if (replaced == 0)
                Log.Warning($"[Vanilla Tune-Up] Could not find the medicine priority in {__originalMethod.Name}; it keeps picking the best medicine.");
        }
    }
}
