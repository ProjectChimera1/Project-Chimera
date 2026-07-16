#nullable enable
using System;
using System.Linq;
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // TriggerDefinition, TriggerEvent, TriggerCondition, TriggerAction
using ProjectChimera.Dsl;               // TriggerGraph
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.2 — the lossless flat↔graph migration AC + I/O rows: <c>FromFlat(x).ToFlat()</c> deep-equals
    /// <c>x</c> in EVERY field and in trigger/event/condition/action order. Covers empty; a single trigger with
    /// multiple events/conditions/Fixed-bearing actions; multiple triggers preserving array + intra-trigger order;
    /// and a bare trigger with zero events/conditions/actions.
    /// </summary>
    public class TriggerGraphMigrationTests
    {
        [Fact]
        public void Empty_RoundTrips_ToEmpty()
        {
            TriggerDefinition[] flat = Array.Empty<TriggerDefinition>();
            TriggerDefinition[] back = TriggerGraph.FromFlat(flat).ToFlat();
            Assert.Empty(back);
        }

        [Fact]
        public void SingleTrigger_MultipleEventsConditionsActions_RoundTripsExactly()
        {
            var flat = new[]
            {
                new TriggerDefinition
                {
                    Name = "Rich", Enabled = true, RunOnce = true,
                    CooldownSeconds = Fixed.FromFloat(2.5f), Priority = 7,
                    Events = new[]
                    {
                        new TriggerEvent { Type = "match_start" },
                        new TriggerEvent { Type = "resource_threshold", Faction = 1, Amount = Fixed.FromInt(500), Operator = ">=" },
                        new TriggerEvent { Type = "building_completed", Faction = 0, BuildingType = "Barracks" },
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
                        new TriggerAction { Type = "create_timer", TimerName = "clock", TimerSeconds = Fixed.FromInt(45) },
                    },
                },
            };

            AssertRoundTrip(flat);
        }

        [Fact]
        public void MultipleTriggers_PreserveArrayAndIntraTriggerOrder()
        {
            var flat = new[]
            {
                new TriggerDefinition
                {
                    Name = "First", Priority = 3,
                    Events = new[] { new TriggerEvent { Type = "unit_dies", Faction = 0 } },
                    Conditions = new[] { new TriggerCondition { Type = "unit_count", Faction = 0, Count = 2, Operator = ">" } },
                    Actions = new[]
                    {
                        new TriggerAction { Type = "set_variable", Variable = "a", Value = 1 },
                        new TriggerAction { Type = "set_variable", Variable = "b", Value = 2 },
                    },
                },
                new TriggerDefinition
                {
                    Name = "Second", Priority = -1, Enabled = false,
                    Events = new[]
                    {
                        new TriggerEvent { Type = "timer_expires", TimerName = "t1" },
                        new TriggerEvent { Type = "unit_count_threshold", Faction = 1, Count = 10, Operator = "<=" },
                    },
                    Actions = new[] { new TriggerAction { Type = "victory", Faction = 1 } },
                },
                new TriggerDefinition { Name = "Third" }, // bare
            };

            AssertRoundTrip(flat);
        }

        [Fact]
        public void TriggerWithZeroEventsConditionsActions_RoundTripsExactly()
        {
            var flat = new[]
            {
                new TriggerDefinition
                {
                    Name = "Empty-bodied", Enabled = true, RunOnce = false,
                    CooldownSeconds = Fixed.Zero, Priority = 0,
                    Events = Array.Empty<TriggerEvent>(),
                    Conditions = Array.Empty<TriggerCondition>(),
                    Actions = Array.Empty<TriggerAction>(),
                },
            };

            AssertRoundTrip(flat);
        }

        [Fact]
        public void DeterministicIdAssignment_MatchesTheDesignNoteExample()
        {
            // Two 1-event/1-cond/2-action triggers → T0=0,E=1,C=2,A0=3,A1=4 ; T1=5,E=6,C=7,A0=8,A1=9.
            var flat = new[]
            {
                MakeTrigger("T0"),
                MakeTrigger("T1"),
            };
            TriggerGraph g = TriggerGraph.FromFlat(flat);

            int[] ids = g.Nodes.Select(n => n.Id).OrderBy(i => i).ToArray();
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, ids);

            // Node 0 is the first TriggerNode; node 5 the second.
            Assert.IsType<TriggerNode>(g.Nodes.Single(n => n.Id == 0));
            Assert.IsType<EventNode>(g.Nodes.Single(n => n.Id == 1));
            Assert.IsType<ConditionNode>(g.Nodes.Single(n => n.Id == 2));
            Assert.IsType<ActionNode>(g.Nodes.Single(n => n.Id == 3));
            Assert.IsType<TriggerNode>(g.Nodes.Single(n => n.Id == 5));

            AssertRoundTrip(flat);
        }

        private static TriggerDefinition MakeTrigger(string name) => new()
        {
            Name = name,
            Events = new[] { new TriggerEvent { Type = "match_start" } },
            Conditions = new[] { new TriggerCondition { Type = "always" } },
            Actions = new[]
            {
                new TriggerAction { Type = "spawn_unit", UnitId = "u", Count = 1 },
                new TriggerAction { Type = "display_message", Text = "m" },
            },
        };

        // ── Deep-equality helpers ────────────────────────────────────────────────

        private static void AssertRoundTrip(TriggerDefinition[] flat)
        {
            TriggerDefinition[] back = TriggerGraph.FromFlat(flat).ToFlat();
            Assert.Equal(flat.Length, back.Length);
            for (int i = 0; i < flat.Length; i++)
                AssertTriggerEqual(flat[i], back[i]);
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
