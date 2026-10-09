using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Verse;

namespace VanillaTuneUp
{
    public class DesignatorOrder : IExposable
    {
        public string category;
        public List<string> keys = new List<string>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref category, "category");
            Scribe_Collections.Look(ref keys, "keys", LookMode.Value);
            if (keys == null)
                keys = new List<string>();
        }
    }

    /// <summary>Saved orders for the Drag Reorder module, stored inside the Vanilla Tune-Up settings.</summary>
    public class DragReorderSettings : IExposable
    {
        public List<string> mainButtons = new List<string>();
        public List<string> categories = new List<string>();
        public List<DesignatorOrder> designators = new List<DesignatorOrder>();
        public List<string> inspectGizmos = new List<string>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref mainButtons, "mainButtons", LookMode.Value);
            Scribe_Collections.Look(ref categories, "categories", LookMode.Value);
            Scribe_Collections.Look(ref designators, "designators", LookMode.Deep);
            Scribe_Collections.Look(ref inspectGizmos, "inspectGizmos", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                mainButtons ??= new List<string>();
                categories ??= new List<string>();
                designators ??= new List<DesignatorOrder>();
                inspectGizmos ??= new List<string>();
                designators.RemoveAll(d => d == null || d.category.NullOrEmpty());
            }
        }

        public List<string> DesignatorKeys(string category)
        {
            foreach (DesignatorOrder order in designators)
            {
                if (order.category == category)
                    return order.keys;
            }
            return null;
        }

        public void SetDesignatorKeys(string category, List<string> keys)
        {
            designators.RemoveAll(d => d.category == category);
            designators.Add(new DesignatorOrder { category = category, keys = keys });
        }

        public static void Draw(Listing_Standard listing)
        {
            DragReorderSettings settings = VanillaTuneUpMod.Settings.dragReorder;
            listing.Label("VTU_DR_Hint".Translate());
            if (listing.ButtonText("VTU_DR_ResetMainButtons".Translate()))
            {
                settings.mainButtons.Clear();
                VanillaTuneUpMod.Save();
                MainButtonOrdering.ApplyToCurrent();
            }
            if (listing.ButtonText("VTU_DR_ResetCategories".Translate()))
            {
                settings.categories.Clear();
                VanillaTuneUpMod.Save();
                CategoryOrdering.ApplyToCurrent();
            }
            if (listing.ButtonText("VTU_DR_ResetDesignators".Translate()))
            {
                settings.designators.Clear();
                VanillaTuneUpMod.Save();
                DesignatorOrdering.Invalidate();
            }
            if (listing.ButtonText("VTU_DR_ResetInspectGizmos".Translate()))
            {
                settings.inspectGizmos.Clear();
                VanillaTuneUpMod.Save();
                InspectOrdering.Invalidate();
            }
        }

        /// <summary>
        /// Copies the orders saved by the standalone Drag Reorder mod. While that mod is active it owns the
        /// orders, so they are copied on every start; once it is gone they are copied a single time.
        /// </summary>
        public static void MigrateLegacy(TuneUpSettings settings)
        {
            bool legacyActive = Modules.Get("DragReorder").ActiveLegacyMod() != null;
            if (!legacyActive && settings.dragReorderMigrated)
                return;
            try
            {
                string file = Directory.Exists(GenFilePaths.ConfigFolderPath)
                    ? Directory.GetFiles(GenFilePaths.ConfigFolderPath, "Mod_*_DragReorderMod.xml")
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
                if (file != null)
                {
                    List<string> inspectGizmos = settings.dragReorder.inspectGizmos;
                    settings.dragReorder = Read(file);
                    settings.dragReorder.inspectGizmos = inspectGizmos;
                    Log.Message($"[Vanilla Tune-Up] Drag Reorder: copied saved orders from {Path.GetFileName(file)}.");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Vanilla Tune-Up] Drag Reorder: could not copy the standalone mod's saved orders: {e.Message}");
            }
            if (!legacyActive)
            {
                settings.dragReorderMigrated = true;
                VanillaTuneUpMod.Save();
            }
        }

        // Read by hand: the file names the standalone mod's settings class, which may not be loaded.
        private static DragReorderSettings Read(string file)
        {
            var doc = new XmlDocument();
            doc.Load(file);
            XmlNode root = doc.SelectSingleNode("SettingsBlock/ModSettings");
            var result = new DragReorderSettings();
            if (root == null)
                return result;
            result.mainButtons = Values(root.SelectSingleNode("mainButtons"));
            result.categories = Values(root.SelectSingleNode("categories"));
            XmlNode designators = root.SelectSingleNode("designators");
            if (designators != null)
            {
                foreach (XmlNode li in designators.SelectNodes("li"))
                {
                    string category = li.SelectSingleNode("category")?.InnerText;
                    if (!category.NullOrEmpty())
                        result.designators.Add(new DesignatorOrder { category = category, keys = Values(li.SelectSingleNode("keys")) });
                }
            }
            return result;
        }

        private static List<string> Values(XmlNode list)
        {
            var values = new List<string>();
            if (list != null)
            {
                foreach (XmlNode li in list.SelectNodes("li"))
                    values.Add(li.InnerText);
            }
            return values;
        }
    }
}
