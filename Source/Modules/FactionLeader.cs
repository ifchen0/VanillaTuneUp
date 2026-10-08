using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla FactionTick only logs "Faction leader for X is null." and never repairs it. Before that check runs,
    /// generate a new leader the same way vanilla does when a leader dies. Each faction is tried at most once per
    /// game session so a faction that cannot produce a leader does not retry every tick.
    /// </summary>
    [HarmonyPatchCategory("FactionLeader")]
    [HarmonyPatch(typeof(Faction), nameof(Faction.FactionTick))]
    public static class Patch_Faction_FactionTick
    {
        private static readonly HashSet<int> attempted = new HashSet<int>();
        private static Game attemptedGame;

        public static void Prefix(Faction __instance)
        {
            if (__instance.leader != null || !ShouldHaveLeader(__instance))
                return;
            if (attemptedGame != Current.Game)
            {
                attempted.Clear();
                attemptedGame = Current.Game;
            }
            if (!attempted.Add(__instance.loadID))
                return;

            bool ok;
            try
            {
                ok = __instance.TryGenerateNewLeader();
            }
            catch (Exception e)
            {
                Log.Warning($"[Vanilla Tune-Up] Failed to generate a new leader for {__instance.Name}: {e}");
                return;
            }
            if (ok)
                Log.Message($"[Vanilla Tune-Up] {__instance.Name} had no leader; generated {__instance.leader.LabelShort} ({__instance.leader.kindDef.defName}).");
            else
                Log.Warning($"[Vanilla Tune-Up] {__instance.Name} ({__instance.def.defName}) has no leader and its def cannot produce one.");
        }

        // Mirrors the private Faction.ShouldHaveLeader.
        private static bool ShouldHaveLeader(Faction f)
        {
            return !f.IsPlayer && !f.Hidden && !f.temporary && f.def.humanlikeFaction;
        }
    }
}
