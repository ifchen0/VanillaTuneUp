using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Grammar;

namespace VanillaTuneUp
{
    /// <summary>
    /// Fixes mistakes in official translations that sit inside grammar rule lists. A mod's DefInjected file cannot
    /// replace these: a second full-list injection for the same key throws and the whole file is skipped. So the
    /// rules are rewritten in place after the translation has been injected, and the cached rules are dropped.
    /// </summary>
    public static class TranslationFixes
    {
        private static readonly AccessTools.FieldRef<RulePack, List<string>> RulesStrings =
            AccessTools.FieldRefAccess<RulePack, List<string>>("rulesStrings");
        private static readonly AccessTools.FieldRef<RulePack, List<Rule>> RulesResolved =
            AccessTools.FieldRefAccess<RulePack, List<Rule>>("rulesResolved");

        public static void Apply()
        {
            QuestFactionRules.Apply();
            UntranslatedRules.Apply();
            TribalNameSyllables.Apply();
        }

        /// <summary>Rewrites every rule in the pack with <paramref name="rewrite"/>; returns how many changed.</summary>
        public static int Rewrite(RulePack pack, Func<string, string> rewrite)
        {
            List<string> rules = pack == null ? null : RulesStrings(pack);
            if (rules == null)
                return 0;
            int count = 0;
            for (int i = 0; i < rules.Count; i++)
            {
                string fixedRule = rewrite(rules[i]);
                if (fixedRule == rules[i])
                    continue;
                rules[i] = fixedRule;
                count++;
            }
            if (count > 0)
                RulesResolved(pack) = null;
            return count;
        }
    }

    /// <summary>
    /// Some official translations compare a quest's faction constant against a faction def that does not exist,
    /// e.g. Traditional Chinese ProblemCauser uses "siteFaction==Mechanoids" where the def is "Mechanoid". No rule
    /// matches, so the quest name and description show "ERR:" with raw [tags]. Rewrite such conditions to the
    /// def name they were meant to be.
    /// </summary>
    public static class QuestFactionRules
    {
        // Translator typo -> real FactionDef name. Applied only while the typo is not itself a FactionDef.
        private static readonly Dictionary<string, string> Typos = new Dictionary<string, string>
        {
            { "Mechanoids", "Mechanoid" },
        };

        private static readonly Regex Condition = new Regex(@"(?<key>\w*[Ff]action)(?<op>==|!=)(?<value>[^,)\s]+)");

        public static void Apply()
        {
            int fixedCount = 0;
            foreach (QuestScriptDef def in DefDatabase<QuestScriptDef>.AllDefsListForReading)
            {
                fixedCount += TranslationFixes.Rewrite(def.questNameRules, FixRule);
                fixedCount += TranslationFixes.Rewrite(def.questDescriptionRules, FixRule);
                fixedCount += TranslationFixes.Rewrite(def.questDescriptionAndNameRules, FixRule);
                fixedCount += TranslationFixes.Rewrite(def.questContentRules, FixRule);
                fixedCount += TranslationFixes.Rewrite(def.questSubjectRules, FixRule);
            }
            if (fixedCount > 0)
                Log.Message($"[Vanilla Tune-Up] Fixed {fixedCount} quest text rule(s) with a misspelled faction condition.");
        }

        private static string FixRule(string rule)
        {
            int arrow = rule.IndexOf("->");
            if (arrow < 0)
                return rule;
            // Only the condition part before "->"; the output text is left untouched.
            string head = rule.Substring(0, arrow);
            string fixedHead = Condition.Replace(head, m =>
            {
                string value = m.Groups["value"].Value;
                if (!Typos.TryGetValue(value, out string real) || DefDatabase<FactionDef>.GetNamedSilentFail(value) != null)
                    return m.Value;
                return m.Groups["key"].Value + m.Groups["op"].Value + real;
            });
            return fixedHead == head ? rule : fixedHead + rule.Substring(arrow);
        }
    }

    /// <summary>
    /// Rules an official translation left (partly) in English. Each fix replaces an exact piece of the shipped
    /// text, so it stops matching on its own once the translation is corrected.
    /// </summary>
    public static class UntranslatedRules
    {
        private class Fix
        {
            public string Language;
            public Func<RulePack> Pack;
            public string Old;
            public string New;
        }

        private const string TraditionalChinese = "ChineseTraditional";

        private static RulePack Tale(string defName) => DefDatabase<TaleDef>.GetNamedSilentFail(defName)?.rulePack;

