using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    public enum ModuleState { Off, Active, Legacy, Unavailable, Failed }

    /// <summary>
    /// One independently toggleable fix or tweak. Its Harmony patch classes carry
    /// [HarmonyPatchCategory(Id)] and are applied only when the module is enabled.
    /// </summary>
    public sealed class TuneUpModule
    {
        public readonly string Id;
        public readonly bool IsTweak;

        /// <summary>packageId of the standalone mod this module replaces; when it is active the module stays off.</summary>
        public readonly string LegacyPackageId;

        /// <summary>False when something the module needs (such as a DLC) is missing.</summary>
        public Func<bool> IsAvailable = () => true;
        public string UnavailableKey;

        /// <summary>Extra settings drawn under the module while it is active.</summary>
        public Action<Listing_Standard> DrawSettings;

        public ModuleState State;
        public string LegacyName;

        public TuneUpModule(string id, bool isTweak, string legacyPackageId)
        {
            Id = id;
            IsTweak = isTweak;
            LegacyPackageId = legacyPackageId;
        }

        public bool DefaultOn => !IsTweak;
        public bool IsActive => State == ModuleState.Active;
        public string Label => ("VTU_" + Id).Translate();
        public string Description => ("VTU_" + Id + "_Desc").Translate();

        public ModMetaData ActiveLegacyMod() => LegacyPackageId == null
            ? null
            : ModLister.GetActiveModWithIdentifier(LegacyPackageId, ignorePostfix: true);
    }

    public static class Modules
    {
        public static readonly List<TuneUpModule> All = new List<TuneUpModule>
        {
            new TuneUpModule("FactionLeader", false, "ifchen0.factionleaderfix"),
            new TuneUpModule("HistoryGraph", false, "ifchen0.historygraphperformancefix"),
            new TuneUpModule("RightClickMenu", false, "ifchen0.rightclickmenulagfix"),
            new TuneUpModule("TradeDeal", false, "ifchen0.tradeperformancefix"),
            new TuneUpModule("SiegeBuilder", false, "ifchen0.siegebuilderfix"),
            new TuneUpModule("PrisonerFoodPolicy", false, "ifchen0.prisonpatch"),
            new TuneUpModule("PrisonerBleeding", false, "ifchen0.prisonpatch"),
            new TuneUpModule("XenogermQueue", true, "ifchen0.xenogermqueue")
            {
                IsAvailable = () => ModsConfig.BiotechActive,
                UnavailableKey = "VTU_RequiresBiotech"
            },
            new TuneUpModule("DragReorder", true, "ifchen0.dragreorder")
            {
                DrawSettings = DragReorderSettings.Draw
            },
            new TuneUpModule("MuteBirthSounds", true, "ifchen0.mutebirthsounds")
            {
                IsAvailable = () => ModsConfig.BiotechActive,
                UnavailableKey = "VTU_RequiresBiotech"
            },
            new TuneUpModule("HybridXenotype", true, null)
            {
                IsAvailable = () => ModsConfig.BiotechActive,
                UnavailableKey = "VTU_RequiresBiotech"
            },
            new TuneUpModule("CompactColonistBar", true, null)
            {
                DrawSettings = CompactColonistBar.DrawSettings
            },
        };

        public static TuneUpModule Get(string id) => All.Find(m => m.Id == id);

        public static bool IsActive(string id) => Get(id)?.IsActive == true;
    }

    public class TuneUpSettings : ModSettings
    {
        // Only modules switched away from their default are stored.
        private Dictionary<string, bool> overrides = new Dictionary<string, bool>();

        public DragReorderSettings dragReorder = new DragReorderSettings();
        public bool dragReorderMigrated;

        // Compact colonist bar: what stays visible while the bar is collapsed.
        public bool compactBarShowLabels = true;
        public bool compactBarShowIcons = true;
        public bool compactBarShowWeapons;
        // Taller entries with a closer portrait camera; defaults match the author's [NL] Custom Portraits setup.
        public bool compactBarCustomPortraits = true;
        public float compactBarPortraitHeight = DefaultPortraitHeight;
        public float compactBarPortraitZoom = DefaultPortraitZoom;
        public float compactBarPortraitOffset = DefaultPortraitOffset;
        private const float DefaultPortraitHeight = 96f;
        private const float DefaultPortraitZoom = 2.3f;
        private const float DefaultPortraitOffset = -0.11f;

        // Per-race zoom on top of compactBarPortraitZoom; only values that differ from the default are stored.
        private Dictionary<string, float> compactBarRaceZoom = new Dictionary<string, float>();
        // Miho draw their heads at 0.75 size, so their portraits look small without extra zoom.
        private static readonly Dictionary<string, float> DefaultRaceZoom = new Dictionary<string, float> { { "Alien_Miho", 1.2f } };

        public float RaceZoom(string defName)
        {
            if (defName == null)
                return 1f;
            if (compactBarRaceZoom.TryGetValue(defName, out float zoom))
                return zoom;
            return DefaultRaceZoom.TryGetValue(defName, out zoom) ? zoom : 1f;
        }

        public void SetRaceZoom(string defName, float zoom)
        {
            float fallback = DefaultRaceZoom.TryGetValue(defName, out float d) ? d : 1f;
            if (Mathf.Approximately(zoom, fallback))
                compactBarRaceZoom.Remove(defName);
            else
                compactBarRaceZoom[defName] = zoom;
        }

        public IEnumerable<string> RaceZoomDefNames()
        {
            foreach (string defName in DefaultRaceZoom.Keys)
                yield return defName;
            foreach (string defName in compactBarRaceZoom.Keys)
                yield return defName;
        }

        public void ResetCompactBarPortraits()
        {
            compactBarPortraitHeight = DefaultPortraitHeight;
            compactBarPortraitZoom = DefaultPortraitZoom;
            compactBarPortraitOffset = DefaultPortraitOffset;
            compactBarRaceZoom.Clear();
        }

        public bool IsEnabled(TuneUpModule module) =>
            overrides.TryGetValue(module.Id, out bool on) ? on : module.DefaultOn;

        public bool IsSet(TuneUpModule module) => overrides.ContainsKey(module.Id);

        public void SetEnabled(TuneUpModule module, bool on)
        {
            if (on == module.DefaultOn)
                overrides.Remove(module.Id);
            else
                overrides[module.Id] = on;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref overrides, "overrides", LookMode.Value, LookMode.Value);
            Scribe_Deep.Look(ref dragReorder, "dragReorder");
            Scribe_Values.Look(ref dragReorderMigrated, "dragReorderMigrated", false);
            Scribe_Values.Look(ref compactBarShowLabels, "compactBarShowLabels", true);
            Scribe_Values.Look(ref compactBarShowIcons, "compactBarShowIcons", true);
            Scribe_Values.Look(ref compactBarShowWeapons, "compactBarShowWeapons", false);
            Scribe_Values.Look(ref compactBarCustomPortraits, "compactBarCustomPortraits", true);
            Scribe_Values.Look(ref compactBarPortraitHeight, "compactBarPortraitHeight", DefaultPortraitHeight);
            Scribe_Values.Look(ref compactBarPortraitZoom, "compactBarPortraitZoom", DefaultPortraitZoom);
            Scribe_Values.Look(ref compactBarPortraitOffset, "compactBarPortraitOffset", DefaultPortraitOffset);
            Scribe_Collections.Look(ref compactBarRaceZoom, "compactBarRaceZoom", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                overrides ??= new Dictionary<string, bool>();
                compactBarRaceZoom ??= new Dictionary<string, float>();
                dragReorder ??= new DragReorderSettings();
            }
        }
    }

    public class VanillaTuneUpMod : Mod
    {
        public static VanillaTuneUpMod Instance;
        public static TuneUpSettings Settings;
        private const float DescriptionIndent = 12f;
        private const float CheckboxColumn = 36f;
        private Vector2 scroll;
        private float viewHeight;

        public VanillaTuneUpMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<TuneUpSettings>();
            DragReorderSettings.MigrateLegacy(Settings);
        }

        public static void Save() => Instance.WriteSettings();

        public override string SettingsCategory() => "Vanilla Tune-Up";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(viewHeight, inRect.height));
            Widgets.BeginScrollView(inRect, ref scroll, view);
            // One column, so content taller than the window extends the scroll view instead of wrapping.
            var listing = new Listing_Standard { maxOneColumn = true };
            listing.Begin(view);
            listing.Label("VTU_RestartNote".Translate());
            DrawSection(listing, "VTU_Fixes", tweaks: false);
            DrawSection(listing, "VTU_Tweaks", tweaks: true);
            viewHeight = listing.CurHeight;
            listing.End();
            Widgets.EndScrollView();
        }

        private static void DrawSection(Listing_Standard listing, string headerKey, bool tweaks)
        {
            bool header = false;
            foreach (TuneUpModule module in Modules.All)
            {
                if (module.IsTweak != tweaks)
                    continue;
                if (!header)
                {
                    listing.GapLine();
                    Text.Font = GameFont.Medium;
                    listing.Label(headerKey.Translate());
                    Text.Font = GameFont.Small;
                    header = true;
                }
                DrawModule(listing, module);
            }
        }

        private static void DrawModule(Listing_Standard listing, TuneUpModule module)
        {
            if (module.State == ModuleState.Legacy)
            {
                GUI.color = Color.gray;
                listing.Label(module.Label + "  " + "VTU_ProvidedByLegacy".Translate(module.LegacyName));
            }
            else if (module.State == ModuleState.Unavailable)
            {
                GUI.color = Color.gray;
                listing.Label(module.Label + "  " + module.UnavailableKey.Translate());
            }
            else
            {
                bool on = Settings.IsEnabled(module);
                listing.CheckboxLabeled(module.Label, ref on, module.Description);
                Settings.SetEnabled(module, on);
                if (module.State == ModuleState.Failed)
                {
                    GUI.color = ColorLibrary.RedReadable;
                    listing.Label("VTU_Failed".Translate());
                }
                else if (on != module.IsActive)
                {
                    GUI.color = ColorLibrary.Gold;
                    listing.Label("VTU_RestartRequired".Translate());
                }
            }
            // Indent only moves the start, so narrow the column too: keeps the text clear of the checkbox column.
            float width = listing.ColumnWidth;
            listing.ColumnWidth = width - DescriptionIndent - CheckboxColumn;
            listing.Indent(DescriptionIndent);
            GUI.color = Color.gray;
            listing.Label(module.Description);
            GUI.color = Color.white;
            if (module.IsActive && module.DrawSettings != null)
                module.DrawSettings(listing);
            listing.Outdent(DescriptionIndent);
            listing.ColumnWidth = width;
            listing.Gap(6f);
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            var harmony = new Harmony("ifchen0.vanillatuneup");
            bool settingsChanged = false;
            foreach (TuneUpModule module in Modules.All)
            {
                ModMetaData legacy = module.ActiveLegacyMod();
                if (legacy != null)
                {
                    module.State = ModuleState.Legacy;
                    module.LegacyName = legacy.Name;
                    Log.Message($"[Vanilla Tune-Up] {module.Id}: skipped, already provided by {legacy.Name}.");
                    // Someone using the standalone version wants the feature: keep it on once that mod is removed.
                    if (!VanillaTuneUpMod.Settings.IsSet(module) && !module.DefaultOn)
                    {
                        VanillaTuneUpMod.Settings.SetEnabled(module, true);
                        settingsChanged = true;
                    }
                    continue;
                }
                if (!module.IsAvailable())
                {
                    module.State = ModuleState.Unavailable;
                    continue;
                }
                if (!VanillaTuneUpMod.Settings.IsEnabled(module))
                    continue;
                try
                {
                    harmony.PatchCategory(module.Id);
                    module.State = ModuleState.Active;
                }
                catch (Exception e)
                {
                    module.State = ModuleState.Failed;
                    Log.Error($"[Vanilla Tune-Up] {module.Id} failed to apply and was turned off: {e}");
                    try { harmony.UnpatchCategory(module.Id); } catch { }
                }
            }
            if (settingsChanged)
                VanillaTuneUpMod.Save();
#if PROFILE
            harmony.PatchCategory("Profile");
            Log.Message("[Vanilla Tune-Up] PROFILE build: History tab timings enabled.");
#endif
        }
    }
}
