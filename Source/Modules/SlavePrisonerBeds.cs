using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla keeps slaves out of prisoner beds, so a colony with both needs separate slave bedrooms and a slave
    /// hospital. Let slaves of the colony use prisoner beds, sleeping and medical, under the prisoner rules.
    /// </summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.CanUseBedNow))]
    public static class SlavePrisonerBeds
    {
        public static bool Prefix(Thing bedThing, Pawn sleeper, bool allowMedBedEvenIfSetToNoCare,
            GuestStatus? guestStatusOverride, ref bool __result)
        {
            if (!(bedThing is Building_Bed bed) || !bed.ForPrisoners || sleeper == null
                || (guestStatusOverride ?? sleeper.GuestStatus) != GuestStatus.Slave || !sleeper.IsSlaveOfColony)
                return true;
            // Vanilla skips the forbidden check for prisoners only; slaves still respect it.
            if (!sleeper.Downed && sleeper.CurJob?.ignoreForbidden != true && bed.IsForbidden(sleeper))
            {
                __result = false;
                return false;
            }
            // Prisoner social properness means "bed in the room the pawn is in now", which a slave walking in from
            // elsewhere never meets; a prisoner bed is always in a prison cell, and that is the point here.
            __result = RestUtility.CanUseBedNow(bedThing, sleeper, checkSocialProperness: false,
                allowMedBedEvenIfSetToNoCare, GuestStatus.Prisoner);
            if (!__result && LyingUnowned(bed, sleeper, out int slot))
                __result = StillUsable(bed, sleeper, slot);
            return false;
        }

        // Slaves do not claim prisoner beds (SlavePrisonerBeds_NoClaim), but vanilla rejects a non-medical bed whose
        // sleeper lies in a slot other than its assigned one, and an unowned sleeper has none: the lay-down toil
        // checks this every tick and would kick the slave out the moment it lies down.
        private static bool LyingUnowned(Building_Bed bed, Pawn sleeper, out int slot)
        {
            slot = -1;
            if (bed.Medical || bed.IsOwner(sleeper) || sleeper.CurrentBed(out int? current) != bed || current == null)
                return false;
            slot = current.Value;
            return true;
        }

        // The rest of vanilla's checks for a sleeper already in the bed, with the slot required to be unowned instead.
        private static bool StillUsable(Building_Bed bed, Pawn sleeper, int slot)
        {
            if (!bed.Spawned || bed.Map != sleeper.MapHeld || bed.IsBurning() || bed.ForSlaves
                || !RestUtility.CanUseBedEver(sleeper, bed.def) || bed.CompAssignableToPawn.IdeoligionForbids(sleeper)
                || !bed.Position.IsInPrisonCell(bed.Map))
                return false;
            if (sleeper.HarmedByVacuum && bed.Position.GetVacuum(bed.Map) >= 0.5f)
                return false;
            // Owners take the first slots in order, so a slot past the owner count belongs to nobody.
            return slot >= bed.OwnersForReading.Count;
        }
    }

    /// <summary>
    /// Rescue and take-to-bed jobs fail at once when the bed's prisoner flag differs from the carried pawn's
    /// (a compiler-generated FailOn lambda in MakeNewToils), so a downed slave could be given a prisoner medical bed
    /// but never carried there. Let that check pass for slaves of the colony.
    /// </summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch]
    public static class SlavePrisonerBeds_TakeToBed
    {
        private static MethodBase target;

        public static bool Prepare()
        {
            MethodInfo forPrisoners = AccessTools.PropertyGetter(typeof(Building_Bed), nameof(Building_Bed.ForPrisoners));
            MethodInfo isPrisoner = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.IsPrisoner));
            target = AccessTools.GetDeclaredMethods(typeof(JobDriver_TakeToBed)).FirstOrDefault(m =>
            {
                if (!m.Name.Contains("MakeNewToils") || m.ReturnType != typeof(bool) || m.GetParameters().Length != 0)
                    return false;
                var code = PatchProcessor.GetOriginalInstructions(m);
                return code.Any(i => i.Calls(forPrisoners)) && code.Any(i => i.Calls(isPrisoner));
            });
            if (target == null)
                Log.Warning("[Vanilla Tune-Up] SlavePrisonerBeds: take-to-bed bed check not found; downed slaves will not be carried to prisoner beds.");
            return target != null;
        }

        public static MethodBase TargetMethod() => target;

        public static bool Prefix(JobDriver_TakeToBed __instance, ref bool __result)
        {
            Job job = __instance.job;
            if (job.def.makeTargetPrisoner || !(job.targetA.Thing is Pawn takee) || !takee.IsSlaveOfColony
                || !(job.targetB.Thing is Building_Bed bed) || !bed.ForPrisoners)
                return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Slaves do not claim prisoner beds: each sleep takes any free one, so a bed whose slave is at work or away can
    /// take a new prisoner, and night-shift slaves leave their beds to day sleepers. Vanilla claims the bed whenever a
    /// pawn lies down or is carried in, all through Pawn_Ownership.ClaimBedIfNonMedical; skip it for these beds and
    /// remember the bed instead, so recreation can still find the slave's cell. Bedroom thoughts never apply to
    /// prisoner beds, so not owning one changes no mood.
    /// </summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch(typeof(Pawn_Ownership), nameof(Pawn_Ownership.ClaimBedIfNonMedical))]
    public static class SlavePrisonerBeds_NoClaim
    {
        private static readonly Dictionary<Pawn, Building_Bed> lastBed = new Dictionary<Pawn, Building_Bed>();

        /// <summary>The prisoner bed this slave last slept in or was carried to, if still usable as one.</summary>
        public static Building_Bed LastBed(Pawn pawn)
        {
            if (pawn == null || !lastBed.TryGetValue(pawn, out Building_Bed bed))
                return null;
            if (bed.Spawned && bed.ForPrisoners && bed.Map == pawn.Map && pawn.IsSlaveOfColony)
                return bed;
            lastBed.Remove(pawn);
            return null;
        }

        public static void Remember(Pawn pawn, Building_Bed bed) => lastBed[pawn] = bed;

        public static void Clear() => lastBed.Clear();

        public static bool Prefix(Pawn ___pawn, Building_Bed newBed, ref bool __result)
        {
            if (newBed == null || !newBed.ForPrisoners || ___pawn == null || !___pawn.IsSlaveOfColony)
                return true;
            Remember(___pawn, newBed);
            __result = false;
            return false;
        }
    }

    /// <summary>Saves made before slaves stopped claiming prisoner beds still list them as owners; free those slots.</summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class SlavePrisonerBeds_Load
    {
        public static void Postfix()
        {
            SlavePrisonerBeds_NoClaim.Clear();
            int cleared = 0;
            foreach (Map map in Find.Maps)
            {
                foreach (Building_Bed bed in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>())
                {
                    if (!bed.ForPrisoners)
                        continue;
                    foreach (Pawn owner in bed.OwnersForReading.ToList())
                    {
                        if (!owner.IsSlaveOfColony)
                            continue;
                        if (owner.ownership?.OwnedBed == bed)
                            owner.ownership.UnclaimBed();
                        else
                            bed.CompAssignableToPawn.ForceRemovePawn(owner);
                        SlavePrisonerBeds_NoClaim.Remember(owner, bed);
                        cleared++;
                    }
                }
            }
            if (cleared > 0)
                Log.Message($"[Vanilla Tune-Up] SlavePrisonerBeds: freed {cleared} prisoner bed slot(s) held by slaves; slaves now take any free prisoner bed.");
        }
    }

    /// <summary>
    /// The prisoner bed button and inspect line still say the beds are for prisoners only. Reword them to include
    /// slaves in English and Traditional Chinese, only where the loaded text is still the official one, so other
    /// translations and text mods are left alone.
    /// </summary>
    [HarmonyPatchCategory("SlavePrisonerBeds")]
    [HarmonyPatch(typeof(LoadedLanguage), nameof(LoadedLanguage.LoadData))]
    public static class SlavePrisonerBeds_Labels
    {
        private static readonly (string language, string key, string old, string text)[] Texts =
        {
            ("English", "CommandBedSetForPrisonersLabel", "For prisoners", "For prisoners and slaves"),
            ("English", "CommandBedSetForPrisonersDesc", "Whether beds in this room should be used by prisoners.",
                "Whether beds in this room should be used by prisoners and slaves."),
            ("English", "ReservedForPrisoners", "reserved for prisoners", "reserved for prisoners and slaves"),
            ("ChineseTraditional", "CommandBedSetForPrisonersLabel", "設為俘虜專用", "設為俘虜和奴隸用"),
            ("ChineseTraditional", "CommandBedSetForPrisonersDesc", "將這個房間内的床設為俘虜專用。", "將這個房間內的床設為俘虜和奴隸使用。"),
            ("ChineseTraditional", "ReservedForPrisoners", "預留給俘虜", "預留給俘虜和奴隸"),
        };

        /// <summary>Rewords the loaded languages now; languages loaded later go through the postfix.</summary>
        public static void Apply()
        {
            Postfix(LanguageDatabase.activeLanguage);
            if (LanguageDatabase.defaultLanguage != LanguageDatabase.activeLanguage)
                Postfix(LanguageDatabase.defaultLanguage);
        }

        public static void Postfix(LoadedLanguage __instance)
        {
            string folder = __instance?.folderName;
            if (folder == null)
                return;
            foreach ((string language, string key, string old, string text) in Texts)
            {
                if (folder.StartsWith(language) && __instance.keyedReplacements.TryGetValue(key, out LoadedLanguage.KeyedReplacement entry)
                    && entry.value == old)
                    entry.value = text;
            }
        }
    }
}
