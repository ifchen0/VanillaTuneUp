using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Trader caravans, visitors and allied reinforcements eat whatever food lies in the colony's stockpiles once they
    /// run out of meals of their own. Their food search on a player home map now skips the colony's food (anything in
    /// storage or in the home area, and crops in growing zones or plant pots) unless they carry no food at all and are
    /// at least as hungry as the setting; even then a human may only take what the chosen food policy allows. Wild
    /// food elsewhere on the map stays open to them, so a visitor never starves while food is reachable.
    /// </summary>
    [HarmonyPatchCategory("VisitorFood")]
    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.BestFoodSourceOnMap))]
    public static class VisitorFood
    {
        public static readonly HungerCategory[] HungerChoices =
            { HungerCategory.Hungry, HungerCategory.UrgentlyHungry, HungerCategory.Starving };

        private static bool failed;

        /// <summary>Wraps the food validator right where vanilla stores it, so both the humanlike and animal scans use it.</summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo wrap = AccessTools.Method(typeof(VisitorFood), nameof(Wrap));
            bool wrapped = false;
            foreach (CodeInstruction code in instructions)
            {
                if (!wrapped && (code.opcode == OpCodes.Stfld || code.opcode == OpCodes.Stloc || code.opcode == OpCodes.Stloc_S)
                    && IsFoodValidator(code.operand))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, wrap);
                    wrapped = true;
                }
                yield return code;
            }
            if (!wrapped)
                Log.Warning("[Vanilla Tune-Up] VisitorFood: could not find the food validator; visitors eat colony food as in vanilla.");
        }

        private static bool IsFoodValidator(object operand) => operand switch
        {
            FieldInfo field => field.Name == "foodValidator" && field.FieldType == typeof(Predicate<Thing>),
            LocalBuilder local => local.LocalType == typeof(Predicate<Thing>),
            _ => false
        };

        public static Predicate<Thing> Wrap(Predicate<Thing> validator, Pawn getter, Pawn eater)
        {
            if (failed || getter != eater)
                return validator;
            try
            {
                if (!IsVisitorOnPlayerMap(eater))
                    return validator;
                if (!MayEatColonyFood(eater))
                    return t => !IsColonyFood(t) && validator(t);
                FoodPolicy policy = eater.RaceProps.Humanlike ? PrisonerDefaultFoodPolicy.FindPolicy(VanillaTuneUpMod.Settings.visitorFoodPolicy) : null;
                if (policy == null)
                    return validator;
                return t => (!IsColonyFood(t) || Allows(policy, t)) && validator(t);
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error($"[Vanilla Tune-Up] VisitorFood: unexpected error, turned off for this session: {e}");
                return validator;
            }
        }

        /// <summary>A non-player pawn on its own (not a guest, prisoner or quest lodger) visiting a player home map.</summary>
        private static bool IsVisitorOnPlayerMap(Pawn pawn)
        {
            Faction faction = pawn?.Faction;
            if (faction == null || faction.IsPlayer || pawn.HostFaction != null || !pawn.Spawned || !pawn.Map.IsPlayerHome)
                return false;
            return !QuestUtility.IsQuestLodger(pawn) && !faction.HostileTo(Faction.OfPlayer);
        }

        /// <summary>No food of any kind left in its inventory, and hungry enough.</summary>
        private static bool MayEatColonyFood(Pawn pawn)
        {
            if (FoodUtility.BestFoodInInventory(pawn, pawn, FoodPreferability.DesperateOnly) != null)
                return false;
            Need_Food food = pawn.needs?.food;
            return food != null && food.CurCategory >= VanillaTuneUpMod.Settings.visitorFoodHunger;
        }

        public static bool IsColonyFood(Thing t)
        {
            Map map = t.MapHeld;
            if (map == null)
                return false;
            IntVec3 cell = t.PositionHeld;
            // Grass and wild bushes stay open for grazing; only crops the colony grows count.
            if (t is Plant)
                return map.zoneManager.ZoneAt(cell) is Zone_Growing || cell.GetEdifice(map) is Building_PlantGrower;
            return t.IsInAnyStorage() || map.areaManager.Home[cell];
        }

        private static bool Allows(FoodPolicy policy, Thing t) => t is Building_NutrientPasteDispenser dispenser
            ? policy.Allows(dispenser.DispensableDef)
            : policy.Allows(t);

        public static string HungerLabel(HungerCategory category) => ("HungerLevel_" + category).Translate();

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            Rect row = listing.GetRect(30f);
            Widgets.Label(row.LeftHalf(), "VTU_VisitorFoodHungerLabel".Translate());
            if (Widgets.ButtonText(row.RightHalf(), HungerLabel(settings.visitorFoodHunger)))
            {
                var options = new List<FloatMenuOption>();
                foreach (HungerCategory category in HungerChoices)
                    options.Add(new FloatMenuOption(HungerLabel(category), () => settings.visitorFoodHunger = category));
                Find.WindowStack.Add(new FloatMenu(options));
            }
            PrisonerDefaultFoodPolicy.DrawPolicyPicker(listing, "VTU_VisitorFoodPolicyLabel", "VTU_VisitorFoodPolicyNone",
                "VTU_VisitorFoodPolicyMissing", settings.visitorFoodPolicy, label => settings.visitorFoodPolicy = label);
        }
    }
}
