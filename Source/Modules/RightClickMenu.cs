using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// While a map right-click menu is open, FloatMenuMap regenerates every option for the selected pawn every 4th
    /// frame (FloatMenuMakerMap.GetOptions) just to grey out options that stopped being valid. With work-related mods
    /// one regeneration can take 15-30 ms, so the menu stutters about 15 times a second. Regenerates every 5 seconds
    /// instead. Choosing an option is still checked against freshly generated options (PreOptionChosen), so a stale
    /// option is greyed out on click instead of being executed.
    /// Opening the menu also regenerated the options twice more in its first frame: once because the menu started
    /// without revalidation options, once because options tied to the clicked pawn or thing are looked up by that
    /// cell's center while the cache is keyed by the exact click position. The menu now starts from the options it
    /// was opened with, and the clicked cell's center shares the click position's cache entry.
    /// </summary>
    [HarmonyPatchCategory("RightClickMenu")]
    [HarmonyPatch(typeof(FloatMenuMap), nameof(FloatMenuMap.DoWindowContents))]
    public static class Patch_FloatMenuMap_DoWindowContents
    {
        private const float RevalidateSeconds = 5f;

        private static readonly ConditionalWeakTable<FloatMenuMap, StrongBox<float>> lastRevalidation =
            new ConditionalWeakTable<FloatMenuMap, StrongBox<float>>();

        private static readonly MethodInfo FrameCount = AccessTools.PropertyGetter(typeof(Time), nameof(Time.frameCount));

        private static readonly MethodInfo CacheAdd =
            AccessTools.Method(typeof(Dictionary<Vector3, List<FloatMenuOption>>), nameof(Dictionary<Vector3, List<FloatMenuOption>>.Add));

        private static readonly AccessTools.FieldRef<FloatMenuMap, Vector3> clickPos =
            AccessTools.FieldRefAccess<FloatMenuMap, Vector3>("clickPos");

        private static readonly AccessTools.FieldRef<FloatMenuMap, List<FloatMenuOption>> lastOptionsForRevalidation =
            AccessTools.FieldRefAccess<FloatMenuMap, List<FloatMenuOption>>("lastOptionsForRevalidation");

        private static readonly AccessTools.FieldRef<Dictionary<Vector3, List<FloatMenuOption>>> cachedChoices =
            AccessTools.StaticFieldRefAccess<Dictionary<Vector3, List<FloatMenuOption>>>(AccessTools.Field(typeof(FloatMenuMap), "cachedChoices"));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            bool gated = false;
            for (int i = 0; i + 2 < codes.Count; i++)
            {
                // Time.frameCount % 4 -> FramesUntilRevalidate(this); the following branch treats 0 as "regenerate now".
                if (codes[i].Calls(FrameCount) && codes[i + 1].LoadsConstant(4) && codes[i + 2].opcode == System.Reflection.Emit.OpCodes.Rem)
                {
                    codes[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0).MoveLabelsFrom(codes[i]);
                    codes[i + 1] = CodeInstruction.Call(typeof(Patch_FloatMenuMap_DoWindowContents), nameof(RevalidateGate));
                    codes.RemoveAt(i + 2);
                    gated = true;
                    break;
                }
            }
            if (!gated)
            {
                Log.Warning("[Vanilla Tune-Up] Could not find the revalidation interval in FloatMenuMap.DoWindowContents; menu left unchanged.");
                return codes;
            }
            // cachedChoices.Add(clickPos, lastOptionsForRevalidation); -> also cache the clicked cell's center.
            int add = codes.FindIndex(c => c.Calls(CacheAdd));
            if (add < 0)
                Log.Warning("[Vanilla Tune-Up] Could not find the option cache in FloatMenuMap.DoWindowContents; clicked targets are re-checked separately.");
            else
                codes.InsertRange(add + 1, new[]
                {
                    new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0),
                    CodeInstruction.Call(typeof(Patch_FloatMenuMap_DoWindowContents), nameof(CacheClickedCell)),
                });
            return codes;
        }

        /// <summary>
        /// Starts a new menu from the options it was opened with, so its first frame does not regenerate them.
        /// Called from the FloatMenuMap constructor postfix.
        /// </summary>
        public static void StartFrom(FloatMenuMap menu, List<FloatMenuOption> options)
        {
            lastRevalidation.GetValue(menu, _ => new StrongBox<float>()).Value = Time.realtimeSinceStartup;
            lastOptionsForRevalidation(menu) = options;
            cachedChoices().Clear();
            cachedChoices().Add(clickPos(menu), options);
            CacheClickedCell(menu);
        }

        /// <summary>
        /// Options tied to a pawn or thing on the clicked cell are re-checked at that cell's center; point it at the
        /// options just generated for the click position instead of generating them again.
        /// </summary>
        public static void CacheClickedCell(FloatMenuMap menu)
        {
            Vector3 center = clickPos(menu).ToIntVec3().ToVector3Shifted();
            Dictionary<Vector3, List<FloatMenuOption>> cache = cachedChoices();
            if (!cache.ContainsKey(center))
                cache.Add(center, lastOptionsForRevalidation(menu));
        }

        /// <summary>Returns 0 when the menu should regenerate its options this frame, 1 otherwise.</summary>
        public static int RevalidateGate(FloatMenuMap menu)
        {
            float now = Time.realtimeSinceStartup;
            StrongBox<float> last = lastRevalidation.GetValue(menu, _ => new StrongBox<float>(float.NegativeInfinity));
            if (now - last.Value < RevalidateSeconds)
                return 1;
            last.Value = now;
            return 0;
        }
    }

    [HarmonyPatchCategory("RightClickMenu")]
    [HarmonyPatch(typeof(FloatMenuMap), MethodType.Constructor, typeof(List<FloatMenuOption>), typeof(string), typeof(Vector3))]
    public static class Patch_FloatMenuMap_Ctor
    {
        public static void Postfix(FloatMenuMap __instance, List<FloatMenuOption> options) =>
            Patch_FloatMenuMap_DoWindowContents.StartFrom(__instance, options);
    }
}
