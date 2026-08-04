#nullable enable
using ProjectChimera.AI;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Reported by Alec, 2026-08-04: a real Claude-key map generation died with
    /// <c>"Invalid JSON: '4' is an invalid end of a number. Expected a delimiter.
    /// Path: $.resource_nodes[5].max_gatherers | LineNumber: 16 | BytePositionInLine: 75"</c>.
    ///
    /// <para>Two separate failures behind that one line. (1) The parse was strict, so a model's two most common
    /// deviations — a trailing comma and a <c>//</c> comment — threw away an entire paid generation over pure
    /// syntax. (2) The raw response is retained NOWHERE, so the malformed token died with the exception and the
    /// failure could not be diagnosed after the fact; the reported error names a path and a byte offset but never
    /// the text. Leniency widens the SYNTAX accepted, never the values trusted — passes 2-7 still decide all
    /// content, which the clamp/slot suites cover.</para>
    /// </summary>
    public class LlmGeneratedJsonToleranceTests
    {
        private static MapGeneratorContext Default()
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        /// <summary>A structurally valid two-slot scenario; <paramref name="nodesJson"/> substitutes the resource
        /// node array so a test can inject exactly one syntax deviation and nothing else.</summary>
        private static string Scenario(string nodesJson) =>
            "{\"player_slots\":[" +
            "{\"slot\":0,\"faction_json\":\"a.json\",\"base_x\":-45,\"base_z\":0}," +
            "{\"slot\":1,\"faction_json\":\"b.json\",\"base_x\":45,\"base_z\":0}" +
            "],\"resource_nodes\":[" + nodesJson +
            "],\"buildings\":[{\"type\":\"CommandCenter\",\"slot\":0,\"x\":-45,\"z\":0}" +
            "],\"units\":[{\"unit_id\":\"worker\",\"slot\":0,\"x\":-42,\"z\":3}]}";

        private const string GoodNodes = "{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5},{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}";

        [Fact]
        public void CleanJson_StillParses()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(GoodNodes), Default());
            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        [Fact]
        public void TrailingComma_IsTolerated()
        {
            // Strict JSON rejects this outright; a model emits it constantly.
            var (scenario, error) = LLMService.ValidateScenario(Scenario(GoodNodes + ","), Default());
            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        [Fact]
        public void LineComment_IsTolerated()
        {
            string nodes = "{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5}, // the contested middle node\n"
                         + "{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}";
            var (scenario, error) = LLMService.ValidateScenario(Scenario(nodes), Default());
            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        [Fact]
        public void MalformedNumber_StillRejects_ButNamesTheOffendingText()
        {
            // The reported shape: a number the parser cannot end (here a leading zero, which is illegal JSON and
            // produces exactly "'4' is an invalid end of a number"). Leniency must NOT swallow a real syntax
            // error — it must still reject, but now the message carries the line so the cause is visible.
            string nodes = "{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5,\"max_gatherers\":04}";
            var (scenario, error) = LLMService.ValidateScenario(Scenario(nodes), Default());

            Assert.Null(scenario);
            Assert.NotNull(error);
            Assert.Contains("Invalid JSON", error);
            Assert.Contains("max_gatherers", error!);   // the offending text itself, not just a JSON path
        }

        [Fact]
        public void OffendingLine_IsQuotedFromTheRightLine_AndLengthCapped()
        {
            string json = "{\n  \"a\": 1,\n  \"b\": " + new string('x', 400) + "\n}";
            string described = LLMService.DescribeOffendingLine(json, 2); // 0-based -> reports "line 3"

            Assert.Contains("line 3", described);
            Assert.Contains("…", described);                 // capped, so a huge blob cannot flood the toast
            Assert.True(described.Length < 200, $"excerpt not capped: {described.Length} chars");
        }

        [Fact]
        public void OffendingLine_OutOfRangeOrMissing_DegradesToEmpty()
        {
            // Never let the diagnostic itself throw on a short/odd payload — it runs inside a catch block.
            Assert.Equal("", LLMService.DescribeOffendingLine("{}", 99));
            Assert.Equal("", LLMService.DescribeOffendingLine("{}", null));
            Assert.Equal("", LLMService.DescribeOffendingLine("{}", -1));
        }
    }
}
