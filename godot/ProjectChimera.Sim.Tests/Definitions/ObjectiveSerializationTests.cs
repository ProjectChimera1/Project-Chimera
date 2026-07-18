#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 7.14 — <see cref="ScenarioData.Objectives"/> serialize/deserialize round-trip, plus the byte-identity
    /// guarantee: a scenario WITHOUT authored objectives serializes with NO "objectives" key (omit-when-null +
    /// empty→null normalization at the serialize chokepoint), so CanonicalModelHash/StartStateHash never move.
    /// </summary>
    public class ObjectiveSerializationTests
    {
        private static readonly JsonSerializerOptions Opt = new()
        {
            Converters = { new JsonStringEnumConverter() },
        };

        [Fact]
        public void Objectives_RoundTrip_PreservingFields()
        {
            var model = new ScenarioData
            {
                Objectives = new[]
                {
                    new ScenarioObjective { Id = "kill_boss", Title = "Kill the boss", Description = "Find and slay it", InitialState = ObjectiveState.Active },
                    new ScenarioObjective { Id = "hold_hill", Title = "Hold the hill", InitialState = ObjectiveState.Hidden },
                },
            };
            string json = ScenarioSerializer.Serialize(model);
            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);

            Assert.NotNull(back);
            Assert.NotNull(back!.Objectives);
            Assert.Equal(2, back.Objectives!.Length);
            Assert.Equal("kill_boss", back.Objectives[0].Id);
            Assert.Equal("Kill the boss", back.Objectives[0].Title);
            Assert.Equal("Find and slay it", back.Objectives[0].Description);
            Assert.Equal(ObjectiveState.Active, back.Objectives[0].InitialState);
            Assert.Equal(ObjectiveState.Hidden, back.Objectives[1].InitialState);
            Assert.Null(back.Objectives[1].Description); // omit-when-null round-trips absent
        }

        [Fact]
        public void NoObjectives_SerializesWithoutTheKey_ByteIdenticalToPre714()
        {
            var withNull  = new ScenarioData { Objectives = null };
            var withEmpty = new ScenarioData { Objectives = System.Array.Empty<ScenarioObjective>() };

            string jsonNull  = ScenarioSerializer.Serialize(withNull);
            string jsonEmpty = ScenarioSerializer.Serialize(withEmpty);

            Assert.DoesNotContain("\"objectives\"", jsonNull);
            Assert.Equal(jsonNull, jsonEmpty); // empty→null normalization ⇒ byte-identical
        }
    }
}
