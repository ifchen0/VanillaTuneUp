using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// A caravan entering a home map marks every member for unloading, but farskip only marks colonists, pack animals
    /// and colony mechs. Prisoners carrying caravan goods (and rebellious slaves, who are not IsColonist) keep them
    /// forever, since haulers only unload pawns flagged UnloadEverything. While a farskip is being applied, flag every
    /// skipped pawn that lands on a home map and belongs to or is held by the player. The setter ignores pawns with
    /// nothing to unload, and free colonists still unload themselves through the vanilla job giver.
    /// </summary>
    public static class FarskipUnload
    {
        public static bool Applying;
    }

    [HarmonyPatchCategory("FarskipUnload")]
    [HarmonyPatch(typeof(CompAbilityEffect_Farskip), nameof(CompAbilityEffect_Farskip.Apply), typeof(RimWorld.Planet.GlobalTargetInfo))]
    public static class FarskipUnload_Apply
    {
        public static void Prefix() => FarskipUnload.Applying = true;

        public static void Finalizer() => FarskipUnload.Applying = false;
    }

    [HarmonyPatchCategory("FarskipUnload")]
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Notify_Teleported))]
    public static class FarskipUnload_Teleported
    {
        public static void Postfix(Pawn __instance)
        {
            if (!FarskipUnload.Applying || __instance.inventory == null)
                return;
            Map map = __instance.MapHeld;
            if (map == null || !map.IsPlayerHome)
                return;
            if (__instance.Faction != Faction.OfPlayer && __instance.HostFaction != Faction.OfPlayer)
                return;
            __instance.inventory.UnloadEverything = true;
        }
    }
}
