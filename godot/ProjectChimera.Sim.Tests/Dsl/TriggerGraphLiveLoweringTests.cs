#nullable enable
using ProjectChimera.Dsl;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // TriggerDefinition, ScenarioData
using ProjectChimera.Economy;           // BuildingStore, ResourceStore
using ProjectChimera.Sim.Tests.Golden;  // DW-501 — ReflectionProbe (null-CHECKED white-box lookups)
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.2/7.3 — verify the LIVE lowering, not just the pure transform. Story 7.3 replaced the 7.2 identity
    /// waypoint (<c>_triggers = FromFlat(...).ToFlat()</c>) with a DIRECT graph walk: <c>LoadScenario</c> builds the
    /// execution view <c>_execs = TriggerGraph.FromFlat(...).BuildExecutionOrder()</c>. This test drives the real
    /// <c>LoadScenario</c> with a RICH flat trigger set, reconstructs the flat triggers from the resulting execution
    /// view (re-sorted to declaration order by ascending node id), and deep-compares field-for-field against the
    /// input — proving the field-mapping fidelity of the production lowering directly on the surface that runs.
    /// </summary>
    public class TriggerGraphLiveLoweringTests
    {
        [Fact]
        public void LiveLoadScenario_RichTriggers_LowersLosslessly()
        {
            TriggerDefinition[] input = TriggerFieldAssert.RichTriggerSet();

            var scenario = new ScenarioData { Triggers = input };
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());

            // Drives the wired graph walk (FromFlat(...).BuildExecutionOrder()) inside the real LoadScenario.
            director.LoadScenario(scenario);

            // DW-501: the execution view is private, so this is a white-box probe BY NAME — routed through the
            // null-CHECKED ReflectionProbe rather than the old `GetField("_execs", …)!` idiom. That `!` suppressed
            // only the compiler warning: renaming _execs (or changing its type) produced a NullReferenceException /
            // InvalidCastException on this line naming neither ScenarioDirector nor the field, so the failure read
            // as a lowering regression when the test had simply gone stale.
            FieldInfo field = ReflectionProbe.Field(typeof(ScenarioDirector), "_execs");
            var execs = ReflectionProbe.Read<IEnumerable<TriggerGraph.TriggerExec>>(field, director);

            // Reconstruct the flat triggers from the execution view. The view is in EXECUTION order (Priority desc,
            // node-id asc); re-sort by ascending trigger node-id (== declaration order for a FromFlat graph) so it
            // aligns with the input array for a lossless field-for-field compare.
            TriggerDefinition[] lowered = execs.OrderBy(e => e.Trigger.Id).Select(ToFlat).ToArray();

            TriggerFieldAssert.AssertEqual(input, lowered);
        }

        private static TriggerDefinition ToFlat(TriggerGraph.TriggerExec ex) => new()
        {
            Name            = ex.Trigger.Name,
            Enabled         = ex.Trigger.Enabled,
            RunOnce         = ex.Trigger.RunOnce,
            CooldownSeconds = ex.Trigger.CooldownSeconds,
            Priority        = ex.Trigger.Priority,
            Events = ex.Events.Select(e => new TriggerEvent
            {
                Type = e.Kind, Faction = e.Faction, BuildingType = e.BuildingType,
                TimerName = e.TimerName, Amount = e.Amount, Count = e.Count, Operator = e.Operator,
            }).ToArray(),
            Conditions = ex.Conditions.Select(c => new TriggerCondition
            {
                Type = c.Kind, Faction = c.Faction, BuildingType = c.BuildingType, Amount = c.Amount,
                Count = c.Count, Variable = c.Variable, RegionId = c.RegionId, Value = c.Value, Operator = c.Operator,
            }).ToArray(),
            Actions = ex.Actions.OfType<ActionNode>().Select(a => new TriggerAction
            {
                Type = a.Kind, UnitId = a.UnitId, Faction = a.Faction, X = a.X, Z = a.Z, Count = a.Count,
                Text = a.Text, Duration = a.Duration, TimerName = a.TimerName, TimerSeconds = a.TimerSeconds,
                Amount = a.Amount, Value = a.Value, Variable = a.Variable, SoundId = a.SoundId,
            }).ToArray(),
        };
    }

    /// <summary>
    /// Shared field-level deep-equality for lowered trigger arrays (used by the live-lowering test and the on-disk
    /// tripwire). Hand-enumerates every flat field so a mapping regression in <c>FromFlat</c>/<c>ToFlat</c> is caught,
    /// not just length/header drift.
    /// </summary>
    public static class TriggerFieldAssert
    {
        /// <summary>A rich trigger set touching every node kind class, multiple events/conditions/actions, and
        /// Fixed-bearing fields across multiple triggers with intra-trigger order that must survive round-trip.</summary>
        public static TriggerDefinition[] RichTriggerSet() => new[]
        {
            new TriggerDefinition
            {
                Name = "Alpha", Enabled = true, RunOnce = true,
                CooldownSeconds = Fixed.FromFloat(2.5f), Priority = 9,
                Events = new[]
                {
                    new TriggerEvent { Type = "match_start" },
                    new TriggerEvent { Type = "building_completed", Faction = 0, BuildingType = "Barracks" },
                    new TriggerEvent { Type = "resource_threshold", Faction = 1, Amount = Fixed.FromInt(500), Operator = ">=" },
                },
                Conditions = new[]
                {
                    new TriggerCondition { Type = "always" },
                    new TriggerCondition { Type = "resource_comparison", Faction = 1, Amount = Fixed.FromInt(100), Operator = "<" },
                    new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = "keep" },
                },
                Actions = new[]
                {
                    new TriggerAction { Type = "spawn_unit", UnitId = "grunt", Faction = 0, X = Fixed.FromFloat(3.25f), Z = Fixed.FromFloat(-4.75f), Count = 5 },
                    new TriggerAction { Type = "display_message", Text = "Hello", Duration = Fixed.FromFloat(6.5f) },
                    new TriggerAction { Type = "add_resources", Faction = 1, Amount = Fixed.FromInt(250) },
                    new TriggerAction { Type = "set_variable", Variable = "phase", Value = 2 },
                    new TriggerAction { Type = "play_sound", SoundId = "horn" },
                },
            },
            new TriggerDefinition
            {
                Name = "Beta", Enabled = false, Priority = -3,
                Events = new[] { new TriggerEvent { Type = "timer_expires", TimerName = "clock" } },
                Actions = new[] { new TriggerAction { Type = "victory", Faction = 1 } },
            },
            new TriggerDefinition { Name = "Gamma" }, // bare: zero events/conditions/actions
        };

        public static void AssertEqual(TriggerDefinition[] expected, TriggerDefinition[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                AssertTriggerEqual(expected[i], actual[i]);
        }

        private static void AssertTriggerEqual(TriggerDefinition e, TriggerDefinition a)
        {
            Assert.Equal(e.Name, a.Name);
            Assert.Equal(e.Enabled, a.Enabled);
            Assert.Equal(e.RunOnce, a.RunOnce);
            Assert.Equal(e.CooldownSeconds.Raw, a.CooldownSeconds.Raw);
            Assert.Equal(e.Priority, a.Priority);

            Assert.Equal(e.Events.Length, a.Events.Length);
            for (int i = 0; i < e.Events.Length; i++)
            {
                Assert.Equal(e.Events[i].Type, a.Events[i].Type);
                Assert.Equal(e.Events[i].Faction, a.Events[i].Faction);
                Assert.Equal(e.Events[i].BuildingType, a.Events[i].BuildingType);
                Assert.Equal(e.Events[i].TimerName, a.Events[i].TimerName);
                Assert.Equal(e.Events[i].Amount.Raw, a.Events[i].Amount.Raw);
                Assert.Equal(e.Events[i].Count, a.Events[i].Count);
                Assert.Equal(e.Events[i].Operator, a.Events[i].Operator);
            }

            Assert.Equal(e.Conditions.Length, a.Conditions.Length);
            for (int i = 0; i < e.Conditions.Length; i++)
            {
                Assert.Equal(e.Conditions[i].Type, a.Conditions[i].Type);
                Assert.Equal(e.Conditions[i].Faction, a.Conditions[i].Faction);
                Assert.Equal(e.Conditions[i].BuildingType, a.Conditions[i].BuildingType);
                Assert.Equal(e.Conditions[i].Amount.Raw, a.Conditions[i].Amount.Raw);
                Assert.Equal(e.Conditions[i].Count, a.Conditions[i].Count);
                Assert.Equal(e.Conditions[i].Variable, a.Conditions[i].Variable);
                Assert.Equal(e.Conditions[i].RegionId, a.Conditions[i].RegionId);
                Assert.Equal(e.Conditions[i].Value, a.Conditions[i].Value);
                Assert.Equal(e.Conditions[i].Operator, a.Conditions[i].Operator);
            }

            Assert.Equal(e.Actions.Length, a.Actions.Length);
            for (int i = 0; i < e.Actions.Length; i++)
            {
                Assert.Equal(e.Actions[i].Type, a.Actions[i].Type);
                Assert.Equal(e.Actions[i].UnitId, a.Actions[i].UnitId);
                Assert.Equal(e.Actions[i].Faction, a.Actions[i].Faction);
                Assert.Equal(e.Actions[i].X.Raw, a.Actions[i].X.Raw);
                Assert.Equal(e.Actions[i].Z.Raw, a.Actions[i].Z.Raw);
                Assert.Equal(e.Actions[i].Count, a.Actions[i].Count);
                Assert.Equal(e.Actions[i].Text, a.Actions[i].Text);
                Assert.Equal(e.Actions[i].Duration.Raw, a.Actions[i].Duration.Raw);
                Assert.Equal(e.Actions[i].TimerName, a.Actions[i].TimerName);
                Assert.Equal(e.Actions[i].TimerSeconds.Raw, a.Actions[i].TimerSeconds.Raw);
                Assert.Equal(e.Actions[i].Amount.Raw, a.Actions[i].Amount.Raw);
                Assert.Equal(e.Actions[i].Value, a.Actions[i].Value);
                Assert.Equal(e.Actions[i].Variable, a.Actions[i].Variable);
                Assert.Equal(e.Actions[i].SoundId, a.Actions[i].SoundId);
            }
        }
    }
}
