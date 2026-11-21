using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace VanillaTuneUp
{
    [DefOf]
    public static class CommsChatDefOf
    {
        public static JobDef VTU_CommsChat;

        static CommsChatDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(CommsChatDefOf));
    }

    /// <summary>
    /// Colonists with a passion for Social may spend their recreation calling someone they know in another, non-hostile
    /// faction over a powered comms console. During the call they have vanilla social interactions with that person
    /// (chitchat, deep talk, slights, insults, kind words, romance and so on), picked with vanilla weights and with vanilla
    /// thoughts, opinions, skill gains and log entries on both sides. Vanilla requires both pawns to be spawned close
    /// together, so the interaction is applied here without the position checks; like vanilla with an unspawned recipient,
    /// no social fight can start.
    /// "Someone they know": a relative or other relation, or a pawn either side holds a memory about.
    /// </summary>
    public static class CommsChat
    {
        // Ticks between interactions during a call, and the delay before the first one.
        public const int InteractionInterval = 500;
        public const int FirstInteractionDelay = 250;

        private static TuneUpModule module;

        /// <summary>
        /// Whether the module was switched on at startup. The defs are always loaded (so saves with the job still load),
        /// so the recreation checks this itself.
        /// </summary>
        public static bool Enabled => (module ??= Modules.Get("CommsChat")).IsActive;

        private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, int> lastInteractionTime =
            AccessTools.FieldRefAccess<Pawn_InteractionsTracker, int>("lastInteractionTime");
        private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, string> lastInteraction =
            AccessTools.FieldRefAccess<Pawn_InteractionsTracker, string>("lastInteraction");
        private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, InteractionDef> lastInteractionDef =
            AccessTools.FieldRefAccess<Pawn_InteractionsTracker, InteractionDef>("lastInteractionDef");

        public static float PassionFactor(Pawn pawn)
        {
            SkillRecord rec = pawn.skills?.GetSkill(SkillDefOf.Social);
            if (rec == null || rec.TotallyDisabled)
                return 0f;
            switch (rec.passion)
            {
                case Passion.Major: return 1.5f;
                case Passion.Minor: return 1f;
                default: return 0f;
            }
        }

        public static bool CanCall(Pawn pawn) =>
            pawn.IsFreeNonSlaveColonist && pawn.Spawned && PassionFactor(pawn) > 0f
            && SocialInteractionUtility.CanInitiateRandomInteraction(pawn);

        /// <summary>An off-map member of a non-hostile faction the comms console can reach, able to talk.</summary>
        public static bool CanBeCalled(Pawn p)
        {
            if (p == null || p.Dead || p.Destroyed || p.Spawned || !p.RaceProps.Humanlike || !p.IsWorldPawn())
                return false;
            if (Find.WorldPawns.GetSituation(p) != WorldPawnSituation.Free)
                return false;
            Faction f = p.Faction;
            if (f == null || f.IsPlayer || f.temporary || f.Hidden || f.HostileTo(Faction.OfPlayer))
                return false;
            // Off the map a pawn has no interactions tracker (vanilla drops it on despawn); a call lends one.
            if (p.IsMutant || p.IsPrisoner || p.IsSlave || p.Downed || p.needs?.mood == null)
                return false;
            if (p.DevelopmentalStage.Baby() || !p.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                return false;
            return true;
        }

        /// <summary>Everyone <paramref name="pawn"/> knows who can be called right now.</summary>
        public static List<Pawn> Contacts(Pawn pawn)
        {
            HashSet<Pawn> known = new HashSet<Pawn>();
            foreach (Pawn p in pawn.relations.RelatedPawns)
                known.Add(p);
            List<Thought_Memory> own = pawn.needs?.mood?.thoughts.memories.Memories;
            if (own != null)
            {
                foreach (Thought_Memory m in own)
                    if (m.otherPawn != null)
                        known.Add(m.otherPawn);
            }
            // World pawns are usually mothballed, so what they remember about colonists stays.
            foreach (Pawn p in Find.WorldPawns.AllPawnsAlive)
            {
                if (known.Contains(p))
                    continue;
                List<Thought_Memory> theirs = p.needs?.mood?.thoughts.memories.Memories;
                if (theirs == null)
                    continue;
                foreach (Thought_Memory m in theirs)
                {
                    if (m.otherPawn == pawn)
                    {
                        known.Add(p);
                        break;
                    }
                }
            }
            List<Pawn> result = new List<Pawn>();
            foreach (Pawn p in known)
                if (p != pawn && CanBeCalled(p))
                    result.Add(p);
            return result;
        }

        public static bool ConsoleUsable(Pawn pawn, Building_CommsConsole console) =>
            console.Spawned && console.CanUseCommsNow && !console.IsForbidden(pawn) && !console.Fogged()
            && console.IsSociallyProper(pawn) && console.IsPoliticallyProper(pawn) && !console.IsBurning()
            && pawn.CanReserve(console);

        public static Job TryMakeJob(Pawn pawn)
        {
            if (!CanCall(pawn))
                return null;
            List<Building_CommsConsole> consoles = new List<Building_CommsConsole>(pawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>());
            if (consoles.Count == 0)
                return null;
            Thing console = GenClosest.ClosestThing_Global_Reachable(pawn.Position, pawn.Map, consoles, PathEndMode.InteractionCell,
                TraverseParms.For(pawn), 9999f, t => ConsoleUsable(pawn, (Building_CommsConsole)t));
            if (console == null)
                return null;
            if (!Contacts(pawn).TryRandomElement(out Pawn contact))
                return null;
            return JobMaker.MakeJob(CommsChatDefOf.VTU_CommsChat, console, contact);
        }

        /// <summary>One interaction picked like vanilla's random interactions, between the caller and the contact.</summary>
        public static bool TryInteract(Pawn initiator, Pawn recipient)
        {
            // Interaction workers may read the recipient's tracker (conversion checks SocialFightPossible), which an
            // off-map pawn does not have: lend it one for the interaction and drop it again, as vanilla would.
            bool lent = recipient.interactions == null;
            if (lent)
                recipient.interactions = new Pawn_InteractionsTracker(recipient);
            InCall = true;
            try
            {
                if (!DefDatabase<InteractionDef>.AllDefsListForReading.TryRandomElementByWeight(
                        d => CanUse(d, initiator, recipient) ? d.Worker.RandomSelectionWeight(initiator, recipient) : 0f,
                        out InteractionDef def))
                    return false;
                Interact(initiator, recipient, def);
                return true;
            }
            finally
            {
                InCall = false;
                if (lent)
                    recipient.interactions = null;
            }
        }

        /// <summary>True while a call interaction runs; social fights are impossible then.</summary>
        public static bool InCall;

        private static bool CanUse(InteractionDef def, Pawn initiator, Pawn recipient) =>
            SocialInteractionUtility.CanInitiateInteraction(initiator, def)
            && !recipient.IsInteractionBlocked(def, isInitiator: false, isRandom: true);

        /// <summary>
        /// Vanilla Pawn_InteractionsTracker.TryInteractWith without the position checks. The recipient is off the map,
        /// so there is no social fight (vanilla skips it for unspawned recipients too), and the speech bubble has no
        /// arrow towards it.
        /// </summary>
        public static void Interact(Pawn initiator, Pawn recipient, InteractionDef def)
        {
            List<RulePackDef> extraSentencePacks = new List<RulePackDef>();
            if (def.initiatorThought != null)
                Pawn_InteractionsTracker.AddInteractionThought(initiator, recipient, def.initiatorThought);
            if (def.recipientThought != null && recipient.needs.mood != null)
                Pawn_InteractionsTracker.AddInteractionThought(recipient, initiator, def.recipientThought);
            if (def.initiatorXpGainSkill != null)
                initiator.skills.Learn(def.initiatorXpGainSkill, def.initiatorXpGainAmount);
            if (def.recipientXpGainSkill != null && recipient.RaceProps.Humanlike)
                recipient.skills.Learn(def.recipientXpGainSkill, def.recipientXpGainAmount);
            def.Worker.Interacted(initiator, recipient, extraSentencePacks, out string letterText, out string letterLabel,
                out LetterDef letterDef, out LookTargets lookTargets);
            MoteMaker.MakeInteractionBubble(initiator, null, def.interactionMote, def.GetSymbol(initiator.Faction, initiator.Ideo),
                def.GetSymbolColor(initiator.Faction));
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in new[] { initiator, recipient })
            {
                if (p.interactions == null)
                    continue;
                lastInteractionTime(p.interactions) = now;
                lastInteraction(p.interactions) = def.defName;
                lastInteractionDef(p.interactions) = def;
            }
            PlayLogEntry_Interaction entry = new PlayLogEntry_Interaction(def, initiator, recipient, extraSentencePacks);
            Find.PlayLog.Add(entry);
            if (letterDef != null)
            {
                string text = entry.ToGameStringFromPOV(initiator);
                if (!letterText.NullOrEmpty())
                    text = text + "\n\n" + letterText;
                Find.LetterStack.ReceiveLetter(letterLabel, text, letterDef, lookTargets ?? (LookTargets)initiator);
            }
        }
    }

    /// <summary>
    /// No social fight over the comms: conversion attempts and other workers that would start one check this first.
    /// </summary>
    [HarmonyPatchCategory("CommsChat")]
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.SocialFightPossible))]
    public static class Patch_SocialFightPossible_CommsChat
    {
        public static bool Prefix(ref bool __result)
        {
            if (!CommsChat.InCall)
                return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Yayo's Animation picks standing animations from a fixed list of job names, so a call stood still.
    /// It is animated like a child's radio talk on the same console; the cached job name is set back afterwards,
    /// otherwise Yayo would see a job change every frame and recompute.
    /// </summary>
    [HarmonyPatchCategory("CommsChat")]
    [HarmonyPatch]
    public static class YayoAnimationCompat_CommsChat
    {
        private const string AnimatedAs = "Radiotalking";
        private static FieldInfo jobNameField;

        private static MethodBase Target()
        {
            if (!ModsConfig.IsActive("com.yayo.yayoAni.continued"))
                return null;
            return AccessTools.Method(AccessTools.TypeByName("YayoAnimation.AnimationCore"), "AniStanding");
        }

        private static bool Prepare() => Target() != null;

        private static MethodBase TargetMethod() => Target();

        private static void Prefix(ref string defName, out bool __state)
        {
            __state = defName == "VTU_CommsChat";
            if (__state)
                defName = AnimatedAs;
        }

        private static void Postfix(object pdd, bool __state)
        {
            if (!__state || pdd == null)
                return;
            jobNameField ??= AccessTools.Field(pdd.GetType(), "jobName");
            jobNameField?.SetValue(pdd, "VTU_CommsChat");
        }
    }

    public class JoyGiver_CommsChat : JoyGiver
    {
        public override float GetChance(Pawn pawn) => def.baseChance * CommsChat.PassionFactor(pawn);

        public override bool CanBeGivenTo(Pawn pawn) =>
            CommsChat.Enabled && base.CanBeGivenTo(pawn) && CommsChat.CanCall(pawn);

        public override Job TryGiveJob(Pawn pawn) => CommsChat.TryMakeJob(pawn);
    }

    /// <summary>Walks to the console and talks with the contact (target B) until recreation is full or time runs out.</summary>
    public class JobDriver_CommsChat : JobDriver
    {
        private Building_CommsConsole Console => (Building_CommsConsole)job.GetTarget(TargetIndex.A).Thing;
        private Pawn Contact => job.GetTarget(TargetIndex.B).Thing as Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !Console.CanUseCommsNow || !CommsChat.CanBeCalled(Contact));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil chat = ToilMaker.MakeToil("CommsChat");
            int nextInteraction = -1;
            chat.tickIntervalAction = delta =>
            {
                int now = Find.TickManager.TicksGame;
                if (nextInteraction < 0)
                    nextInteraction = now + CommsChat.FirstInteractionDelay;
                if (now >= nextInteraction)
                {
                    nextInteraction = now + CommsChat.InteractionInterval;
                    if (SocialInteractionUtility.CanInitiateRandomInteraction(pawn))
                        CommsChat.TryInteract(pawn, Contact);
                }
                JoyUtility.JoyTickCheckEnd(pawn, delta, JoyTickFullJoyAction.EndJob);
            };
            chat.handlingFacing = true;
            chat.tickAction = () => pawn.rotationTracker.FaceTarget(Console);
            chat.defaultCompleteMode = ToilCompleteMode.Delay;
            chat.defaultDuration = job.def.joyDuration;
            chat.socialMode = RandomSocialMode.Off;
            yield return chat;
        }
    }
}
