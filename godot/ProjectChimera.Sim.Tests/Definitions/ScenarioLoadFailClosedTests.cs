#nullable enable
using System;
using System.IO;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-533 — <see cref="ScenarioSerializer.LoadFromFile"/> honours the <c>== null</c> "parse-failed" contract its
    /// two production callers code to.
    ///
    /// <para>
    /// DW-274 tightened the scenario enum boundary to <c>allowIntegerValues:false</c> (correct and deliberate) but
    /// left <c>LoadFromFile</c> with no try/catch, while both callers still branch on null:
    ///   • <c>ScenarioLoadPhase.LoadAndApplyScenario</c> — "Scenario not found or failed to parse … using defaults",
    ///     then boots the VALIDATED fallback mirror;
    ///   • <c>MainScene.BuildHeadlessServerSimHost</c> — "missing/parse-failed", then runs relay + quorum only.
    /// A hand-edited <c>user://</c> map carrying <c>"win_condition": 0</c> therefore threw a <c>JsonException</c> past
    /// <c>ScenarioLoadPhase</c> and past <c>ScenePhaseRunner.Run</c> (neither wraps), aborting <c>_Ready</c> mid-phase
    /// with a half-initialised scene — instead of taking the fallback path that exists precisely to survive it.
    /// </para>
    ///
    /// <para>
    /// Every test below FAILS without the fix (the call throws instead of returning null), except the two fences —
    /// <see cref="OverCapFile_StillThrows_TheParseCatchDoesNotSwallowTheDw366SizeGuard"/> and
    /// <see cref="MissingFile_ReturnsNull_WithNoParseError"/> — which pin what the fix must NOT change.
    /// </para>
    ///
    /// <para>
    /// Fail-closed is preserved: no test here asserts that malformed content LOADS. Each asserts it is REFUSED
    /// (null model, never a partial/miscoded one) and that the refusal carries a located reason, so "this file is
    /// broken" stays distinguishable from "there is no scenario here".
    /// </para>
    /// </summary>
    public class ScenarioLoadFailClosedTests
    {
        // ── the parse-failure surface now lands on the null/fallback contract ────────────────────────────────────

        /// <summary>Truncated/ill-formed JSON — System.Text.Json's own syntax rejection.</summary>
        [Fact]
        public void MalformedSyntax_ReturnsNull_WithALocatedReason()
        {
            ScenarioData? s = Load("{\"id\":\"m\",\"display_name\":", out string? err, out string path);

            Assert.Null(s);
            Assert.NotNull(err);
            Assert.Contains(path, err!); // located: the reason names the offending file
        }

        /// <summary>The DW-533 headline case: a numeric top-level enum under the DW-274 fail-closed posture.</summary>
        [Fact]
        public void NumericTopLevelEnum_ReturnsNull_WithALocatedReason()
        {
            ScenarioData? s = Load("{\"id\":\"m\",\"display_name\":\"M\",\"win_condition\":0}", out string? err, out _);

            Assert.Null(s);
            Assert.NotNull(err);
            Assert.Contains("win_condition", err!);
        }

        /// <summary>The nested spellings the ledger names explicitly — a numeric <c>type</c>/<c>scope</c> on a
        /// scenario variable declaration.</summary>
        [Theory]
        [InlineData("{\"id\":\"m\",\"display_name\":\"M\",\"variables\":[{\"name\":\"v\",\"type\":0}]}")]
        [InlineData("{\"id\":\"m\",\"display_name\":\"M\",\"variables\":[{\"name\":\"v\",\"scope\":1}]}")]
        [InlineData("{\"id\":\"m\",\"display_name\":\"M\",\"win_condition_spec\":{\"preset\":2}}")]
        public void NumericNestedEnum_ReturnsNull_WithALocatedReason(string json)
        {
            ScenarioData? s = Load(json, out string? err, out _);

            Assert.Null(s);
            Assert.NotNull(err);
        }

        /// <summary>A CONVERTER-thrown rejection (not System.Text.Json's own) routes to the same contract:
        /// <c>FixedJsonConverter</c> refusing a non-number for a <see cref="ProjectChimera.Core.Fixed"/> field.</summary>
        [Fact]
        public void RejectedFixedValue_ReturnsNull_WithALocatedReason()
        {
            ScenarioData? s = Load(
                "{\"id\":\"m\",\"display_name\":\"M\",\"timers\":[{\"name\":\"t\",\"seconds\":\"soon\"}]}",
                out string? err, out _);

            Assert.Null(s);
            Assert.NotNull(err);
        }

        /// <summary>The other converter on the scenario posture — <c>WidgetBaseJsonConverter</c>'s closed-kind gate —
        /// also lands on null rather than escaping. Together with the Fixed case above this covers the whole
        /// malformed-CONTENT surface, not just the enum spelling the ledger entry was filed on.</summary>
        [Fact]
        public void RejectedWidgetKind_ReturnsNull_WithALocatedReason()
        {
            ScenarioData? s = Load(
                "{\"id\":\"m\",\"display_name\":\"M\",\"custom_ui\":{\"widgets\":[{\"kind\":\"NotAWidget\",\"id\":1}]}}",
                out string? err, out _);

            Assert.Null(s);
            Assert.NotNull(err);
        }

        /// <summary>Fail-closed half: a malformed file must never yield a PARTIAL model carrying the keys that parsed
        /// before the failure. The refusal is total.</summary>
        [Fact]
        public void MalformedFile_NeverYieldsAPartialModel()
        {
            // "id" and "display_name" parse cleanly; the numeric enum that follows is what fails.
            ScenarioData? s = Load(
                "{\"id\":\"good_id\",\"display_name\":\"Good Name\",\"win_condition\":0}", out _, out _);

            Assert.Null(s);
        }

        // ── the two nulls stay distinguishable ──────────────────────────────────────────────────────────────────

        /// <summary>FENCE (passes before and after): a MISSING file is still null with NO parse error, so a caller can
        /// still tell "no scenario here" from "this scenario is broken". The fix must not blur the two.</summary>
        [Fact]
        public void MissingFile_ReturnsNull_WithNoParseError()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_dw533_absent_{Guid.NewGuid():N}.json");

            ScenarioData? s = ScenarioSerializer.LoadFromFile(path, out string? err);

            Assert.Null(s);
            Assert.Null(err);
        }

        /// <summary>A well-formed file still loads, and reports no parse error.</summary>
        [Fact]
        public void ValidScenario_StillLoads_WithNoParseError()
        {
            ScenarioData? s = Load(
                "{\"id\":\"m\",\"display_name\":\"M\",\"win_condition\":\"EliminateAllUnits\"}",
                out string? err, out _);

            Assert.NotNull(s);
            Assert.Equal("m", s!.Id);
            Assert.Equal(WinCondition.EliminateAllUnits, s.WinCondition);
            Assert.Null(err);
        }

        /// <summary>The JSON literal <c>null</c> is WELL-FORMED — it deserializes to a null model and must NOT be
        /// reported as a parse failure (the pre-existing "or if the JSON is the literal null" branch).</summary>
        [Fact]
        public void JsonLiteralNull_ReturnsNull_WithNoParseError()
        {
            ScenarioData? s = Load("null", out string? err, out _);

            Assert.Null(s);
            Assert.Null(err);
        }

        // ── DW-366 is not collateral damage ─────────────────────────────────────────────────────────────────────

        /// <summary>FENCE: the DW-366 over-cap size guard bounds HOSTILE input and still THROWS — it is checked
        /// against the on-disk length before the file is read, so the DW-533 catch (which wraps only the deserialize)
        /// must not swallow it. A fix that wrapped the whole method body would fail this.</summary>
        [Fact]
        public void OverCapFile_StillThrows_TheParseCatchDoesNotSwallowTheDw366SizeGuard()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_dw533_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllBytes(path, new byte[(int)(ScenarioSerializer.MaxScenarioFileBytes + 1)]);

                JsonException ex = Assert.Throws<JsonException>(() => ScenarioSerializer.LoadFromFile(path));
                Assert.Contains("MaxScenarioFileBytes", ex.Message);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // ── helper ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Drive the REAL loader over a JSON literal written to a temp file, returning the model, the located
        /// parse reason, and the path the reason should name.</summary>
        private static ScenarioData? Load(string json, out string? parseError, out string absolutePath)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_dw533_{Guid.NewGuid():N}.json");
            absolutePath = path;
            File.WriteAllText(path, json);
            try { return ScenarioSerializer.LoadFromFile(path, out parseError); }
            finally { File.Delete(path); }
        }
    }
}
