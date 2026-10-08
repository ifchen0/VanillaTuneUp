using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace VanillaTuneUp
{
    /// <summary>
    /// Bill-style editor for a gene assembler's xenogerm queue: repeat mode, count, order and removal.
    /// </summary>
    public class Dialog_XenogermQueue : Window
    {
        private const float RowHeight = 40f;
        private const float Gap = 6f;

        private readonly CompXenogermQueue comp;
        private Vector2 scrollPosition;

        public override Vector2 InitialSize => new Vector2(720f, 520f);

        public Dialog_XenogermQueue(CompXenogermQueue comp)
        {
            this.comp = comp;
            doCloseX = true;
            doCloseButton = true;
            draggable = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (comp.parent.DestroyedOrNull() || !comp.parent.Spawned)
            {
                Close();
                return;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 160f, 35f), "VTU_XQ_WindowTitle".Translate());
            Text.Font = GameFont.Small;

            Rect pauseRect = new Rect(inRect.xMax - 150f - 30f, inRect.y, 150f, 30f);
            if (comp.paused)
            {
                if (Widgets.ButtonText(pauseRect, "VTU_XQ_Resume".Translate()))
                    comp.Resume();
            }
            else if (Widgets.ButtonText(pauseRect, "VTU_XQ_Pause".Translate()))
            {
                comp.paused = true;
            }

            float y = inRect.y + 40f;
            if (comp.paused)
            {
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "VTU_XQ_PausedHint".Translate());
                GUI.color = Color.white;
                y += 26f;
            }

            Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - CloseButSize.y - 10f);
            if (comp.entries.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Color.gray;
                Widgets.Label(outRect, "VTU_XQ_Empty".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, comp.entries.Count * (RowHeight + Gap));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            XenogermQueueEntry toRemove = null;
            int moveFrom = -1, moveTo = -1;
            for (int i = 0; i < comp.entries.Count; i++)
            {
                Rect row = new Rect(0f, i * (RowHeight + Gap), viewRect.width, RowHeight);
                DoRow(row, i, ref toRemove, ref moveFrom, ref moveTo);
            }
            Widgets.EndScrollView();

            if (toRemove != null)
                comp.Remove(toRemove);
            if (moveFrom >= 0 && moveTo >= 0 && moveTo < comp.entries.Count)
            {
                XenogermQueueEntry entry = comp.entries[moveFrom];
                comp.entries.RemoveAt(moveFrom);
                comp.entries.Insert(moveTo, entry);
            }
        }

        private void DoRow(Rect row, int index, ref XenogermQueueEntry toRemove, ref int moveFrom, ref int moveTo)
        {
            XenogermQueueEntry entry = comp.entries[index];
            bool running = entry == comp.current && comp.Assembler.Working;
            bool available = comp.PacksAvailable(entry);

            Widgets.DrawMenuSection(row);
            if (running)
                Widgets.DrawHighlightSelected(row);
            else if (Mouse.IsOver(row))
                Widgets.DrawHighlight(row);

            Rect inner = row.ContractedBy(4f);
            float x = inner.x;
            float midY = inner.y + (inner.height - 24f) / 2f;

            // Reorder.
            if (index > 0 && Widgets.ButtonImage(new Rect(x, inner.y, 16f, 16f), TexButton.ReorderUp))
            {
                moveFrom = index;
                moveTo = index - 1;
            }
            if (index < comp.entries.Count - 1 && Widgets.ButtonImage(new Rect(x, inner.y + 16f, 16f, 16f), TexButton.ReorderDown))
            {
                moveFrom = index;
                moveTo = index + 1;
            }
            x += 22f;

            // Icon and name with gene list tooltip.
            Rect iconRect = new Rect(x, inner.y, inner.height, inner.height);
            GUI.color = XenotypeDef.IconColor;
            GUI.DrawTexture(iconRect, (entry.iconDef ?? XenotypeIconDefOf.Basic).Icon);
            GUI.color = Color.white;
            x += inner.height + 6f;

            Rect nameRect = new Rect(x, inner.y, 190f, inner.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            string status;
            if (!available)
                status = "VTU_XQ_StatusMissing".Translate().Colorize(ColorLibrary.RedReadable);
            else if (running && comp.CurrentHeld)
                status = "VTU_XQ_StatusHeld".Translate(comp.Assembler.ProgressPercent.ToStringPercent()).Colorize(ColorLibrary.Yellow);
            else if (running)
                status = "VTU_XQ_StatusRunning".Translate(comp.Assembler.ProgressPercent.ToStringPercent()).Colorize(ColorLibrary.Green);
            else if (comp.IsSatisfied(entry))
                status = "VTU_XQ_StatusSatisfied".Translate().Colorize(Color.gray);
            else if (entry.savedWork > 0f)
                status = "VTU_XQ_StatusSaved".Translate().Colorize(ColorLibrary.Yellow);
            else
                status = null;
            Widgets.Label(nameRect, status == null ? entry.xenotypeName.CapitalizeFirst() : entry.xenotypeName.CapitalizeFirst() + "\n<size=11>" + status + "</size>");
            Text.Anchor = TextAnchor.UpperLeft;
            if (Mouse.IsOver(nameRect))
                TooltipHandler.TipRegion(nameRect, GeneTooltip(entry));
            x += nameRect.width + 6f;

            // Repeat mode.
            Rect modeRect = new Rect(x, midY, 150f, 24f);
            if (Widgets.ButtonText(modeRect, ModeLabel(entry.mode)))
            {
                Find.WindowStack.Add(new FloatMenu(new[] { RepeatMode.Count, RepeatMode.UntilHave, RepeatMode.Forever }
                    .Select(m => new FloatMenuOption(ModeLabel(m), () => entry.mode = m)).ToList()));
            }
            x += modeRect.width + 6f;

            // Count with -/+ (shift/ctrl multiply like bills).
            if (entry.mode != RepeatMode.Forever)
            {
                Rect minus = new Rect(x, midY, 24f, 24f);
                Rect countRect = new Rect(x + 26f, midY, 90f, 24f);
                Rect plus = new Rect(countRect.xMax + 2f, midY, 24f, 24f);
                int step = GenUI.CurrentAdjustmentMultiplier();
                if (Widgets.ButtonText(minus, "-"))
                    entry.count = Mathf.Max(1, entry.count - step);
                if (Widgets.ButtonText(plus, "+"))
                    entry.count += step;
                Text.Anchor = TextAnchor.MiddleCenter;
                string countLabel = entry.mode == RepeatMode.UntilHave
                    ? comp.CountOnMap(entry) + " / " + entry.count
                    : "VTU_XQ_TimesLeft".Translate(entry.count).ToString();
                Widgets.Label(countRect, countLabel);
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(x, midY, 142f, 24f), "∞");
                Text.Anchor = TextAnchor.UpperLeft;
            }

            // Delete.
            Rect deleteRect = new Rect(inner.xMax - 24f, midY, 24f, 24f);

            // Skip the running entry, keeping its progress.
            if (running)
            {
                Rect skipRect = new Rect(deleteRect.x - 76f, midY, 70f, 24f);
                bool canSkip = comp.CanSkip;
                TooltipHandler.TipRegion(skipRect, canSkip ? "VTU_XQ_SkipDesc".Translate() : "VTU_XQ_SkipNoNext".Translate());
                if (Widgets.ButtonText(skipRect, "VTU_XQ_Skip".Translate(), active: canSkip) && canSkip)
                    comp.Skip();
            }
            if (Widgets.ButtonImage(deleteRect, TexButton.Delete))
                toRemove = entry;
            TooltipHandler.TipRegion(deleteRect, running ? "VTU_XQ_DeleteRunningDesc".Translate() : "Delete".Translate());
        }

        private static string ModeLabel(RepeatMode mode)
        {
            switch (mode)
            {
                case RepeatMode.UntilHave:
                    return "VTU_XQ_ModeUntilHave".Translate();
                case RepeatMode.Forever:
                    return "VTU_XQ_ModeForever".Translate();
                default:
                    return "VTU_XQ_ModeCount".Translate();
            }
        }

        private static string GeneTooltip(XenogermQueueEntry entry)
        {
            string genes = entry.genepacks
                .Where(p => p != null && p.GeneSet != null)
                .SelectMany(p => p.GeneSet.GenesListForReading)
                .Select(g => " - " + g.LabelCap.ToString())
                .ToList()
                .ToLineList();
            string text = entry.xenotypeName.CapitalizeFirst().AsTipTitle() + "\n" + genes;
            if (entry.architesRequired > 0)
                text += "\n\n" + "ArchitesRequired".Translate() + ": " + entry.architesRequired;
            return text;
        }
    }
}
