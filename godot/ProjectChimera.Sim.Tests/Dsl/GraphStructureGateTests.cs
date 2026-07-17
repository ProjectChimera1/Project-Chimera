#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.7 — the whole-graph structural rulebook (<see cref="GraphStructureGate"/>): duplicate node ids,
    /// dangling edge endpoints, per-kind port legality, forked exec / forked data edges, stray data edges, and
    /// compile checks over UNCONSUMED expression subgraphs — plus the posture rules (trigger condition-in fan-in
    /// stays legal, mere unreachability of an individually-valid node is NOT a reject) and both-gates parity
    /// (the SAME malformed IR rejects at <see cref="ScenarioValidator"/> AND at
    /// <c>ScenarioDirector.LoadScenario</c>).
    /// </summary>
    public class GraphStructureGateTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> NoVars =
            new(System.StringComparer.Ordinal);
        private static readonly Dictionary<string, (DslValueType Elem, int Capacity)> NoArrays =
            new(System.StringComparer.Ordinal);

        private static string? Check(TriggerGraph g) => GraphStructureGate.Check(g, NoVars, NoArrays);

        /// <summary>A minimal sound single-trigger graph: event 1 → trigger 0 → action 2.</summary>
        private static TriggerGraph SoundGraph()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "hi" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            return g;
        }

        [Fact]
        public void SoundGraph_Passes()
        {
            Assert.Null(Check(SoundGraph()));
        }

        [Fact]
        public void FromFlatGraph_Passes() // sanctioned content: the flat lowering must clear the gate unchanged
        {
            var flat = new[]
            {
                new TriggerDefinition
                {
                    Name = "koth",
                    Events = new[] { new TriggerEvent { Type = "match_start" }, new TriggerEvent { Type = "unit_dies" } },
                    Conditions = new[]
                    {
                        new TriggerCondition { Type = "always" },
                        new TriggerCondition { Type = "resource_comparison", Amount = Fixed.FromInt(100) },
                    },
                    Actions = new[]
                    {
                        new TriggerAction { Type = "display_message", Text = "a" },
                        new TriggerAction { Type = "add_resources", Amount = Fixed.FromInt(5) },
                    },
                },
                new TriggerDefinition { Name = "empty" },
            };
            Assert.Null(Check(TriggerGraph.FromFlat(flat)));
        }

        [Fact]
        public void DuplicateNodeIds_Reject()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "victory" }); // id 2 again
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("duplicate node ids", err);
            Assert.Contains("2", err);
        }

        [Fact]
        public void DanglingExecEdge_Rejects()
        {
            var g = SoundGraph();
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 99, TriggerGraph.ActionExecInPort));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("dangling", err);
            Assert.Contains("99", err);
        }

        [Fact]
        public void DanglingDataEdge_Rejects()
        {
            var g = SoundGraph();
            g.DataEdges.Add(new DataEdge(77, TriggerGraph.ConditionDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("dangling", err);
            Assert.Contains("77", err);
        }

        [Fact]
        public void ExecEdgeOutOfADataOnlyNode_Rejects()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            g.ExecEdges.Add(new ExecEdge(3, 0, 2, TriggerGraph.ActionExecInPort)); // conditions have no exec-out
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("not an exec-out port", err);
        }

        [Fact]
        public void ForkedExecEdges_Reject()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "victory" });
            // A SECOND exec edge out of the trigger's exec-out port (the chain already leaves it to node 2).
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 3, TriggerGraph.ActionExecInPort));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("multiple exec edges", err);
        }

        [Fact]
        public void ForkedDataEdgesIntoOnePort_Reject()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "v" });
            g.Nodes.Add(new ExprLiteralNode { Id = 4, ValueType = DslValueType.Int, Raw = 1 });
            g.Nodes.Add(new ExprLiteralNode { Id = 5, ValueType = DslValueType.Int, Raw = 2 });
            g.DataEdges.Add(new DataEdge(4, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(5, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("multiple data edges", err);
        }

        [Fact]
        public void TriggerConditionInFanIn_StaysLegal() // multi-condition AND wiring is sanctioned
        {
            var g = SoundGraph();
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            g.Nodes.Add(new ConditionNode { Id = 4, Kind = "always" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ConditionDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(4, TriggerGraph.ConditionDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            Assert.Null(Check(g));
        }

        [Fact]
        public void StrayDataEdgeIntoANonDataPort_Rejects()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ConditionDataOutPort, 1, 0, DataWireType.Boolean)); // into an EVENT node
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("not a data-in port", err);
        }

        [Fact]
        public void StrayDataEdgeFromANonDataSource_Rejects()
        {
            var g = SoundGraph();
            // An ACTION node has no data-out ports — wiring it as a data source is malformed.
            g.DataEdges.Add(new DataEdge(2, 0, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("not a data-out port", err);
        }

        [Fact]
        public void ValueInEdgeOnRunEffect_RejectsStructurally()
        {
            var g = SoundGraph();
            g.Nodes.Add(new EffectActionNode { Id = 3, Effect = new ProjectChimera.Effects.DirectHpDeltaEffect(Fixed.FromInt(-1)) });
            g.Nodes.Add(new ExprLiteralNode { Id = 4, ValueType = DslValueType.Int, Raw = 1 });
            g.DataEdges.Add(new DataEdge(4, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("not a data-in port", err); // run_effect takes no data-in ports
        }

        [Fact]
        public void UnconsumedExpressionSubgraph_WithTypeError_Rejects() // no orphan-node semantic skip
        {
            var g = SoundGraph();
            // add(Bool, Int) — a type error — wired to NOTHING (a fully orphaned expression subgraph).
            g.Nodes.Add(new ExprBinaryNode { Id = 10, Op = "add" });
            g.Nodes.Add(new ExprLiteralNode { Id = 11, ValueType = DslValueType.Bool, Raw = 1 });
            g.Nodes.Add(new ExprLiteralNode { Id = 12, ValueType = DslValueType.Int, Raw = 2 });
            g.DataEdges.Add(new DataEdge(11, TriggerGraph.ExprDataOutPort, 10, TriggerGraph.ExprOperandPort0, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(12, TriggerGraph.ExprDataOutPort, 10, TriggerGraph.ExprOperandPort1, DataWireType.Int));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("expression subgraph rooted at node 10", err);
        }

        [Fact]
        public void UnreachableButIndividuallyValidNode_IsNotAReject() // T3 WIP posture
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 10, Kind = "victory" });                        // disconnected action
            g.Nodes.Add(new ExprLiteralNode { Id = 11, ValueType = DslValueType.Int, Raw = 7 }); // disconnected, valid expr
            Assert.Null(Check(g));
        }

        [Fact]
        public void MutuallyConsumingExpressionCycle_Rejects()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ExprUnaryNode { Id = 10, Op = "neg" });
            g.Nodes.Add(new ExprUnaryNode { Id = 11, Op = "neg" });
            g.DataEdges.Add(new DataEdge(10, TriggerGraph.ExprDataOutPort, 11, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(11, TriggerGraph.ExprDataOutPort, 10, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            string? err = Check(g);
            Assert.NotNull(err);
            Assert.Contains("cyclic or mutually-consuming", err);
        }

        [Fact]
        public void DataEdgeMissingWire_IsALocatedParseReject() // parse-level fail-closed (no Boolean default)
        {
            const string json = @"{
                ""nodes"": [
                    { ""id"": 0, ""kind"": ""trigger"", ""name"": ""t"", ""enabled"": true, ""run_once"": false, ""cooldown_seconds"": 0, ""priority"": 0 },
                    { ""id"": 1, ""kind"": ""always"", ""faction"": 0, ""amount"": 0, ""count"": 0, ""value"": 0, ""operator"": "">="" }
                ],
                ""exec_edges"": [],
                ""data_edges"": [ { ""src"": 1, ""src_port"": 0, ""dst"": 0, ""dst_port"": 1 } ]
            }";
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("wire", ex.Message);
        }

        // ── DataEdge converter strictness (review follow-up): every key required, duplicates reject, wire by
        //    exact NAME only — a missing endpoint used to silently default to node/port 0. ──

        private static string GraphWithDataEdge(string edgeJson) => @"{
                ""nodes"": [
                    { ""id"": 0, ""kind"": ""trigger"", ""name"": ""t"" },
                    { ""id"": 1, ""kind"": ""always"" }
                ],
                ""exec_edges"": [],
                ""data_edges"": [ " + edgeJson + @" ]
            }";

        [Theory]
        [InlineData(@"{ ""src_port"": 0, ""dst"": 0, ""dst_port"": 1, ""wire"": ""Boolean"" }", "src")]
        [InlineData(@"{ ""src"": 1, ""dst"": 0, ""dst_port"": 1, ""wire"": ""Boolean"" }", "src_port")]
        [InlineData(@"{ ""src"": 1, ""src_port"": 0, ""dst_port"": 1, ""wire"": ""Boolean"" }", "dst")]
        [InlineData(@"{ ""src"": 1, ""src_port"": 0, ""dst"": 0, ""wire"": ""Boolean"" }", "dst_port")]
        public void DataEdgeMissingAnyEndpointKey_IsALocatedParseReject(string edgeJson, string missingKey)
        {
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => TriggerGraph.FromJson(GraphWithDataEdge(edgeJson)));
            Assert.Contains($"'{missingKey}'", ex.Message);
            Assert.Contains("missing", ex.Message);
        }

        [Fact]
        public void DataEdgeDuplicateKey_IsALocatedParseReject()
        {
            // JsonDocument permits duplicate names; without the reject, a second value could smuggle past validation.
            string edge = @"{ ""src"": 1, ""src"": 99, ""src_port"": 0, ""dst"": 0, ""dst_port"": 1, ""wire"": ""Boolean"" }";
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => TriggerGraph.FromJson(GraphWithDataEdge(edge)));
            Assert.Contains("duplicate", ex.Message);
        }

        [Theory]
        [InlineData("2")]        // numeric string — Enum.TryParse would parse it BY VALUE
        [InlineData("-1")]
        [InlineData("boolean")]  // wrong case — the converter's posture is exact-name
        [InlineData("BOOLEAN")]
        public void DataEdgeWireNotAnExactName_IsALocatedParseReject(string wire)
        {
            string edge = @"{ ""src"": 1, ""src_port"": 0, ""dst"": 0, ""dst_port"": 1, ""wire"": """ + wire + @""" }";
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => TriggerGraph.FromJson(GraphWithDataEdge(edge)));
            Assert.Contains("not a known wire type", ex.Message);
        }

        // ── Graph-channel comparison-operator vocabulary (review follow-up): membership enforced at parse from
        //    the ONE NodeKinds.Operators source (the same array the flat ScenarioValidator gate aliases). ──

        [Fact]
        public void EventNodeUnknownOperator_IsALocatedParseReject()
        {
            const string json = @"{
                ""nodes"": [ { ""id"": 0, ""kind"": ""match_start"", ""operator"": ""~="" } ],
                ""exec_edges"": [], ""data_edges"": []
            }";
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains(".operator", ex.Message);
            Assert.Contains("'~='", ex.Message);
        }

        [Fact]
        public void ConditionNodeUnknownOperator_IsALocatedParseReject()
        {
            const string json = @"{
                ""nodes"": [ { ""id"": 0, ""kind"": ""always"", ""operator"": ""equals"" } ],
                ""exec_edges"": [], ""data_edges"": []
            }";
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains(".operator", ex.Message);
            Assert.Contains("'equals'", ex.Message);
        }

        [Fact]
        public void UnknownGraphOperator_RejectsAtBothGates_WithTheSameErrorClass()
        {
            // The MalformedGraph_RejectsAtBothGates pattern, for the operator vocabulary: the same unknown-operator
            // IR rejects located at the validator AND at the LoadScenario backstop (both route through FromJson).
            const string json = @"{
                ""nodes"": [
                    { ""id"": 0, ""kind"": ""trigger"", ""name"": ""t"" },
                    { ""id"": 1, ""kind"": ""resource_threshold"", ""operator"": ""~="" }
                ],
                ""exec_edges"": [ { ""src"": 1, ""src_port"": 0, ""dst"": 0, ""dst_port"": 0 } ],
                ""data_edges"": []
            }";
            ScenarioData model = ModelWithGraph(json);

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("not a known comparison operator", r.Error);

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => host.ScenarioDirector.LoadScenario(model));
            Assert.Contains("not a known comparison operator", ex.Message);
        }

        // ── `_editor` bag size cap (review follow-up): DslBounds.MaxEditorBagBytes, enforced at parse. ──

        /// <summary>A single-node graph whose `_editor` bag's RAW JSON text is exactly <paramref name="bagBytes"/>
        /// bytes (all-ASCII, no interior whitespace, so raw text length == byte count).</summary>
        private static string GraphWithEditorBagOfSize(int bagBytes)
        {
            string bag = @"{""p"":""" + new string('x', bagBytes - 8) + @"""}"; // {"p":"…"} → 8 framing chars
            Assert.Equal(bagBytes, bag.Length);
            return @"{ ""nodes"": [ { ""id"": 0, ""kind"": ""trigger"", ""name"": ""t"", ""_editor"": " + bag + @" } ], ""exec_edges"": [], ""data_edges"": [] }";
        }

        [Fact]
        public void EditorBagAtTheCap_Parses()
        {
            TriggerGraph g = TriggerGraph.FromJson(GraphWithEditorBagOfSize(DslBounds.MaxEditorBagBytes));
            Assert.NotNull(g.Nodes[0].Editor);
        }

        [Fact]
        public void EditorBagOverTheCap_IsALocatedParseReject()
        {
            var ex = Assert.Throws<System.Text.Json.JsonException>(
                () => TriggerGraph.FromJson(GraphWithEditorBagOfSize(DslBounds.MaxEditorBagBytes + 1)));
            Assert.Contains("_editor", ex.Message);
            Assert.Contains("MaxEditorBagBytes", ex.Message);
        }

        [Fact]
        public void EditorBag_RoundTripsVerbatim_ThroughTheConverter()
        {
            var g = SoundGraph();
            using var doc = System.Text.Json.JsonDocument.Parse("{\"x\": 12, \"nested\": {\"a\": [1, 2]}}");
            g.Nodes[2].Editor = doc.RootElement.Clone();

            string json = g.ToCanonicalJson();
            Assert.Contains("_editor", json);

            TriggerGraph back = TriggerGraph.FromJson(json);
            NodeBase action = back.Nodes.Find(n => n.Id == 2)!;
            Assert.NotNull(action.Editor);
            Assert.Equal(12, action.Editor!.Value.GetProperty("x").GetInt32());
            // Stable across a second round-trip (deterministic canonical serialization).
            Assert.Equal(json, back.ToCanonicalJson());
            // And the structural gate never trips on it.
            Assert.Null(Check(back));
        }

        // ── Both-gates parity: the SAME malformed IR rejects at the validator AND the LoadScenario backstop ──

        private static ScenarioData ModelWithGraph(string graphJson) => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0 } },
            TriggerGraphJson = graphJson,
        };

        [Fact]
        public void MalformedGraph_RejectsAtBothGates_WithTheSameErrorClass()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "victory" }); // duplicate id 2
            string json = g.ToCanonicalJson();
            ScenarioData model = ModelWithGraph(json);

            // Gate 1: the validator — located Fail naming the duplicate.
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("duplicate node ids", r.Error);

            // Gate 2: the LoadScenario backstop — located JsonException of the same class.
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => host.ScenarioDirector.LoadScenario(model));
            Assert.Contains("duplicate node ids", ex.Message);
        }

        [Fact]
        public void LoopVarShadowing_RejectsAtBothGates()
        {
            // trigger 0 ← event 1; chain → for_each 2 (loop_var lv) body → for_each 3 (loop_var lv) body → action 4.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachNode { Id = 2, Source = "faction_units", Faction = -1, UpTo = 4, LoopVar = "lv" });
            g.Nodes.Add(new ForEachNode { Id = 3, Source = "faction_units", Faction = -1, UpTo = 4, LoopVar = "lv" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "display_message", Text = "x" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ForEachBodyOutPort, 4, TriggerGraph.ActionExecInPort));

            ScenarioData model = ModelWithGraph(g.ToCanonicalJson());
            model.Variables = new[]
            {
                new ScenarioVariable { Name = "lv", Type = DslValueType.Int, Scope = VarScope.TriggerLocal },
            };

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("already bound by enclosing for_each node 2", r.Error);
            Assert.Contains("'lv'", r.Error);

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => host.ScenarioDirector.LoadScenario(model));
            Assert.Contains("already bound by enclosing for_each node", ex.Message);
        }

        [Fact]
        public void SiblingLoops_MayReuseALoopVar()
        {
            // for_each 2 (lv) body → action 3; continuation → for_each 4 (lv) body → action 5 — NOT nested.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachNode { Id = 2, Source = "faction_units", Faction = -1, UpTo = 4, LoopVar = "lv" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "display_message", Text = "x" });
            g.Nodes.Add(new ForEachNode { Id = 4, Source = "faction_units", Faction = -1, UpTo = 4, LoopVar = "lv" });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "display_message", Text = "y" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort)); // continuation
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.ForEachBodyOutPort, 5, TriggerGraph.ActionExecInPort));

            ScenarioData model = ModelWithGraph(g.ToCanonicalJson());
            model.Variables = new[]
            {
                new ScenarioVariable { Name = "lv", Type = DslValueType.Int, Scope = VarScope.TriggerLocal },
            };
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
        }
    }
}
