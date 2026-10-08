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
    // Vanilla draws every line segment with its own GL matrix push and draw call (about 1000 per
    // curve per repaint). The curves of the History graph are rasterized into one texture instead,
    // rebuilt only when the curves, the shown range or the graph size change.
    [HarmonyPatchCategory("HistoryGraph")]
    [HarmonyPatch(typeof(SimpleCurveDrawer), nameof(SimpleCurveDrawer.DrawCurveLines))]
    public static class DrawCurveLinesPatch
    {
        private static readonly GraphRaster raster = new();
        private static List<SimpleCurveDrawInfo> builtCurves;
        private static int builtVersion = -1;
        private static Vector2 builtSize;
        private static Rect builtView;
        private static float builtScale;

        public static bool Prefix(Rect rect, SimpleCurveDrawInfo curve, Rect viewRect)
        {
            List<SimpleCurveDrawInfo> curves = DrawGraphPatch.Drawing;
            if (curves == null || Event.current.type != EventType.Repaint)
                return true;
            int index = curves.IndexOf(curve);
            if (index < 0)
                return true;
            if (index == 0)
                Draw(rect, viewRect, curves);
            return false;
        }

        private static void Draw(Rect rect, Rect viewRect, List<SimpleCurveDrawInfo> curves)
        {
            // Vanilla draws inside a group that starts 1 unit above the graph and ends 1 unit below it.
            Rect area = new Rect(rect.x, rect.y - 1f, rect.width, rect.height + 2f);
            if (builtCurves != curves || builtVersion != DrawGraphPatch.CurvesVersion || builtSize != rect.size
                || builtView != viewRect || builtScale != Prefs.UIScale || raster.Texture == null)
            {
                Rebuild(rect, viewRect, curves, area.size);
                builtCurves = curves;
                builtVersion = DrawGraphPatch.CurvesVersion;
                builtSize = rect.size;
                builtView = viewRect;
                builtScale = Prefs.UIScale;
            }
            GUI.color = Color.white;
            GUI.DrawTexture(area, raster.Texture);
        }

        // Same segments as vanilla's anti-aliased branch, including the flat lines from the first
        // and last point to the graph edges.
        private static void Rebuild(Rect rect, Rect viewRect, List<SimpleCurveDrawInfo> curves, Vector2 size)
        {
            raster.Begin(size);
            foreach (SimpleCurveDrawInfo info in curves)
            {
                SimpleCurve curve = info.curve;
                if (curve == null || curve.PointsCount == 0)
                    continue;
                Vector2 prev = default;
                for (int i = 0; i < curve.PointsCount; i++)
                {
                    Vector2 point = SimpleCurveDrawer.CurveToScreenCoordsInsideScreenRect(rect, viewRect, curve[i].Loc);
                    if (i > 0 && ((prev.x >= 0f && prev.x <= rect.width) || (point.x >= 0f && point.x <= rect.width)))
                        raster.Line(prev, point);
                    prev = point;
                }
                Vector2 head = SimpleCurveDrawer.CurveToScreenCoordsInsideScreenRect(rect, viewRect, curve[0].Loc);
                Vector2 tail = SimpleCurveDrawer.CurveToScreenCoordsInsideScreenRect(rect, viewRect, curve[curve.PointsCount - 1].Loc);
                raster.Line(head, new Vector2(0f, head.y));
                raster.Line(tail, new Vector2(rect.width, tail.y));
                raster.FlushStroke(info.color);
            }
            raster.End();
        }
    }

    // Vanilla rebuilds the mark list from every tale on every GUI event (several times a frame).
    // The marks are built once and copied in, and rebuilt when the tale list changes (checked
    // at least every 2 seconds for anything else, such as a hidden flag or the language).
    [HarmonyPatchCategory("HistoryGraph")]
    [HarmonyPatch(typeof(MainTabWindow_History), "DoGraphPage")]
    public static class DoGraphPagePatch
    {
        /// <summary>The History tab's mark list, so DrawCurveMarksPatch can use its cached drawing.</summary>
        public static List<CurveMark> Marks;

        /// <summary>Bumped whenever the cached marks change.</summary>
        public static int Version;

        private static readonly AccessTools.FieldRef<List<CurveMark>> MarksField =
            AccessTools.StaticFieldRefAccess<List<CurveMark>>(AccessTools.Field(typeof(MainTabWindow_History), "marks"));
        private static readonly MethodInfo TaleManagerGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.TaleManager));
        private static readonly MethodInfo AllTalesGetter = AccessTools.PropertyGetter(typeof(TaleManager), nameof(TaleManager.AllTalesListForReading));
        private static readonly List<Tale> NoTales = new();
        private static readonly List<CurveMark> cached = new();
        private static readonly List<CurveMark> fresh = new();
        private static int talesCount = -1;
        private static Tale lastTale;
        private static Game game;
        private static float nextCheck;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 1 < codes.Count; i++)
            {
                // Find.TaleManager.AllTalesListForReading -> FillMarks(); the vanilla loop then runs over an empty list.
                if (codes[i].Calls(TaleManagerGetter) && codes[i + 1].Calls(AllTalesGetter))
                {
                    codes[i] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(codes[i]);
                    codes[i + 1] = CodeInstruction.Call(typeof(DoGraphPagePatch), nameof(FillMarks)).MoveLabelsFrom(codes[i + 1]);
                    return codes;
                }
            }
            Log.Warning("[Vanilla Tune-Up] Could not find the tale loop in MainTabWindow_History.DoGraphPage; marks left unchanged.");
            return codes;
        }

        public static List<Tale> FillMarks()
        {
            List<Tale> tales = Find.TaleManager.AllTalesListForReading;
            Tale last = tales.Count > 0 ? tales[tales.Count - 1] : null;
            float now = Time.realtimeSinceStartup;
            if (tales.Count != talesCount || last != lastTale || game != Current.Game || now >= nextCheck)
            {
                talesCount = tales.Count;
                lastTale = last;
                game = Current.Game;
                nextCheck = now + 2f;
                fresh.Clear();
                foreach (Tale tale in tales)
                {
                    if (tale.def.type == TaleType.PermanentHistorical && !tale.hidden)
                        fresh.Add(new CurveMark(GenDate.TickAbsToGame(tale.date) / 60000f, tale.ShortSummary, tale.def.historyGraphColor));
                }
                if (!Same(fresh, cached))
                {
                    cached.Clear();
                    cached.AddRange(fresh);
                    Version++;
                }
            }
            List<CurveMark> marks = MarksField();
            marks.AddRange(cached);
            Marks = marks;
            return NoTales;
        }

        private static bool Same(List<CurveMark> a, List<CurveMark> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].X != b[i].X || a[i].Color != b[i].Color || a[i].Message != b[i].Message)
                    return false;
            }
            return true;
        }
    }

    // Every mark is drawn separately; hundreds of raids land on the same pixel when a long history
    // is shown. The History tab's marks are rasterized into one texture (one dot per pixel and
    // color), and tooltips are looked up only for marks near the mouse. Each mark under the mouse
    // still gets its own tooltip region, as in vanilla.
    [HarmonyPatchCategory("HistoryGraph")]
    [HarmonyPatch(typeof(SimpleCurveDrawer), nameof(SimpleCurveDrawer.DrawCurveMarks))]
    public static class DrawCurveMarksPatch
    {
        private static readonly HashSet<long> drawn = new();
        private static readonly Texture2D pointTex =
            (Texture2D)AccessTools.Field(typeof(SimpleCurveDrawer), "CurvePoint").GetValue(null);
        private static Color[] pointPixels;
        private static bool pointRead;

        private static readonly GraphRaster raster = new();
        private static int builtVersion = -1;
        private static Vector2 builtSize;
        private static Rect builtView;
        private static float builtScale;
        private static float[] tipX = new float[0];
        private static int[] tipIndex = new int[0];
        private static readonly List<float> xs = new();
        private static readonly List<int> indices = new();
        private static readonly List<int> hits = new();

        // Reads the dot texture once while patching, outside GUI drawing.
        public static bool Prepare()
        {
            if (!pointRead)
            {
                pointRead = true;
                try
                {
                    pointPixels = GraphRaster.ReadPixels(pointTex);
                }
                catch (Exception e)
                {
                    Log.Warning($"[Vanilla Tune-Up] Could not read the graph mark texture; marks are drawn one by one: {e.Message}");
                }
            }
            return true;
        }

        public static bool Prefix(Rect rect, Rect viewRect, List<CurveMark> marks)
        {
            if (pointPixels == null || marks != DoGraphPagePatch.Marks)
            {
                DrawEach(rect, viewRect, marks);
                return false;
            }
            if (builtVersion != DoGraphPagePatch.Version || builtSize != rect.size || builtView != viewRect
                || builtScale != Prefs.UIScale || raster.Texture == null)
            {
                Rebuild(rect, viewRect, marks);
                builtVersion = DoGraphPagePatch.Version;
                builtSize = rect.size;
                builtView = viewRect;
                builtScale = Prefs.UIScale;
            }
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(rect.x - 5f, rect.y, rect.width + 10f, 10f), raster.Texture);
            Tooltips(rect, viewRect, marks);
            return false;
        }

        // Mark centers are stored relative to rect.x; the texture starts 5 units left of the graph.
        private static void Rebuild(Rect rect, Rect viewRect, List<CurveMark> marks)
        {
            float xMin = viewRect.x;
            float xMax = viewRect.x + viewRect.width;
            raster.Begin(new Vector2(rect.width + 10f, 10f));
            drawn.Clear();
            xs.Clear();
            indices.Clear();
            for (int i = 0; i < marks.Count; i++)
            {
                CurveMark mark = marks[i];
                if (mark.X < xMin || mark.X > xMax)
                    continue;
                float x = (mark.X - xMin) / (xMax - xMin) * rect.width;
                xs.Add(x);
                indices.Add(i);
                Color32 c = mark.Color;
                long key = ((long)Mathf.RoundToInt(x) << 32) | (uint)(c.r | c.g << 8 | c.b << 16 | c.a << 24);
                if (drawn.Add(key))
                    raster.Stamp(new Rect(x, 0f, 10f, 10f), pointPixels, pointTex.width, pointTex.height, mark.Color);
            }
            raster.End();
            tipX = xs.ToArray();
            tipIndex = indices.ToArray();
            Array.Sort(tipX, tipIndex);
        }

        private static void Tooltips(Rect rect, Rect viewRect, List<CurveMark> marks)
        {
            Vector2 mouse = Event.current.mousePosition;
            float y = rect.y + 5f;
            if (mouse.y < y - 5f || mouse.y > y + 5f)
                return;
            float rel = mouse.x - rect.x;
            int lo = 0, hi = tipX.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (tipX[mid] < rel - 5f) lo = mid + 1; else hi = mid;
            }
            hits.Clear();
            for (int j = lo; j < tipX.Length && tipX[j] <= rel + 5f; j++)
                hits.Add(tipIndex[j]);
            hits.Sort();
            float xMin = viewRect.x;
            float xMax = viewRect.x + viewRect.width;
            foreach (int i in hits)
            {
                CurveMark mark = marks[i];
                float x = rect.x + (mark.X - xMin) / (xMax - xMin) * rect.width;
                Rect tipRect = new Rect(x - 5f, y - 5f, 10f, 10f);
                if (Mouse.IsOver(tipRect))
                    TooltipHandler.TipRegion(tipRect, new TipSignal(mark.Message));
            }
        }

        private static void DrawEach(Rect rect, Rect viewRect, List<CurveMark> marks)
        {
            float xMin = viewRect.x;
            float xMax = viewRect.x + viewRect.width;
            float y = rect.y + 5f;
            drawn.Clear();
            for (int i = 0; i < marks.Count; i++)
            {
                CurveMark mark = marks[i];
                if (mark.X < xMin || mark.X > xMax)
                    continue;
                float x = rect.x + (mark.X - xMin) / (xMax - xMin) * rect.width;
                Color32 c = mark.Color;
                long key = ((long)Mathf.RoundToInt(x) << 32) | (uint)(c.r | c.g << 8 | c.b << 16 | c.a << 24);
                if (drawn.Add(key))
                {
                    GUI.color = mark.Color;
                    GUI.DrawTexture(new Rect(x - 5f, y - 5f, 10f, 10f), pointTex);
                }
                Rect tipRect = new Rect(x - 5f, y - 5f, 10f, 10f);
                if (Mouse.IsOver(tipRect))
                    TooltipHandler.TipRegion(tipRect, new TipSignal(mark.Message));
            }
            GUI.color = Color.white;
        }
    }
}
