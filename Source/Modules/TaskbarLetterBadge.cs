using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace VanillaTuneUp
{
    /// <summary>
    /// Mirrors the letter stack on a small badge over the game's taskbar button: the number of letters in the stack,
    /// coloured like the most urgent of them (the letter's flash colour). Opening or dismissing letters updates it.
    /// With no letters, a message on the top left shows a plain dot in the matching letter colour while it is on screen.
    /// While the game is paused the count gives way to a pause sign, on a grey badge when nothing else is showing.
    /// No banners, sounds or flashing.
    /// </summary>
    [HarmonyPatchCategory("TaskbarLetterBadge")]
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.LetterStackUpdate))]
    public static class Patch_LetterStack_LetterStackUpdate
    {
        private static readonly AccessTools.FieldRef<List<Message>> LiveMessages =
            AccessTools.StaticFieldRefAccess<List<Message>>(AccessTools.Field(typeof(Messages), "liveMessages"));

        public static void Postfix(LetterStack __instance) =>
            TaskbarLetterBadge.Sync(__instance.LettersListForReading, LiveMessages(), Find.TickManager?.Paused ?? false);
    }

    /// <summary>
    /// Critical (red) alerts such as a life-threatening condition turn the badge big-threat red, or show a red dot when
    /// there are no letters.
    /// </summary>
    [HarmonyPatchCategory("TaskbarLetterBadge")]
    [HarmonyPatch(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutUpdate))]
    public static class Patch_AlertsReadout_AlertsReadoutUpdate
    {
        private static readonly AccessTools.FieldRef<AlertsReadout, List<Alert>> ActiveAlerts =
            AccessTools.FieldRefAccess<AlertsReadout, List<Alert>>("activeAlerts");

        public static void Postfix(AlertsReadout __instance) => TaskbarLetterBadge.SyncAlerts(ActiveAlerts(__instance));
    }

    [HarmonyPatchCategory("TaskbarLetterBadge")]
    [HarmonyPatch(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld))]
    public static class Patch_MemoryUtility_ClearAllMapsAndWorld_TaskbarLetterBadge
    {
        public static void Postfix()
        {
            TaskbarLetterBadge.Sync(null, null, false);
            TaskbarLetterBadge.SyncAlerts(null);
        }
    }

    public static class TaskbarLetterBadge
    {
        private static readonly Color PausedGrey = new Color(0.45f, 0.45f, 0.45f);

        private static int shownCount;
        private static LetterDef shownDef;
        private static bool shownCritical;
        private static bool shownPaused;
        private static LetterDef shownMessage;

        public static void SyncAlerts(List<Alert> alerts)
        {
            bool critical = false;
            if (alerts != null)
            {
                foreach (Alert alert in alerts)
                {
                    if (alert != null && alert.Priority == AlertPriority.Critical)
                    {
                        critical = true;
                        break;
                    }
                }
            }
            if (critical == shownCritical)
                return;
            shownCritical = critical;
            Redraw();
        }

        /// <summary>Called every frame; the icon is redrawn only when the count, a colour or the pause state changes.</summary>
        public static void Sync(List<Letter> letters, List<Message> messages, bool paused)
        {
            int count = 0;
            LetterDef mostUrgent = null;
            if (letters != null)
            {
                foreach (Letter letter in letters)
                {
                    if (letter?.def == null)
                        continue;
                    count++;
                    if (mostUrgent == null || MoreUrgent(letter.def, mostUrgent))
                        mostUrgent = letter.def;
                }
            }
            LetterDef message = null;
            if (messages != null)
            {
                foreach (Message msg in messages)
                {
                    LetterDef def = msg == null || msg.Expired ? null : MessageColour(msg.def);
                    if (def != null && (message == null || MoreUrgent(def, message)))
                        message = def;
                }
            }
            if (count == shownCount && mostUrgent == shownDef && paused == shownPaused && message == shownMessage)
                return;
            shownCount = count;
            shownDef = mostUrgent;
            shownPaused = paused;
            shownMessage = message;
            Redraw();
        }

        // A critical alert outranks every letter, so it paints the badge in the big-threat colour; with no letters
        // it is a plain red dot. A message on screen ranks below both and shows a plain dot. Pausing swaps the count
        // for a pause sign and keeps the colour; with nothing else to show the badge is grey.
        private static void Redraw()
        {
            Color fill;
            if (shownCritical)
                fill = LetterDefOf.ThreatBig.flashColor;
            else if (shownCount > 0)
                fill = shownDef.flashColor;
            else if (shownMessage != null)
                fill = shownMessage.flashColor;
            else if (shownPaused)
                fill = PausedGrey;
            else
            {
                TaskbarOverlay.Clear();
                return;
            }
            // The description is only alt text for accessibility tools; Windows does not show it on the taskbar.
            string tip = shownCount > 0 ? "VTU_TaskbarLetterBadge_Tip".Translate(shownCount).ToString()
                : shownCritical ? "VTU_TaskbarLetterBadge_CriticalTip".Translate().ToString()
                : shownMessage != null ? "VTU_TaskbarLetterBadge_MessageTip".Translate().ToString()
                : "VTU_TaskbarLetterBadge_PausedTip".Translate().ToString();
            TaskbarOverlay.Show(BadgeIcon.Draw(fill, shownCount, shownPaused), tip);
        }

        // Messages have no colour of their own, so each event type borrows the matching letter's. Feedback on the
        // player's own clicks (rejected, caution, silent input) and "situation resolved" are left out.
        private static LetterDef MessageColour(MessageTypeDef def)
        {
            if (def == MessageTypeDefOf.ThreatBig)
                return LetterDefOf.ThreatBig;
            if (def == MessageTypeDefOf.ThreatSmall)
                return LetterDefOf.ThreatSmall;
            if (def == MessageTypeDefOf.PawnDeath)
                return LetterDefOf.Death;
            if (def == MessageTypeDefOf.NegativeHealthEvent || def == MessageTypeDefOf.NegativeEvent)
                return LetterDefOf.NegativeEvent;
            if (def == MessageTypeDefOf.NeutralEvent || def == MessageTypeDefOf.TaskCompletion)
                return LetterDefOf.NeutralEvent;
            if (def == MessageTypeDefOf.PositiveEvent)
                return LetterDefOf.PositiveEvent;
            return null;
        }

        // Vanilla's own urgency order: letters that pause on a lower auto-pause setting first, then the faster flash.
        private static bool MoreUrgent(LetterDef a, LetterDef b)
        {
            int ra = PauseRank(a.pauseMode), rb = PauseRank(b.pauseMode);
            if (ra != rb)
                return ra < rb;
            return a.flashInterval < b.flashInterval;
        }

        private static int PauseRank(AutomaticPauseMode mode) => mode == AutomaticPauseMode.Never ? int.MaxValue : (int)mode;
    }

    /// <summary>Draws the badge as BGRA pixels: a filled circle with a 3x5 pixel-font count or pause sign.</summary>
    internal static class BadgeIcon
    {
        private static readonly string[] Glyphs =
        {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
            "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111",
        };
        private const string Plus = "000010111010000";
        private const string Pause = "101101101101101";

        public static byte[] Draw(Color fill, int count, bool paused = false)
        {
            int size = TaskbarOverlay.IconSize;
            var pixels = new byte[size * size * 4];
            float radius = size / 2f;
            const int Samples = 4;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            float dx = x + (sx + 0.5f) / Samples - radius, dy = y + (sy + 0.5f) / Samples - radius;
                            if (dx * dx + dy * dy <= radius * radius)
                                covered++;
                        }
                    }
                    if (covered > 0)
                        SetPixel(pixels, size, x, y, fill, covered / (float)(Samples * Samples));
                }
            }

            if (count == 0 && !paused)
                return pixels;
            string text = paused ? "|" : count > 9 ? "9+" : count.ToString();
            int scale = Math.Max(1, size / 8);
            // Shrink "9+" until its corners stay inside the circle.
            while (scale > 1 && Square(text.Length * 4 * scale - scale) + Square(5 * scale) > Square(size - 2))
                scale--;
            int width = text.Length * 3 * scale + (text.Length - 1) * scale;
            int left = (size - width) / 2, top = (size - 5 * scale) / 2;
            float luminance = 0.299f * fill.r + 0.587f * fill.g + 0.114f * fill.b;
            Color ink = luminance > 0.55f ? new Color(0.1f, 0.1f, 0.1f) : Color.white;
            for (int i = 0; i < text.Length; i++)
            {
                string glyph = text[i] == '|' ? Pause : text[i] == '+' ? Plus : Glyphs[text[i] - '0'];
                int gx = left + i * 4 * scale;
                for (int bit = 0; bit < 15; bit++)
                {
                    if (glyph[bit] != '1')
                        continue;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            SetPixel(pixels, size, gx + bit % 3 * scale + dx, top + bit / 3 * scale + dy, ink, 1f);
                }
            }
            return pixels;
        }

        private static int Square(int v) => v * v;

        private static void SetPixel(byte[] pixels, int size, int x, int y, Color c, float alpha)
        {
            if (x < 0 || y < 0 || x >= size || y >= size)
                return;
            int i = (y * size + x) * 4;
            pixels[i] = (byte)(Mathf.Clamp01(c.b) * 255f);
            pixels[i + 1] = (byte)(Mathf.Clamp01(c.g) * 255f);
            pixels[i + 2] = (byte)(Mathf.Clamp01(c.r) * 255f);
            pixels[i + 3] = (byte)(Mathf.Clamp01(alpha) * 255f);
        }
    }

    /// <summary>
    /// ITaskbarList3::SetOverlayIcon on the game window. COM is called through the raw vtable so it works on both the
    /// Unity (1.6) and the new (1.7) runtime without either one's COM interop.
    /// </summary>
    internal static class TaskbarOverlay
    {
        private static IntPtr taskbar;
        private static IntPtr window;
        private static IntPtr icon;
        private static bool failed;
        private static EnumWindowsProc enumProc;

        public static int IconSize => Math.Max(16, GetSystemMetrics(SM_CXSMICON));

        public static void Show(byte[] pixels, string description)
        {
            if (!Init())
                return;
            try
            {
                IntPtr newIcon = CreateIcon(pixels, IconSize);
                SetOverlay(newIcon, description);
                // The previous icon is freed only after it has been replaced on the taskbar.
                if (icon != IntPtr.Zero)
                    DestroyIcon(icon);
                icon = newIcon;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        public static void Clear()
        {
            if (taskbar == IntPtr.Zero || failed)
                return;
            try
            {
                SetOverlay(IntPtr.Zero, null);
                if (icon != IntPtr.Zero)
                    DestroyIcon(icon);
                icon = IntPtr.Zero;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static IntPtr Window()
        {
            if (window != IntPtr.Zero && IsWindow(window))
                return window;
            window = IntPtr.Zero;
            uint pid = (uint)Process.GetCurrentProcess().Id;
            enumProc ??= (h, _) =>
            {
                GetWindowThreadProcessId(h, out uint owner);
                if (owner != pid || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero)
                    return true;
                window = h;
                return false;
            };
            EnumWindows(enumProc, IntPtr.Zero);
            return window;
        }

        private static bool Init()
        {
            if (failed || Window() == IntPtr.Zero)
                return false;
            if (taskbar != IntPtr.Zero)
                return true;
            try
            {
                CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                Guid clsid = CLSID_TaskbarList, iid = IID_ITaskbarList3;
                Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out taskbar));
                Check(Method<HrInitFn>(SlotHrInit)(taskbar));
                return true;
            }
            catch (Exception e)
            {
                Fail(e);
                return false;
            }
        }

        private static void SetOverlay(IntPtr hIcon, string description) =>
            Check(Method<SetOverlayIconFn>(SlotSetOverlayIcon)(taskbar, window, hIcon, description));

        private static T Method<T>(int slot) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(taskbar);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
        }

        private static IntPtr CreateIcon(byte[] bgra, int size)
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
            try
            {
                if (color == IntPtr.Zero || mask == IntPtr.Zero)
                    throw new InvalidOperationException("bitmap creation failed");
                Marshal.Copy(bgra, 0, bits, bgra.Length);
                var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
                IntPtr result = CreateIconIndirect(ref info);
                if (result == IntPtr.Zero)
                    throw new InvalidOperationException("CreateIconIndirect failed");
                return result;
            }
            finally
            {
                if (color != IntPtr.Zero)
                    DeleteObject(color);
                if (mask != IntPtr.Zero)
                    DeleteObject(mask);
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        private static void Fail(Exception e)
        {
            failed = true;
            Log.Warning($"[Vanilla Tune-Up] TaskbarLetterBadge: taskbar badge unavailable, turned off for this session: {e.Message}");
        }

        private static readonly Guid CLSID_TaskbarList = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
        private static readonly Guid IID_ITaskbarList3 = new Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");
        // IUnknown (3) + ITaskbarList (HrInit 3 .. SetActiveAlt 7) + ITaskbarList2 (8) + ITaskbarList3 (9 ..).
        private const int SlotHrInit = 3;
        private const int SlotSetOverlayIcon = 18;
        private const uint CLSCTX_INPROC_SERVER = 1;
        private const uint COINIT_APARTMENTTHREADED = 2;
        private const int SM_CXSMICON = 49;
        private const uint GW_OWNER = 4;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HrInitFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int SetOverlayIconFn(IntPtr self, IntPtr hwnd, IntPtr hIcon, string description);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr ppv);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
    }
}
