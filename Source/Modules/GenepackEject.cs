using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Genepacks ejected from a gene bank keep their "auto-load" toggle on, so the next hauler carries them straight
    /// back into the nearest bank that allows all genepacks. Ejecting by hand is a request to take the pack out, so
    /// turn auto-load off for every pack the player ejects. A bank that is deconstructed, minified or destroyed also
    /// drops its packs, but there the vanilla re-haul into another bank is wanted, so that path is left alone.
    /// </summary>
    public static class GenepackEject
    {
        private static readonly AccessTools.FieldRef<Genepack, bool> AutoLoad =
            AccessTools.FieldRefAccess<Genepack, bool>("autoLoad");

        public static void DisableAutoLoad(Thing t)
        {
            if (t is Genepack pack)
                AutoLoad(pack) = false;
        }
    }

    /// <summary>"Eject all" gizmo. PostDeSpawn also calls EjectContents, but only after the bank has despawned.</summary>
    [HarmonyPatchCategory("GenepackEject")]
    [HarmonyPatch(typeof(CompGenepackContainer), nameof(CompGenepackContainer.EjectContents))]
    public static class Patch_CompGenepackContainer_EjectContents
    {
        public static void Prefix(CompGenepackContainer __instance)
        {
            if (!__instance.parent.Spawned)
                return;
            foreach (Thing t in __instance.innerContainer)
                GenepackEject.DisableAutoLoad(t);
        }
    }

    /// <summary>Per-pack eject button in the gene bank's contents tab.</summary>
    [HarmonyPatchCategory("GenepackEject")]
    [HarmonyPatch(typeof(ITab_ContentsBase), "OnDropThing")]
    public static class Patch_ITab_ContentsBase_OnDropThing
    {
        public static void Prefix(Thing t)
        {
            if (t.ParentHolder is CompGenepackContainer)
                GenepackEject.DisableAutoLoad(t);
        }
    }

    /// <summary>
    /// "Eject genepack from gene bank" right-click option in the gene assembler's xenogerm dialog. The option's
    /// lambda drops the pack with innerContainer.TryDrop directly, so it is found by its call to
    /// GetGeneBankHoldingPack (vanilla's only caller) and patched itself.
    /// </summary>
    [HarmonyPatchCategory("GenepackEject")]
    [HarmonyPatch]
    public static class Patch_Dialog_CreateXenogerm_EjectOption
    {
        private static readonly MethodInfo GetHoldingBank =
            AccessTools.Method(typeof(Building_GeneAssembler), nameof(Building_GeneAssembler.GetGeneBankHoldingPack));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in typeof(Dialog_CreateXenogerm).GetNestedTypes(AccessTools.all).Prepend(typeof(Dialog_CreateXenogerm)))
            {
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.GetMethodBody() == null)
                        continue;
                    if (PatchProcessor.ReadMethodBody(method).Any(op => op.Value as MethodInfo == GetHoldingBank))
                        yield return method;
                }
            }
        }

        // The pack is either captured in a closure (field "genepack") or is the lambda's own instance member;
        // it is spawned afterwards only if TryDrop took it out of the bank.
        public static void Postfix(object __instance)
        {
            if (__instance == null)
                return;
            var pack = Traverse.Create(__instance).Field("genepack").GetValue() as Genepack;
            if (pack != null && pack.Spawned)
                GenepackEject.DisableAutoLoad(pack);
        }
    }
}
