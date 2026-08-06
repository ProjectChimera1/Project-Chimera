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
        /// <summary>
        /// DW-523 - the PRODUCTION scenario options, not a hand-rolled replica of them. This file used to declare its
        /// own <see cref="JsonSerializerOptions"/> which was strictly LOOSER than the real loader (default
        /// <see cref="JsonStringEnumConverter"/>, so integer enums were accepted; no widget converter; no comment /
        /// trailing-comma handling), so the round-trips below asserted a format the loader does not actually use.
        /// Pointing at the shared instance means a converter or strictness change at the <see cref="ContentJson"/>
        /// choke point reaches this suite the moment it lands.
        /// </summary>
        private static readonly JsonSerializerOptions Opt = ContentJson.ScenarioOptions;

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

        /// <summary>
        /// DW-523 (the half that FAILS without the fix). A NUMERIC objective state must fail closed at parse, exactly
        /// as it does through the real loader (DW-274 made the scenario posture <c>allowIntegerValues: false</c>).
        /// With this file's old hand-rolled options — a bare <c>new JsonStringEnumConverter()</c>, integer values
        /// allowed — <c>"initial_state": 1</c> parsed happily as whichever member holds ordinal 1, so the round-trip
        /// above was pinning a format strictly looser than the one production reads.
        /// </summary>
        [Fact]
        public void NumericObjectiveState_FailsClosed_MatchingTheRealLoader()
        {
            const string json = "{\"objectives\":[{\"id\":\"o\",\"title\":\"T\",\"initial_state\":1}]}";
            JsonException ex = Assert.Throws<JsonException>(
                () => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("initial_state", ex.Message);
        }

        /// <summary>The NAMED spelling every shipped/authored scenario uses still round-trips — the tightening
        /// rejects only the numeric form.</summary>
        [Fact]
        public void NamedObjectiveState_StillParses()
        {
            const string json = "{\"objectives\":[{\"id\":\"o\",\"title\":\"T\",\"initial_state\":\"Hidden\"}]}";
            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);
            Assert.Equal(ObjectiveState.Hidden, back!.Objectives![0].InitialState);
        }
    }
}
