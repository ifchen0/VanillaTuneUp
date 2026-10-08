using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// CPU rasterizer for the History graph. Draws into a cached texture so a repaint is one
    /// GUI.DrawTexture instead of one GL matrix push and draw call per line segment or mark.
    /// Coordinates are GUI units inside the texture's rect, y pointing down.
    /// </summary>
    public sealed class GraphRaster
    {
        public Texture2D Texture;
        private Color32[] pixels;
        private float[] coverage;
        private int width, height;
        private float sx, sy;
        private int covMinX, covMaxX, covMinY, covMaxY;

        /// <summary>Clears the canvas, resizing it to cover <paramref name="size"/> GUI units at the current UI scale.</summary>
        public void Begin(Vector2 size)
        {
            int w = Mathf.Max(1, Mathf.CeilToInt(size.x * Prefs.UIScale));
            int h = Mathf.Max(1, Mathf.CeilToInt(size.y * Prefs.UIScale));
            if (Texture == null || w != width || h != height)
            {
                if (Texture != null)
                    UnityEngine.Object.Destroy(Texture);
                width = w;
                height = h;
                Texture = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                pixels = new Color32[w * h];
                coverage = new float[w * h];
            }
            else
            {
                Array.Clear(pixels, 0, pixels.Length);
            }
            sx = w / size.x;
            sy = h / size.y;
            covMinX = covMinY = int.MaxValue;
            covMaxX = covMaxY = -1;
        }

        public void End()
        {
            Texture.SetPixels32(pixels);
            Texture.Apply(updateMipmaps: false);
        }

        /// <summary>
        /// Adds a line to the current stroke, matching Widgets.DrawLine with width 1: a 3-unit quad whose
        /// 1x3 texture fades linearly from full alpha at the center to none 1 unit away.
        /// Overlapping lines of one stroke take the highest alpha instead of stacking.
        /// </summary>
        public void Line(Vector2 a, Vector2 b)
        {
            float x0 = a.x * sx, y0 = a.y * sy, x1 = b.x * sx, y1 = b.y * sy;
            float dx = x1 - x0, dy = y1 - y0;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 0.0001f * sx * sx)
                return;
            float reach = (sx + sy) * 0.5f;
            float invReach = 1f / reach;

            int colFrom = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(x0, x1) - reach));
            int colTo = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(x0, x1) + reach));
            for (int col = colFrom; col <= colTo; col++)
            {
                float cx = col + 0.5f;
                // Part of the line within `reach` of this column, so only rows near it are visited.
                float t0 = 0f, t1 = 1f;
                if (Mathf.Abs(dx) > 0.0001f)
                {
                    t0 = Mathf.Clamp01((cx - reach - x0) / dx);
                    t1 = Mathf.Clamp01((cx + reach - x0) / dx);
                }
                float ya = y0 + dy * t0, yb = y0 + dy * t1;
                int rowFrom = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ya, yb) - reach));
                int rowTo = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(ya, yb) + reach));
                for (int row = rowFrom; row <= rowTo; row++)
                {
                    float cy = row + 0.5f;
                    float t = Mathf.Clamp01(((cx - x0) * dx + (cy - y0) * dy) / lenSq);
                    float ex = x0 + dx * t - cx, ey = y0 + dy * t - cy;
                    float alpha = 1f - Mathf.Sqrt(ex * ex + ey * ey) * invReach;
                    if (alpha <= 0f)
                        continue;
                    int i = (height - 1 - row) * width + col;
                    if (alpha > coverage[i])
                        coverage[i] = alpha;
                    if (col < covMinX) covMinX = col;
                    if (col > covMaxX) covMaxX = col;
                    if (row < covMinY) covMinY = row;
                    if (row > covMaxY) covMaxY = row;
                }
            }
        }

        /// <summary>Blends the current stroke over the canvas in <paramref name="color"/> and starts a new stroke.</summary>
        public void FlushStroke(Color color)
        {
            for (int row = covMinY; row <= covMaxY; row++)
            {
                int start = (height - 1 - row) * width;
                for (int col = covMinX; col <= covMaxX; col++)
                {
                    int i = start + col;
                    float c = coverage[i];
                    if (c <= 0f)
                        continue;
                    coverage[i] = 0f;
                    Blend(i, color.r, color.g, color.b, color.a * c);
                }
            }
            covMinX = covMinY = int.MaxValue;
            covMaxX = covMaxY = -1;
        }

        /// <summary>Draws <paramref name="image"/> (readable pixels, row 0 at the bottom) into <paramref name="rect"/>, tinted.</summary>
        public void Stamp(Rect rect, Color[] image, int imageW, int imageH, Color tint)
        {
            float px0 = rect.xMin * sx, py0 = rect.yMin * sy, pw = rect.width * sx, ph = rect.height * sy;
            int colFrom = Mathf.Max(0, Mathf.FloorToInt(px0)), colTo = Mathf.Min(width - 1, Mathf.CeilToInt(px0 + pw) - 1);
            int rowFrom = Mathf.Max(0, Mathf.FloorToInt(py0)), rowTo = Mathf.Min(height - 1, Mathf.CeilToInt(py0 + ph) - 1);
            for (int row = rowFrom; row <= rowTo; row++)
            {
                float v = 1f - (row + 0.5f - py0) / ph;
                for (int col = colFrom; col <= colTo; col++)
                {
                    float u = (col + 0.5f - px0) / pw;
                    if (u < 0f || u > 1f || v < 0f || v > 1f)
                        continue;
                    Color s = Sample(image, imageW, imageH, u, v);
                    float a = s.a * tint.a;
                    if (a <= 0f)
                        continue;
                    Blend((height - 1 - row) * width + col, s.r * tint.r, s.g * tint.g, s.b * tint.b, a);
                }
            }
        }

        private static Color Sample(Color[] image, int w, int h, float u, float v)
        {
            float fx = Mathf.Clamp(u * w - 0.5f, 0f, w - 1), fy = Mathf.Clamp(v * h - 0.5f, 0f, h - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = fx - x0, ty = fy - y0;
            Color bottom = Color.LerpUnclamped(image[y0 * w + x0], image[y0 * w + x1], tx);
            Color top = Color.LerpUnclamped(image[y1 * w + x0], image[y1 * w + x1], tx);
            return Color.LerpUnclamped(bottom, top, ty);
        }

        // Straight-alpha "over", as the GUI shader blends.
        private void Blend(int i, float r, float g, float b, float a)
        {
            Color32 dst = pixels[i];
            float da = dst.a / 255f;
            float keep = da * (1f - a);
            float oa = a + keep;
            if (oa <= 0f)
                return;
            pixels[i] = new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt((r * a + dst.r / 255f * keep) / oa * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((g * a + dst.g / 255f * keep) / oa * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((b * a + dst.b / 255f * keep) / oa * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(oa * 255f), 0, 255));
        }

        /// <summary>Copies a GPU-only texture into readable pixels. Call outside GUI drawing; returns null on failure.</summary>
        public static Color[] ReadPixels(Texture2D source)
        {
            if (source == null)
                return null;
            if (source.isReadable)
                return source.GetPixels();
            RenderTexture previous = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, mipChain: false);
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                copy.Apply();
                return copy.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null)
                    UnityEngine.Object.Destroy(copy);
            }
        }
    }
}
