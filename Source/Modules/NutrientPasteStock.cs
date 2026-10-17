using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    /// <summary>
    /// Map-wide "dispense until you have X" target for nutrient paste, counted like a cooking bill (paste in
    /// storage plus paste being carried) and also counting unstored paste on the ground, so dispensing onto the
    /// floor for haulers does not overshoot. Every dispenser on a map carries the same value, so the target lives
    /// on the dispensers themselves and removing the mod leaves nothing unreadable in the save.
    /// </summary>
    public static class PasteStock
    {
        public static bool Enabled => Modules.IsActive("NutrientPasteStock");

        // The resource counter refreshes every 204 ticks, so paste just stored is not counted right away.
        private const int CooldownTicks = 250;

        private static readonly Dictionary<int, int> lastFinishedTick = new Dictionary<int, int>();

        private static JobDef jobDef;
        public static JobDef JobDef => jobDef ??= DefDatabase<JobDef>.GetNamed("VTU_DispensePasteToStock");

        public static IEnumerable<CompPasteStock> Comps(Map map)
        {
            foreach (Building_NutrientPasteDispenser dispenser in map.listerBuildings.AllBuildingsColonistOfClass<Building_NutrientPasteDispenser>())
            {
                CompPasteStock comp = dispenser.GetComp<CompPasteStock>();
                if (comp != null)
                    yield return comp;
            }
        }

        public static int Target(Map map, Thing except = null)
        {
            int target = 0;
            foreach (CompPasteStock comp in Comps(map))
            {
                if (comp.parent != except)
                    target = Mathf.Max(target, comp.target);
            }
            return target;
        }

        public static void SetTarget(Map map, int target)
        {
            foreach (CompPasteStock comp in Comps(map))
                comp.target = target;
        }

        /// <summary>
        /// The bill count (resource counter plus paste carried by colony pawns) plus unstored paste on the ground,
        /// skipping forbidden, rotting and fogged stacks like the resource counter does.
        /// </summary>
        public static int Count(Map map)
        {
            ThingDef paste = ThingDefOf.MealNutrientPaste;
            int count = map.resourceCounter.GetCount(paste);
            foreach (Thing thing in map.listerThings.ThingsOfDef(paste))
            {
                if (thing.GetSlotGroup() == null && !thing.IsForbidden(Faction.OfPlayer) && !thing.IsNotFresh() && !thing.Position.Fogged(map))
                    count += thing.stackCount;
            }
            foreach (Pawn pawn in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            {
                Thing carried = pawn.carryTracker?.CarriedThing;
                if (carried != null && carried.def == paste)
                    count += carried.stackCount;
            }
            return count;
        }

        public static int Needed(Map map) => Target(map) - Count(map);

        public static void MarkFinished(Map map)
        {
            if (map != null)
                lastFinishedTick[map.uniqueID] = Find.TickManager.TicksGame;
        }

        public static bool CoolingDown(Map map) =>
            lastFinishedTick.TryGetValue(map.uniqueID, out int tick) && Find.TickManager.TicksGame - tick < CooldownTicks;

        /// <summary>One worker per map, so two workers never dispense for the same shortfall.</summary>
        public static bool SomeoneElseDispensing(Pawn pawn)
        {
            foreach (Pawn other in pawn.Map.mapPawns.SpawnedPawnsInFaction(pawn.Faction))
            {
                if (other != pawn && other.CurJobDef == JobDef)
                    return true;
            }
            return false;
        }
    }

    public class CompPasteStock : ThingComp
    {
        public int target;

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref target, "vtuPasteStockTarget", 0);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            // A newly built dispenser joins the map's existing target.
            if (!respawningAfterLoad)
                target = PasteStock.Target(parent.Map, parent);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!PasteStock.Enabled || parent.Faction != Faction.OfPlayer)
                yield break;
            Map map = parent.Map;
            yield return new Command_Action
            {
                defaultLabel = target > 0 ? "VTU_PS_GizmoLabel".Translate(target) : "VTU_PS_GizmoOff".Translate(),
                defaultDesc = "VTU_PS_GizmoDesc".Translate(),
                icon = ThingDefOf.MealNutrientPaste.uiIcon,
                action = () => Find.WindowStack.Add(new Dialog_PasteStockTarget(map))
            };
        }

        public override string CompInspectStringExtra()
        {
            if (!PasteStock.Enabled || target <= 0 || !parent.Spawned)
                return null;
            return "VTU_PS_Inspect".Translate(PasteStock.Count(parent.Map), target);
        }
    }

    public class Dialog_PasteStockTarget : Window
    {
        private readonly Map map;
        private int value;
        private string buffer;

        public override Vector2 InitialSize => new Vector2(360f, 170f);

        public Dialog_PasteStockTarget(Map map)
        {
            this.map = map;
            value = PasteStock.Target(map);
            buffer = value.ToString();
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "VTU_PS_DialogTitle".Translate());
            Text.Font = GameFont.Small;
            int before = value;
            Widgets.IntEntry(new Rect(0f, 40f, inRect.width, 24f), ref value, ref buffer);
            if (value < 0)
            {
                value = 0;
                buffer = "0";
            }
            if (value != before)
                PasteStock.SetTarget(map, value);
            GUI.color = Color.gray;
            Widgets.Label(new Rect(0f, 72f, inRect.width, inRect.height - 72f), "VTU_PS_DialogNote".Translate(PasteStock.Count(map)));
            GUI.color = Color.white;
        }
    }

    public class WorkGiver_DispensePasteToStock : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.InteractionCell;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) =>
            pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building_NutrientPasteDispenser>();

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            Map map = pawn.Map;
            return !PasteStock.Enabled || PasteStock.Needed(map) <= 0 || PasteStock.CoolingDown(map)
                || PasteStock.SomeoneElseDispensing(pawn);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Building_NutrientPasteDispenser dispenser) || dispenser.GetComp<CompPasteStock>() == null)
                return false;
            if (t.Faction != pawn.Faction || t.IsForbidden(pawn) || t.IsBurning() || !dispenser.CanDispenseNow)
                return false;
            return pawn.carryTracker.CarriedThing == null && pawn.CanReach(t, PathEndMode.InteractionCell, pawn.NormalMaxDanger());
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            int count = PasteStock.Needed(pawn.Map);
            if (count <= 0)
                return null;
            Job job = JobMaker.MakeJob(PasteStock.JobDef, t);
            job.count = count;
            return job;
        }
    }

    /// <summary>
    /// Dispenses paste one meal at a time into a carried stack and drops it by the dispenser for haulers,
    /// like a bill set to drop on the floor; a full stack is dropped and dispensing goes on.
    /// </summary>
    public class JobDriver_DispensePasteToStock : JobDriver
    {
        private const TargetIndex DispenserInd = TargetIndex.A;

        private int dispensed;

        private Building_NutrientPasteDispenser Dispenser => job.GetTarget(DispenserInd).Thing as Building_NutrientPasteDispenser;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref dispensed, "dispensed", 0);
        }

        // The dispenser is not reserved, so colonists can still eat from it meanwhile.
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        private bool CanDispenseMore()
        {
            Building_NutrientPasteDispenser dispenser = Dispenser;
            return dispensed < job.count && dispenser != null && dispenser.Spawned && dispenser.CanDispenseNow;
        }

        private void DropCarried()
        {
            if (pawn.carryTracker.CarriedThing != null)
                pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Map map = pawn.Map;
            AddFinishAction(_ => PasteStock.MarkFinished(map));

            yield return Toils_Goto.GotoThing(DispenserInd, PathEndMode.InteractionCell).FailOnDespawnedNullOrForbidden(DispenserInd);

            Toil dispense = ToilMaker.MakeToil("DispensePasteToStock");
            dispense.initAction = () =>
            {
                Thing paste = CanDispenseMore() ? Dispenser.TryDispenseFood() : null;
                if (paste == null)
                {
                    DropCarried();
                    EndJobWith(dispensed > 0 ? JobCondition.Succeeded : JobCondition.Incompletable);
                    return;
                }
                dispensed++;
                if (pawn.carryTracker.CarriedThing == null)
                    pawn.carryTracker.TryStartCarry(paste);
                else if (!pawn.carryTracker.innerContainer.TryAdd(paste))
                    GenPlace.TryPlaceThing(paste, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                if (pawn.carryTracker.CarriedThing != null && pawn.carryTracker.Full)
                    DropCarried();
            };
            dispense.FailOnCannotTouch(DispenserInd, PathEndMode.InteractionCell);
            dispense.defaultCompleteMode = ToilCompleteMode.Delay;
            dispense.defaultDuration = Building_NutrientPasteDispenser.CollectDuration;
            yield return dispense;

            Toil repeat = ToilMaker.MakeToil("RepeatDispensePaste");
            repeat.initAction = () =>
            {
                if (CanDispenseMore())
                    JumpToToil(dispense);
                else
                    DropCarried();
            };
            yield return repeat;
        }
    }
}
