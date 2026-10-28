using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using Verse;
using Verse.Steam;

namespace VanillaTuneUp
{
    /// <summary>
    /// Lists active Workshop mods on the main menu, newest update first, marking those Steam updated since the player
    /// last read them. Update times come from Steam's local install info, so the list needs no network. A mod seen for
    /// the first time (new subscription, or the first run of this module) is recorded as read. An update left unopened
    /// through <see cref="MaxUnreadStartups"/> game starts is marked read on the next one, so players who never open
    /// the notes do not end up with every mod highlighted. Change notes are fetched from the item's Workshop change log
    /// page when the player opens it.
    /// </summary>
    public static class ModUpdateNotes
    {
        public sealed class Entry
        {
            public ModMetaData Mod;
            public PublishedFileId_t FileId;
            // Last read install time as of startup; kept when the entry is marked read so its notes stay the same.
            public uint SeenTime;
            public uint InstalledTime;
            public bool Unread;

            public NotesState State;
            public List<ChangeNote> Notes;
            public UnityWebRequest Request;

            public string Key => FileId.ToString();
            public string ChangeLogUrl => "https://steamcommunity.com/sharedfiles/filedetails/changelog/" + Key;
            // Unread: everything since the last read version. Read: just the installed version's entry.
            public uint NotesAfter => SeenTime < InstalledTime ? SeenTime : InstalledTime - 1;
        }

        public enum NotesState { NotFetched, Loading, Loaded, Failed }

        public struct ChangeNote
        {
            public uint Time;
            public string Text;
        }

        public static readonly List<Entry> Entries = new List<Entry>();
        public static int UnreadCount;

        public const int MaxUnreadStartups = 3;

