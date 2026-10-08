using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// TradeDeal.AddAllTradeables adds every colony item through TransferableUtility.TradeableMatching, which scans all
    /// tradeables created so far and evaluates TraderWillTrade (a loop over the trader's stock generators) for each of
    /// them. With thousands of items that is hundreds of thousands of checks, several seconds per deal.
    /// TransferAsOne never matches things of different defs, so while the deal is being built, the tradeables are
    /// indexed by the def of their AnyThing and only the ones with the same def are checked, in the original order.
    /// </summary>
    [HarmonyPatchCategory("TradeDeal")]
    [HarmonyPatch(typeof(TradeDeal), "AddAllTradeables")]
    public static class Patch_TradeDeal_AddAllTradeables
    {
        private static readonly AccessTools.FieldRef<TradeDeal, List<Tradeable>> Tradeables =
            AccessTools.FieldRefAccess<TradeDeal, List<Tradeable>>("tradeables");

        internal static TradeableIndex Active;

        public static void Prefix(TradeDeal __instance)
        {
            Active = new TradeableIndex(Tradeables(__instance));
        }

        public static Exception Finalizer(Exception __exception)
        {
            Active = null;
            return __exception;
        }
    }

    [HarmonyPatchCategory("TradeDeal")]
    [HarmonyPatch(typeof(TransferableUtility), nameof(TransferableUtility.TradeableMatching))]
    public static class Patch_TransferableUtility_TradeableMatching
    {
        public static bool Prefix(Thing thing, List<Tradeable> tradeables, ref Tradeable __result)
        {
            TradeableIndex index = Patch_TradeDeal_AddAllTradeables.Active;
            if (index == null || thing == null || !index.TryMatch(thing, tradeables, out __result))
                return true;
            return false;
        }
    }

    internal class TradeableIndex
    {
        private readonly List<Tradeable> list;
        private readonly Dictionary<ThingDef, List<Tradeable>> byDef = new Dictionary<ThingDef, List<Tradeable>>();
        private int synced;
        private bool broken;

        public TradeableIndex(List<Tradeable> list)
        {
            this.list = list;
        }

        public bool TryMatch(Thing thing, List<Tradeable> tradeables, out Tradeable result)
        {
            result = null;
            if (broken || tradeables != list || !Sync())
                return false;
            if (!byDef.TryGetValue(thing.def, out List<Tradeable> candidates))
                return true;
            for (int i = 0; i < candidates.Count; i++)
            {
                Tradeable tradeable = candidates[i];
                TransferAsOneMode mode = tradeable.TraderWillTrade ? TransferAsOneMode.Normal : TransferAsOneMode.InactiveTradeable;
                if (TransferableUtility.TransferAsOne(thing, tradeable.AnyThing, mode))
                {
                    result = tradeable;
                    return true;
                }
            }
            return true;
        }

        // Indexes tradeables appended since the last call. The list only grows while the deal is being built; anything
        // unexpected (shrinking, an entry without things) disables the index and leaves the rest to vanilla.
        private bool Sync()
        {
            if (list.Count < synced)
            {
                broken = true;
                return false;
            }
            for (; synced < list.Count; synced++)
            {
                Tradeable tradeable = list[synced];
                if (tradeable == null)
                    continue;
                if (!tradeable.HasAnyThing)
                {
                    broken = true;
                    return false;
                }
                ThingDef def = tradeable.AnyThing.def;
                if (!byDef.TryGetValue(def, out List<Tradeable> bucket))
                    byDef[def] = bucket = new List<Tradeable>();
                bucket.Add(tradeable);
            }
            return true;
        }
    }

    /// <summary>
    /// Auto Seller executes its trade the way the trade dialog does and then calls TradeDeal.Reset, which rebuilds the
    /// whole deal for a dialog that does not exist. Nothing reads the deal afterwards, so it is only emptied instead.
    /// </summary>
    [HarmonyPatchCategory("TradeDeal")]
    [HarmonyPatch]
    public static class Patch_AutoSeller_Forcetrade
    {
        private static readonly MethodInfo Reset = AccessTools.Method(typeof(TradeDeal), nameof(TradeDeal.Reset));

        private static readonly AccessTools.FieldRef<TradeDeal, List<Tradeable>> Tradeables =
            AccessTools.FieldRefAccess<TradeDeal, List<Tradeable>>("tradeables");

        private static readonly AccessTools.FieldRef<TradeDeal, List<string>> CannotSellReasons =
            AccessTools.FieldRefAccess<TradeDeal, List<string>>("cannotSellReasons");

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => AccessTools.Method("RWAutoSell.ASTrade:Forcetrade");

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(Reset))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(Patch_AutoSeller_Forcetrade), nameof(Clear));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                Log.Warning($"[Vanilla Tune-Up] Expected one TradeDeal.Reset call in Auto Seller's Forcetrade, found {replaced}.");
        }

        public static void Clear(TradeDeal deal)
        {
            Tradeables(deal).Clear();
            CannotSellReasons(deal).Clear();
        }
    }
}
