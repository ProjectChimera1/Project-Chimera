#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 7.14 — units for the pure objective resolver + win→text map (the single deterministic source both the
    /// sim reserved-var declaration and the presentation surfaces consume).
    /// </summary>
    public class ObjectiveResolverTests
    {
        [Theory]
        [InlineData(WinCondition.DestroyAllBuildings, "Destroy all enemy buildings")]
        [InlineData(WinCondition.EliminateAllUnits,   "Eliminate all enemy units")]
        public void WinObjectiveText_BuiltInEnum_MapsToItsLine(WinCondition win, string expected)
            => Assert.Equal(expected, WinObjectiveText.For(win, null));

        [Theory]
        [InlineData(WinPresetKind.KingOfTheHill,       "Hold the contested region")]
        [InlineData(WinPresetKind.TimedSurvival,       "Survive until the timer expires")]
        [InlineData(WinPresetKind.Assassination,       "Eliminate the enemy leader")]
        [InlineData(WinPresetKind.LandmarkDestruction, "Destroy the enemy landmark")]
        public void WinObjectiveText_Preset_WinsOverBuiltIn(WinPresetKind preset, string expected)
        {
            // A preset takes precedence over the bare built-in enum.
            var spec = new WinConditionSpec { Preset = preset };
            Assert.Equal(expected, WinObjectiveText.For(WinCondition.DestroyAllBuildings, spec));
        }

        [Fact]
        public void WinObjectiveText_NonePreset_FallsThroughToBuiltIn()
        {
            var spec = new WinConditionSpec { Preset = WinPresetKind.None };
            Assert.Equal("Eliminate all enemy units", WinObjectiveText.For(WinCondition.EliminateAllUnits, spec));
        }

        [Fact]
        public void Resolve_NoAuthoredObjectives_SynthesizesExactlyOnePresentationOnlyDefault()
        {
            var scenario = new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings };
            ResolvedObjective[] resolved = ObjectiveResolver.Resolve(scenario);

            Assert.Single(resolved);
            Assert.Equal(ObjectiveResolver.DefaultObjectiveId, resolved[0].Id);
            Assert.Equal("Destroy all enemy buildings", resolved[0].Title);
            Assert.Equal(ObjectiveState.Active, resolved[0].InitialState);
            // The synthesized default is PRESENTATION-ONLY — it declares NO folded reserved var (so an objective-less
            // scenario adds no folded state and its SimChecksum stays byte-identical: no bump, no world-golden churn).
            Assert.False(resolved[0].HasReservedVar);
        }

        [Fact]
        public void Resolve_NullScenario_StillYieldsGenericDefault_NeverZero()
        {
            ResolvedObjective[] resolved = ObjectiveResolver.Resolve(null);
            Assert.Single(resolved);
            Assert.False(resolved[0].HasReservedVar);
        }

        [Fact]
        public void Resolve_AuthoredObjectives_MappedInOrder_EachWithAReservedVar()
        {
            var scenario = new ScenarioData
            {
                Objectives = new[]
                {
                    new ScenarioObjective { Id = "kill_boss", Title = "Kill the boss", InitialState = ObjectiveState.Active },
                    new ScenarioObjective { Id = "hold_hill", Title = "Hold the hill", InitialState = ObjectiveState.Hidden },
                },
            };
            ResolvedObjective[] resolved = ObjectiveResolver.Resolve(scenario);

            Assert.Equal(2, resolved.Length);
            Assert.Equal("kill_boss", resolved[0].Id);
            Assert.Equal("hold_hill", resolved[1].Id);
            Assert.Equal(ObjectiveState.Hidden, resolved[1].InitialState);
            Assert.True(resolved[0].HasReservedVar);
            Assert.True(resolved[1].HasReservedVar);
            Assert.Equal("objective:kill_boss", resolved[0].ReservedVarName);
        }
    }
}
