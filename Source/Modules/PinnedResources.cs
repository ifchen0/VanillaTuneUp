using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace VanillaTuneUp
{
    /// <summary>
    /// Left-clicking a resource in the categorized resource readout pins it. A pinned resource stays listed while its
    /// category is collapsed (drawn under the category row) and is shown even when the colony has none of it, in
    /// which case its categories are shown too. Pins are stored by defName in the mod settings, so they apply to
    /// every save and survive a mod being removed for a while.
    /// </summary>
    public static class PinnedResources
    {
        private static HashSet<ThingDef> pinned;
        private static readonly Dictionary<ThingCategoryDef, List<ThingDef>> pinnedUnder = new Dictionary<ThingCategoryDef, List<ThingDef>>();

        private static HashSet<ThingDef> Pinned
        {
            get
            {
                if (pinned == null)
                {
                    pinned = new HashSet<ThingDef>();
                    foreach (string defName in VanillaTuneUpMod.Settings.pinnedResources)
                    {
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                        if (def != null)
                            pinned.Add(def);
                    }
                }
                return pinned;
            }
        }

        public static bool IsPinned(ThingDef def) => Pinned.Contains(def);

        public static void Toggle(ThingDef def)
        {
            List<string> names = VanillaTuneUpMod.Settings.pinnedResources;
            if (Pinned.Remove(def))
            {
                names.Remove(def.defName);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            }
            else
            {
                Pinned.Add(def);
                names.Add(def.defName);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
            pinnedUnder.Clear();
            VanillaTuneUpMod.Save();
        }

        /// <summary>Pinned resources anywhere under this category, in the order vanilla lists them when expanded.</summary>
        public static List<ThingDef> PinnedUnder(ThingCategoryDef cat)
        {
            if (!pinnedUnder.TryGetValue(cat, out List<ThingDef> list))
            {
                list = new List<ThingDef>();
                if (Pinned.Count > 0)
                    CollectPinned(cat, list);
                pinnedUnder[cat] = list;
            }
            return list;
        }

        private static void CollectPinned(ThingCategoryDef cat, List<ThingDef> list)
        {
            foreach (ThingCategoryDef child in cat.childCategories)
            {
                if (!child.resourceReadoutRoot)
                    CollectPinned(child, list);
            }
            foreach (ThingDef def in cat.childThingDefs)
            {
                if (def.PlayerAcquirable && Pinned.Contains(def) && !list.Contains(def))
                    list.Add(def);
            }
        }
    }

    /// <summary>
    /// Vanilla's categorized listing with pinning added. A subclass, because the drawing helpers it needs
    /// (open/close widget, indent, line handling) are protected.
    /// </summary>
    [StaticConstructorOnStartup]
    public class Listing_PinnedResourceReadout : Listing_ResourceReadout
    {
        // Same as vanilla's private SolidCategoryBG.
        private static readonly Texture2D SolidCategoryBG = SolidColorMaterials.NewSolidColorTexture(new Color(0.1f, 0.1f, 0.1f, 0.6f));
        private static readonly Color PinColor = new Color(1f, 1f, 1f, 0.3f);
        private const float PinMarkWidth = 2f;

        private readonly Map map;

        public Listing_PinnedResourceReadout(Map map) : base(map)
        {
            this.map = map;
        }

        public void DoCategoryPinned(TreeNode_ThingCategory node, int nestLevel, int openMask)
        {
            int countIn = map.resourceCounter.GetCountIn(node.catDef);
            List<ThingDef> pinnedHere = PinnedResources.PinnedUnder(node.catDef);
            if (countIn == 0 && pinnedHere.Count == 0)
                return;
            OpenCloseWidget(node, nestLevel, openMask);
            Rect rect = new Rect(0f, curY, LabelWidth, lineHeight);
            rect.xMin = XAtIndentLevel(nestLevel) + 18f;
            Rect bg = rect;
            bg.width = 80f;
            bg.yMax -= 3f;
            bg.yMin += 3f;
            GUI.DrawTexture(bg, SolidCategoryBG);
            if (Mouse.IsOver(rect))
            {
                GUI.DrawTexture(rect, TexUI.HighlightTex);
                TooltipHandler.TipRegion(rect, new TipSignal(node.catDef.LabelCap, node.catDef.GetHashCode()));
            }
            Rect icon = new Rect(rect.x, rect.y + rect.height / 2f - 14f, 28f, 28f);
            GUI.DrawTexture(icon, node.catDef.icon);
            Rect label = new Rect(rect);
            label.xMin = icon.xMax + 6f;
            Widgets.Label(label, countIn.ToStringCached());
            EndLine();
            if (IsOpen(node, openMask))
            {
                foreach (TreeNode_ThingCategory child in node.ChildCategoryNodes)
                {
                    if (!child.catDef.resourceReadoutRoot)
                        DoCategoryPinned(child, nestLevel + 1, openMask);
                }
                foreach (ThingDef def in node.catDef.childThingDefs)
                {
                    if (def.PlayerAcquirable)
                        DoThingDefPinned(def, nestLevel + 2);
                }
            }
            else
            {
                // Collapsed: pinned resources stay at the indent they have when the category is open.
                foreach (ThingDef def in pinnedHere)
                    DoThingDefPinned(def, nestLevel + 2);
            }
        }

        private void DoThingDefPinned(ThingDef thingDef, int nestLevel)
        {
            int count = map.resourceCounter.GetCount(thingDef);
            bool isPinned = PinnedResources.IsPinned(thingDef);
            if (count == 0 && !isPinned)
                return;
            Rect rect = new Rect(0f, curY, LabelWidth, lineHeight);
            rect.xMin = XAtIndentLevel(nestLevel) + 18f;
            if (Mouse.IsOver(rect))
            {
                GUI.DrawTexture(rect, TexUI.HighlightTex);
                TooltipHandler.TipRegion(rect, new TipSignal(() => thingDef.LabelCap + ": " + thingDef.description.CapitalizeFirst()
                    + "\n\n" + (PinnedResources.IsPinned(thingDef) ? "VTU_PinnedResources_Unpin" : "VTU_PinnedResources_Pin").Translate(),
                    thingDef.shortHash));
            }
            if (isPinned)
                Widgets.DrawBoxSolid(new Rect(rect.x - PinMarkWidth - 3f, rect.y + 7f, PinMarkWidth, rect.height - 14f), PinColor);
            Rect icon = new Rect(rect.x, rect.y + rect.height / 2f - 14f, 28f, 28f);
            Widgets.ThingIcon(icon, thingDef);
            Rect label = new Rect(rect);
            label.xMin = icon.xMax + 6f;
            Widgets.Label(label, count.ToStringCached());
            if (Widgets.ButtonInvisible(rect, doMouseoverSound: false))
                PinnedResources.Toggle(thingDef);
            EndLine();
        }
    }

    [HarmonyPatchCategory("PinnedResources")]
    [HarmonyPatch(typeof(ResourceReadout), "DoReadoutCategorized")]
    public static class Patch_ResourceReadout_DoReadoutCategorized
    {
        private static readonly AccessTools.FieldRef<ResourceReadout, List<ThingCategoryDef>> RootThingCategories =
            AccessTools.FieldRefAccess<ResourceReadout, List<ThingCategoryDef>>("RootThingCategories");
        private static readonly AccessTools.FieldRef<ResourceReadout, float> LastDrawnHeight =
            AccessTools.FieldRefAccess<ResourceReadout, float>("lastDrawnHeight");

        // Same as vanilla, with the pinning listing in place of Listing_ResourceReadout.
        public static bool Prefix(ResourceReadout __instance, Rect rect)
        {
            var listing = new Listing_PinnedResourceReadout(Find.CurrentMap);
            listing.Begin(rect);
            listing.nestIndentWidth = 7f;
            listing.lineHeight = 24f;
            listing.verticalSpacing = 0f;
            List<ThingCategoryDef> roots = RootThingCategories(__instance);
            for (int i = 0; i < roots.Count; i++)
                listing.DoCategoryPinned(roots[i].treeNode, 0, 32);
            listing.End();
            LastDrawnHeight(__instance) = listing.CurHeight;
            return false;
        }
    }
}