        private static readonly List<Fix> Fixes = new List<Fix>
        {
            // Ideology: pilgrims quest when every pilgrim is a child.
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => DefDatabase<QuestScriptDef>.GetNamedSilentFail("ReliquaryPilgrims")?.questDescriptionRules,
                Old = "A group of child [pilgrimsLabelSingularOrPlural] has arrived to venerate the [relic_name].",
                New = "一群兒童[pilgrimsLabelSingularOrPlural]為朝拜[relic_name]來訪。"
            },
            // Ideology: accusation in a trial, translation followed by the English original.
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => DefDatabase<InteractionDef>.GetNamedSilentFail("Trial_Accuse")?.logRulesInitiator,
                Old = "wrongdoing->不尊重崇高的理念disrespecting sacred ideas",
                New = "wrongdoing->不尊重崇高的理念"
            },
            // Ideology: founder job in ideoligion stories.
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => DefDatabase<PlaceDef>.GetNamedSilentFail("Oubanyen")?.placeRules,
                Old = "place_job->goat-herd",
                New = "place_job->牧羊人"
            },
            // Core: art descriptions of a killing or a prisoner sale.
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => Tale("KilledBy"),
                Old = "image->[VICTIM_nameFull]being killed by[KILLER_nameFull][circumstance_group]",
                New = "image->[VICTIM_nameFull][circumstance_group]被[KILLER_nameFull]殺死"
            },
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => Tale("KilledBy"),
                Old = "image->[KILLER_nameFull]killing[VICTIM_nameFull][circumstance_group]",
                New = "image->[KILLER_nameFull][circumstance_group]殺死[VICTIM_nameFull]"
            },
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => Tale("KilledBy"),
                Old = "image->[KILLER_nameFull]ending[VICTIM_nameFull]'s life[circumstance_group]",
                New = "image->[KILLER_nameFull][circumstance_group]結束了[VICTIM_nameFull]的生命"
            },
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => Tale("KilledBy"),
                Old = "image->[KILLER_nameFull]brutally murdering[VICTIM_nameFull][circumstance_group]",
                New = "image->[KILLER_nameFull][circumstance_group]殘殺[VICTIM_nameFull]"
            },
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => Tale("KilledBy"),
                Old = "image->[VICTIM_nameFull]being killed[circumstance_group]",
                New = "image->[VICTIM_nameFull][circumstance_group]遭到殺害"
            },
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => Tale("SoldPrisoner"),
                Old = "image->[SELLER_nameFull]selling[PRISONER_nameFull]to[TRADER_nameFull][circumstance_group]",
                New = "image->[SELLER_nameFull][circumstance_group]將[PRISONER_nameFull]賣給[TRADER_nameFull]"
            },
            // Core: art title for the ship escape message.
            new Fix
            {
                Language = TraditionalChinese,
                Pack = () => Tale("EndGame_ShipEscape"),
                Old = "tale_noun->an opportunity arriving for[PAWN_nameDef]的[Community]",
                New = "tale_noun->[PAWN_nameDef]的[Community]迎來的機會"
            },
        };

        public static void Apply()
        {
            string language = LanguageDatabase.activeLanguage?.folderName;
            if (language == null)
                return;
            int fixedCount = 0;
            foreach (Fix fix in Fixes)
            {
                if (!language.StartsWith(fix.Language))
                    continue;
                fixedCount += TranslationFixes.Rewrite(fix.Pack(), rule => rule.Contains(fix.Old) ? rule.Replace(fix.Old, fix.New) : rule);
            }
            if (fixedCount > 0)
                Log.Message($"[Vanilla Tune-Up] Fixed {fixedCount} untranslated rule(s) in the official translation.");
        }
    }

    /// <summary>
    /// Traditional Chinese tribal names are much longer than English ones. A tribal word is built from 2-4 entries
    /// of the Galician syllable list, but the translated entries are 1-5 characters (about 2.4 on average), and one
    /// Chinese character is already one syllable. The tribal word list was also replaced by that same syllable list.
    /// Split every syllable entry into single characters (duplicates kept, so common characters stay common), and
    /// do the same to the word list while it is still a copy of the syllable list. A mod's Strings file is appended
    /// to the official one rather than replacing it, so the loaded lists are rewritten in place.
    /// </summary>
    public static class TribalNameSyllables
    {
        private const string TraditionalChinese = "ChineseTraditional";
        private const string SyllableFile = "WordParts/Syllables_Galician";
        private const string TribalWordFile = "Words/Foreign/Tribal";

        public static void Apply()
        {
            LoadedLanguage language = LanguageDatabase.activeLanguage;
            if (language?.folderName == null || !language.folderName.StartsWith(TraditionalChinese))
                return;
            if (!language.TryGetStringsFromFile(SyllableFile, out List<string> syllables))
                return;
            bool wordsAreSyllables = language.TryGetStringsFromFile(TribalWordFile, out List<string> words)
                && words.SequenceEqual(syllables);
            if (!SplitIntoCharacters(syllables))
                return;
            if (wordsAreSyllables)
            {
                words.Clear();
                words.AddRange(syllables);
            }
            Log.Message($"[Vanilla Tune-Up] Split Traditional Chinese tribal name syllables into {syllables.Count} single characters.");
        }

        /// <summary>Replaces each multi-character entry with its characters; returns whether anything changed.</summary>
        private static bool SplitIntoCharacters(List<string> list)
        {
            List<string> split = new List<string>();
            foreach (string entry in list)
            {
                foreach (char c in entry.Trim())
                    split.Add(c.ToString());
            }
            if (split.SequenceEqual(list))
                return false;
            list.Clear();
            list.AddRange(split);
            return true;
        }
    }

    /// <summary>Changing the language reloads all defs after startup; fix the newly injected rules again.</summary>
    [HarmonyPatchCategory("TranslationFixes")]
    [HarmonyPatch(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_AfterImpliedDefs))]
    public static class Patch_LoadedLanguage_InjectIntoData_AfterImpliedDefs
    {
        public static void Postfix() => TranslationFixes.Apply();
    }
}
