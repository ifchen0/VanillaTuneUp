using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Caravans with many wounded pile up corpses (members who die in a caravan go into its inventory) and tainted
    /// apparel, and vanilla abandons them one entry at a time. Adds a button next to "Assign drug policies" on the
    /// caravan Items tab that abandons every humanlike corpse and every tainted apparel in the inventory after one
    /// confirmation, which names any colonist among the dead. Animal corpses are kept for butchering, and apparel worn
    /// by living members is left alone.
    /// </summary>
    [HarmonyPatchCategory("CaravanAbandonDead")]
    [HarmonyPatch(typeof(WITab_Caravan_Items), "FillTab")]
    public static class Patch_WITab_Caravan_Items_FillTab
    {
        public static void Postfix()
        {
            // Same object as the tab's protected SelCaravan.
            Caravan caravan = Find.WorldSelector.SingleSelectedObject as Caravan;
            if (caravan == null)
                return;
            List<Thing> targets = CaravanAbandonDead.Targets(caravan);
            if (targets.Count == 0)
                return;
            Rect rect = new Rect(220f, 10f, 200f, 27f);
            TooltipHandler.TipRegion(rect, "VTU_CaravanAbandonDead_Tip".Translate());
            if (Widgets.ButtonText(rect, "VTU_CaravanAbandonDead_Button".Translate()))
                CaravanAbandonDead.Confirm(caravan, targets);
        }
    }

    public static class CaravanAbandonDead
    {
        public static List<Thing> Targets(Caravan caravan)
        {
            return CaravanInventoryUtility.AllInventoryItems(caravan)
                .Where(t => (t is Corpse c && c.InnerPawn != null && c.InnerPawn.RaceProps.Humanlike)
                    || (t is Apparel a && a.WornByCorpse))
                .ToList();
        }

        public static void Confirm(Caravan caravan, List<Thing> targets)
        {
            int corpses = targets.Count(t => t is Corpse);
            int apparel = targets.Count - corpses;
            TaggedString text = "VTU_CaravanAbandonDead_Confirm".Translate(corpses, apparel);
            List<string> colonists = targets.OfType<Corpse>()
                .Select(c => c.InnerPawn)
                .Where(p => p != null && p.RaceProps.Humanlike && p.Faction == Faction.OfPlayer)
                .Select(p => p.LabelShortCap)
                .ToList();
            if (colonists.Count > 0)
                text += "\n\n" + "VTU_CaravanAbandonDead_Colonists".Translate() + "\n" + colonists.ToLineList("  - ");
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, () => Abandon(caravan), destructive: true));
        }

        // Re-collected on confirm since the inventory may change while the dialog is open.
        private static void Abandon(Caravan caravan)
        {
            PlanetTile tile = caravan.GetTileCurrentlyOver();
            foreach (Thing t in Targets(caravan))
            {
                Pawn owner = CaravanInventoryUtility.GetOwnerOf(caravan, t);
                if (owner == null)
                    continue;
                t.Notify_AbandonedAtTile(tile);
                owner.inventory.innerContainer.Remove(t);
                t.Destroy();
            }
            caravan.RecacheInventory();
        }
    }
}
