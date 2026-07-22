#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.5 — prompt staleness guard (8.3/8.4 guard style, exact-line-token match): every member of
    /// <see cref="BalanceSuggestionApplier.TunableFields"/> must head its own line in
    /// <see cref="LLMService.BuildBalanceAnalysisPrompt"/>, and the Fixed-safe range line must be present. A tunable-field
    /// member absent from the builder fails here — preventing prompt drift from the single-source tunable set.
    /// </summary>
    public class BalanceAnalysisPromptTests
    {
        private static BalanceAnalysisContext Ctx() =>
            new() { UnitIds = new List<string> { "grunt", "archer" } };

        [Fact]
        public void BuildBalanceAnalysisPrompt_EnumeratesEveryTunableField()
        {
            string prompt = LLMService.BuildBalanceAnalysisPrompt(Ctx());
            foreach (string field in BalanceSuggestionApplier.TunableFields)
                Assert.True(HeadsALine(prompt, field), $"balance prompt omits tunable field '{field}'");
        }

        [Fact]
        public void BuildBalanceAnalysisPrompt_StatesFixedSafeRange()
        {
            string prompt = LLMService.BuildBalanceAnalysisPrompt(Ctx());
            Assert.Contains("32768", prompt);
        }

        [Fact]
        public void BuildBalanceAnalysisPrompt_ListsRosterUnitIds()
        {
            string prompt = LLMService.BuildBalanceAnalysisPrompt(Ctx());
            Assert.Contains("grunt", prompt);
            Assert.Contains("archer", prompt);
        }

        /// <summary>True when some prompt line's first whitespace-delimited token is exactly <paramref name="token"/> —
        /// i.e. the member heads its own line, not merely appears as a substring of another (the 8.3/8.4 guard idiom).</summary>
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
