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
    /// </summary>
    [HarmonyPatchCategory("RightClickMenu")]
    [HarmonyPatch(typeof(FloatMenuMap), nameof(FloatMenuMap.DoWindowContents))]
    public static class Patch_FloatMenuMap_DoWindowContents
    {
        private const float RevalidateSeconds = 5f;

        private static readonly ConditionalWeakTable<FloatMenuMap, StrongBox<float>> lastRevalidation =
            new ConditionalWeakTable<FloatMenuMap, StrongBox<float>>();

        private static readonly MethodInfo FrameCount = AccessTools.PropertyGetter(typeof(Time), nameof(Time.frameCount));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 2 < codes.Count; i++)
            {
                // Time.frameCount % 4 -> FramesUntilRevalidate(this); the following branch treats 0 as "regenerate now".
                if (codes[i].Calls(FrameCount) && codes[i + 1].LoadsConstant(4) && codes[i + 2].opcode == System.Reflection.Emit.OpCodes.Rem)
                {
                    codes[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0).MoveLabelsFrom(codes[i]);
                    codes[i + 1] = CodeInstruction.Call(typeof(Patch_FloatMenuMap_DoWindowContents), nameof(RevalidateGate));
                    codes.RemoveAt(i + 2);
                    return codes;
                }
            }
            Log.Warning("[Vanilla Tune-Up] Could not find the revalidation interval in FloatMenuMap.DoWindowContents; menu left unchanged.");
            return codes;
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
}
