using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// The form/reform caravan and transport loading lists do not show which faction a pawn belongs to, so guests,
    /// prisoners and colonists look the same. Every pawn row gets a column between the info button and the name;
    /// pawns of a non-player faction show their faction icon there (with the vanilla faction tooltip), other pawns
    /// leave it empty so the names stay aligned. The trade window is left alone since it already shows captive info.
    /// </summary>
    [HarmonyPatchCategory("CaravanFactionIcon")]
    [HarmonyPatch(typeof(TransferableOneWayWidget), "DoRow")]
    public static class Patch_TransferableOneWayWidget_DoRow
    {
        public static bool InRow;

        public static void Prefix() => InRow = true;

        public static void Finalizer() => InRow = false;
    }

    [HarmonyPatchCategory("CaravanFactionIcon")]
    [HarmonyPatch(typeof(TransferableUIUtility), nameof(TransferableUIUtility.DrawTransferableInfo))]
    public static class Patch_TransferableUIUtility_DrawTransferableInfo
    {
        // Vanilla draws the label at x = 80, right after the info card button.
        private const float VanillaLabelX = 80f;
        private const float IconSize = 24f;
        private const float ColumnWidth = 28f;

        private static float labelX = VanillaLabelX;

        public static float LabelX() => labelX;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction code in instructions)
            {
                // Label rect x and width (idRect.width - 80f).
                if (code.opcode == OpCodes.Ldc_R4 && code.operand is float f && f == VanillaLabelX)
                {
                    replaced++;
                    yield return CodeInstruction.Call(typeof(Patch_TransferableUIUtility_DrawTransferableInfo), nameof(LabelX)).MoveLabelsFrom(code);
                    continue;
                }
                yield return code;
            }
            if (replaced == 0)
                Log.Warning("[Vanilla Tune-Up] CaravanFactionIcon: label position not found, faction icons are drawn over the name.");
        }

        public static void Prefix(Transferable trad)
        {
            bool pawnRow = Patch_TransferableOneWayWidget_DoRow.InRow && trad.AnyThing is Pawn;
            labelX = pawnRow ? VanillaLabelX + ColumnWidth : VanillaLabelX;
        }

        public static void Postfix(Transferable trad, Rect idRect)
        {
            if (labelX == VanillaLabelX)
                return;
            labelX = VanillaLabelX;
            Faction faction = (trad.AnyThing as Pawn)?.Faction;
            // Factions without an icon path get the red-cross BadTex from FactionDef.FactionIcon; leave those blank.
            Texture2D icon = faction?.def.FactionIcon;
            if (icon == null || icon == BaseContent.BadTex || faction.IsPlayer)
                return;
            var iconRect = new Rect(VanillaLabelX, (idRect.height - IconSize) / 2f, IconSize, IconSize);
            FactionUIUtility.DrawFactionIconWithTooltip(iconRect, faction);
        }

        public static void Finalizer() => labelX = VanillaLabelX;
    }
}
