using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace VanillaTuneUp
{
    /// <summary>
    /// The psychic ritual dialog lists non-participating pawns in map order and only shows psychic sensitivity in the
    /// tooltip. Sort the candidates by sensitivity (highest first) and print the value on each portrait.
    /// The portrait code lives in the generic PawnRoleSelectionWidgetBase&lt;T&gt;, whose shared code Harmony cannot
    /// patch reliably on Mono (the patch stopped applying after the first dialog), so only non-generic methods are
    /// patched here. Other ritual dialogs are left alone.
    /// </summary>
    [HarmonyPatchCategory("PsychicRitualSensitivity")]
    [HarmonyPatch(typeof(Dialog_BeginPsychicRitual), MethodType.Constructor, typeof(PsychicRitualDef),
        typeof(PsychicRitualCandidatePool), typeof(PsychicRitualRoleAssignments), typeof(Map))]
    public static class Patch_Dialog_BeginPsychicRitual_Ctor
    {
        // The pool is built fresh for each dialog and only read by the dialog's pawn list (role assignments are built
        // separately), so sorting it in place changes the display order and nothing else.
        public static void Postfix(PsychicRitualCandidatePool candidatePool)
        {
            if (candidatePool == null)
                return;
            SortStable(candidatePool.AllCandidatePawns);
            SortStable(candidatePool.NonAssignablePawns);
        }

        // List.Sort is unstable; OrderByDescending keeps map order among pawns with equal sensitivity.
        private static void SortStable(List<Pawn> pawns)
        {
            if (pawns == null || pawns.Count < 2)
                return;
            List<Pawn> sorted = pawns.OrderByDescending(PsychicRitualSensitivity.Sensitivity).ToList();
            pawns.Clear();
            pawns.AddRange(sorted);
        }
    }

    [HarmonyPatchCategory("PsychicRitualSensitivity")]
    [HarmonyPatch(typeof(Dialog_BeginLordJob), nameof(Dialog_BeginLordJob.DoWindowContents))]
    public static class Patch_Dialog_BeginLordJob_DoWindowContents
    {
        public static void Prefix(Dialog_BeginLordJob __instance) =>
            PsychicRitualSensitivity.Drawing = __instance is Dialog_BeginPsychicRitual;

        public static void Finalizer() => PsychicRitualSensitivity.Drawing = false;
    }

    // Called once per portrait in the role selection widget, right after the portrait itself is drawn.
    [HarmonyPatchCategory("PsychicRitualSensitivity")]
    [HarmonyPatch(typeof(PawnPortraitIconsDrawer), nameof(PawnPortraitIconsDrawer.DrawPawnPortraitIcons))]
    public static class Patch_PawnPortraitIconsDrawer_DrawPawnPortraitIcons
    {
        public static void Postfix(Rect portraitRect, Pawn p)
        {
            if (PsychicRitualSensitivity.Drawing)
                PsychicRitualSensitivity.DrawLabel(portraitRect, p);
        }
    }

    public static class PsychicRitualSensitivity
    {
        public static bool Drawing;

        private const float LabelHeight = 16f;
        private static readonly Color LabelBackground = new Color(0f, 0f, 0f, 0.55f);

        public static float Sensitivity(Pawn pawn) => pawn.GetStatValue(StatDefOf.PsychicSensitivity);

        // Top-left corner of the portrait: vanilla draws its status icons along the bottom edge.
        public static void DrawLabel(Rect portraitRect, Pawn pawn)
        {
            string text = Sensitivity(pawn).ToStringPercent();
            using (new TextBlock(GameFont.Tiny, TextAnchor.MiddleLeft, Color.white))
            {
                float scale = portraitRect.height / 50f;
                float width = Text.CalcSize(text).x + 4f;
                Rect rect = new Rect(portraitRect.x, portraitRect.y, Mathf.Min(width, portraitRect.width), LabelHeight * scale);
                Widgets.DrawBoxSolid(rect, LabelBackground);
                rect.xMin += 2f;
                Widgets.Label(rect, text);
            }
        }
    }
}
