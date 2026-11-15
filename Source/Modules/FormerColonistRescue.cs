using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// The "prisoner willing to join" rescue quest asks for a former colonist first
    /// (forceRedressWorldPawnIfFormerColonist, any faction): a colonist kidnapped and later recruited by the kidnappers
    /// is meant to come back as the prisoner to rescue. The candidate must still be of the slave kind's race (human)
    /// and, with Biotech, of a xenotype in the holding faction's xenotype set. A former colonist of a modded humanlike
    /// race, or of a xenotype that faction does not generate (a sanguophage held by pirates), was never picked.
    /// For such requests a humanlike former colonist filling a humanlike kind skips the race and xenotype set checks.
    /// Every other check (forced xenotype/genes, health, skills, age, the creepjoiner exclusion, ...) stays vanilla.
    /// The pick among former colonists is also weighted by the days each spent as a colonist (see below), and only
    /// takes factionless ones or members of factions hostile to the player.
    /// </summary>
    [HarmonyPatchCategory("FormerColonistRescue")]
    [HarmonyPatch(typeof(PawnGenerator), "IsValidCandidateToRedress")]
    public static class FormerColonistRescue
    {
        // Race of the candidate being checked when its race may differ from the kind's; null = vanilla checks.
        [System.ThreadStatic] private static ThingDef relaxedRace;

        private static readonly FieldInfo KindRaceField = AccessTools.Field(typeof(PawnKindDef), nameof(PawnKindDef.race));
        private static readonly MethodInfo XenotypeSetContains = AccessTools.Method(typeof(XenotypeSet), nameof(XenotypeSet.Contains));

        public static bool Relaxes(Pawn pawn, PawnGenerationRequest request) =>
            request.ForceRedressWorldPawnIfFormerColonist
            && pawn.RaceProps.Humanlike
            && PawnUtility.EverBeenColonistOrTameAnimal(pawn)
            && request.KindDef?.race?.race != null
            && request.KindDef.RaceProps.Humanlike;

        // The prisoner is moved into the (always hostile) holding faction. A former colonist who now lives with a
        // friendly or neutral faction, or the player's own, is left where they are; factionless or hostile ones only.
        public static bool LeftAlone(Pawn pawn, PawnGenerationRequest request) =>
            request.ForceRedressWorldPawnIfFormerColonist
            && pawn.Faction != null
            && !pawn.Faction.HostileTo(Faction.OfPlayer)
            && PawnUtility.EverBeenColonistOrTameAnimal(pawn);

        public static bool Prefix(Pawn pawn, PawnGenerationRequest request, ref bool __result)
        {
            relaxedRace = null;
            if (LeftAlone(pawn, request))
            {
                __result = false;
                return false;
            }
            relaxedRace = Relaxes(pawn, request) ? pawn.def : null;
            return true;
        }

        public static void Finalizer()
        {
            relaxedRace = null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool raceDone = false;
            int xenotypeChecks = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(XenotypeSetContains))
                {
                    // set.Contains(xenotype) -> SetContains(set, xenotype): same stack, faction and kind sets alike.
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(FormerColonistRescue), nameof(SetContains));
                    xenotypeChecks++;
                }
                yield return code;
                if (!raceDone && code.LoadsField(KindRaceField))
                {
                    // pawn.def != request.KindDef.race -> pawn.def != KindRace(request.KindDef.race)
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FormerColonistRescue), nameof(KindRace)));
                    raceDone = true;
                }
            }
            if (!raceDone || xenotypeChecks != 2)
                Log.Warning("[Vanilla Tune-Up] FormerColonistRescue: expected 1 race check and 2 xenotype set checks in "
                    + $"PawnGenerator.IsValidCandidateToRedress, found {(raceDone ? 1 : 0)} and {xenotypeChecks}.");
        }

        public static ThingDef KindRace(ThingDef kindRace) => relaxedRace ?? kindRace;

        public static bool SetContains(XenotypeSet set, XenotypeDef xenotype) => relaxedRace != null || set.Contains(xenotype);

        // True while a request that asks for a former colonist first is being generated on this thread.
        [System.ThreadStatic] internal static bool formerColonistPick;

        public static float DaysAsColonist(Pawn p) => p.records.GetValue(RecordDefOf.TimeAsColonistOrColonyAnimal) / GenDate.TicksPerDay;
    }

    // Vanilla picks the former colonist at random, so a refugee who stayed a few hours is as likely as someone who
    // lived in the colony for years. While such a request is generated, a former colonist's pick weight is scaled by
    // the days they spent as a colonist.
    [HarmonyPatchCategory("FormerColonistRescue")]
    [HarmonyPatch(typeof(PawnGenerator), "GenerateOrRedressPawnInternal")]
    public static class FormerColonistRescue_Generate
    {
        public static void Prefix(PawnGenerationRequest request, out bool __state)
        {
            __state = FormerColonistRescue.formerColonistPick;
            FormerColonistRescue.formerColonistPick = request.ForceRedressWorldPawnIfFormerColonist;
        }

        public static void Finalizer(bool __state)
        {
            FormerColonistRescue.formerColonistPick = __state;
        }
    }

    [HarmonyPatchCategory("FormerColonistRescue")]
    [HarmonyPatch(typeof(PawnGenerator), "WorldPawnSelectionWeight")]
    public static class FormerColonistRescue_Weight
    {
        public static void Postfix(Pawn p, ref float __result)
        {
            if (FormerColonistRescue.formerColonistPick && PawnUtility.EverBeenColonistOrTameAnimal(p))
                __result *= FormerColonistRescue.DaysAsColonist(p);
        }
    }
}
