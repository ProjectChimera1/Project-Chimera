#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-274 (Story 15.6) — <see cref="ScenarioSerializer"/> and <see cref="FactionDefinition"/> no longer declare
    /// their own private <see cref="JsonSerializerOptions"/>; all three content postures are declared in
    /// <see cref="ContentJson"/> and derive from one shared base. These tests pin the three things that migration is
    /// only allowed to do:
    ///
    ///   1. CLOSE the drift: the shared authoring half (comments + trailing commas) actually reaches every posture,
    ///      and the faction loader is literally the shared instance — no replica to drift.
    ///   2. TIGHTEN the scenario enum boundary to <see cref="ContentJson"/>'s posture: a hand-edited NUMERIC enum in
    ///      a scenario file now fails closed at parse instead of silently resolving to whichever member happens to
    ///      hold that ordinal. (This is the half that fails without the fix.)
    ///   3. CHANGE NOTHING ELSE: the scenario bytes stay indented + LF + name-only enums (so every golden pinned on
    ///      them holds), unknown top-level scenario keys stay a forward-compat no-op, the faction loader stays
    ///      lenient, and the strict ability posture keeps its exact converter set and order.
    ///
    /// (3) is deliberately a fence against the WRONG migration — pointing either loader at
    /// <see cref="ContentJson.Options"/> itself would minify the scenario bytes (moving goldens) and hard-reject
    /// shipped faction content.
    /// </summary>
    public class ContentJsonUnificationTests
    {
        // ── 1. the drift is structurally closed ──────────────────────────────────────────────────────────────────

        /// <summary>Every content posture shares <see cref="ContentJson"/>'s authoring base. This is the guard the
        /// migration made possible at all: before it, the scenario/faction option objects were unreachable replicas
        /// (one private, one hand-declared) and no test could assert they agreed with the canonical one.</summary>
        [Fact]
        public void EveryContentPosture_SharesTheAuthoringBase()
        {
            foreach (JsonSerializerOptions o in new[]
                     { ContentJson.Options, ContentJson.ScenarioOptions, ContentJson.LenientOptions })
            {
                Assert.Equal(JsonCommentHandling.Skip, o.ReadCommentHandling);
                Assert.True(o.AllowTrailingCommas);
            }
        }

        /// <summary>The faction loader's public options handle is the SHARED instance, not a copy of it — so a
        /// converter or setting added to the lenient posture reaches every faction/unit reader at once.</summary>
        [Fact]
        public void FactionJsonOptions_IsTheSharedLenientInstance() =>
            Assert.Same(ContentJson.LenientOptions, FactionDefinition.JsonOptions);

        /// <summary>The shared base is not decorative: the scenario READ path really does tolerate comments and a
        /// trailing comma, through the shared object rather than a private duplicate of the setting.</summary>
        [Fact]
        public void ScenarioLoad_ToleratesCommentsAndTrailingCommas()
        {
            ScenarioData? s = LoadScenarioJson(
                "{\n  // a note from the author\n  \"id\": \"m\",\n  \"display_name\": \"M\",\n}");
            Assert.NotNull(s);
            Assert.Equal("m", s!.Id);
        }

        // ── 2. the scenario enum boundary now fails closed (FAILS without the fix) ───────────────────────────────

        /// <summary>A hand-edited NUMERIC enum in a scenario file is rejected at parse. Before DW-274 the scenario
        /// options used the DEFAULT <see cref="JsonStringEnumConverter"/> (<c>allowIntegerValues: true</c>), so
        /// <c>"win_condition": 1</c> loaded silently as whichever member holds ordinal 1 — the exact silent-miscode
        /// class <see cref="ContentJson.Options"/> has always rejected for abilities.
        /// <para>DW-533 updated the CHANNEL, not the boundary: the rejection now surfaces as the null/fallback contract
        /// both production callers code to, with the located reason on the <c>out</c> overload, instead of an exception
        /// escaping <c>LoadFromFile</c> and aborting boot. What DW-274 pins — the numeric spelling never resolving to a
        /// member — is asserted more directly here than the old <c>Assert.Throws</c> did.</para></summary>
        [Fact]
        public void NumericEnum_InScenarioJson_FailsClosed()
        {
            ScenarioData? s = LoadScenarioJson(
                "{\"id\":\"m\",\"display_name\":\"M\",\"win_condition\":1}", out string? parseError);

            Assert.Null(s);                                  // never silently miscoded into a model
            Assert.NotNull(parseError);                       // …and never mistaken for "no scenario here"
            Assert.Contains("win_condition", parseError!);    // located at the offending property
        }

        /// <summary>Same fail-closed boundary one level down — a numeric <c>preset</c> inside the win-condition spec
        /// (the nested POCO the scenario converter set also covers).</summary>
        [Fact]
        public void NumericEnum_InNestedScenarioSpec_FailsClosed()
        {
            ScenarioData? s = LoadScenarioJson(
                "{\"id\":\"m\",\"display_name\":\"M\",\"win_condition_spec\":{\"preset\":2}}", out string? parseError);

            Assert.Null(s);
            Assert.NotNull(parseError);
        }

        /// <summary>The named form is what every shipped map authors, and it still loads — the tightening rejects
        /// only the numeric spelling, never the canonical one.</summary>
        [Fact]
        public void NamedEnum_InScenarioJson_StillLoads()
        {
            ScenarioData? s = LoadScenarioJson(
                "{\"id\":\"m\",\"display_name\":\"M\",\"win_condition\":\"EliminateAllUnits\"}");
            Assert.Equal(WinCondition.EliminateAllUnits, s!.WinCondition);
        }

        /// <summary>Every SHIPPED scenario still parses through the migrated loader — the tightened enum boundary is
        /// proven against real content, not just hand-written fixtures.</summary>
        [Fact]
        public void EveryShippedScenario_StillLoads()
        {
            string dir = ResolveShippedDir("scenarios");
            string[] files = Directory.GetFiles(dir, "*.json");
            Assert.NotEmpty(files);
            foreach (string f in files)
                Assert.NotNull(ScenarioSerializer.LoadFromFile(f));
        }

        // ── 3. nothing else moved ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The written scenario bytes keep the format every golden hash is pinned on: INDENTED (not the
        /// minified shape <see cref="ContentJson.Options"/> would produce), LF-only line endings (A4-E3), and enums
        /// by NAME. A naive "just use ContentJson.Options" migration fails this test.</summary>
        [Fact]
        public void ScenarioBytes_StayIndented_LfOnly_AndNameEnumed()
        {
            string json = ScenarioSerializer.Serialize(Model());

            Assert.Contains("\n  \"id\": \"m\"", json);          // indented, 2-space, with the space after ':'
            Assert.DoesNotContain("\r", json);                    // LF-only
            Assert.Contains("\"win_condition\": \"EliminateAllUnits\"", json);
            Assert.DoesNotContain("\"win_condition\": 1", json);
        }

        /// <summary>The scenario format still round-trips through the REAL loader byte-identically — the migration
        /// changed neither direction for content the enums actually define.</summary>
        [Fact]
        public void ScenarioBytes_RoundTripThroughTheRealLoader_Unchanged()
        {
            string first = ScenarioSerializer.Serialize(Model());
            string path  = Path.Combine(Path.GetTempPath(), $"chimera_dw274_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, first);
                ScenarioData? back = ScenarioSerializer.LoadFromFile(path);
                Assert.NotNull(back);
                Assert.Equal(first, ScenarioSerializer.Serialize(back!));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>An unknown TOP-LEVEL scenario key stays a forward-compat no-op (no <c>Disallow</c> on the
        /// scenario posture) — a newer build's map is rejected by the located <c>schema_version</c> validator check,
        /// never by a stray key at parse. Guards the over-tightening half of a careless unification.</summary>
        [Fact]
        public void UnknownScenarioKey_StaysAForwardCompatNoOp()
        {
            ScenarioData? s = LoadScenarioJson(
                "{\"id\":\"m\",\"display_name\":\"M\",\"a_key_from_the_future\":{\"x\":1}}");
            Assert.Equal("m", s!.Id);
        }

        /// <summary>The faction/unit loader stays LENIENT: an unknown top-level faction key still loads (the card
        /// panels and content-first descriptor fields depend on it) and the posture registers NO converters — the
        /// dual-path DTO constraint (plain <c>float</c>, plain string categories) is preserved.</summary>
        [Fact]
        public void FactionLoader_StaysLenient()
        {
            Assert.NotEqual(JsonUnmappedMemberHandling.Disallow, ContentJson.LenientOptions.UnmappedMemberHandling);
            Assert.Empty(ContentJson.LenientOptions.Converters);

            FactionDefinition? f = JsonSerializer.Deserialize<FactionDefinition>(
                "{\"id\":\"f\",\"display_name\":\"F\",\"a_key_from_the_future\":7}", FactionDefinition.JsonOptions);
            Assert.Equal("f", f!.Id);
        }

        /// <summary>Every SHIPPED faction file still deserializes through the (unchanged) lenient posture.</summary>
        [Fact]
        public void EveryShippedFaction_StillDeserializes()
        {
            string dir = ResolveShippedDir("factions");
            string[] files = Directory.GetFiles(dir, "*_faction.json");
            Assert.NotEmpty(files);
            foreach (string f in files)
                Assert.NotNull(JsonSerializer.Deserialize<FactionDefinition>(
                    File.ReadAllText(f), FactionDefinition.JsonOptions));
        }

        /// <summary>The canonical STRICT posture is untouched by the unification: <c>Disallow</c> plus exactly the
        /// enum → Fixed → effect-node converter set, in that ORDER (first match wins, so order is behavior).</summary>
        [Fact]
        public void StrictPosture_KeepsItsExactConverterSetAndOrder()
        {
            Assert.Equal(JsonUnmappedMemberHandling.Disallow, ContentJson.Options.UnmappedMemberHandling);
            Assert.False(ContentJson.Options.WriteIndented);
            Assert.Equal(
                new[] { typeof(JsonStringEnumConverter), typeof(FixedJsonConverter), typeof(EffectNodeJsonConverter) },
                Types(ContentJson.Options));
        }

        /// <summary>The scenario posture's converter set + order is the pre-migration one verbatim (enum → Fixed →
        /// widget). A widget graph must keep resolving through <see cref="WidgetBaseJsonConverter"/>, and no
        /// effect-node converter may leak in (that would change how a scenario's tree serializes).</summary>
        [Fact]
        public void ScenarioPosture_KeepsItsExactConverterSetAndOrder()
        {
            Assert.True(ContentJson.ScenarioOptions.WriteIndented);
            Assert.NotEqual(JsonUnmappedMemberHandling.Disallow, ContentJson.ScenarioOptions.UnmappedMemberHandling);
            Assert.Equal(
                new[] { typeof(JsonStringEnumConverter), typeof(FixedJsonConverter), typeof(WidgetBaseJsonConverter) },
                Types(ContentJson.ScenarioOptions));
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A small but feature-carrying model: a named enum, a slot, and a preset spec.</summary>
        private static ScenarioData Model() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.EliminateAllUnits,
            PlayerSlots  = new[] { new ScenarioPlayerSlot { Slot = 0 } },
            WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 90 },
        };

        /// <summary>Drive the REAL <see cref="ScenarioSerializer.LoadFromFile"/> (and therefore the real options
        /// object) over a JSON literal written to a temp file.</summary>
        private static ScenarioData? LoadScenarioJson(string json) => LoadScenarioJson(json, out _);

        /// <summary>As above, surfacing the DW-533 located parse-failure reason (null unless the content failed).</summary>
        private static ScenarioData? LoadScenarioJson(string json, out string? parseError)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_dw274_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            try { return ScenarioSerializer.LoadFromFile(path, out parseError); }
            finally { File.Delete(path); }
        }

        private static Type[] Types(JsonSerializerOptions o)
        {
            var t = new Type[o.Converters.Count];
            for (int i = 0; i < o.Converters.Count; i++) t[i] = o.Converters[i].GetType();
            return t;
        }

        /// <summary>Walk up from the test assembly to <c>resources/data/&lt;leaf&gt;</c> (the established
        /// shipped-content resolution pattern — see <c>BuildingDefinitionValidatorTests.ResolveDataPath</c>).</summary>
        private static string ResolveShippedDir(string leaf)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", leaf);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate resources/data/{leaf} above {AppContext.BaseDirectory}");
        }
    }
}
