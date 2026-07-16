#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.3 (Block-If regression net) — a LEGACY scenario (all-Global-Int variables, timers in seconds, flat
    /// triggers) produces byte-identical OBSERVABLE tick behavior after migration onto the typed <see cref="DslVarTable"/>
    /// + the direct graph walk. Drives a real <see cref="ScenarioDirector"/> and asserts observable outcomes
    /// (variable_comparison-gated display_message, timer_expires firing) — the I/O-matrix parity rows.
    /// </summary>
    public class DslMigrationParityTests
    {
        private const string Fired = "FIRED";

        private static ScenarioDirector Build(ScenarioData scenario, out List<string> messages)
        {
            var msgs = new List<string>();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.OnDisplayMessage = (text, _) => msgs.Add(text);
            director.LoadScenario(scenario);
            messages = msgs;
            return director;
        }

        [Fact]
        public void LegacyVariableParity_SetThenCompare_Fires()
        {
            // set_variable(v,5) on an UNDECLARED name (→ Global/Int/default-0) then variable_comparison(v>=5): the
            // comparison is true after the set, exactly like the flat path. Writer has higher priority so it runs first.
            var scenario = new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "writer", Priority = 10,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "v", Value = 5 } },
                    },
                    new TriggerDefinition
                    {
                        Name = "gate", Priority = 1,
                        Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "variable_comparison", Variable = "v", Operator = ">=", Value = 5 } },
                        Actions    = new[] { new TriggerAction { Type = "display_message", Text = Fired } },
                    },
                },
            };
            var director = Build(scenario, out var messages);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Contains(Fired, messages);
        }

        [Fact]
        public void LegacyTimerParity_CreateThenExpire_Fires()
        {
            // create_timer(t, 1 s) → SecondsToTicks(1s) = 30 integer ticks (no float→int truncation). timer_expires(t)
            // fires when the timer decrements to 0 — never on the arm tick, and exactly once.
            var scenario = new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "arm", RunOnce = true,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "create_timer", TimerName = "t", TimerSeconds = Fixed.FromInt(1) } },
                    },
                    new TriggerDefinition
                    {
                        Name = "onExpire",
                        Events  = new[] { new TriggerEvent { Type = "timer_expires", TimerName = "t" } },
                        Actions = new[] { new TriggerAction { Type = "display_message", Text = Fired } },
                    },
                },
            };
            var director = Build(scenario, out var messages);
            var world = new EntityWorld();
            director.Tick(world, Fixed.One); // arm tick — the timer is created here, must NOT fire this tick
            Assert.DoesNotContain(Fired, messages);
            // The 30-tick timer expires on a later tick; drive up to its whole-tick duration.
            for (int i = 0; i < 30 && !messages.Contains(Fired); i++)
                director.Tick(world, Fixed.One);
            Assert.Contains(Fired, messages);
        }

        [Fact]
        public void LegacyScenario_IsDeterministicAcrossFreshRuns()
        {
            ScenarioData Make() => new()
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "w", Priority = 10,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "v", Value = 3 } },
                    },
                    new TriggerDefinition
                    {
                        Name = "g", Priority = 1,
                        Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "variable_comparison", Variable = "v", Operator = "==", Value = 3 } },
                        Actions    = new[] { new TriggerAction { Type = "display_message", Text = Fired } },
                    },
                },
            };

            var d1 = Build(Make(), out var m1);
            var d2 = Build(Make(), out var m2);
            d1.Tick(new EntityWorld(), Fixed.One);
            d2.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(m1, m2);
            Assert.Contains(Fired, m1);
        }

        [Fact]
        public void DeclaredTimers_DecrementInATriggerlessScenario()
        {
            // Review follow-up: ScenarioData.Timers made trigger-less timers representable, but Tick's
            // zero-triggers early-out used to skip the timer decrement entirely — the folded remaining-ticks froze
            // at their initial forever, violating the "declared timers start active" contract.
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(new ScenarioData
            {
                Timers = new[] { new ScenarioTimer { Name = "t", Seconds = Fixed.FromInt(1) } }, // 30 ticks
            });

            static uint Fold(DslVarTable t)
            {
                uint h = 2166136261u;
                t.FoldInto(ref h, static (hash, value) =>
                {
                    const uint prime = 16777619u;
                    uint v = (uint)value;
                    hash ^= v & 0xFF;         hash *= prime;
                    hash ^= (v >> 8) & 0xFF;  hash *= prime;
                    hash ^= (v >> 16) & 0xFF; hash *= prime;
                    hash ^= (v >> 24) & 0xFF; hash *= prime;
                    return hash;
                });
                return h;
            }

            uint before = Fold(vars);
            director.Tick(new EntityWorld(), Fixed.One); // zero triggers — the timer must still decrement (30→29)
            Assert.NotEqual(before, Fold(vars));
        }
    }
}