        // Each change log entry is <p id="unix time of the update">notes</p>; the id matches Steam's install timestamp.
        private static readonly Regex NoteRegex = new Regex("<p id=\"(\\d+)\">(.*?)</p>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex HeadingRegex = new Regex("<div class=\"bb_h\\d\">(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex TagRegex = new Regex("<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex BlankLinesRegex = new Regex("\n{3,}", RegexOptions.Compiled);

        public static void Scan()
        {
            Dictionary<string, int> seen = VanillaTuneUpMod.Settings.seenModUpdates;
            // Rebuilt every start from the updates still unread, so read or replaced versions drop out on their own.
            Dictionary<string, int> oldShows = VanillaTuneUpMod.Settings.unreadModUpdateShows;
            var shows = new Dictionary<string, int>();
            bool changed = false;
            foreach (ModMetaData mod in ModLister.AllInstalledMods)
            {
                if (!mod.OnSteamWorkshop)
                    continue;
                PublishedFileId_t fileId = mod.GetPublishedFileId();
                if (fileId == PublishedFileId_t.Invalid || !SteamUGC.GetItemInstallInfo(fileId, out _, out _, 257u, out uint installed))
                    continue;
                string key = fileId.ToString();
                if (!seen.TryGetValue(key, out int seenTime))
                {
                    seenTime = (int)installed;
                    seen[key] = seenTime;
                    changed = true;
                }
                if (!mod.Active)
                    continue;
                bool unread = installed > (uint)seenTime;
                if (unread)
                {
                    string showKey = key + "@" + installed;
                    oldShows.TryGetValue(showKey, out int shown);
                    if (shown >= MaxUnreadStartups)
                    {
                        seenTime = (int)installed;
                        seen[key] = seenTime;
                        unread = false;
                    }
                    else
                    {
                        shows[showKey] = shown + 1;
                    }
                    changed = true;
                }
                Entries.Add(new Entry { Mod = mod, FileId = fileId, SeenTime = (uint)seenTime, InstalledTime = installed, Unread = unread });
                if (unread)
                    UnreadCount++;
            }
            Entries.SortByDescending(e => e.InstalledTime);
            if (oldShows.Count > 0)
                changed = true;
            VanillaTuneUpMod.Settings.unreadModUpdateShows = shows;
            if (changed)
                VanillaTuneUpMod.Save();
        }

        public static void MarkRead(Entry entry)
        {
            if (!entry.Unread)
                return;
            entry.Unread = false;
            UnreadCount--;
            VanillaTuneUpMod.Settings.seenModUpdates[entry.Key] = (int)entry.InstalledTime;
        }

        public static string FormatTime(uint time) =>
            DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        public static void RequestNotes(Entry entry)
        {
            entry.State = NotesState.Loading;
            entry.Request = UnityWebRequest.Get(entry.ChangeLogUrl);
            entry.Request.timeout = 20;
            entry.Request.SendWebRequest();
        }

        /// <summary>Polled from the notes window; UnityWebRequest results must be read on the main thread.</summary>
        public static void PollNotes(Entry entry)
        {
            if (entry.State != NotesState.Loading || !entry.Request.isDone)
                return;
            try
            {
                if (entry.Request.result != UnityWebRequest.Result.Success)
                {
                    entry.State = NotesState.Failed;
                    return;
                }
                entry.Notes = ParseNotes(entry.Request.downloadHandler.text, entry.NotesAfter);
                entry.State = NotesState.Loaded;
            }
            catch
            {
                entry.State = NotesState.Failed;
            }
            finally
            {
                entry.Request.Dispose();
                entry.Request = null;
            }
        }

        public static void AbortRequest(Entry entry)
        {
            if (entry.Request == null)
                return;
            entry.Request.Abort();
            entry.Request.Dispose();
            entry.Request = null;
            entry.State = NotesState.NotFetched;
        }

        private static List<ChangeNote> ParseNotes(string html, uint after)
        {
            var notes = new List<ChangeNote>();
            foreach (Match match in NoteRegex.Matches(html))
            {
                if (!uint.TryParse(match.Groups[1].Value, out uint time) || time <= after)
                    continue;
                notes.Add(new ChangeNote { Time = time, Text = ToRichText(match.Groups[2].Value) });
            }
            return notes;
        }

        private static string ToRichText(string html)
        {
            // Placeholders survive tag stripping and decoding; real '<' from the decoded text must not form rich text tags.
            const string BoldOpen = "\u0001", BoldClose = "\u0002";
            string text = HeadingRegex.Replace(html, m => BoldOpen + m.Groups[1].Value + BoldClose + "\n");
            text = Regex.Replace(text, "<(b|strong)>", BoldOpen, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "</(b|strong)>", BoldClose, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<li>", "\n• ", RegexOptions.IgnoreCase);
            text = TagRegex.Replace(text, "");
            text = WebUtility.HtmlDecode(text).Replace("<", "<​");
            text = text.Replace(BoldOpen, "<b>").Replace(BoldClose, "</b>");
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                lines[i] = lines[i].Trim();
            return BlankLinesRegex.Replace(string.Join("\n", lines), "\n\n").Trim();
        }
    }

    /// <summary>
    /// The update list takes the place of the main menu's translation credits box (top left), which is also shown
    /// in English so the list is always there.
    /// </summary>
    [HarmonyPatchCategory("ModUpdateNotes")]
    [HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoTranslationInfoRect))]
    public static class Patch_MainMenuDrawer_DoTranslationInfoRect
    {
        private const float RowHeight = 40f;
        private static Vector2 scroll;

        public static bool Prefix(Rect outRect)
        {
            List<ModUpdateNotes.Entry> entries = ModUpdateNotes.Entries;
            if (entries.Count == 0)
                return true;

            Widgets.DrawWindowBackground(outRect);
            Rect rect = outRect.ContractedBy(8f);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 32f), "VTU_MU_Title".Translate(ModUpdateNotes.UnreadCount));
            Text.Font = GameFont.Small;

