using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Refugees from the hospitality quest are quest lodgers, and vanilla keeps every lodger out of apparel
    /// optimization and shows "Unchangeable" for their apparel policy. Refugees then sit in tattered clothes: the
    /// quest locks what they arrive in, but vanilla unlocks a piece once it drops below 50% hit points, and only then
    /// it counts for the tattered apparel thought. Lets refugees follow an apparel policy like colonists, so they
    /// replace those unlocked pieces; still-locked apparel stays on. Other lodgers (guards, royal guests, prisoner
    /// escorts and so on) keep the vanilla behaviour. Optionally gives arriving refugees the apparel policy named in
    /// the settings, and puts a recruited refugee back on the default policy if still on it, like the slave policy in
    /// PrisonerDefaultFoodPolicy.
    /// </summary>
    public static class RefugeeApparel
    {
        /// <summary>The refugee apparel policy named in the settings in the current game, or null when unset or missing.</summary>
        public static ApparelPolicy Configured() =>
            PrisonerDefaultFoodPolicy.FindApparelPolicy(VanillaTuneUpMod.Settings.refugeeApparelPolicy);

        public static List<Pawn> CurrentRefugees() =>
            PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Where(IsRefugee).ToList();

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            PrisonerDefaultFoodPolicy.DrawApparelPicker(listing, "VTU_RefugeeApparelLabel", "VTU_PrisonerPolicyNone",
                "VTU_RefugeeApparelMissing", settings.refugeeApparelPolicy, label => settings.refugeeApparelPolicy = label);
            ApparelPolicy policy = Configured();
            PrisonerDefaultFoodPolicy.DrawApplyButton(listing, policy?.label, "VTU_RefugeeApparelApplyAll",
                "VTU_RefugeeApparelApplied", Current.Game == null ? new List<Pawn>() : CurrentRefugees(),
                pawn => PrisonerDefaultFoodPolicy.SetApparel(pawn, policy));
        }

        /// <summary>Like vanilla IsQuestLodger, but false for a pawn lodging only through refugee quests.</summary>
        public static bool IsLockedLodger(Pawn pawn) => pawn.IsQuestLodger() && !IsRefugee(pawn);

        public static bool IsRefugee(Pawn pawn)
        {
            QuestScriptDef refugeeQuest = QuestScriptDefOf.Hospitality_Refugee;
            if (refugeeQuest == null)
                return false;
            bool found = false;
            List<QuestPart_ExtraFaction> parts = Find.QuestManager.ExtraFactionQuestParts;
            for (int i = 0; i < parts.Count; i++)
            {
                QuestPart_ExtraFaction part = parts[i];
                if (part.extraFaction?.faction == null || part.quest.State != QuestState.Ongoing
                    || !part.affectedPawns.Contains(pawn))
                    continue;
                // Also lodging for another quest: that quest's rules win.
                if (part.quest.root != refugeeQuest)
                    return false;
                found = true;
            }
            return found;
        }

        /// <summary>Replaces each IsQuestLodger call with IsLockedLodger.</summary>
        public static IEnumerable<CodeInstruction> ReplaceLodgerCheck(IEnumerable<CodeInstruction> instructions,
            string target)
        {
            MethodInfo isQuestLodger = AccessTools.Method(typeof(QuestUtility), nameof(QuestUtility.IsQuestLodger));
            MethodInfo isLockedLodger = AccessTools.Method(typeof(RefugeeApparel), nameof(IsLockedLodger));
            bool replaced = false;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(isQuestLodger))
                {
                    code.operand = isLockedLodger;
                    replaced = true;
                }
                yield return code;
            }
            if (!replaced)
                Log.Warning($"[Vanilla Tune-Up] RefugeeApparel: no lodger check found in {target}.");
        }
    }

    [HarmonyPatchCategory("RefugeeApparel")]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    public static class Patch_Pawn_SetFaction_RefugeeApparel
    {
        // Refugees join the colony when they arrive; the quest is already ongoing by then.
        public static void Postfix(Pawn __instance, Faction newFaction)
        {
            if (newFaction == null || newFaction != Faction.OfPlayer || __instance.outfits == null)
                return;
            ApparelPolicy policy = RefugeeApparel.Configured();
            if (policy != null && RefugeeApparel.IsRefugee(__instance))
                __instance.outfits.CurrentApparelPolicy = policy;
        }
    }

    [HarmonyPatchCategory("RefugeeApparel")]
    [HarmonyPatch(typeof(QuestPart_ExtraFaction), nameof(QuestPart_ExtraFaction.Notify_QuestSignalReceived))]
    public static class Patch_QuestPart_ExtraFaction_RefugeeLeft
    {
        // A recruited refugee is taken out of the quest's extra faction here and stays a colonist.
        public static void Prefix(QuestPart_ExtraFaction __instance, out List<Pawn> __state)
        {
            __state = __instance.quest?.root != null && __instance.quest.root == QuestScriptDefOf.Hospitality_Refugee
                ? new List<Pawn>(__instance.affectedPawns)
                : null;
        }

        public static void Postfix(QuestPart_ExtraFaction __instance, List<Pawn> __state)
        {
            if (__state == null)
                return;
            ApparelPolicy policy = RefugeeApparel.Configured();
            if (policy == null)
                return;
            foreach (Pawn pawn in __state)
            {
                if (__instance.affectedPawns.Contains(pawn) || pawn.outfits == null || !pawn.IsColonist
                    || pawn.IsQuestLodger() || pawn.outfits.CurrentApparelPolicy != policy)
                    continue;
                pawn.outfits.CurrentApparelPolicy = Current.Game.outfitDatabase.DefaultOutfit();
            }
        }
    }

    [HarmonyPatchCategory("RefugeeApparel")]
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob")]
    public static class Patch_JobGiver_OptimizeApparel_TryGiveJob
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RefugeeApparel.ReplaceLodgerCheck(instructions, "JobGiver_OptimizeApparel.TryGiveJob");
    }

    [HarmonyPatchCategory("RefugeeApparel")]
    [HarmonyPatch(typeof(PawnColumnWorker_Outfit), nameof(PawnColumnWorker_Outfit.DoCell))]
    public static class Patch_PawnColumnWorker_Outfit_DoCell
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RefugeeApparel.ReplaceLodgerCheck(instructions, "PawnColumnWorker_Outfit.DoCell");
    }
}
