#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;   // DirectHpDeltaEffect (run_effect embedded subgraph)
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.3 — the direct graph-walk execution core: parity with the old flat walk; an
    /// <c>EffectActionNode</c> (run_effect) firing via the EXISTING EffectExecutor with an observable sim effect; a
    /// variable read/write leaf hitting the <see cref="DslVarTable"/>; and a Per-player variable selecting distinct
    /// slots per faction.
    /// </summary>
    public class TriggerGraphExecutionTests
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
        public void GraphWalk_FlatScenario_ExecutesLikeFlat()
        {
            // A plain flat scenario (no trigger_graph) executes through FromFlat → the direct walk: match_start fires
            // the trigger and its display_message action runs — identical to the legacy flat path.
            var scenario = new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "t",
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "display_message", Text = Fired } },
                    },
                },
            };
            var director = Build(scenario, out var messages);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Contains(Fired, messages);
        }

        [Fact]
        public void RunEffect_FiresViaEffectExecutor_ApplyingObservableSimEffect()
        {
            // A graph-only trigger (run_effect embeds a direct_hp_delta -10) authored as canonical IR JSON. On
            // match_start the embedded EffectNode runs via the existing EffectExecutor against the lowest-id alive
            // entity anchor → its HP drops by 10.
            const string graph = """
            {
              "nodes": [
                { "id": 0, "kind": "trigger" },
                { "id": 1, "kind": "match_start" },
                { "id": 2, "kind": "run_effect", "effect": { "kind": "direct_hp_delta", "delta": -10 } }
              ],
              "exec_edges": [
                { "src": 1, "src_port": 0, "dst": 0, "dst_port": 0 },
                { "src": 0, "src_port": 0, "dst": 2, "dst_port": 0 }
              ],
              "data_edges": []
            }
            """;
            var director = Build(new ScenarioData { TriggerGraphJson = graph }, out _);

            var world = new EntityWorld();
            int e = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                 Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.EffectiveMaxHealth[e] = Fixed.FromInt(100); // so the -10 delta is not clamped away

            director.Tick(world, Fixed.One);
            Assert.Equal(Fixed.FromInt(90).Raw, world.Health[e].Raw); // the run_effect fired and applied
        }

        [Fact]
        public void RawIr_UnknownKind_IsRejectedFailClosed()
        {
            // The raw-IR converter fails closed on an unknown kind (no partial trigger), at LoadScenario (FromJson).
            const string bad = """
            { "nodes": [ { "id": 0, "kind": "totally_unknown_kind" } ], "exec_edges": [], "data_edges": [] }
            """;
            Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
                Build(new ScenarioData { TriggerGraphJson = bad }, out _));
        }

        [Fact]
        public void VariableLeaf_ReadWrite_HitsTable()
        {
            // set_variable writes the table; variable_comparison reads it back and gates the display_message.
            var scenario = new ScenarioData
            {
                Variables = new[] { new ScenarioVariable { Name = "n", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero } },
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "writer", Priority = 10,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "n", Value = 42 } },
                    },
                    new TriggerDefinition
                    {
                        Name = "gate", Priority = 1,
                        Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "variable_comparison", Variable = "n", Operator = "==", Value = 42 } },
                        Actions    = new[] { new TriggerAction { Type = "display_message", Text = Fired } },
                    },
                },
            };
            var director = Build(scenario, out var messages);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Contains(Fired, messages);
        }

        [Fact]
        public void BuildRunEffectTriggerHelper_FiresEmbeddedEffect_ViaScenarioDirector()
        {
            // P1: the extracted TriggerGraph.BuildRunEffectTrigger helper produces a graph that, run through
            // ScenarioDirector, actually FIRES its embedded effect (mirrors RunEffect_FiresViaEffectExecutor, but via
            // the helper the editor now calls instead of hand-built JSON).
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-10)));
            var director = Build(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }, out _);

            var world = new EntityWorld();
            int e = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                 Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.EffectiveMaxHealth[e] = Fixed.FromInt(100);

            director.Tick(world, Fixed.One);
            Assert.Equal(Fixed.FromInt(90).Raw, world.Health[e].Raw);
        }

        [Fact]
        public void BothChannels_FlatTriggersAndTriggerGraph_AllExecute_NoneDropped()
        {
            // P1: a scenario carrying BOTH a flat Triggers array AND a TriggerGraphJson must execute EVERY trigger
            // from BOTH channels (the pre-fix code ran only ONE channel, silently dropping the other). The flat
            // display_message fires AND the graph run_effect fires (HP drops).
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("graphTrig", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-10)));
            var scenario = new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "flatTrig",
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "display_message", Text = Fired } },
                    },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            var director = Build(scenario, out var messages);

            var world = new EntityWorld();
            int e = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                 Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.EffectiveMaxHealth[e] = Fixed.FromInt(100);

            director.Tick(world, Fixed.One);
            Assert.Contains(Fired, messages);                              // flat channel executed
            Assert.Equal(Fixed.FromInt(90).Raw, world.Health[e].Raw);     // graph channel executed
        }

        [Fact]
        public void MergingTwoRunEffectTriggers_PreservesBoth()
        {
            // P1: merging a second run_effect trigger into an existing graph keeps BOTH (offset ids, no collision).
            // Both anchor on the lowest-id alive entity, so the two hp deltas (-10 and -20) both apply → HP 100→70.
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("a", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-10)));
            g.Merge(TriggerGraph.BuildRunEffectTrigger("b", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-20))));
            var director = Build(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }, out _);

            var world = new EntityWorld();
            int e = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                 Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.EffectiveMaxHealth[e] = Fixed.FromInt(100);

            director.Tick(world, Fixed.One);
            Assert.Equal(Fixed.FromInt(70).Raw, world.Health[e].Raw); // both run_effect triggers fired
        }

        [Fact]
        public void BuildRunEffectTriggerHelper_WithCondition_GatesTheEffect()
        {
            // Review follow-up: the preset form's chosen condition rides into the graph as a ConditionNode (it was
            // silently DISCARDED before — the effect fired unconditionally against the authored logic). The same
            // conditioned graph must NOT fire while the variable fails the comparison, and must fire when it passes.
            TriggerGraph Graph() => TriggerGraph.BuildRunEffectTrigger(
                "t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-10)),
                conditionKind: "variable_comparison", conditionVariable: "gate", conditionValue: 1);
            ScenarioData Scenario(int initial) => new ScenarioData
            {
                Variables = new[] { new ScenarioVariable { Name = "gate", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(initial) } },
                TriggerGraphJson = Graph().ToCanonicalJson(),
            };
            static EntityWorld WorldWithUnit(out int e)
            {
                var world = new EntityWorld();
                e = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                 Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                world.EffectiveMaxHealth[e] = Fixed.FromInt(100);
                return world;
            }

            var blockedWorld = WorldWithUnit(out int b);
            Build(Scenario(initial: 0), out _).Tick(blockedWorld, Fixed.One);
            Assert.Equal(Fixed.FromInt(100).Raw, blockedWorld.Health[b].Raw); // gate==0 ≠ 1 → effect gated OFF

            var passWorld = WorldWithUnit(out int p);
            Build(Scenario(initial: 1), out _).Tick(passWorld, Fixed.One);
            Assert.Equal(Fixed.FromInt(90).Raw, passWorld.Health[p].Raw);     // gate==1 → effect fires
        }

        [Fact]
        public void PerPlayerVariable_SelectsDistinctSlotsPerFaction()
        {
            // A Per-player var written for faction 0 must satisfy a faction-0 comparison but NOT a faction-1 one.
            var scenario = new ScenarioData
            {
                Variables = new[] { new ScenarioVariable { Name = "s", Type = DslValueType.Int, Scope = VarScope.PerPlayer, Initial = Fixed.Zero } },
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "writer", Priority = 10,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "s", Faction = 0, Value = 5 } },
                    },
                    new TriggerDefinition
                    {
                        Name = "gate0", Priority = 1,
                        Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "variable_comparison", Variable = "s", Faction = 0, Operator = "==", Value = 5 } },
                        Actions    = new[] { new TriggerAction { Type = "display_message", Text = "P0" } },
                    },
                    new TriggerDefinition
                    {
                        Name = "gate1", Priority = 1,
                        Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "variable_comparison", Variable = "s", Faction = 1, Operator = "==", Value = 5 } },
                        Actions    = new[] { new TriggerAction { Type = "display_message", Text = "P1" } },
                    },
                },
            };
            var director = Build(scenario, out var messages);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Contains("P0", messages);
            Assert.DoesNotContain("P1", messages);
        }
    }
}
