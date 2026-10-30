using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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

        /// <summary>Runs once after the module's patches are applied at startup.</summary>
        public Action OnActivated;

        public ModuleState State;
        public string LegacyName;

        public TuneUpModule(string id, bool isTweak, string legacyPackageId)
        {
            Id = id;
            IsTweak = isTweak;
            LegacyPackageId = legacyPackageId;
        }

        /// <summary>Still being tuned; marked as such next to its name.</summary>
        public bool IsExperimental;

        public bool DefaultOn => !IsTweak;
        public bool IsActive => State == ModuleState.Active;
        public string Label => IsExperimental
            ? ("VTU_" + Id).Translate() + " " + "VTU_Experimental".Translate()
            : ("VTU_" + Id).Translate();
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
            new TuneUpModule("PrisonerFoodDelivery", false, null),
            new TuneUpModule("GenepackEject", false, null)
            {
                IsAvailable = () => ModsConfig.BiotechActive,
                UnavailableKey = "VTU_RequiresBiotech"
            },
            new TuneUpModule("TranslationFixes", false, null)
            {
                // Translations are already injected by the time patches are applied.
                OnActivated = TranslationFixes.Apply
            },
            new TuneUpModule("XenogermQueue", true, "ifchen0.xenogermqueue")
            {
                IsAvailable = () => ModsConfig.BiotechActive,
                UnavailableKey = "VTU_RequiresBiotech"
            },
            new TuneUpModule("MuteBirthSounds", true, "ifchen0.mutebirthsounds")
            {
                IsAvailable = () => ModsConfig.BiotechActive,
                UnavailableKey = "VTU_RequiresBiotech"
            },
            new TuneUpModule("RemoveFogOfWar", true, null),
            new TuneUpModule("CaravanFactionIcon", true, null),
            new TuneUpModule("CaravanEncounterNoJump", true, null),
            new TuneUpModule("PinnedResources", true, null),
            new TuneUpModule("NutrientPasteStock", true, null),
            // Main menu additions: mod update list (Steam only) and the Continue button.
            new TuneUpModule("ModUpdateNotes", true, null)
            {
                OnActivated = ModUpdateNotes.Scan
            },
            new TuneUpModule("BreakUpFights", true, null),
            new TuneUpModule("ShortMapNames", true, null),
            new TuneUpModule("GhoulApparel", true, null)
            {
                IsAvailable = () => ModsConfig.AnomalyActive,
                UnavailableKey = "VTU_RequiresAnomaly"
            },
            new TuneUpModule("CaravanAbandonDead", true, null),
            new TuneUpModule("TaskbarLetterBadge", true, null)
            {
                IsAvailable = () => Environment.OSVersion.Platform == PlatformID.Win32NT,
                UnavailableKey = "VTU_RequiresWindows"
            },
            new TuneUpModule("BundleLetterDismiss", true, null),
            // Modules with extra settings go last, the longest at the end, so plain toggles stay near the top.
            new TuneUpModule("Quicksave", true, null)
            {
                DrawSettings = Quicksave.DrawSettings
            },
            new TuneUpModule("PrisonerDefaultFoodPolicy", true, null)
            {
                DrawSettings = PrisonerDefaultFoodPolicy.DrawSettings
            },
            new TuneUpModule("HuntGuard", true, null)
            {
                DrawSettings = HuntGuard.DrawSettings
            },
            new TuneUpModule("HybridXenotype", true, null)
            {
                IsAvailable = () => ModsConfig.BiotechActive,
                UnavailableKey = "VTU_RequiresBiotech",
                DrawSettings = HybridXenotype.DrawSettings
            },
            new TuneUpModule("FactionColors", true, null)
            {
                DrawSettings = FactionColors.DrawSettings
            },
            new TuneUpModule("DragReorder", true, "ifchen0.dragreorder")
            {
                DrawSettings = DragReorderSettings.Draw
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
        public bool hybridIncludeXenogenes;
        // Food policy name given to new prisoners; empty leaves vanilla behavior.
        public string prisonerFoodPolicy = "";
        public float factionColorMinDistance = FactionColors.DefaultMinDistance;
        public float huntGuardMinRevengeChance = HuntGuard.DefaultMinRevengeChance;
        public bool huntGuardStrict;
        public int quicksaveCount = Quicksave.DefaultCount;
        public List<string> pinnedResources = new List<string>();
        // Resource readout categories left open; only used once the player has toggled one.
        public bool resourceCategoriesRecorded;
        public List<string> openResourceCategories = new List<string>();
        // Workshop file id -> Steam install time of the version the player last marked as read.
        public Dictionary<string, int> seenModUpdates = new Dictionary<string, int>();
        // "<file id>@<install time>" -> game starts an unread update has been listed on.
        public Dictionary<string, int> unreadModUpdateShows = new Dictionary<string, int>();

        // Compact colonist bar: what stays visible while the bar is collapsed.
        public bool compactBarShowLabels = true;
        public bool compactBarShowIcons = true;
        public bool compactBarShowWeapons;
        public bool compactBarMagnifyHovered = true;
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
            Scribe_Values.Look(ref hybridIncludeXenogenes, "hybridIncludeXenogenes", false);
            Scribe_Values.Look(ref prisonerFoodPolicy, "prisonerFoodPolicy", "");
            Scribe_Values.Look(ref factionColorMinDistance, "factionColorMinDistance", FactionColors.DefaultMinDistance);
            Scribe_Values.Look(ref huntGuardMinRevengeChance, "huntGuardMinRevengeChance", HuntGuard.DefaultMinRevengeChance);
            Scribe_Values.Look(ref huntGuardStrict, "huntGuardStrict", false);
            Scribe_Values.Look(ref quicksaveCount, "quicksaveCount", Quicksave.DefaultCount);
            Scribe_Values.Look(ref compactBarShowLabels, "compactBarShowLabels", true);
            Scribe_Values.Look(ref compactBarShowIcons, "compactBarShowIcons", true);
            Scribe_Values.Look(ref compactBarShowWeapons, "compactBarShowWeapons", false);
            Scribe_Values.Look(ref compactBarMagnifyHovered, "compactBarMagnifyHovered", true);
            Scribe_Values.Look(ref compactBarCustomPortraits, "compactBarCustomPortraits", true);
            Scribe_Values.Look(ref compactBarPortraitHeight, "compactBarPortraitHeight", DefaultPortraitHeight);
            Scribe_Values.Look(ref compactBarPortraitZoom, "compactBarPortraitZoom", DefaultPortraitZoom);
            Scribe_Values.Look(ref compactBarPortraitOffset, "compactBarPortraitOffset", DefaultPortraitOffset);
            Scribe_Collections.Look(ref compactBarRaceZoom, "compactBarRaceZoom", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref pinnedResources, "pinnedResources", LookMode.Value);
            Scribe_Values.Look(ref resourceCategoriesRecorded, "resourceCategoriesRecorded", false);
            Scribe_Collections.Look(ref openResourceCategories, "openResourceCategories", LookMode.Value);
            Scribe_Collections.Look(ref seenModUpdates, "seenModUpdates", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref unreadModUpdateShows, "unreadModUpdateShows", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                overrides ??= new Dictionary<string, bool>();
                prisonerFoodPolicy ??= "";
                compactBarRaceZoom ??= new Dictionary<string, float>();
                pinnedResources ??= new List<string>();
                openResourceCategories ??= new List<string>();
                seenModUpdates ??= new Dictionary<string, int>();
                unreadModUpdateShows ??= new Dictionary<string, int>();
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
        private const float SearchHeight = 30f;
        private Vector2 scroll;
        private float viewHeight;
        private readonly QuickSearchWidget search = new QuickSearchWidget();

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
            // Kept above the scroll view so it stays in reach however far the list is scrolled.
            search.OnGUI(new Rect(inRect.x, inRect.y, Mathf.Min(300f, inRect.width), SearchHeight - 6f), () => scroll = Vector2.zero);
            inRect.yMin += SearchHeight;
            Rect view = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(viewHeight, inRect.height));
            Widgets.BeginScrollView(inRect, ref scroll, view);
            // One column, so content taller than the window extends the scroll view instead of wrapping.
            var listing = new Listing_Standard { maxOneColumn = true };
            listing.Begin(view);
            listing.Label("VTU_RestartNote".Translate());
            int shown = DrawSection(listing, "VTU_Fixes", tweaks: false) + DrawSection(listing, "VTU_Tweaks", tweaks: true);
            search.noResultsMatched = shown == 0;
            if (shown == 0)
            {
                listing.GapLine();
                GUI.color = Color.gray;
                listing.Label("VTU_NoSearchMatches".Translate());
                GUI.color = Color.white;
            }
            viewHeight = listing.CurHeight;
            listing.End();
            Widgets.EndScrollView();
        }

        /// <summary>Draws the section's modules that match the search; returns how many were drawn.</summary>
        private int DrawSection(Listing_Standard listing, string headerKey, bool tweaks)
        {
            bool header = false;
            int shown = 0;
            foreach (TuneUpModule module in Modules.All)
            {
                if (module.IsTweak != tweaks || !search.filter.Matches(module.Label) && !search.filter.Matches(module.Description))
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
                shown++;
            }
            return shown;
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
                    module.OnActivated?.Invoke();
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
