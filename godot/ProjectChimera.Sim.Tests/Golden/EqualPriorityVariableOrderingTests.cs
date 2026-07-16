#nullable enable
using ProjectChimera.Dsl;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 7.1 (AC2) — two EQUAL-priority set_variable triggers writing the SAME variable to different values
    /// resolve their last-writer by ASCENDING DECLARATION INDEX, deterministically. This covers the shared-variable
    /// ordering surface WITHOUT depending on the (not-yet-implemented, Story 7.3) SimChecksum fold of variables: it
    /// drives a real ScenarioDirector and asserts an OBSERVABLE outcome (a variable_comparison-gated
    /// display_message), identical across two fresh headless runs.
    ///
    /// The two writers have IDENTICAL priority, so ONLY the declaration-index tiebreak decides who writes last:
    /// declaration index 1 (value 20) runs after index 0 (value 10) in the same tick, so the variable ends at 20. A
    /// gate on ==20 fires; a gate on ==10 does not (the negative control proving it is the HIGHER-index writer, not
    /// the lower).
    /// </summary>
    public class EqualPriorityVariableOrderingTests
    {
        private const string ObserverFired = "OBSERVER_FIRED";

        /// <summary>
        /// Two equal-priority writers set "v" to 10 (declaration index 0) then 20 (declaration index 1); a third,
        /// lower-priority observer evaluates AFTER both writers in the same tick and fires only if v == expected.
        /// Returns whether the observer's display_message fired.
        /// </summary>
        private static bool ObserverFires(int expected)
        {
            var writer0 = new TriggerDefinition
            {
                Name = "writer0", Priority = 5,
                Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "v", Value = 10 } },
            };
            var writer1 = new TriggerDefinition
            {
                Name = "writer1", Priority = 5, // SAME priority — only the declaration index breaks the tie
                Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "v", Value = 20 } },
            };
            var observer = new TriggerDefinition
            {
                Name = "observer", Priority = 1, // lower priority → evaluates AFTER both writers this tick
                Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                Conditions = new[] { new TriggerCondition { Type = "variable_comparison", Variable = "v", Operator = "==", Value = expected } },
                Actions    = new[] { new TriggerAction { Type = "display_message", Text = ObserverFired } },
            };

            bool fired = false;
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.OnDisplayMessage = (text, _) => { if (text == ObserverFired) fired = true; };
            director.LoadScenario(new ScenarioData { Triggers = new[] { writer0, writer1, observer } });
            director.Tick(new EntityWorld(), Fixed.One);
            return fired;
        }

        [Fact]
        public void LastWriter_FollowsDeclarationIndex_DeterministicallyAcrossFreshRuns()
        {
            // Declaration index 1 (value 20) writes last → the variable ends at 20, identically on two fresh runs.
            Assert.True(ObserverFires(20), "The higher-declaration-index writer (value 20) must win the tie.");
            Assert.True(ObserverFires(20), "Deterministic: a second fresh ScenarioDirector run resolves identically.");

            // Negative control: the LOWER-index writer (value 10) did NOT win, so a ==10 gate never fires.
            Assert.False(ObserverFires(10), "The lower-declaration-index writer (value 10) must NOT be the last writer.");
        }
    }
}