            Rect markRect = new Rect(rect.x, rect.yMax - 28f, rect.width, 28f);
            Rect listRect = new Rect(rect.x, rect.y + 36f, rect.width, markRect.y - rect.y - 40f);
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, entries.Count * RowHeight);
            Widgets.BeginScrollView(listRect, ref scroll, viewRect);
            for (int i = 0; i < entries.Count; i++)
            {
                ModUpdateNotes.Entry entry = entries[i];
                Rect row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                // Skip rows outside the visible part; the list can hold a few hundred mods.
                if (row.yMax < scroll.y || row.y > scroll.y + listRect.height)
                    continue;
                if (Mouse.IsOver(row))
                    Widgets.DrawHighlight(row);
                Rect inner = row.ContractedBy(4f, 1f);
                GUI.color = entry.Unread ? ColorLibrary.Gold : Color.white;
                Text.WordWrap = false;
                Widgets.Label(new Rect(inner.x, inner.y, inner.width, 22f), entry.Mod.Name.Truncate(inner.width));
                Text.WordWrap = true;
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(inner.x, inner.y + 20f, inner.width, 18f), ModUpdateNotes.FormatTime(entry.InstalledTime));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(row))
                {
                    Find.WindowStack.TryRemove(typeof(Dialog_ModUpdateNotes));
                    Find.WindowStack.Add(new Dialog_ModUpdateNotes(entry));
                    ModUpdateNotes.MarkRead(entry);
                    VanillaTuneUpMod.Save();
                }
            }
            Widgets.EndScrollView();

            if (ModUpdateNotes.UnreadCount > 0 && Widgets.ButtonText(markRect, "VTU_MU_MarkAllRead".Translate()))
            {
                foreach (ModUpdateNotes.Entry entry in entries)
                    ModUpdateNotes.MarkRead(entry);
                VanillaTuneUpMod.Save();
            }
            return false;
        }
    }

    public class Dialog_ModUpdateNotes : Window
    {
        private readonly ModUpdateNotes.Entry entry;
        private Vector2 scroll;
        private float notesHeight;

        public override Vector2 InitialSize => new Vector2(760f, 600f);

        public Dialog_ModUpdateNotes(ModUpdateNotes.Entry entry)
        {
            this.entry = entry;
            // The main menu hides itself while any Dialog-layer window is open; this one should sit beside it.
            layer = WindowLayer.GameUI;
            doCloseX = true;
            draggable = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            // A failed fetch (offline, timeout) is retried each time the window opens.
            if (entry.State == ModUpdateNotes.NotesState.NotFetched || entry.State == ModUpdateNotes.NotesState.Failed)
                ModUpdateNotes.RequestNotes(entry);
        }

        public override void PostClose()
        {
            base.PostClose();
            ModUpdateNotes.AbortRequest(entry);
        }

        public override void DoWindowContents(Rect inRect)
        {
            ModUpdateNotes.PollNotes(entry);

            Text.Font = GameFont.Medium;
            Text.WordWrap = false;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 32f), entry.Mod.Name.Truncate(inRect.width - 40f));
            Text.WordWrap = true;
            Text.Font = GameFont.Small;

            float buttonsY = inRect.y + 36f;
            if (Widgets.ButtonText(new Rect(inRect.x, buttonsY, 220f, 30f), "VTU_MU_OpenChangeLog".Translate()))
                SteamUtility.OpenUrl(entry.ChangeLogUrl);

            Rect outRect = new Rect(inRect.x, buttonsY + 38f, inRect.width, inRect.yMax - buttonsY - 38f);
            Widgets.DrawMenuSection(outRect);
            outRect = outRect.ContractedBy(8f);
            if (entry.State != ModUpdateNotes.NotesState.Loaded || entry.Notes.Count == 0)
            {
                GUI.color = Color.gray;
                string key = entry.State == ModUpdateNotes.NotesState.Loaded ? "VTU_MU_NoNotes"
                    : entry.State == ModUpdateNotes.NotesState.Failed ? "VTU_MU_Failed" : "VTU_MU_Loading";
                Widgets.Label(outRect, key.Translate());
                GUI.color = Color.white;
                return;
            }

            float width = outRect.width - 16f;
            Rect viewRect = new Rect(0f, 0f, width, Mathf.Max(notesHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0f;
            foreach (ModUpdateNotes.ChangeNote note in entry.Notes)
            {
                GUI.color = ColorLibrary.Gold;
                Widgets.Label(new Rect(0f, y, width, 24f), ModUpdateNotes.FormatTime(note.Time));
                GUI.color = Color.white;
                y += 26f;
                string text = note.Text.NullOrEmpty() ? "VTU_MU_EmptyNote".Translate().ToString() : note.Text;
                float height = Text.CalcHeight(text, width);
                Widgets.Label(new Rect(0f, y, width, height), text);
                y += height + 16f;
            }
            notesHeight = y;
            Widgets.EndScrollView();
        }
    }
}
