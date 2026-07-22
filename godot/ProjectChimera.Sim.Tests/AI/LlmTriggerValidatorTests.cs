#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Dsl; // NodeKinds
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.3 — the trigger prompt/validator extension: <see cref="LLMService.Validate"/> accepts the newly-added
    /// flat constructs (<c>unit_damaged</c>, <c>unit_in_region</c>) and rejects an unknown/graph-only
    /// event/condition/action <c>Type</c> with a LOCATED error (path + offending value); and
    /// <see cref="LLMService.BuildSystemPrompt"/> enumerates every member of the flat <see cref="NodeKinds"/> sets
    /// (staleness guard — a future flat construct added to the registry but not the prompt fails this test).
    /// </summary>
    public class LlmTriggerValidatorTests
    {
        private static ScenarioContext Ctx() => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        // ── Accepts the newly-added flat constructs ───────────────────────────

        [Fact]
        public void Validate_AcceptsNewFlatEvent_UnitDamaged()
        {
            string json =
                "{\"name\":\"T\",\"events\":[{\"type\":\"unit_damaged\",\"faction\":0}]," +
                "\"conditions\":[],\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
        }

        [Fact]
        public void Validate_AcceptsNewFlatCondition_UnitInRegion()
        {
            string json =
                "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}]," +
                "\"conditions\":[{\"type\":\"unit_in_region\",\"faction\":0,\"region_id\":\"r1\"}]," +
                "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
        }

        // ── Rejects unknown constructs with a LOCATED error ───────────────────

        [Fact]
        public void Validate_RejectsUnknownEventType_LocatedError()
        {
            string json =
                "{\"name\":\"T\",\"events\":[{\"type\":\"foo\"}],\"conditions\":[]," +
                "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, Ctx());
            Assert.Null(trigger);
            Assert.Equal("events[0].type='foo' is not a known trigger event type.", error);
        }

        [Fact]
        public void Validate_RejectsUnknownConditionType_LocatedError()
        {
            string json =
                "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}]," +
                "\"conditions\":[{\"type\":\"always\"},{\"type\":\"bogus\"}]," +
                "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, Ctx());
            Assert.Null(trigger);
            Assert.Equal("conditions[1].type='bogus' is not a known trigger condition type.", error);
        }

        [Fact]
        public void Validate_RejectsUnknownActionType_LocatedError()
        {
            string json =
                "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}],\"conditions\":[]," +
                "\"actions\":[{\"type\":\"victory\",\"faction\":0},{\"type\":\"foo\",\"faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, Ctx());
            Assert.Null(trigger);
            Assert.Equal("actions[1].type='foo' is not a known trigger action type.", error);
        }

        [Fact]
        public void Validate_RejectsGraphOnlyAction_OrderUnits()
        {
            // order_units is a graph-channel-only leaf (not in FlatActionTypes) — a flat trigger carrying it must be
            // rejected at the LLM gate, exactly like an unknown construct.
            string json =
                "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}],\"conditions\":[]," +
                "\"actions\":[{\"type\":\"order_units\",\"faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, Ctx());
            Assert.Null(trigger);
            Assert.Equal("actions[0].type='order_units' is not a known trigger action type.", error);
        }

        [Fact]
        public void Validate_RejectsGraphOnlyEvent_CustomEvent()
        {
            string json =
                "{\"name\":\"T\",\"events\":[{\"type\":\"custom_event\"}],\"conditions\":[]," +
                "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, Ctx());
            Assert.Null(trigger);
            Assert.Equal("events[0].type='custom_event' is not a known trigger event type.", error);
        }

        // ── Prompt staleness guard ────────────────────────────────────────────

        [Fact]
        public void BuildSystemPrompt_EnumeratesEveryFlatConstruct()
        {
            string prompt = LLMService.BuildSystemPrompt(Ctx());

            // Exact line-token match, NOT substring: each construct must HEAD its own description line. A plain
            // Contains would false-pass on a substring collision (e.g. the "unit_count" condition is a substring of the
            // "unit_count_threshold" event line, so deleting the unit_count line would still pass a substring guard).
            foreach (string ev in NodeKinds.EventTypes)
                Assert.True(PromptDescribesConstruct(prompt, ev), $"prompt omits event '{ev}'");
            foreach (string cond in NodeKinds.ConditionTypes)
                Assert.True(PromptDescribesConstruct(prompt, cond), $"prompt omits condition '{cond}'");
            foreach (string act in NodeKinds.FlatActionTypes)
                Assert.True(PromptDescribesConstruct(prompt, act), $"prompt omits action '{act}'");
        }

        /// <summary>True when some prompt line's first whitespace-delimited token is exactly <paramref name="construct"/>
        /// — i.e. the construct heads its own description line, not merely appears as a substring of another.</summary>
        private static bool PromptDescribesConstruct(string prompt, string construct)
        {
            foreach (string raw in prompt.Split('\n'))
            {
                string line = raw.Trim();
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                string token = sp < 0 ? line : line.Substring(0, sp);
                if (token == construct) return true;
            }
            return false;
        }

        [Fact]
        public void BuildSystemPrompt_IncludesPreviouslyOmittedConstructs()
        {
            string prompt = LLMService.BuildSystemPrompt(Ctx());
            // The five 7.13 events + the 6.4 condition the stale prompt omitted before this story.
            Assert.Contains("unit_damaged", prompt);
            Assert.Contains("unit_trained", prompt);
            Assert.Contains("ability_cast", prompt);
            Assert.Contains("hero_level", prompt);
            Assert.Contains("player_chat", prompt);
            Assert.Contains("unit_in_region", prompt);
        }
    }
}
