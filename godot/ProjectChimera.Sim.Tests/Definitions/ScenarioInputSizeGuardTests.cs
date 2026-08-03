#nullable enable
using System;
using System.IO;
using System.Text.Json;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-366 — the upstream byte/size guard on UNTRUSTED scenario input (the recorded 2026-07-30 decision:
    /// cap input size before deserialization — cheapest, uniform). Every per-collection cap (widgets, triggers,
    /// regions, units) runs only AFTER System.Text.Json has materialized the full collection, so a pathological
    /// flat array of millions of elements used to allocate before rejection. The guard closes that gap at BOTH
    /// untrusted entries:
    ///   • <see cref="ScenarioSerializer.LoadFromFile"/> — checked against the ON-DISK length before the file is
    ///     even read (no string, no collections);
    ///   • <c>LLMService.ValidateScenario</c> — the AI-generated string's UTF-8 byte count, before pass-1 schema
    ///     deserialization.
    /// Every reject is a fail-closed <see cref="JsonException"/> naming
    /// <see cref="ScenarioSerializer.MaxScenarioFileBytes"/> and the input source. These tests FAIL without the
    /// DW-366 guard (an over-cap file would surface a generic parse error instead, or worse, parse fully).
    /// </summary>
    public class ScenarioInputSizeGuardTests
    {
        // ── The guard seam itself (boundary) ────────────────────────────────────

        [Fact]
        public void Guard_AtCap_Passes()
        {
            ScenarioSerializer.GuardScenarioInputSize(ScenarioSerializer.MaxScenarioFileBytes, "boundary");
        }

        [Fact]
        public void Guard_OverCap_Throws_NamingConstantAndSource()
        {
            var ex = Assert.Throws<JsonException>(() =>
                ScenarioSerializer.GuardScenarioInputSize(ScenarioSerializer.MaxScenarioFileBytes + 1, "some-source"));
            Assert.Contains("MaxScenarioFileBytes", ex.Message);
            Assert.Contains("some-source", ex.Message);
        }

        // ── LoadFromFile adoption (the on-disk untrusted entry) ─────────────────

        [Fact]
        public void LoadFromFile_OverCapFile_Rejects_BeforeParse()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_dw366_{Guid.NewGuid():N}.json");
            try
            {
                // cap+1 zero bytes — NOT valid JSON. If the guard were missing (or ran after the read/parse), the
                // parser would throw its own JsonException about an invalid token, which does NOT name the
                // constant — so the Contains assertions prove the SIZE guard tripped, before any parse.
                File.WriteAllBytes(path, new byte[(int)(ScenarioSerializer.MaxScenarioFileBytes + 1)]);
                var ex = Assert.Throws<JsonException>(() => ScenarioSerializer.LoadFromFile(path));
                Assert.Contains("MaxScenarioFileBytes", ex.Message);
                Assert.Contains(path, ex.Message); // located: the message names the offending file
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void LoadFromFile_UnderCap_StillRoundTrips()
        {
            // The guard must not false-positive on legitimate content (the largest shipped map is ~4 KB).
            string path = Path.Combine(Path.GetTempPath(), $"chimera_dw366_{Guid.NewGuid():N}.json");
            try
            {
                ScenarioSerializer.SaveToFile(new ScenarioData { MapBounds = 120f }, path);
                ScenarioData? loaded = ScenarioSerializer.LoadFromFile(path);
                Assert.NotNull(loaded);
                Assert.Equal(120f, loaded!.MapBounds);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ── LLM adoption (the AI-generated untrusted entry) ─────────────────────

        [Fact]
        public void ValidateScenario_OverCapGeneratedJson_FailsPassOne_NamingTheCap()
        {
            // An over-cap AI-generated string must be rejected by the DW-366 guard INSIDE pass 1 (surfaced through
            // the (null, error) contract), before deserialization materializes anything.
            string oversized = new string('x', (int)(ScenarioSerializer.MaxScenarioFileBytes + 1));
            (ScenarioData? scenario, string? error) = LLMService.ValidateScenario(oversized, new MapGeneratorContext());
            Assert.Null(scenario);
            Assert.NotNull(error);
            Assert.Contains("MaxScenarioFileBytes", error!);
        }
    }
}
