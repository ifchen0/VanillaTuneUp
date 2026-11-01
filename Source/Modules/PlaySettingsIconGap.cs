using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// The map/world view buttons in the bottom-right corner are laid out by one WidgetRow. Toggle icons advance it
    /// by 24 + gap, but the search and entity codex buttons use WidgetRow.ButtonIcon, which advances by 24 only, so
    /// every icon drawn after them (including other mods' icons) sits 4 px off the grid. The ButtonIcon calls in
    /// vanilla's two control methods are redirected to a wrapper that adds the missing gap; their order is kept.
    /// </summary>
    [HarmonyPatchCategory("PlaySettingsIconGap")]
    [HarmonyPatch]
    public static class Patch_PlaySettings_IconGap
    {
        private static readonly MethodInfo ButtonIcon = AccessTools.Method(typeof(WidgetRow), nameof(WidgetRow.ButtonIcon));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlaySettings), "DoMapControls");
            yield return AccessTools.Method(typeof(PlaySettings), "DoWorldViewControls");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            int replaced = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(ButtonIcon))
                {
                    replaced++;
                    yield return CodeInstruction.Call(typeof(Patch_PlaySettings_IconGap), nameof(ButtonIconWithGap)).MoveLabelsFrom(code).MoveBlocksFrom(code);
                    continue;
                }
                yield return code;
            }
            if (replaced == 0)
                Log.Warning($"[Vanilla Tune-Up] PlaySettingsIconGap: no icon button found in {__originalMethod.Name}.");
        }

        public static bool ButtonIconWithGap(WidgetRow row, Texture2D tex, string tooltip, Color? mouseoverColor, Color? backgroundColor,
            Color? mouseoverBackgroundColor, bool doMouseoverSound, float overrideSize)
        {
            bool result = row.ButtonIcon(tex, tooltip, mouseoverColor, backgroundColor, mouseoverBackgroundColor, doMouseoverSound, overrideSize);
            // Gap does nothing at the start of a row, so wrapping matches ToggleableIcon.
            row.Gap(row.CellGap);
            return result;
        }
    }
}
