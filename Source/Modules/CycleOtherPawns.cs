using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace VanillaTuneUp
{
    /// <summary>
    /// When the selection is a pawn that is not on the colonist bar, the next/previous colonist keys cycle through
    /// pawns of the same kind on the current map instead: enemies (downed included), prisoners, visitors, colony
    /// animals, colony mechs or wild animals. Slaves get their own cycle only while HideSlavesFromBar takes them off the bar;
    /// otherwise they stay in the vanilla colonist cycle. Pawns under fog are skipped so the keys never reveal them.
    /// Anything else (colonists, nothing selected, world view) runs the vanilla method unchanged.
    /// In both cycles a quiet tick plays when the keys come back to the pawn the round started from: the pawn that
    /// was selected when the keys were first pressed, or after the selection was changed some other way.
    /// </summary>
    public static class CycleOtherPawns
    {
        public enum Kind
        {
            None,
            Enemy,
            Prisoner,
            Slave,
            Visitor,
            ColonyAnimal,
            ColonyMech,
            WildAnimal
        }

        private static readonly List<Pawn> tmpPawns = new List<Pawn>();

        // Thing IDs only, so no pawn is kept alive after its game is gone.
        private static int lastJumpedId = -1;
        private static int roundStartId = -1;

        /// <summary>
        /// Tracks the round and plays the tick when <paramref name="target"/> is where it started. A selection
        /// that is not the pawn the keys last jumped to starts a new round from that pawn (or, with nothing
        /// selected, from the first jump target).
        /// </summary>
        public static void NotifyJump(Pawn before, Pawn target)
        {
            if (before == null || before.thingIDNumber != lastJumpedId)
                roundStartId = before != null ? before.thingIDNumber : target.thingIDNumber;
            lastJumpedId = target.thingIDNumber;
            if (before != null && target.thingIDNumber == roundStartId)
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        }

        public static Pawn FirstSelectedPawnOnMap()
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return null;
            foreach (object obj in Find.Selector.SelectedObjectsListForReading)
            {
                if (obj is Pawn p && p.Spawned && p.Map == map)
                    return p;
            }
            return null;
        }

        public static Kind KindOf(Pawn p)
        {
            if (p.IsPrisonerOfColony)
                return Kind.Prisoner;
            if (p.IsSlaveOfColony)
                return Modules.IsActive("HideSlavesFromBar") ? Kind.Slave : Kind.None;
            if (p.Faction == Faction.OfPlayer)
            {
                if (p.RaceProps.Animal)
                    return Kind.ColonyAnimal;
                // Colony mechs are not on the colonist bar either (it lists free colonists only).
                return p.RaceProps.IsMechanoid ? Kind.ColonyMech : Kind.None;
            }
            if (p.HostileTo(Faction.OfPlayer))
                return Kind.Enemy;
            if (p.RaceProps.Animal && p.Faction == null)
                return Kind.WildAnimal;
            return Kind.Visitor;
        }

        /// <summary>
        /// Same default order as the mechs tab (PawnTable_Mechs.LabelSortFunction): overseer, control group, kind,
        /// name. A column sort picked in the tab is not followed. Thing IDs break the remaining ties.
        /// </summary>
        private static void SortLikeMechTab(List<Pawn> pawns)
        {
            pawns.SortStable((a, b) =>
            {
                int c = OverseerId(a).CompareTo(OverseerId(b));
                if (c == 0)
                    c = ControlGroupIndex(a).CompareTo(ControlGroupIndex(b));
                if (c == 0)
                    c = string.CompareOrdinal(a.KindLabel, b.KindLabel);
                if (c == 0)
                    c = string.CompareOrdinal(a.Label, b.Label);
                return c != 0 ? c : a.thingIDNumber.CompareTo(b.thingIDNumber);
            });
        }

        private static int OverseerId(Pawn p) => p.GetOverseer()?.thingIDNumber ?? int.MaxValue;

        private static int ControlGroupIndex(Pawn p) => p.GetMechControlGroup()?.Index ?? int.MaxValue;

        /// <summary>
        /// Animals all share one faction (or none), so they are grouped by kind instead, then by name (wild animals
        /// are named after their kind, so thing IDs decide among them).
        /// </summary>
        private static void SortByAnimalKind(List<Pawn> pawns)
        {
            pawns.SortStable((a, b) =>
            {
                int c = string.CompareOrdinal(a.KindLabel, b.KindLabel);
                if (c == 0)
                    c = string.CompareOrdinal(a.Label, b.Label);
                return c != 0 ? c : a.thingIDNumber.CompareTo(b.thingIDNumber);
            });
        }

        /// <summary>
        /// Groups pawns of one faction together (factions in creation order, no faction last), then by thing ID.
        /// Spawn order shifts as pawns come and go; thing IDs keep the cycle order stable.
        /// </summary>
        private static void SortByFaction(List<Pawn> pawns)
        {
            pawns.SortStable((a, b) =>
            {
                int c = FactionKey(a).CompareTo(FactionKey(b));
                return c != 0 ? c : a.thingIDNumber.CompareTo(b.thingIDNumber);
            });
        }

        private static int FactionKey(Pawn p) => p.Faction?.loadID ?? int.MaxValue;

        /// <summary>Returns true when the key was handled here and vanilla should be skipped.</summary>
        public static bool TryCycle(int step)
        {
            if (WorldRendererUtility.WorldSelected)
                return false;
            Map map = Find.CurrentMap;
            if (map == null)
                return false;
            Pawn current = null;
            Kind kind = Kind.None;
            foreach (object obj in Find.Selector.SelectedObjectsListForReading)
            {
                if (obj is Pawn p && p.Spawned && p.Map == map)
                {
                    kind = KindOf(p);
                    if (kind != Kind.None)
                    {
                        current = p;
                        break;
                    }
                }
            }
            if (current == null)
                return false;

            tmpPawns.Clear();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (p.def.selectable && !p.Position.Fogged(map) && KindOf(p) == kind)
                    tmpPawns.Add(p);
            }
            if (kind == Kind.ColonyMech)
                SortLikeMechTab(tmpPawns);
            else if (kind == Kind.ColonyAnimal || kind == Kind.WildAnimal)
                SortByAnimalKind(tmpPawns);
            else
                SortByFaction(tmpPawns);
            int index = tmpPawns.IndexOf(current);
            Pawn target = index < 0
                ? (tmpPawns.Count > 0 ? tmpPawns[0] : null)
                : tmpPawns[GenMath.PositiveMod(index + step, tmpPawns.Count)];
            tmpPawns.Clear();
            if (target != null)
            {
                NotifyJump(current, target);
                CameraJumper.TryJumpAndSelect(target);
            }
            return true;
        }
    }

    /// <summary>
    /// Shared prefix/postfix body: our cycle replaces vanilla for other pawns; for the vanilla colonist cycle the
    /// postfix compares the selection before and after to drive the round tick.
    /// </summary>
    public static class CycleOtherPawns_Patch
    {
        public static bool Prefix(int step, out Pawn before)
        {
            before = null;
            if (CycleOtherPawns.TryCycle(step))
                return false;
            if (!WorldRendererUtility.WorldSelected)
                before = CycleOtherPawns.FirstSelectedPawnOnMap();
            return true;
        }

        public static void Postfix(bool ranOriginal, Pawn before)
        {
            if (!ranOriginal || WorldRendererUtility.WorldSelected)
                return;
            // The same pawn before and after means a lone colonist jumped to itself, which is a full round too
            // (vanilla only leaves the selection alone when there is no colonist at all).
            if (Find.Selector.SingleSelectedThing is Pawn after)
                CycleOtherPawns.NotifyJump(before, after);
        }
    }

    [HarmonyPatchCategory("CycleOtherPawns")]
    [HarmonyPatch(typeof(ThingSelectionUtility), nameof(ThingSelectionUtility.SelectNextColonist))]
    public static class CycleOtherPawns_SelectNextColonist
    {
        public static bool Prefix(out Pawn __state) => CycleOtherPawns_Patch.Prefix(1, out __state);

        public static void Postfix(bool __runOriginal, Pawn __state) =>
            CycleOtherPawns_Patch.Postfix(__runOriginal, __state);
    }

    [HarmonyPatchCategory("CycleOtherPawns")]
    [HarmonyPatch(typeof(ThingSelectionUtility), nameof(ThingSelectionUtility.SelectPreviousColonist))]
    public static class CycleOtherPawns_SelectPreviousColonist
    {
        public static bool Prefix(out Pawn __state) => CycleOtherPawns_Patch.Prefix(-1, out __state);

        public static void Postfix(bool __runOriginal, Pawn __state) =>
            CycleOtherPawns_Patch.Postfix(__runOriginal, __state);
    }
}
