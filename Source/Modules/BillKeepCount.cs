using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace VanillaTuneUp
{
    /// <summary>
    /// Smelt / destroy bill improvements. Items without quality count as the best quality for the bill's quality
    /// filter (vanilla lets them through any range). And a "Keep X of each" repeat mode for smelt / destroy style bills (no products, one single-item ingredient):
    /// for every item type the bill allows, the X most valuable on the map (market value) are left alone and
    /// only the rest are processed. The mode is never written to the save as a def: the bill is saved as
    /// "repeat 0 times" plus a VTU flag, so removing the mod leaves a bill that does nothing instead of a load error.
    /// </summary>
    public static class BillKeepCount
    {
        private const int CacheTicks = 250;

        private static BillRepeatModeDef mode;
        public static BillRepeatModeDef Mode => mode ??= new BillRepeatModeDef
        {
            defName = "VTU_KeepCount",
            label = "VTU_BillKeepCount_Mode".Translate(),
            listOrder = 100
        };

        public static bool IsKeepMode(Bill bill) =>
            bill is Bill_Production production && mode != null && production.repeatMode == mode;

        private static readonly Dictionary<RecipeDef, bool> eligible = new Dictionary<RecipeDef, bool>();

        /// <summary>Recipes that consume one weapon or apparel item and make no counted product: smelting, destroying, burning.</summary>
        public static bool Eligible(RecipeDef recipe)
        {
            if (recipe == null)
                return false;
            if (eligible.TryGetValue(recipe, out bool result))
                return result;
            result = recipe.products.NullOrEmpty()
                && recipe.ingredients != null && recipe.ingredients.Count == 1
                && recipe.ingredients[0].GetBaseCount() == 1f
                && recipe.fixedIngredientFilter != null
                && recipe.fixedIngredientFilter.AllowedDefCount > 0
                && recipe.fixedIngredientFilter.AllowedThingDefs.All(d => d.stackLimit == 1 && (d.IsWeapon || d.IsApparel));
            eligible[recipe] = result;
            return result;
        }

        // ---- Which things are kept ----

        private struct KeepKey : IEquatable<KeepKey>
        {
            public int MapId;
            public ThingDef Def;
            public int Keep;

            public bool Equals(KeepKey other) => MapId == other.MapId && Def == other.Def && Keep == other.Keep;
            public override bool Equals(object obj) => obj is KeepKey other && Equals(other);
            public override int GetHashCode() => Gen.HashCombineInt(Gen.HashCombineInt(MapId, Def.shortHash), Keep);
        }

        private class KeepEntry
        {
            public int Tick;
            /// <summary>Null when every thing of the def is kept (no more than X on the map).</summary>
            public HashSet<Thing> Kept;
        }

        private static readonly Dictionary<KeepKey, KeepEntry> keepCache = new Dictionary<KeepKey, KeepEntry>();
        private static readonly List<Thing> tmpThings = new List<Thing>();

        private static bool Expired(int tick)
        {
            int now = Find.TickManager.TicksGame;
            return now < tick || now - tick >= CacheTicks;
        }

        public static bool IsKept(Thing thing, int keep)
        {
            Map map = thing.MapHeld;
            if (map == null)
                return false;
            var key = new KeepKey { MapId = map.uniqueID, Def = thing.def, Keep = keep };
            if (!keepCache.TryGetValue(key, out KeepEntry entry) || Expired(entry.Tick))
            {
                entry = new KeepEntry { Tick = Find.TickManager.TicksGame, Kept = ComputeKept(map, thing.def, keep) };
                keepCache[key] = entry;
            }
            return entry.Kept == null || entry.Kept.Contains(thing);
        }

        /// <summary>The best <paramref name="keep"/> things of the def lying on the map, or null if there are no more than that.</summary>
        private static HashSet<Thing> ComputeKept(Map map, ThingDef def, int keep)
        {
            tmpThings.Clear();
            foreach (Thing thing in map.listerThings.ThingsOfDef(def))
            {
                if (thing.Spawned && !thing.Position.Fogged(map))
                    tmpThings.Add(thing);
            }
            if (tmpThings.Count <= keep)
            {
                tmpThings.Clear();
                return null;
            }
            // Market value already weighs quality, hit points, tainted apparel, biocoding and weapon traits.
            // Ties go to the older thing so the kept set stays stable between recomputes.
            tmpValues.Clear();
            foreach (Thing thing in tmpThings)
                tmpValues[thing] = thing.MarketValue;
            tmpThings.Sort((a, b) =>
            {
                int c = tmpValues[b].CompareTo(tmpValues[a]);
                return c != 0 ? c : a.thingIDNumber.CompareTo(b.thingIDNumber);
            });
            var kept = new HashSet<Thing>();
            for (int i = 0; i < keep; i++)
                kept.Add(tmpThings[i]);
            tmpThings.Clear();
            tmpValues.Clear();
            return kept;
        }

        private static readonly Dictionary<Thing, float> tmpValues = new Dictionary<Thing, float>();

        // ---- Surplus per bill ----

        private class SurplusEntry
        {
            public int Tick = -99999;
            public int Keep = -1;
            public int Surplus;
        }

        private static readonly ConditionalWeakTable<Bill_Production, SurplusEntry> surplusCache =
            new ConditionalWeakTable<Bill_Production, SurplusEntry>();

        /// <summary>Unforbidden things on the bill's map that pass the bill's filters and are not among the kept ones.</summary>
        public static int Surplus(Bill_Production bill)
        {
            Map map = bill.Map;
            if (map == null)
                return 0;
            SurplusEntry entry = surplusCache.GetOrCreateValue(bill);
            if (entry.Keep == bill.targetCount && !Expired(entry.Tick))
                return entry.Surplus;
            int surplus = 0;
            foreach (ThingDef def in bill.ingredientFilter.AllowedThingDefs)
            {
                List<Thing> things = map.listerThings.ThingsOfDef(def);
                if (things.Count <= bill.targetCount)
                    continue;
                foreach (Thing thing in things)
                {
                    // The kept check runs inside IsFixedOrAllowedIngredient through the postfix below.
                    if (thing.Spawned && !thing.IsForbidden(Faction.OfPlayer) && bill.IsFixedOrAllowedIngredient(thing))
                        surplus++;
                }
            }
            entry.Tick = Find.TickManager.TicksGame;
            entry.Keep = bill.targetCount;
            entry.Surplus = surplus;
            return surplus;
        }

        public static void Invalidate(Bill_Production bill)
        {
            surplusCache.Remove(bill);
        }

        // ---- Bill details window ----

        private static readonly AccessTools.FieldRef<Dialog_BillConfig, Bill_Production> dialogBill =
            AccessTools.FieldRefAccess<Dialog_BillConfig, Bill_Production>("bill");

        private static Bill_Production bufferBill;
        private static string keepBuffer;

        public static void DrawDialogSection(Listing_Standard listing, Dialog_BillConfig dialog)
        {
            Bill_Production bill = dialogBill(dialog);
            if (!IsKeepMode(bill))
                return;
            if (bufferBill != bill)
            {
                bufferBill = bill;
                keepBuffer = null;
            }
            listing.Label("VTU_BillKeepCount_Dialog".Translate(bill.targetCount));
            int before = bill.targetCount;
            listing.IntEntry(ref bill.targetCount, ref keepBuffer);
            bill.targetCount = Mathf.Max(0, bill.targetCount);
            if (bill.targetCount != before)
                Invalidate(bill);
            listing.Label("VTU_BillKeepCount_Surplus".Translate(Surplus(bill)));
            listing.Gap(6f);
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            listing.Label("VTU_BillKeepCount_Note".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }
    }

    /// <summary>
    /// Marks the bill whose repeat-mode menu is being built. Other mods (Hauler's Dream) replace the whole menu
    /// with their own prefix, so the options are swapped where the menu window is created instead.
    /// </summary>
    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(BillRepeatModeUtility), nameof(BillRepeatModeUtility.MakeConfigFloatMenu))]
    public static class BillKeepCount_FloatMenu
    {
        public static Bill_Production MenuBill;

        [HarmonyPriority(Priority.First + 1)]
        public static void Prefix(Bill_Production bill)
        {
            MenuBill = BillKeepCount.Eligible(bill?.recipe) ? bill : null;
        }

        public static void Finalizer()
        {
            MenuBill = null;
        }
    }

    /// <summary>
    /// On smelt / destroy bills, "Do until you have X" can never be picked (no product to count): its option is
    /// replaced by the keep mode, and copies of it that other mods add (Hauler's Dream "Batch: ...") are removed.
    /// </summary>
    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(FloatMenu), MethodType.Constructor, typeof(List<FloatMenuOption>))]
    public static class BillKeepCount_FloatMenuOptions
    {
        public static void Prefix(List<FloatMenuOption> options)
        {
            Bill_Production bill = BillKeepCount_FloatMenu.MenuBill;
            if (bill == null || options == null)
                return;
            string target = BillRepeatModeDefOf.TargetCount.LabelCap;
            bool replaced = false;
            for (int i = options.Count - 1; i >= 0; i--)
            {
                string label = options[i].Label;
                if (label == target && !replaced)
                {
                    options[i] = new FloatMenuOption(BillKeepCount.Mode.LabelCap, delegate
                    {
                        bill.repeatMode = BillKeepCount.Mode;
                        BillKeepCount.Invalidate(bill);
                    });
                    replaced = true;
                }
                else if (label != null && label.EndsWith(": " + target))
                {
                    options.RemoveAt(i);
                }
            }
            if (!replaced)
            {
                options.Add(new FloatMenuOption(BillKeepCount.Mode.LabelCap, delegate
                {
                    bill.repeatMode = BillKeepCount.Mode;
                    BillKeepCount.Invalidate(bill);
                }));
            }
        }
    }

    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.RepeatInfoText), MethodType.Getter)]
    public static class BillKeepCount_RepeatInfoText
    {
        public static bool Prefix(Bill_Production __instance, ref string __result)
        {
            if (!BillKeepCount.IsKeepMode(__instance))
                return true;
            __result = "VTU_BillKeepCount_Info".Translate(__instance.targetCount);
            return false;
        }
    }

    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.ShouldDoNow))]
    public static class BillKeepCount_ShouldDoNow
    {
        public static bool Prefix(Bill_Production __instance, ref bool __result)
        {
            if (!BillKeepCount.IsKeepMode(__instance))
                return true;
            __instance.paused = false;
            __result = !__instance.suspended && BillKeepCount.Surplus(__instance) > 0;
            return false;
        }
    }

    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(Bill), nameof(Bill.IsFixedOrAllowedIngredient), typeof(Thing))]
    public static class BillKeepCount_Ingredient
    {
        public static void Postfix(Bill __instance, Thing thing, ref bool __result)
        {
            if (!__result || thing == null)
                return;
            // Vanilla lets items without quality through any quality filter. On these bills they count as the
            // best quality: kept out unless the allowed range reaches legendary.
            if (BillKeepCount.Eligible(__instance.recipe) && !thing.def.FollowQualityThingFilter()
                && __instance.ingredientFilter.allowedQualitiesConfigurable
                && __instance.ingredientFilter.AllowedQualityLevels.max != QualityCategory.Legendary)
            {
                __result = false;
                return;
            }
            if (thing.Spawned && BillKeepCount.IsKeepMode(__instance)
                && BillKeepCount.IsKept(thing, ((Bill_Production)__instance).targetCount))
                __result = false;
        }
    }

    /// <summary>The bill row's label, buttons and +/- for the keep mode (vanilla's +/- only know its own three modes).</summary>
    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(Bill_Production), "DoConfigInterface")]
    public static class BillKeepCount_ConfigInterface
    {
        private static readonly MethodInfo getBillDialog = AccessTools.Method(typeof(Bill_Production), "GetBillDialog");

        public static bool Prefix(Bill_Production __instance, Rect baseRect, Color baseColor)
        {
            if (!BillKeepCount.IsKeepMode(__instance))
                return true;
            Bill_Production bill = __instance;
            Rect rect = new Rect(28f, 32f, 100f, 30f);
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            Widgets.Label(rect, bill.RepeatInfoText);
            GUI.color = baseColor;
            WidgetRow row = new WidgetRow(baseRect.xMax, baseRect.y + 29f, UIDirection.LeftThenUp);
            if (row.ButtonText("Details".Translate() + "..."))
                Find.WindowStack.Add((Window)getBillDialog.Invoke(bill, null));
            if (row.ButtonText(bill.repeatMode.LabelCap.Resolve().PadRight(20)))
                BillRepeatModeUtility.MakeConfigFloatMenu(bill);
            if (row.ButtonIcon(TexButton.Plus))
            {
                bill.targetCount += GenUI.CurrentAdjustmentMultiplier();
                BillKeepCount.Invalidate(bill);
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
            }
            if (row.ButtonIcon(TexButton.Minus))
            {
                bill.targetCount = Mathf.Max(0, bill.targetCount - GenUI.CurrentAdjustmentMultiplier());
                BillKeepCount.Invalidate(bill);
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
            }
            return false;
        }
    }

    /// <summary>
    /// Saves the keep mode as "repeat 0 times" plus a flag, so the save never references a def that only exists
    /// while the module is active; the real repeat count is kept in its own node.
    /// </summary>
    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.ExposeData))]
    public static class BillKeepCount_ExposeData
    {
        public static void Prefix(Bill_Production __instance, out int __state)
        {
            __state = -1;
            if (Scribe.mode == LoadSaveMode.Saving && BillKeepCount.IsKeepMode(__instance))
            {
                __state = __instance.repeatCount;
                __instance.repeatMode = BillRepeatModeDefOf.RepeatCount;
                __instance.repeatCount = 0;
            }
        }

        public static void Postfix(Bill_Production __instance, int __state)
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (__state < 0)
                    return;
                __instance.repeatMode = BillKeepCount.Mode;
                __instance.repeatCount = __state;
                bool keep = true;
                int repeat = __state;
                Scribe_Values.Look(ref keep, "vtuKeepCount");
                Scribe_Values.Look(ref repeat, "vtuRepeatCount");
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                bool keep = false;
                int repeat = 0;
                Scribe_Values.Look(ref keep, "vtuKeepCount");
                Scribe_Values.Look(ref repeat, "vtuRepeatCount");
                if (keep && BillKeepCount.Eligible(__instance.recipe))
                {
                    __instance.repeatMode = BillKeepCount.Mode;
                    __instance.repeatCount = repeat;
                }
            }
        }
    }

    /// <summary>Draws the keep count inside the repeat-mode section of the bill details window.</summary>
    [HarmonyPatchCategory("BillKeepCount")]
    [HarmonyPatch(typeof(Dialog_BillConfig), nameof(Dialog_BillConfig.DoWindowContents))]
    public static class BillKeepCount_Dialog
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo endSection = AccessTools.Method(typeof(Listing_Standard), nameof(Listing_Standard.EndSection));
            MethodInfo draw = AccessTools.Method(typeof(BillKeepCount), nameof(BillKeepCount.DrawDialogSection));
            bool done = false;
            foreach (CodeInstruction code in instructions)
            {
                if (!done && code.Calls(endSection))
                {
                    // Stack: outer listing, section listing. Draw into the section before it closes.
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, draw);
                    done = true;
                }
                yield return code;
            }
            if (!done)
                Log.Error("[Vanilla Tune-Up] BillKeepCount: Listing_Standard.EndSection not found in Dialog_BillConfig.DoWindowContents; the keep count is not shown in the details window.");
        }
    }
}
