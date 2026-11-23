using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace VanillaTuneUp
{
    /// <summary>
    /// Left-clicking a letter in the stack jumps to what it points at and dismisses it, instead of opening it, when the
    /// letter's dialog would offer nothing but "Close" and "Jump to location". Such letters carry a small jump badge so
    /// the outcome of a click is visible beforehand, and their tooltip (which already holds the full text) ends with a
    /// hint line. Letters with a choice, a quest link or info-card links open as usual.
    /// Opening from the History tab is untouched: only clicks on the letter stack are redirected.
    /// </summary>
    public static class LetterJumpClick
    {
        private static Texture2D badgeTex;
        private static bool badgeTexLoaded;

        /// <summary>The letter whose stack button is being drawn, so OpenLetter can tell a stack click from other callers.</summary>
        internal static Letter drawingButton;

        /// <summary>The letter whose stack tooltip is being built, so the hint line stays out of the History tab.</summary>
        internal static Letter buildingTooltip;

        private static Texture2D BadgeTex
        {
            get
            {
                if (!badgeTexLoaded)
                {
                    badgeTexLoaded = true;
                    badgeTex = ContentFinder<Texture2D>.Get("UI/VanillaTuneUp/LetterJumpBadge", reportFailure: false);
                }
                return badgeTex;
            }
        }

        /// <summary>
        /// Exactly a vanilla StandardLetter (subclasses may change their dialog) whose dialog holds only Close and
        /// Jump to location, with a target the camera can reach.
        /// </summary>
        public static bool Eligible(Letter letter)
        {
            if (letter == null || letter.GetType() != typeof(StandardLetter))
                return false;
            StandardLetter standard = (StandardLetter)letter;
            if (!standard.CanDismissWithRightClick || !standard.lookTargets.IsValid())
                return false;
            if (standard.quest != null && !standard.quest.hidden)
                return false;
            if (!standard.hyperlinkThingDefs.NullOrEmpty() || !standard.hyperlinkHediffDefs.NullOrEmpty())
                return false;
            return CameraJumper.CanJump(standard.lookTargets.TryGetPrimaryTarget());
        }

        /// <summary>Same as the dialog's "Jump to location" option.</summary>
        public static void JumpAndDismiss(Letter letter)
        {
            CameraJumper.TryJumpAndSelect(letter.lookTargets.TryGetPrimaryTarget());
            Find.LetterStack.RemoveLetter(letter);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }

        /// <summary>Draws the badge on the icon's top-right corner, following the icon's fall-in and bounce offsets.</summary>
        public static void DrawBadge(Letter letter, float topY)
        {
            Texture2D tex = BadgeTex;
            if (tex == null)
                return;
            // Mirrors the icon rect math in Letter.DrawButtonAt.
            Rect rect = new Rect(UI.screenWidth - Letter.DrawWidth - 12f, topY, Letter.DrawWidth, Letter.DrawHeight);
            Rect icon = rect;
            float age = Time.time - letter.arrivalTime;
            float alpha = 1f;
            if (age < 1f)
            {
                icon.y -= (1f - age) * 200f;
                alpha = age;
            }
            if (!Mouse.IsOver(rect) && letter.def.bounce && age > 15f && age % 5f < 1f)
            {
                float t = 2f * (age % 1f) - 1f;
                icon.x -= UI.screenWidth * 0.06f * (1f - t * t);
            }
            // The pin's point sits on the icon's top-right corner; the head rises into the gap above.
            const float size = 16f;
            Rect badge = new Rect(icon.xMax - size + 4f, icon.y - 7f, size, size);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(badge, tex);
            GUI.color = Color.white;
        }
    }

    [HarmonyPatchCategory("LetterJumpClick")]
    [HarmonyPatch(typeof(Letter), nameof(Letter.DrawButtonAt))]
    public static class Patch_Letter_DrawButtonAt_LetterJumpClick
    {
        public static void Prefix(Letter __instance) => LetterJumpClick.drawingButton = __instance;

        public static void Postfix(Letter __instance, float topY)
        {
            LetterJumpClick.drawingButton = null;
            if (Event.current.type == EventType.Repaint && LetterJumpClick.Eligible(__instance))
                LetterJumpClick.DrawBadge(__instance, topY);
        }

        public static void Finalizer() => LetterJumpClick.drawingButton = null;
    }

    /// <summary>StandardLetter does not override OpenLetter, so the hook sits on ChoiceLetter and filters by type.</summary>
    [HarmonyPatchCategory("LetterJumpClick")]
    [HarmonyPatch(typeof(ChoiceLetter), nameof(ChoiceLetter.OpenLetter))]
    public static class Patch_ChoiceLetter_OpenLetter_LetterJumpClick
    {
        public static bool Prefix(ChoiceLetter __instance)
        {
            if (LetterJumpClick.drawingButton != __instance || !LetterJumpClick.Eligible(__instance))
                return true;
            LetterJumpClick.JumpAndDismiss(__instance);
            return false;
        }
    }

    [HarmonyPatchCategory("LetterJumpClick")]
    [HarmonyPatch(typeof(Letter), nameof(Letter.CheckForMouseOverTextAt))]
    public static class Patch_Letter_CheckForMouseOverTextAt_LetterJumpClick
    {
        public static void Prefix(Letter __instance) => LetterJumpClick.buildingTooltip = __instance;

        public static void Finalizer() => LetterJumpClick.buildingTooltip = null;
    }

    [HarmonyPatchCategory("LetterJumpClick")]
    [HarmonyPatch(typeof(ChoiceLetter), "GetMouseoverText")]
    public static class Patch_ChoiceLetter_GetMouseoverText_LetterJumpClick
    {
        public static void Postfix(ChoiceLetter __instance, ref string __result)
        {
            if (LetterJumpClick.buildingTooltip != __instance || !LetterJumpClick.Eligible(__instance))
                return;
            __result = __result + "\n\n" + "VTU_LetterJumpClick_Hint".Translate().Colorize(ColoredText.SubtleGrayColor);
        }
    }
}
