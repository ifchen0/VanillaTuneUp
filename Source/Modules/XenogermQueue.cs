using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace VanillaTuneUp
{
    [StaticConstructorOnStartup]
    public static class XenogermQueueTextures
    {
        public static readonly Texture2D QueueIcon = ContentFinder<Texture2D>.Get("UI/Gizmos/RecombineGenes");
        public static readonly Texture2D QueueBadge = ContentFinder<Texture2D>.Get("UI/VanillaTuneUp/XenogermQueueBadge");
    }

    public enum RepeatMode
    {
        Count,
        UntilHave,
        Forever,
    }

    /// <summary>
    /// One queued xenogerm recipe: the genepacks to combine plus how often to repeat it, like a workbench bill.
    /// </summary>
    public class XenogermQueueEntry : IExposable
    {
        public List<Genepack> genepacks = new List<Genepack>();
        public string xenotypeName;
        public XenotypeIconDef iconDef;
        public int architesRequired;
        public RepeatMode mode = RepeatMode.Count;
        public int count = 1;

        /// <summary>Work already done on this entry before it was skipped; restored when it starts again.</summary>
        public float savedWork;

        [Unsaved(false)]
        public bool warnedMissing;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref genepacks, "genepacks", LookMode.Reference);
            Scribe_Values.Look(ref xenotypeName, "xenotypeName");
            Scribe_Defs.Look(ref iconDef, "iconDef");
            Scribe_Values.Look(ref architesRequired, "architesRequired", 0);
            Scribe_Values.Look(ref mode, "mode", RepeatMode.Count);
            Scribe_Values.Look(ref count, "count", 1);
            Scribe_Values.Look(ref savedWork, "savedWork", 0f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (genepacks == null)
                    genepacks = new List<Genepack>();
                if (iconDef == null)
                    iconDef = XenotypeIconDefOf.Basic;
            }
        }
    }

    /// <summary>
    /// Holds the per-assembler xenogerm queue and starts the next entry whenever the assembler goes idle.
    /// </summary>
    /// <remarks>
    /// The comp is added to the assembler even while the module is switched off, so a saved queue survives;
    /// it then does nothing until the module is switched back on.
    /// </remarks>
    public class CompXenogermQueue : ThingComp
    {
        private static bool Enabled => Modules.IsActive("XenogermQueue");

        private const int CheckInterval = 250;

        public List<XenogermQueueEntry> entries = new List<XenogermQueueEntry>();
        public bool paused;

        /// <summary>The entry the assembler is currently working on, or null for a manual (non-queued) job.</summary>
        public XenogermQueueEntry current;

        private int currentIndex = -1;

        public Building_GeneAssembler Assembler => (Building_GeneAssembler)parent;

        public override void CompTick()
        {
            if (Enabled && parent.IsHashIntervalTick(CheckInterval))
                TryStartNext();
        }

        public void Add(XenogermQueueEntry entry)
        {
            entries.Add(entry);
            paused = false;
            TryStartNext();
        }

        public void Remove(XenogermQueueEntry entry)
        {
            entries.Remove(entry);
            if (current == entry)
                current = null;
        }

        public void Resume()
        {
            paused = false;
            foreach (XenogermQueueEntry entry in entries)
                entry.warnedMissing = false;
            TryStartNext();
        }

        public int CountOnMap(XenogermQueueEntry entry)
        {
            Map map = parent.MapHeld;
            if (map == null)
                return 0;
            int num = 0;
            foreach (Thing thing in map.listerThings.ThingsOfDef(ThingDefOf.Xenogerm))
            {
                if (thing is Xenogerm xenogerm && xenogerm.xenotypeName == entry.xenotypeName)
                    num++;
            }
            return num;
        }

        public bool IsSatisfied(XenogermQueueEntry entry)
        {
            return entry.mode == RepeatMode.UntilHave && CountOnMap(entry) >= entry.count;
        }

        public bool PacksAvailable(XenogermQueueEntry entry)
        {
            if (entry.genepacks.NullOrEmpty())
                return false;
            foreach (Genepack pack in entry.genepacks)
            {
                if (pack == null || pack.Destroyed || Assembler.GetGeneBankHoldingPack(pack) == null)
                    return false;
            }
            return true;
        }

        /// <summary>True while the current queued "until you have X" entry has reached its target.</summary>
        public bool CurrentHeld => current != null && Assembler.Working && IsSatisfied(current);

        public bool CanSkip => current != null && Assembler.Working && entries.Any(e => e != current && IsRunnable(e));

        public void TryStartNext()
        {
            if (paused || !parent.Spawned)
                return;
            // Progress is kept on the entry, so a held entry can always give way to the next runnable one.
            if (CurrentHeld && CanSkip)
            {
                Skip();
                return;
            }
            if (Assembler.Working)
                return;
            foreach (XenogermQueueEntry entry in entries)
            {
                if (entry.mode == RepeatMode.Count && entry.count <= 0)
                    continue;
                if (IsSatisfied(entry))
                    continue;
                if (!PacksAvailable(entry))
                {
                    if (!entry.warnedMissing)
                    {
                        entry.warnedMissing = true;
                        Messages.Message("VTU_XQ_MissingGenepacks".Translate(entry.xenotypeName.CapitalizeFirst()), parent, MessageTypeDefOf.NegativeEvent, historical: false);
                    }
                    continue;
                }
                entry.warnedMissing = false;
                StartEntry(entry);
                return;
            }
        }

        /// <summary>
        /// Set the current entry aside (keeping its progress on the entry) and start the next runnable entry.
        /// Inserted archite capsules are dropped by vanilla Reset and hauled back when the entry resumes.
        /// </summary>
        public bool Skip()
        {
            XenogermQueueEntry skipped = current;
            XenogermQueueEntry next = entries.FirstOrDefault(e => e != skipped && IsRunnable(e));
            if (skipped == null || next == null || !Assembler.Working)
                return false;
            skipped.savedWork = XenogermQueuePatches.workDone(Assembler);
            current = null;
            XenogermQueuePatches.internalReset++;
            try
            {
                XenogermQueuePatches.resetMethod.Invoke(Assembler, null);
            }
            finally
            {
                XenogermQueuePatches.internalReset--;
            }
            StartEntry(next);
            return true;
        }

        private void StartEntry(XenogermQueueEntry entry)
        {
            XenogermQueuePatches.startingFromQueue = true;
            try
            {
                Assembler.Start(new List<Genepack>(entry.genepacks), entry.architesRequired, entry.xenotypeName, entry.iconDef);
            }
            finally
            {
                XenogermQueuePatches.startingFromQueue = false;
            }
            if (entry.savedWork > 0f)
            {
                XenogermQueuePatches.workDone(Assembler) = entry.savedWork;
                entry.savedWork = 0f;
            }
            current = entry;
            SoundDefOf.StartRecombining.PlayOneShot(SoundInfo.InMap(parent));
        }

        private bool IsRunnable(XenogermQueueEntry entry)
        {
            return (entry.mode != RepeatMode.Count || entry.count > 0) && !IsSatisfied(entry) && PacksAvailable(entry);
        }

        public void Notify_Finished()
        {
            XenogermQueueEntry entry = current;
            current = null;
            if (entry != null && entries.Contains(entry) && entry.mode == RepeatMode.Count)
            {
                entry.count--;
                if (entry.count <= 0)
                    entries.Remove(entry);
            }
            TryStartNext();
        }

        public void Notify_Cancelled()
        {
            // Cancelling a manual (non-queued) job is not about the queue, so leave it running.
            bool wasQueued = current != null;
            current = null;
            if (wasQueued && entries.Count > 0 && !paused)
            {
                paused = true;
                Messages.Message("VTU_XQ_QueuePaused".Translate(), parent, MessageTypeDefOf.CautionInput, historical: false);
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!Enabled)
                yield break;
            yield return new Command_XenogermQueue
            {
                defaultLabel = "VTU_XQ_QueueGizmo".Translate(),
                defaultDesc = "VTU_XQ_QueueGizmoDesc".Translate(),
                icon = XenogermQueueTextures.QueueIcon,
                action = () => Find.WindowStack.Add(new Dialog_XenogermQueue(this)),
            };
        }

        public override string CompInspectStringExtra()
        {
            if (!Enabled || entries.Count == 0)
                return null;
            string text = "VTU_XQ_InspectQueue".Translate(entries.Count);
            if (paused)
                text += " (" + "VTU_XQ_Paused".Translate() + ")";
            return text;
        }

        public override void PostExposeData()
        {
            Scribe_Collections.Look(ref entries, "xqEntries", LookMode.Deep);
            Scribe_Values.Look(ref paused, "xqPaused", false);
            if (Scribe.mode == LoadSaveMode.Saving)
                currentIndex = current == null ? -1 : entries.IndexOf(current);
            Scribe_Values.Look(ref currentIndex, "xqCurrentIndex", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (entries == null)
                    entries = new List<XenogermQueueEntry>();
                current = currentIndex >= 0 && currentIndex < entries.Count ? entries[currentIndex] : null;
            }
        }
    }

    /// <summary>
    /// The vanilla recombine icon with a list badge in the bottom-right corner, so it is not mistaken for "Recombine".
    /// </summary>
    public class Command_XenogermQueue : Command_Action
    {
        private const float BadgeSize = 0.42f;

        public override void DrawIcon(Rect rect, Material buttonMat, GizmoRenderParms parms)
        {
            base.DrawIcon(rect, buttonMat, parms);
            float size = rect.width * BadgeSize;
            Rect badge = new Rect(rect.xMax - size - 3f, rect.yMax - size - 3f, size, size);
            if (parms.lowLight)
                GUI.color = Color.white.ToTransparent(0.6f);
            GUI.DrawTexture(badge, XenogermQueueTextures.QueueBadge);
            GUI.color = Color.white;
        }
    }

    public static class XenogermQueuePatches
    {
        /// <summary>Set while the queue itself calls Start, so the Start prefix keeps the current entry.</summary>
        public static bool startingFromQueue;

        /// <summary>Nonzero while inside Start or Finish, whose internal Reset calls are not cancellations.</summary>
        public static int internalReset;

        public static readonly MethodInfo resetMethod = AccessTools.Method(typeof(Building_GeneAssembler), "Reset");

        public static readonly AccessTools.FieldRef<Building_GeneAssembler, float> workDone =
            AccessTools.FieldRefAccess<Building_GeneAssembler, float>("workDone");
    }

    /// <summary>
    /// Hold work (keeping progress) while the current "until you have X" entry is satisfied, like a bill that
    /// stops being done once its target count is reached. Pawns working it fail out through the job's FailOn.
    /// </summary>
    [HarmonyPatchCategory("XenogermQueue")]
    [HarmonyPatch(typeof(Building_GeneAssembler), nameof(Building_GeneAssembler.CanBeWorkedOnNow), MethodType.Getter)]
    public static class Patch_CanBeWorkedOnNow
    {
        public static void Postfix(Building_GeneAssembler __instance, ref AcceptanceReport __result)
        {
            if (!__result.Accepted)
                return;
            CompXenogermQueue comp = __instance.GetComp<CompXenogermQueue>();
            if (comp != null && comp.CurrentHeld)
                __result = "VTU_XQ_HeldTargetReached".Translate();
        }
    }

    [HarmonyPatchCategory("XenogermQueue")]
    [HarmonyPatch(typeof(Building_GeneAssembler), nameof(Building_GeneAssembler.Start))]
    public static class Patch_Start
    {
        public static void Prefix(Building_GeneAssembler __instance)
        {
            XenogermQueuePatches.internalReset++;
            if (!XenogermQueuePatches.startingFromQueue)
            {
                // A manual recombine replacing a queued entry keeps that entry's progress, same as skipping it.
                CompXenogermQueue comp = __instance.GetComp<CompXenogermQueue>();
                if (comp?.current != null && __instance.Working && comp.entries.Contains(comp.current))
                    comp.current.savedWork = XenogermQueuePatches.workDone(__instance);
                if (comp != null)
                    comp.current = null;
            }
        }

        public static void Finalizer()
        {
            XenogermQueuePatches.internalReset--;
        }
    }

    [HarmonyPatchCategory("XenogermQueue")]
    [HarmonyPatch(typeof(Building_GeneAssembler), nameof(Building_GeneAssembler.Finish))]
    public static class Patch_Finish
    {
        public static void Prefix()
        {
            XenogermQueuePatches.internalReset++;
        }

        public static void Postfix(Building_GeneAssembler __instance)
        {
            __instance.GetComp<CompXenogermQueue>()?.Notify_Finished();
        }

        public static void Finalizer()
        {
            XenogermQueuePatches.internalReset--;
        }
    }

    /// <summary>
    /// Reset outside Start/Finish means the job was cancelled (cancel gizmo or a genepack went missing);
    /// pause the queue so it does not immediately restart what the player just stopped.
    /// </summary>
    [HarmonyPatchCategory("XenogermQueue")]
    [HarmonyPatch(typeof(Building_GeneAssembler), "Reset")]
    public static class Patch_Reset
    {
        public static void Prefix(Building_GeneAssembler __instance)
        {
            if (XenogermQueuePatches.internalReset > 0 || !__instance.Working)
                return;
            __instance.GetComp<CompXenogermQueue>()?.Notify_Cancelled();
        }
    }

    [HarmonyPatchCategory("XenogermQueue")]
    [HarmonyPatch(typeof(Dialog_CreateXenogerm), "DoBottomButtons")]
    public static class Patch_DoBottomButtons
    {
        private static readonly AccessTools.FieldRef<Dialog_CreateXenogerm, Building_GeneAssembler> geneAssembler =
            AccessTools.FieldRefAccess<Dialog_CreateXenogerm, Building_GeneAssembler>("geneAssembler");
        private static readonly AccessTools.FieldRef<Dialog_CreateXenogerm, List<Genepack>> selectedGenepacks =
            AccessTools.FieldRefAccess<Dialog_CreateXenogerm, List<Genepack>>("selectedGenepacks");
        private static readonly AccessTools.FieldRef<GeneCreationDialogBase, int> arc =
            AccessTools.FieldRefAccess<GeneCreationDialogBase, int>("arc");
        private static readonly AccessTools.FieldRef<GeneCreationDialogBase, string> xenotypeName =
            AccessTools.FieldRefAccess<GeneCreationDialogBase, string>("xenotypeName");
        private static readonly AccessTools.FieldRef<GeneCreationDialogBase, XenotypeIconDef> iconDef =
            AccessTools.FieldRefAccess<GeneCreationDialogBase, XenotypeIconDef>("iconDef");
        private static readonly MethodInfo canAccept = AccessTools.Method(typeof(Dialog_CreateXenogerm), "CanAccept");

        private static readonly Vector2 ButSize = new Vector2(150f, 38f);

        private const float ButGap = 6f;

        /// <summary>
        /// Narrow the button row so vanilla draws "Start recombining" and the duration label one button further
        /// left, leaving the far right free for "Add to queue" without overlapping the label.
        /// </summary>
        public static void Prefix(Dialog_CreateXenogerm __instance, ref Rect rect, out Rect __state)
        {
            __state = rect;
            if (geneAssembler(__instance)?.GetComp<CompXenogermQueue>() != null)
                rect.width -= ButSize.x + ButGap;
        }

        public static void Postfix(Dialog_CreateXenogerm __instance, Rect __state)
        {
            Building_GeneAssembler assembler = geneAssembler(__instance);
            CompXenogermQueue comp = assembler?.GetComp<CompXenogermQueue>();
            if (comp == null)
                return;
            Rect butRect = new Rect(__state.xMax - ButSize.x, __state.y, ButSize.x, ButSize.y);
            TooltipHandler.TipRegion(butRect, "VTU_XQ_AddToQueueDesc".Translate());
            if (!Widgets.ButtonText(butRect, "VTU_XQ_AddToQueue".Translate()) || !(bool)canAccept.Invoke(__instance, null))
                return;
            comp.Add(new XenogermQueueEntry
            {
                genepacks = new List<Genepack>(selectedGenepacks(__instance)),
                xenotypeName = xenotypeName(__instance)?.Trim(),
                iconDef = iconDef(__instance) ?? XenotypeIconDefOf.Basic,
                architesRequired = arc(__instance),
            });
            __instance.Close(doCloseSound: false);
            Find.WindowStack.Add(new Dialog_XenogermQueue(comp));
        }
    }
}
