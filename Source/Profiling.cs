#if PROFILE
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>Measurement-only build: logs per-frame History tab timings every 120 frames.</summary>
    public static class HistoryProfiler
    {
        public static readonly string[] Names = { "DoWindowContents", "DoGraphPage", "DrawGraph", "DrawCurveLines", "DrawCurveMarks" };
        public static readonly long[] Ticks = new long[Names.Length];
        private static int frames;
        private static int lastFrame = -1;
        private static int events;

        public static void Begin(ref long state) => state = Stopwatch.GetTimestamp();

        public static void End(int slot, long state) => Ticks[slot] += Stopwatch.GetTimestamp() - state;

        public static void OnWindowEvent()
        {
            events++;
            if (Time.frameCount == lastFrame)
                return;
            lastFrame = Time.frameCount;
            if (++frames < 120)
                return;
            var sb = new System.Text.StringBuilder("[Vanilla Tune-Up] History tab ms/frame over 120 frames:");
            for (int i = 0; i < Names.Length; i++)
            {
                sb.Append($" {Names[i]}={Ticks[i] * 1000.0 / Stopwatch.Frequency / frames:F2}");
                Ticks[i] = 0;
            }
            sb.Append($" events/frame={events / (float)frames:F1} unpaused={!Find.TickManager.Paused}");
            Log.Message(sb.ToString());
            frames = 0;
            events = 0;
        }
    }

    [HarmonyPatchCategory("Profile")]
    [HarmonyPatch(typeof(MainTabWindow_History), nameof(MainTabWindow_History.DoWindowContents))]
    public static class Profile_DoWindowContents
    {
        public static void Prefix(ref long __state) { HistoryProfiler.OnWindowEvent(); HistoryProfiler.Begin(ref __state); }
        public static void Postfix(long __state) => HistoryProfiler.End(0, __state);
    }

    [HarmonyPatchCategory("Profile")]
    [HarmonyPatch(typeof(MainTabWindow_History), "DoGraphPage")]
    public static class Profile_DoGraphPage
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref long __state) => HistoryProfiler.Begin(ref __state);
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(long __state) => HistoryProfiler.End(1, __state);
    }

    [HarmonyPatchCategory("Profile")]
    [HarmonyPatch(typeof(HistoryAutoRecorderGroup), nameof(HistoryAutoRecorderGroup.DrawGraph))]
    public static class Profile_DrawGraph
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref long __state) => HistoryProfiler.Begin(ref __state);
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(long __state) => HistoryProfiler.End(2, __state);
    }

    [HarmonyPatchCategory("Profile")]
    [HarmonyPatch(typeof(SimpleCurveDrawer), nameof(SimpleCurveDrawer.DrawCurveLines))]
    public static class Profile_DrawCurveLines
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref long __state) => HistoryProfiler.Begin(ref __state);
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(long __state) => HistoryProfiler.End(3, __state);
    }

    [HarmonyPatchCategory("Profile")]
    [HarmonyPatch(typeof(SimpleCurveDrawer), nameof(SimpleCurveDrawer.DrawCurveMarks))]
    public static class Profile_DrawCurveMarks
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref long __state) => HistoryProfiler.Begin(ref __state);
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(long __state) => HistoryProfiler.End(4, __state);
    }
}
#endif
