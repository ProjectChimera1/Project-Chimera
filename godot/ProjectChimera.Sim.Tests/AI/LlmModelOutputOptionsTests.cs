#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-526 — every <see cref="LLMService"/> parse path for UNTRUSTED MODEL OUTPUT now shares one options object
    /// (<c>ContentJson.ModelOutputOptions</c>) instead of building its own per call. Three sites diverged before:
    /// the trigger validator (<c>PropertyNameCaseInsensitive</c> + Fixed, no syntax tolerance), the scenario
    /// validator (Fixed + syntax tolerance, no enum converter), and the balance-report validator (NO converters at
    /// all — the one content-shaped parse path that could not read a <see cref="ProjectChimera.Core.Fixed"/>-typed
    /// field and rejected the syntax every other LLM path accepted).
    ///
    /// <para>Each test below is a behavior that DIFFERS between the old per-site sets and the shared posture, so the
    /// suite fails if any site drifts back off the choke point. The structural half (converter set/order, the two
    /// documented deltas) lives in <c>Definitions.ContentJsonDerivedPostureTests</c>.</para>
    /// </summary>
    public class LlmModelOutputOptionsTests
    {
        private static ScenarioContext TriggerCtx() => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        private static MapGeneratorContext MapCtx() => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        private static BalanceAnalysisContext BalanceCtx() =>
            new() { UnitIds = new List<string> { "grunt", "archer" } };

        /// <summary>A structurally valid two-slot scenario; <paramref name="extra"/> injects additional top-level keys.</summary>
        private static string Scenario(string extra = "") =>
            "{\"player_slots\":[" +
            "{\"slot\":0,\"faction_json\":\"a.json\",\"base_x\":-45,\"base_z\":0}," +
            "{\"slot\":1,\"faction_json\":\"b.json\",\"base_x\":45,\"base_z\":0}" +
            "]" + extra + "}";

        private const string ValidTrigger =
            "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}],\"conditions\":[]," +
            "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";

        // ── The syntax tolerance now reaches EVERY model-output path ─────────────────────────────────────────────

        /// <summary>RED before DW-526: the trigger site built <c>{ PropertyNameCaseInsensitive, FixedJsonConverter }</c>
        /// with neither <c>AllowTrailingCommas</c> nor comment handling, so a model's single most common deviation
        /// threw away the whole (paid) generation — while the scenario path beside it tolerated exactly that.</summary>
        [Fact]
        public void TriggerParse_ToleratesTrailingComma()
        {
            string json = ValidTrigger.Replace(
                "\"actions\":[{\"type\":\"victory\",\"faction\":0}]",
                "\"actions\":[{\"type\":\"victory\",\"faction\":0},]");
            var (trigger, error) = LLMService.Validate(json, TriggerCtx());

            Assert.Null(error);
            Assert.NotNull(trigger);
        }

        /// <summary>RED before DW-526 — same site, the other common deviation.</summary>
        [Fact]
        public void TriggerParse_ToleratesLineComment()
        {
            string json = "{\n  // the opening trigger\n  \"name\":\"T\",\n" +
                          "  \"events\":[{\"type\":\"match_start\"}],\"conditions\":[],\n" +
                          "  \"actions\":[{\"type\":\"victory\",\"faction\":0}]\n}";
            var (trigger, error) = LLMService.Validate(json, TriggerCtx());

            Assert.Null(error);
            Assert.NotNull(trigger);
        }

        /// <summary>RED before DW-526: the balance-report site registered NOTHING — no converters, no tolerance —
        /// so a suggestions payload with a trailing comma died as "Invalid JSON" while the identical deviation was
        /// accepted two methods away.</summary>
        [Fact]
        public void BalanceReportParse_ToleratesTrailingCommaAndComment()
        {
            string json =
                "{\n  // the analysis\n  \"suggestions\":[\n" +
                "    {\"unit_id\":\"grunt\",\"field\":\"attack_damage\",\"current\":10,\"proposed\":14,\"rationale\":\"weak\"},\n" +
                "  ]\n}";
            var (report, err) = LLMService.ValidateBalanceReport(json, BalanceCtx());

            Assert.Null(err);
            Assert.NotNull(report);
            Assert.Single(report!.Suggestions);
            Assert.Equal("attack_damage", report.Suggestions[0].Field);
        }

        /// <summary>A REAL syntax error still rejects — the shared posture widens the syntax accepted, it does not
        /// swallow malformed output (the diagnostic that made DW-526's leniency safe stays intact).</summary>
        [Fact]
        public void BalanceReportParse_StillRejectsMalformedJson()
        {
            var (report, err) = LLMService.ValidateBalanceReport("{\"suggestions\":[{\"unit_id\":", BalanceCtx());
            Assert.Null(report);
            Assert.Contains("Invalid JSON", err);
        }

        // ── The enum boundary now fails closed on generated scenarios ───────────────────────────────────────────

        /// <summary>RED before DW-526: the scenario site registered no enum converter, so <c>win_condition</c> fell
        /// through to the enum type's own attribute (<c>allowIntegerValues: true</c>) and a generated numeric enum
        /// silently resolved to whichever member holds that ordinal. The scenario FILE format has rejected that
        /// since DW-274; the generation path now matches it.</summary>
        [Fact]
        public void GeneratedScenario_NumericEnum_FailsClosed()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(",\"win_condition\":1"), MapCtx());

            Assert.Null(scenario);
            Assert.Contains("Invalid JSON", error);
            Assert.Contains("win_condition", error!);
        }

        /// <summary>The NAMED spelling the prompt asks for still parses — the tightening rejects only the numeric form.</summary>
        [Fact]
        public void GeneratedScenario_NamedEnum_StillParses()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(",\"win_condition\":\"EliminateAllUnits\""), MapCtx());

            Assert.Null(error);
            Assert.Equal(ProjectChimera.Core.Definitions.WinCondition.EliminateAllUnits, scenario!.WinCondition);
        }

        /// <summary>
        /// RED before DW-526 for the opposite reason: an enum WITHOUT a type-level attribute (here
        /// <see cref="DslValueType"/> / <see cref="VarScope"/>) had no string converter at all on the generation
        /// path, so a model that declared variables by NAME — the only spelling the scenario writer ever emits —
        /// could not be parsed at all. The shared posture's name-only converter covers every enum, attributed or not.
        /// </summary>
        [Fact]
        public void GeneratedScenario_UnattributedEnum_ParsesByName()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(",\"variables\":[{\"name\":\"score\",\"type\":\"Int\",\"scope\":\"PerPlayer\",\"initial\":0}]"),
                MapCtx());

            Assert.Null(error);
            Assert.NotNull(scenario!.Variables);
            Assert.Equal(DslValueType.Int, scenario.Variables![0].Type);
            Assert.Equal(VarScope.PerPlayer, scenario.Variables[0].Scope);
        }

        // ── The unmapped-tolerant half of the decided posture ────────────────────────────────────────────────────

        /// <summary>
        /// The draft posture is deliberately unmapped-TOLERANT (the DW-526 decision), matching the scenario file
        /// format the generated artifact must load back through: a hallucinated top-level key is ignored, not a hard
        /// reject that discards a whole paid generation over something the loader itself skips. Guards the
        /// over-tightening half — routing these sites at <c>ContentJson.Options</c> verbatim would fail this.
        /// </summary>
        [Fact]
        public void GeneratedScenario_StrayKey_IsIgnored_NotAHardReject()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(",\"designer_notes\":\"a contested middle\""), MapCtx());

            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        /// <summary>Same posture on the trigger path, so all three sites agree.</summary>
        [Fact]
        public void Trigger_StrayKey_IsIgnored_NotAHardReject()
        {
            string json = ValidTrigger.Replace("{\"name\":\"T\"", "{\"name\":\"T\",\"why\":\"opening move\"");
            var (trigger, error) = LLMService.Validate(json, TriggerCtx());

            Assert.Null(error);
            Assert.NotNull(trigger);
        }

        /// <summary>Case-insensitive property matching survives the migration — it is the one thing all three sites
        /// already agreed on and the reason a hand-rolled set existed at all.</summary>
        [Fact]
        public void ModelOutput_PropertyNamesStayCaseInsensitive()
        {
            string json =
                "{\"NAME\":\"T\",\"Events\":[{\"Type\":\"match_start\"}],\"conditions\":[]," +
                "\"ACTIONS\":[{\"type\":\"victory\",\"Faction\":0}]}";
            var (trigger, error) = LLMService.Validate(json, TriggerCtx());

            Assert.Null(error);
            Assert.Equal("T", trigger!.Name);
        }
    }
}
