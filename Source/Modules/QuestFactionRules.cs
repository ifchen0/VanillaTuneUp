using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Grammar;

namespace VanillaTuneUp
{
    /// <summary>
    /// Some official translations compare a quest's faction constant against a faction def that does not exist,
    /// e.g. Traditional Chinese ProblemCauser uses "siteFaction==Mechanoids" where the def is "Mechanoid". No rule
    /// matches, so the quest name and description show "ERR:" with raw [tags]. Rewrite such conditions to the
    /// def name they were meant to be, then drop the cached rules so they are parsed again.
    /// </summary>
    public static class QuestFactionRules
    {
        // Translator typo -> real FactionDef name. Applied only while the typo is not itself a FactionDef.
        private static readonly Dictionary<string, string> Typos = new Dictionary<string, string>
        {
            { "Mechanoids", "Mechanoid" },
        };

        private static readonly Regex Condition = new Regex(@"(?<key>\w*[Ff]action)(?<op>==|!=)(?<value>[^,)\s]+)");

        private static readonly AccessTools.FieldRef<RulePack, List<string>> RulesStrings =
            AccessTools.FieldRefAccess<RulePack, List<string>>("rulesStrings");
        private static readonly AccessTools.FieldRef<RulePack, List<Rule>> RulesResolved =
            AccessTools.FieldRefAccess<RulePack, List<Rule>>("rulesResolved");

        public static void Apply()
        {
            int fixedCount = 0;
            foreach (QuestScriptDef def in DefDatabase<QuestScriptDef>.AllDefsListForReading)
            {
                fixedCount += Fix(def.questNameRules);
                fixedCount += Fix(def.questDescriptionRules);
                fixedCount += Fix(def.questDescriptionAndNameRules);
                fixedCount += Fix(def.questContentRules);
                fixedCount += Fix(def.questSubjectRules);
            }
            if (fixedCount > 0)
                Log.Message($"[Vanilla Tune-Up] Fixed {fixedCount} quest text rule(s) with a misspelled faction condition.");
        }

        private static int Fix(RulePack pack)
        {
            List<string> rules = pack == null ? null : RulesStrings(pack);
            if (rules == null)
                return 0;
            int count = 0;
            for (int i = 0; i < rules.Count; i++)
            {
                string rule = rules[i];
                int arrow = rule.IndexOf("->");
                if (arrow < 0)
                    continue;
                // Only the condition part before "->"; the output text is left untouched.
                string head = rule.Substring(0, arrow);
                string fixedHead = Condition.Replace(head, m =>
                {
                    string value = m.Groups["value"].Value;
                    if (!Typos.TryGetValue(value, out string real) || DefDatabase<FactionDef>.GetNamedSilentFail(value) != null)
                        return m.Value;
                    return m.Groups["key"].Value + m.Groups["op"].Value + real;
                });
                if (fixedHead == head)
                    continue;
                rules[i] = fixedHead + rule.Substring(arrow);
                count++;
            }
            if (count > 0)
                RulesResolved(pack) = null;
            return count;
        }
    }

    /// <summary>Changing the language reloads all defs after startup; fix the newly injected rules again.</summary>
    [HarmonyPatchCategory("QuestFactionRules")]
    [HarmonyPatch(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_AfterImpliedDefs))]
    public static class Patch_LoadedLanguage_InjectIntoData_AfterImpliedDefs
    {
        public static void Postfix() => QuestFactionRules.Apply();
    }
}
