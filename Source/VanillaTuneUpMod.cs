using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    public enum ModuleState { Off, Active, Legacy, Failed }

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

        public ModuleState State;
        public string LegacyName;

        public TuneUpModule(string id, bool isTweak, string legacyPackageId)
        {
            Id = id;
            IsTweak = isTweak;
            LegacyPackageId = legacyPackageId;
        }

        public bool DefaultOn => !IsTweak;
        public string Label => ("VTU_" + Id).Translate();
        public string Description => ("VTU_" + Id + "_Desc").Translate();
    }

    public static class Modules
    {
        public static readonly List<TuneUpModule> All = new List<TuneUpModule>
        {
            new TuneUpModule("FactionLeader", false, "ifchen0.factionleaderfix"),
            new TuneUpModule("HistoryGraph", false, "ifchen0.historygraphperformancefix"),
            new TuneUpModule("RightClickMenu", false, "ifchen0.rightclickmenulagfix"),
        };
    }

    public class TuneUpSettings : ModSettings
    {
        // Only modules switched away from their default are stored.
        private Dictionary<string, bool> overrides = new Dictionary<string, bool>();

        public bool IsEnabled(TuneUpModule module) =>
            overrides.TryGetValue(module.Id, out bool on) ? on : module.DefaultOn;

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
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                overrides ??= new Dictionary<string, bool>();
        }
    }

    public class VanillaTuneUpMod : Mod
    {
        public static TuneUpSettings Settings;
        private Vector2 scroll;
        private float viewHeight;

        public VanillaTuneUpMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<TuneUpSettings>();
        }

        public override string SettingsCategory() => "Vanilla Tune-Up";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(viewHeight, inRect.height));
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var listing = new Listing_Standard();
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
                else if (on != (module.State == ModuleState.Active))
                {
                    GUI.color = ColorLibrary.Gold;
                    listing.Label("VTU_RestartRequired".Translate());
                }
            }
            GUI.color = Color.gray;
            listing.Indent();
            listing.Label(module.Description);
            listing.Outdent();
            GUI.color = Color.white;
            listing.Gap(6f);
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            var harmony = new Harmony("ifchen0.vanillatuneup");
            foreach (TuneUpModule module in Modules.All)
            {
                ModMetaData legacy = module.LegacyPackageId == null
                    ? null
                    : ModLister.GetActiveModWithIdentifier(module.LegacyPackageId, ignorePostfix: true);
                if (legacy != null)
                {
                    module.State = ModuleState.Legacy;
                    module.LegacyName = legacy.Name;
                    Log.Message($"[Vanilla Tune-Up] {module.Id}: skipped, already provided by {legacy.Name}.");
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
#if PROFILE
            harmony.PatchCategory("Profile");
            Log.Message("[Vanilla Tune-Up] PROFILE build: History tab timings enabled.");
#endif
        }
    }
}
