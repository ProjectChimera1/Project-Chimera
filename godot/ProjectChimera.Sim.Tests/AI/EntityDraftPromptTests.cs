#nullable enable
using System;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;
using Xunit;
using static ProjectChimera.Sim.Tests.AI.EntityDraftTestData;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.4 — prompt staleness guards (8.3 guard style, exact-line-token match): the ability draft prompt must
    /// enumerate every member of the closed effect-kind / targeting / activation vocabularies; the faction draft prompt
    /// must enumerate every <c>ai_preset</c> closed-set member; and the unit/hero prompts must state the Fixed-safe
    /// range + the archetype+ability composition guidance. A closed-vocabulary member absent from its builder fails here.
    /// </summary>
    public class EntityDraftPromptTests
    {
        // The closed effect-kind discriminator set (EffectNodeJsonConverter's hardcoded kind registry). If a new kind is
        // added to the converter, add it here AND to BuildAbilityDraftPrompt — this guard makes the omission loud.
        private static readonly string[] EffectKinds =
            { "direct_hp_delta", "heal", "damage", "apply_modifier", "sequence", "search_area", "persistent" };

        // The activation JSON tokens (snake_case) mirroring PassiveActivation / AbilityDefinition.ParsedActivation.
        private static readonly string[] ActivationTokens = { "active", "aura", "on_hit", "while_alive" };

        [Fact]
        public void BuildAbilityDraftPrompt_EnumeratesEveryEffectKind()
        {
            string prompt = LLMService.BuildAbilityDraftPrompt(AbilityCtx());
            foreach (string kind in EffectKinds)
                Assert.True(HeadsALine(prompt, kind), $"ability prompt omits effect kind '{kind}'");
        }

        [Fact]
        public void BuildAbilityDraftPrompt_EnumeratesEveryTargeting()
        {
            string prompt = LLMService.BuildAbilityDraftPrompt(AbilityCtx());
            foreach (string t in Enum.GetNames(typeof(AbilityTargeting)))
                Assert.True(HeadsALine(prompt, t), $"ability prompt omits targeting '{t}'");
        }

        [Fact]
        public void BuildAbilityDraftPrompt_EnumeratesEveryActivation()
        {
            string prompt = LLMService.BuildAbilityDraftPrompt(AbilityCtx());
            foreach (string a in ActivationTokens)
                Assert.True(HeadsALine(prompt, a), $"ability prompt omits activation '{a}'");
        }

        [Fact]
        public void BuildFactionDraftPrompt_EnumeratesEveryAiPreset()
        {
            string prompt = LLMService.BuildFactionDraftPrompt(FactionCtx());
            foreach (string preset in FactionValidator.KnownAiPresets)
                Assert.True(HeadsALine(prompt, preset), $"faction prompt omits ai_preset '{preset}'");
        }

        [Fact]
        public void BuildUnitDraftPrompt_StatesRangeAndComposition()
        {
            string prompt = LLMService.BuildUnitDraftPrompt(UnitCtx());
            Assert.Contains("32768", prompt);        // the Fixed-safe range
            Assert.Contains("composition", prompt);  // archetype + ability composition guidance
            Assert.Contains("archetype", prompt);
        }

        [Fact]
        public void BuildHeroDraftPrompt_StatesRangeCompositionAndHeroRequirement()
        {
            string prompt = LLMService.BuildHeroDraftPrompt(UnitCtx());
            Assert.Contains("32768", prompt);
            Assert.Contains("composition", prompt);
            Assert.Contains("is_hero", prompt);   // a hero draft requires is_hero:true + a hero block
        }

        /// <summary>True when some prompt line's first whitespace-delimited token is exactly <paramref name="token"/> —
        /// i.e. the member heads its own line, not merely appears as a substring of another (the 8.3 guard idiom).</summary>
        private static bool HeadsALine(string prompt, string token)
        {
            foreach (string raw in prompt.Split('\n'))
            {
                string line = raw.Trim();
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                string head = sp < 0 ? line : line.Substring(0, sp);
                if (head == token) return true;
            }
            return false;
        }
    }
}
