using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Vanilla only spreads colors between factions of the same def, so factions of different defs (often from
    /// different mods) can end up with nearly the same color. On load, compare the factions that have settlements on
    /// the world map in OKLab and, if any two are too close, push the close pairs apart a little at a time, both
    /// sides sharing the move, while each color is pulled back toward its original. Shifts stay small and colors
    /// keep their hue family instead of one faction jumping across the palette; a faction the push cannot clear
    /// (stuck at the gamut edge) takes the nearest clear color within a short reach instead. The new colors are stored in
    /// Faction.color, so they are saved with the game and later loads find nothing to change.
    /// </summary>
    public static class FactionColors
    {
        public const float DefaultMinDistance = 0.15f;

        private const int MaxIterations = 5000;
        // Aim slightly past the threshold so the 3-decimal color saved in the file still clears it on the next load.
        private const float TargetMargin = 1.03f;
        private const float PushRate = 0.5f;
        private const float PullBackRate = 0.002f;
        private const float MinReportedShift = 0.005f;
        // World map icons tint their bright parts with the faction color, so a dark color disappears into the
        // terrain (worse with mods that dim settlement icons). Never move a color below this OKLab lightness,
        // or below its original lightness when that is already darker.
        private const float MinLightness = 0.6f;
        private const float SearchStep = 0.02f;
        // The fallback search stays this close to the original, so a faction never jumps across the palette.
        private const float SearchRadius = 0.3f;
        // A load writes new colors only when the closest pair moves at least this much further apart. When the
        // target cannot be reached, later loads would otherwise keep shuffling colors around a little each time.
        private const float MinImprovement = 0.01f;

        // Offsets around a color in OKLab, nearest first, for the fallback search.
        private static Vector3[] searchOffsets;

        public static void DrawSettings(Listing_Standard listing)
        {
            TuneUpSettings settings = VanillaTuneUpMod.Settings;
            listing.Label("VTU_FactionColorsMinDistance".Translate((settings.factionColorMinDistance * 100f).ToString("0")),
                tooltip: "VTU_FactionColorsMinDistance_Desc".Translate());
            settings.factionColorMinDistance = Mathf.Round(listing.Slider(settings.factionColorMinDistance, 0.1f, 0.25f) * 100f) / 100f;
        }

        public static void Apply()
        {
            float minDistance = VanillaTuneUpMod.Settings.factionColorMinDistance;
            // Only factions with settlements show up on the world map, where the icons need telling apart.
            var onMap = new HashSet<Faction>(Find.WorldObjects.Settlements.Select(s => s.Faction));
            List<Faction> factions = Find.FactionManager.AllFactionsListForReading
                .Where(f => onMap.Contains(f) && !f.Hidden && !f.temporary && !f.defeated)
                .ToList();
            bool[] fixedColor = factions.Select(IsFixed).ToArray();
            Vector3[] original = factions.Select(f => ToOkLab(f.Color)).ToArray();
            float closestBefore = ClosestPair(original, fixedColor);
            if (closestBefore >= minDistance)
                return;

            float[] floor = original.Select(c => Mathf.Min(MinLightness, c.x)).ToArray();
            Vector3[] current = (Vector3[])original.Clone();
            Relax(current, original, fixedColor, floor, minDistance * TargetMargin);
            // Pushing apart can stall at the edge of the gamut, e.g. white next to the Empire's near-white is
            // pushed brighter and clamped back. Move any faction still too close to its nearest clear color.
            foreach (int i in Enumerable.Range(0, factions.Count).OrderBy(i => factions[i].loadID))
            {
                if (!fixedColor[i] && NearestDistance(current[i], current, i) < minDistance
                    && TryFindClearColor(original[i], current, i, floor[i], minDistance * TargetMargin, out Vector3 clear))
                    current[i] = clear;
            }

            int changed = 0;
            float closestAfter = ClosestPair(current, fixedColor);
            if (closestAfter < closestBefore + MinImprovement)
                return;
            for (int i = 0; i < factions.Count; i++)
            {
                if (fixedColor[i] || Vector3.Distance(current[i], original[i]) < MinReportedShift)
                    continue;
                Faction faction = factions[i];
                Color before = faction.Color;
                faction.color = FromOkLab(current[i]);
                changed++;
                Log.Message($"[Vanilla Tune-Up] Faction color of {faction.Name} ({faction.def.defName}) changed from "
                    + $"#{ColorUtility.ToHtmlStringRGB(before)} to #{ColorUtility.ToHtmlStringRGB(faction.color.Value)}.");
            }
            if (changed > 0)
            {
                ColoredText.ClearCache();
                Find.World?.renderer?.SetAllLayersDirty();
            }
        }

        // The Empire's near-white is part of its identity, so others move away from it instead.
        private static bool IsFixed(Faction f) => f.IsPlayer || (FactionDefOf.Empire != null && f.def == FactionDefOf.Empire);

        /// <summary>Distance of the closest pair, ignoring pairs where neither color may move.</summary>
        private static float ClosestPair(Vector3[] colors, bool[] fixedColor)
        {
            float closest = float.MaxValue;
            for (int i = 0; i < colors.Length; i++)
            {
                for (int j = i + 1; j < colors.Length; j++)
                {
                    if (!(fixedColor[i] && fixedColor[j]))
                        closest = Mathf.Min(closest, Vector3.Distance(colors[i], colors[j]));
                }
            }
            return closest;
        }

        /// <summary>
        /// Each round, every pair closer than target is pushed apart by part of the gap (all of it on the movable
        /// side when the other is fixed), then every movable color is pulled slightly back toward its original and
        /// kept inside the sRGB gamut. Stops once no pair is short of the target.
        /// </summary>
        private static void Relax(Vector3[] current, Vector3[] original, bool[] fixedColor, float[] floor, float target)
        {
            int n = current.Length;
            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                bool anyShort = false;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        if (fixedColor[i] && fixedColor[j])
                            continue;
                        Vector3 delta = current[i] - current[j];
                        float distance = delta.magnitude;
                        float gap = target - distance;
                        if (gap <= 0f)
                            continue;
                        anyShort = true;
                        Vector3 direction = distance > 1e-6f ? delta / distance : TieDirection(current[i], i * 31 + j, fixedColor[j]);
                        float shareI = fixedColor[i] ? 0f : fixedColor[j] ? 1f : 0.5f;
                        current[i] += direction * (gap * PushRate * shareI);
                        current[j] -= direction * (gap * PushRate * (1f - shareI));
                    }
                }
                if (!anyShort)
                    return;
                for (int i = 0; i < n; i++)
                {
                    if (!fixedColor[i])
                        current[i] = Constrain(Vector3.Lerp(current[i], original[i], PullBackRate), floor[i]);
                }
            }
        }

        /// <summary>
        /// Identical colors have no direction to separate along: pick one per pair, darker for light colors and
        /// lighter for dark ones so the moving side heads into the gamut, with a different hue for each pair.
        /// The returned direction moves i away from j, so it is flipped when only i moves.
        /// </summary>
        private static Vector3 TieDirection(Vector3 color, int seed, bool onlyIMoves)
        {
            float angle = seed * 2.39996f;
            Vector3 direction = new Vector3(color.x > 0.6f ? 0.5f : -0.5f, Mathf.Cos(angle), Mathf.Sin(angle)).normalized;
            return onlyIMoves ? -direction : direction;
        }

        /// <summary>
        /// The in-gamut color at or above the lightness floor, within SearchRadius of <paramref name="original"/>,
        /// nearest to it and at least target from every other color. False when there is none.
        /// </summary>
        private static bool TryFindClearColor(Vector3 original, Vector3[] colors, int self, float floor, float target, out Vector3 clear)
        {
            foreach (Vector3 offset in SearchOffsets())
            {
                clear = original + offset;
                if (clear.x >= floor && InGamut(clear) && NearestDistance(clear, colors, self) >= target)
                    return true;
            }
            clear = original;
            return false;
        }

        private static Vector3[] SearchOffsets()
        {
            if (searchOffsets != null)
                return searchOffsets;
            int steps = Mathf.FloorToInt(SearchRadius / SearchStep);
            var offsets = new List<Vector3>();
            for (int x = -steps; x <= steps; x++)
                for (int y = -steps; y <= steps; y++)
                    for (int z = -steps; z <= steps; z++)
                    {
                        Vector3 offset = new Vector3(x, y, z) * SearchStep;
                        if (offset.magnitude <= SearchRadius + 1e-4f)
                            offsets.Add(offset);
                    }
            return searchOffsets = offsets.OrderBy(o => o.sqrMagnitude).ToArray();
        }

        private static float NearestDistance(Vector3 color, Vector3[] colors, int self)
        {
            float nearest = float.MaxValue;
            for (int j = 0; j < colors.Length; j++)
            {
                if (j != self)
                    nearest = Mathf.Min(nearest, Vector3.Distance(color, colors[j]));
            }
            return nearest;
        }

        private static bool InGamut(Vector3 lab)
        {
            const float tolerance = 1e-4f;
            Vector3 lin = OkLabToLinear(lab);
            return lin.x >= -tolerance && lin.x <= 1f + tolerance && lin.y >= -tolerance && lin.y <= 1f + tolerance
                && lin.z >= -tolerance && lin.z <= 1f + tolerance;
        }

        // OKLab: Euclidean distance roughly follows how different two colors look. x = lightness, y/z = a/b.
        private static Vector3 ToOkLab(Color c) => LinearToOkLab(new Vector3(ToLinear(c.r), ToLinear(c.g), ToLinear(c.b)));

        private static Color FromOkLab(Vector3 lab)
        {
            Vector3 lin = OkLabToLinear(lab);
            return new Color(ToSrgb(lin.x), ToSrgb(lin.y), ToSrgb(lin.z));
        }

        /// <summary>Raises lightness to the floor, then pulls the color into the sRGB gamut.</summary>
        private static Vector3 Constrain(Vector3 lab, float floor)
        {
            if (lab.x < floor)
                lab.x = floor;
            return ClampToGamut(lab);
        }

        private static Vector3 ClampToGamut(Vector3 lab)
        {
            Vector3 lin = OkLabToLinear(lab);
            return LinearToOkLab(new Vector3(Mathf.Clamp01(lin.x), Mathf.Clamp01(lin.y), Mathf.Clamp01(lin.z)));
        }

        private static Vector3 LinearToOkLab(Vector3 c)
        {
            float l = Cbrt(0.4122214708f * c.x + 0.5363325363f * c.y + 0.0514459929f * c.z);
            float m = Cbrt(0.2119034982f * c.x + 0.6806995451f * c.y + 0.1073969566f * c.z);
            float s = Cbrt(0.0883024619f * c.x + 0.2817188376f * c.y + 0.6299787005f * c.z);
            return new Vector3(
                0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
        }

        private static Vector3 OkLabToLinear(Vector3 lab)
        {
            float l = lab.x + 0.3963377774f * lab.y + 0.2158037573f * lab.z;
            float m = lab.x - 0.1055613458f * lab.y - 0.0638541728f * lab.z;
            float s = lab.x - 0.0894841775f * lab.y - 1.2914855480f * lab.z;
            l = l * l * l;
            m = m * m * m;
            s = s * s * s;
            return new Vector3(
                4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
                -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
                -0.0041960863f * l - 0.7034186147f * m + 1.7076949308f * s);
        }

        private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        private static float ToSrgb(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private static float Cbrt(float x) => Mathf.Pow(Mathf.Max(x, 0f), 1f / 3f);
    }

    [HarmonyPatchCategory("FactionColors")]
    [HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
    public static class Patch_Game_LoadGame_FactionColors
    {
        public static void Postfix() => FactionColors.Apply();
    }
}
