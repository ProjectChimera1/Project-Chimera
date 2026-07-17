#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 7.8 — proves the presentation READ RAIL is driven END-TO-END by the sim tick: a
    /// <see cref="ScenarioDirector"/> handed a <see cref="DslVarReadback"/> (via <c>SetReadback</c>, exactly as
    /// <c>SimulationHost</c> wires it) publishes a version-stamped copy of post-tick <c>DslVarTable</c> state at
    /// each tick boundary. A declared Global scalar mutated by a first-tick trigger surfaces on the readback with a
    /// bumped version after ONE <c>Tick</c>. Complements <c>DslVarReadbackTests</c> (which drives the readback in
    /// isolation) by proving the director's per-tick publish wiring.
    /// </summary>
    public class ScenarioDirectorReadbackTests
    {
        [Fact]
        public void Tick_PublishesPostTickValue_WithBumpedVersion()
        {
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var readback = new DslVarReadback();
            director.SetReadback(readback); // the SimulationHost wiring

            director.LoadScenario(new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "seed",
                        Enabled = true,
                        Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "score", Value = 42, Faction = 0 } },
                    },
                },
            });

            // LoadScenario re-inits the readback: initial value 0 at version 1 (before any tick).
            Assert.True(readback.TryGetScalar("score", 0, out _, out int v0, out _, out uint ver0));
            Assert.Equal(0, v0);
            Assert.Equal(1u, ver0);

            // One tick — the match_start trigger sets score=42, then the director publishes at the tick boundary.
            director.Tick(new EntityWorld(), Fixed.FromInt(1));

            Assert.True(readback.TryGetScalar("score", 0, out _, out int v1, out _, out uint ver1));
            Assert.Equal(42, v1);          // post-tick value reflected on the read rail
            Assert.Equal(2u, ver1);        // version bumped by the change
        }
    }
}
