using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    // Vanilla rebuilds every curve from all records whenever TicksGame changes (every frame while
    // unpaused) and then draws one line segment per record. Here the curves are built in advance,
    // reduced to the shown range, and vanilla's own cache stamp is set so it reuses them.
    [HarmonyPatchCategory("HistoryGraph")]
    [HarmonyPatch(typeof(HistoryAutoRecorderGroup), nameof(HistoryAutoRecorderGroup.DrawGraph))]
    public static class DrawGraphPatch
    {
        // Each slice contributes its lowest and highest record. 480 slices keep a curve under the
        // 1000 visible points above which vanilla's PointsRemoveOptimization drops every 4th/5th point.
        private const int Slices = 480;

        private class BuiltState
        {
            public int records = -1;
            public float min, max;
        }

        private static readonly ConditionalWeakTable<HistoryAutoRecorderGroup, BuiltState> built = new();
        private static readonly List<int> picked = new();

        /// <summary>Curves of the group being drawn, so DrawCurveLinesPatch only takes over this graph.</summary>
        public static List<SimpleCurveDrawInfo> Drawing;

        /// <summary>Bumped whenever any group's curves are rebuilt.</summary>
        public static int CurvesVersion;

        public static void Finalizer() => Drawing = null;

        public static void Prefix(HistoryAutoRecorderGroup __instance, FloatRange section,
            List<SimpleCurveDrawInfo> ___curves, ref int ___cachedGraphTickCount)
        {
            int records = 0;
            foreach (HistoryAutoRecorder recorder in __instance.recorders)
                records += recorder.records.Count;

            BuiltState state = built.GetOrCreateValue(__instance);
            if (state.records != records || state.min != section.min || state.max != section.max
                || ___curves.Count != __instance.recorders.Count)
            {
                Rebuild(__instance, section, ___curves);
                state.records = records;
                state.min = section.min;
                state.max = section.max;
                CurvesVersion++;
            }
            ___cachedGraphTickCount = Find.TickManager.TicksGame;
            Drawing = ___curves;
        }

        private static void Rebuild(HistoryAutoRecorderGroup group, FloatRange section, List<SimpleCurveDrawInfo> curves)
        {
            curves.Clear();
            foreach (HistoryAutoRecorder recorder in group.recorders)
            {
                var info = new SimpleCurveDrawInfo
                {
                    color = recorder.def.graphColor,
                    label = recorder.def.LabelCap,
                    valueFormat = recorder.def.valueFormat,
                    curve = new SimpleCurve()
                };
                List<float> values = recorder.records;
                float daysPerRecord = recorder.def.recordTicksFrequency / 60000f;
                PickIndices(values, section, daysPerRecord);
                foreach (int i in picked)
                    info.curve.Add(new CurvePoint(i * daysPerRecord, values[i]), sort: false);
                info.curve.SortPoints();
                if (values.Count == 1)
                    info.curve.Add(new CurvePoint(1.6666667E-05f, values[0]));
                curves.Add(info);
            }
        }

        // Fills `picked` with ascending record indices: every record of the shown range (plus one
        // neighbour on each side so the edge segments match vanilla), reduced to per-slice min/max
        // when the range is long, and the first/last/min/max records outside it so the curve's
        // x and y extents (which set vanilla's axis scale) are unchanged.
        private static void PickIndices(List<float> values, FloatRange section, float daysPerRecord)
        {
            picked.Clear();
            int count = values.Count;
            if (count == 0)
                return;

            int first = Mathf.Clamp(Mathf.FloorToInt(section.min / daysPerRecord) - 1, 0, count - 1);
            int last = Mathf.Clamp(Mathf.CeilToInt(section.max / daysPerRecord) + 1, 0, count - 1);
            if (first > last)
                first = last;

            if (first > 0)
                AddExtremes(values, 0, first - 1, includeEnds: true);

            int shown = last - first + 1;
            if (shown <= Slices * 2)
            {
                for (int i = first; i <= last; i++)
                    picked.Add(i);
            }
            else
            {
                picked.Add(first);
                for (int s = 0; s < Slices; s++)
                {
                    int from = first + 1 + (int)((long)(shown - 2) * s / Slices);
                    int to = first + (int)((long)(shown - 2) * (s + 1) / Slices);
                    AddExtremes(values, from, to, includeEnds: false);
                }
                picked.Add(last);
            }

            if (last < count - 1)
                AddExtremes(values, last + 1, count - 1, includeEnds: true);
        }

        private static void AddExtremes(List<float> values, int from, int to, bool includeEnds)
        {
            if (from > to)
                return;
            int lo = from, hi = from;
            for (int i = from + 1; i <= to; i++)
            {
                if (values[i] < values[lo]) lo = i;
                if (values[i] > values[hi]) hi = i;
            }
            int a = Mathf.Min(lo, hi), b = Mathf.Max(lo, hi);
            if (includeEnds && from < a) picked.Add(from);
            picked.Add(a);
            if (b != a) picked.Add(b);
            if (includeEnds && to > b) picked.Add(to);
        }
    }
}

