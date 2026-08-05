#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 7.3 — <see cref="ScenarioData.Variables"/> / <see cref="ScenarioData.Timers"/> /
    /// <see cref="ScenarioData.TriggerGraphJson"/> serialize/deserialize round-trip (initial preserved as
    /// <c>Fixed.Raw</c>); and a scenario WITHOUT them serializes byte-identically to a pre-7.3 scenario (the
    /// omit-when-null + empty→null normalization keeps the bytes — and hence CanonicalModelHash/StartStateHash —
    /// unchanged: the Block-If protection).
    /// </summary>
    public class ScenarioVariableRoundTripTests
    {
        /// <summary>
        /// DW-523 - the PRODUCTION scenario options (<see cref="ContentJson.ScenarioOptions"/>), not a hand-rolled
        /// replica that was looser than the real loader on the enum axis and missing its widget converter. The
        /// DslValueType / VarScope round-trips below now go through the same enum posture the loader enforces.
        /// </summary>
        private static readonly JsonSerializerOptions Opt = ContentJson.ScenarioOptions;

        [Fact]
        public void Variables_And_Timers_RoundTrip_PreservingFixedRaw()
        {
            var model = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "score", Type = DslValueType.Int,   Scope = VarScope.PerPlayer, Initial = Fixed.FromInt(0) },
                    new ScenarioVariable { Name = "rate",  Type = DslValueType.Fixed, Scope = VarScope.Global,    Initial = Fixed.FromFloat(2.5f) },
                },
                Timers = new[] { new ScenarioTimer { Name = "clock", Seconds = Fixed.FromInt(30) } },
            };
            string json = ScenarioSerializer.Serialize(model);
            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);

            Assert.NotNull(back);
            Assert.Equal(2, back!.Variables!.Length);
            Assert.Equal("score", back.Variables[0].Name);
            Assert.Equal(DslValueType.Int, back.Variables[0].Type);
            Assert.Equal(VarScope.PerPlayer, back.Variables[0].Scope);
            Assert.Equal("rate", back.Variables[1].Name);
            Assert.Equal(DslValueType.Fixed, back.Variables[1].Type);
            // initial preserved EXACTLY as Fixed.Raw (2.5 → 163840).
            Assert.Equal(Fixed.FromFloat(2.5f).Raw, back.Variables[1].Initial.Raw);

            Assert.Single(back.Timers!);
            Assert.Equal("clock", back.Timers![0].Name);
            Assert.Equal(Fixed.FromInt(30).Raw, back.Timers[0].Seconds.Raw);
        }

        [Fact]
        public void TriggerGraphJson_RoundTrips()
        {
            const string graph = "{\"nodes\":[{\"id\":0,\"kind\":\"trigger\"}],\"exec_edges\":[],\"data_edges\":[]}";
            var model = new ScenarioData { TriggerGraphJson = graph };
            string json = ScenarioSerializer.Serialize(model);
            Assert.Contains("trigger_graph", json);

            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);
            Assert.NotNull(back);
            Assert.Equal(graph, back!.TriggerGraphJson);
        }

        [Fact]
        public void AbsentDeclarations_SerializeByteIdenticallyToPre73()
        {
            // A scenario that never touches the new fields...
            string baseline = ScenarioSerializer.Serialize(new ScenarioData());
            Assert.DoesNotContain("\"variables\"", baseline);
            Assert.DoesNotContain("\"timers\"", baseline);
            Assert.DoesNotContain("\"trigger_graph\"", baseline);

            // ...and one with EMPTY arrays + whitespace graph must serialize to the EXACT SAME bytes (empty→null
            // normalization at the serialize chokepoint). This is the byte-identity guarantee that keeps
            // CanonicalModelHash / StartStateHash from moving.
            string normalized = ScenarioSerializer.Serialize(new ScenarioData
            {
                Variables = System.Array.Empty<ScenarioVariable>(),
                Timers    = System.Array.Empty<ScenarioTimer>(),
                TriggerGraphJson = "   ",
            });
            Assert.Equal(baseline, normalized);
        }

        [Fact]
        public void Serialize_DoesNotMutateCallerModel()
        {
            // Serialize is a pure byte-source; the empty→null swap must be restored under try/finally.
            var model = new ScenarioData
            {
                Variables = System.Array.Empty<ScenarioVariable>(),
                Timers    = System.Array.Empty<ScenarioTimer>(),
                TriggerGraphJson = "  ",
            };
            _ = ScenarioSerializer.Serialize(model);
            Assert.NotNull(model.Variables); // restored (still an empty array, not nulled)
            Assert.NotNull(model.Timers);
            Assert.Equal("  ", model.TriggerGraphJson);
        }
    }
}
