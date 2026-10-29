using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace VanillaTuneUp
{
    /// <summary>
    /// When the letter stack overflows, vanilla folds the oldest letters into one "N more..." bundle letter that
    /// ignores right-click. Right-clicking it now dismisses every bundled letter that can be dismissed with a right
    /// click on its own, so letters that still wait for a choice (joiners, visitors, etc.) stay in the stack.
    /// BundleLetter does not override DrawButtonAt, so the hook sits on Letter and filters by type.
    /// </summary>
    [HarmonyPatchCategory("BundleLetterDismiss")]
    [HarmonyPatch(typeof(Letter), nameof(Letter.DrawButtonAt))]
    public static class Patch_Letter_DrawButtonAt
    {
        private static readonly AccessTools.FieldRef<BundleLetter, List<Letter>> BundledLetters =
            AccessTools.FieldRefAccess<BundleLetter, List<Letter>>("bundledLetters");

        public static void Prefix(Letter __instance, float topY)
        {
            if (!(__instance is BundleLetter bundle))
                return;
            Event ev = Event.current;
            if (ev.type != EventType.MouseDown || ev.button != 1)
                return;
            // Same hit rect as vanilla's right-click check in Letter.DrawButtonAt.
            Rect rect = new Rect(UI.screenWidth - 38f - 12f, topY, 38f, 30f);
            if (!Mouse.IsOver(rect))
                return;
            List<Letter> targets = new List<Letter>(BundledLetters(bundle));
            foreach (Letter letter in targets)
            {
                if (letter.CanDismissWithRightClick && Find.LetterStack.LettersListForReading.Contains(letter))
                    Find.LetterStack.RemoveLetter(letter);
            }
            SoundDefOf.Click.PlayOneShotOnCamera();
            ev.Use();
        }
    }
}
