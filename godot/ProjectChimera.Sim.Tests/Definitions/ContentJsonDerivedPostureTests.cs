#nullable enable
using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// The json-options-choke-point bundle (DW-523 / DW-524 / DW-526) — the follow-on to
    /// <see cref="ContentJsonUnificationTests"/> (DW-274). DW-274 unified the three LOADER postures into
    /// <see cref="ContentJson"/>; three replica families survived it and are closed here:
    ///
    ///   • DW-523 — six scenario Tier-1 suites hand-declared a <see cref="JsonSerializerOptions"/> that was strictly
    ///     LOOSER than the real loader (integer enums accepted, converters missing), so they under-constrained the
    ///     very format they claim to pin. They now hold the SHARED instance.
    ///   • DW-524 — <see cref="ItemWriter.Options"/> and <see cref="DslJson.Options"/> hand-copied the strict
    ///     converter list ("mirrors ContentJson.Options" == a hand-maintained duplicate), so a converter added at the
    ///     choke point never reached them. They now derive from <see cref="ContentJson.NewStrict"/>.
    ///   • DW-526 — the untrusted-model-output posture now exists as one shared object
    ///     (<see cref="ContentJson.ModelOutputOptions"/>); its behavior is covered in
    ///     <c>ProjectChimera.Sim.Tests.AI.LlmModelOutputOptionsTests</c>, its shape here.
    ///
    /// These are drift guards: each one fails the moment a future edit re-introduces a replica or reorders a
    /// converter list (order is behavior — first match wins).
    /// </summary>
    public class ContentJsonDerivedPostureTests
    {
        // ── DW-523: the six scenario suites hold the shared instance, not a replica ──────────────────────────────

        /// <summary>
        /// Every scenario Tier-1 suite's <c>Opt</c> field IS <see cref="ContentJson.ScenarioOptions"/> — reference
        /// identity, so no copy can drift and a converter added at the choke point reaches all six at once.
        /// FAILS without the fix: each was its own <c>new JsonSerializerOptions { ... }</c>.
        /// </summary>
        [Theory]
        [InlineData(typeof(ObjectiveSerializationTests))]
        [InlineData(typeof(ScenarioDataRegionsTests))]
        [InlineData(typeof(CustomUiSerializationTests))]
        [InlineData(typeof(CustomUiButtonSerializationTests))]
        [InlineData(typeof(ScenarioItemRoundTripTests))]
        [InlineData(typeof(ScenarioVariableRoundTripTests))]
        public void ScenarioTestSuites_UseTheSharedScenarioOptions(Type suite)
        {
            FieldInfo? f = suite.GetField("Opt", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(f != null, $"{suite.Name} no longer declares a static 'Opt' field — update this guard.");
            Assert.Same(ContentJson.ScenarioOptions, f!.GetValue(null));
        }

        // ── DW-524: the two write/graph postures derive from the strict one ──────────────────────────────────────

        /// <summary>
        /// <see cref="ItemWriter.Options"/> carries the strict posture's converter set in the strict posture's ORDER,
        /// plus its two write deltas — and, the half that FAILS without the fix, the shared authoring base
        /// (<c>ReadCommentHandling.Skip</c> + <c>AllowTrailingCommas</c>), which the hand-declared replica never had.
        /// </summary>
        [Fact]
        public void ItemWriterOptions_DeriveFromTheStrictPosture()
        {
            Assert.Equal(ConverterTypes(ContentJson.Options), ConverterTypes(ItemWriter.Options));

            // The shared base actually reached it (the replica set neither of these).
            Assert.Equal(JsonCommentHandling.Skip, ItemWriter.Options.ReadCommentHandling);
            Assert.True(ItemWriter.Options.AllowTrailingCommas);

            // ...and the write deltas that make it a WRITER are still there.
            Assert.True(ItemWriter.Options.WriteIndented);
            Assert.Equal(JsonIgnoreCondition.WhenWritingDefault, ItemWriter.Options.DefaultIgnoreCondition);
        }

        /// <summary>The write BYTES are unchanged by the derivation: an item still serializes indented, with enums by
        /// NAME and defaults omitted. (The inherited read-only settings are inert on a write path — this is the
        /// assertion that says so out loud.)</summary>
        [Fact]
        public void ItemWriterBytes_StayIndented_NameEnumed_AndDefaultOmitting()
        {
            string json = ItemWriter.Serialize(new ItemDefinition { Id = "ring", DisplayName = "Ring" });

            Assert.Contains("\n  \"id\": \"ring\"", json);   // indented, 2-space, space after ':'
            Assert.DoesNotContain(": 0,", json);              // zero-valued deltas/costs omitted
            Assert.DoesNotContain("\"effect\"", json);        // null effect omitted
        }

        /// <summary>
        /// <see cref="DslJson.Options"/> is the strict posture PLUS the three trigger-IR converters, APPENDED in
        /// their original order — never interleaved, because first-match-wins order decides how a node/edge
        /// serializes and the scenario's <c>trigger_graph_json</c> bytes are folded into CanonicalModelHash.
        /// </summary>
        [Fact]
        public void DslJsonOptions_DeriveFromTheStrictPosture_ThenAppendTheIrConverters()
        {
            Assert.Equal(
                new[]
                {
                    typeof(JsonStringEnumConverter), typeof(FixedJsonConverter), typeof(EffectNodeJsonConverter),
                    typeof(NodeBaseJsonConverter), typeof(DataEdgeJsonConverter), typeof(ExecEdgeJsonConverter),
                },
                ConverterTypes(DslJson.Options));

            // The strict half is a PREFIX of the graph half — this is what "derives from" means mechanically.
            Type[] strict = ConverterTypes(ContentJson.Options);
            Type[] graph  = ConverterTypes(DslJson.Options);
            for (int i = 0; i < strict.Length; i++)
                Assert.Equal(strict[i], graph[i]);

            Assert.Equal(JsonUnmappedMemberHandling.Disallow, DslJson.Options.UnmappedMemberHandling);
            Assert.Equal(JsonCommentHandling.Skip, DslJson.Options.ReadCommentHandling);
            Assert.True(DslJson.Options.AllowTrailingCommas);
            Assert.True(DslJson.Options.WriteIndented);
        }

        /// <summary>The derivation seam hands back a FRESH, still-mutable object every call — deriving a posture must
        /// never hand out (and then mutate) a shared instance, which would throw once the shared one has been used.</summary>
        [Fact]
        public void NewStrict_ReturnsAFreshMutableInstance_NotTheSharedOne()
        {
            JsonSerializerOptions a = ContentJson.NewStrict();
            JsonSerializerOptions b = ContentJson.NewStrict();

            Assert.NotSame(a, b);
            Assert.NotSame(ContentJson.Options, a);
            a.WriteIndented = true;                       // mutable: would throw on a used shared instance
            Assert.False(ContentJson.Options.WriteIndented);
            Assert.False(b.WriteIndented);
            Assert.Equal(ConverterTypes(ContentJson.Options), ConverterTypes(a));

            JsonSerializerOptions baseOpts = ContentJson.NewBase();
            Assert.NotSame(ContentJson.LenientOptions, baseOpts);
            Assert.Empty(baseOpts.Converters);
            Assert.Equal(JsonCommentHandling.Skip, baseOpts.ReadCommentHandling);
            Assert.True(baseOpts.AllowTrailingCommas);
        }

        // ── DW-526: the model-output posture's shape ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The untrusted-model-output posture is the strict one plus exactly two documented deltas: case-insensitive
        /// names, and unmapped-TOLERANT (matching the scenario file format the generated artifact must load back
        /// through). Everything fail-closed survives — most importantly the <see cref="FixedJsonConverter"/>, whose
        /// absence at the balance-report site is the concrete defect DW-526 names.
        /// </summary>
        [Fact]
        public void ModelOutputOptions_AreTheStrictPosturePlusTwoDeltas()
        {
            Assert.Equal(ConverterTypes(ContentJson.Options), ConverterTypes(ContentJson.ModelOutputOptions));
            Assert.True(ContentJson.ModelOutputOptions.PropertyNameCaseInsensitive);
            Assert.Equal(JsonUnmappedMemberHandling.Skip, ContentJson.ModelOutputOptions.UnmappedMemberHandling);

            // Inherited from the shared base: a model's two most common syntax deviations are tolerated.
            Assert.Equal(JsonCommentHandling.Skip, ContentJson.ModelOutputOptions.ReadCommentHandling);
            Assert.True(ContentJson.ModelOutputOptions.AllowTrailingCommas);

            // Not a writer: the generated string is parsed, never emitted through this posture.
            Assert.False(ContentJson.ModelOutputOptions.WriteIndented);
        }

        /// <summary>The strict posture itself is untouched by all three derivations (the DW-274 fence, re-asserted
        /// here because three new consumers now build off it).</summary>
        [Fact]
        public void StrictPosture_IsUnchangedByTheDerivations()
        {
            Assert.Equal(JsonUnmappedMemberHandling.Disallow, ContentJson.Options.UnmappedMemberHandling);
            Assert.False(ContentJson.Options.WriteIndented);
            Assert.False(ContentJson.Options.PropertyNameCaseInsensitive);
            Assert.Equal(
                new[] { typeof(JsonStringEnumConverter), typeof(FixedJsonConverter), typeof(EffectNodeJsonConverter) },
                ConverterTypes(ContentJson.Options));
        }

        private static Type[] ConverterTypes(JsonSerializerOptions o)
        {
            var t = new Type[o.Converters.Count];
            for (int i = 0; i < o.Converters.Count; i++) t[i] = o.Converters[i].GetType();
            return t;
        }
    }
}
