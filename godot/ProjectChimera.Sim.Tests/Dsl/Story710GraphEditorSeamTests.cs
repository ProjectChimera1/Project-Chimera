#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // ScenarioData, CanonicalModelHash
using ProjectChimera.Dsl;
using ProjectChimera.Effects;           // DirectHpDeltaEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.10 — the Godot-free SEAMS behind the T3 visual node-graph editor view: the <c>_editor</c> position
    /// annotation round-trip (<see cref="NodeEditorAnnotation"/>), the wire-color palette
    /// (<see cref="DataWireColorPalette"/>), the located structural-error path
    /// (<see cref="GraphStructureGate.CheckGraphLocated"/> + load-gate string parity), the graph-only
    /// classification predicate (the T2 read-only fallback detector), and the graph-channel position-edit
    /// round-trip (node-id + semantic-hash preservation). Every seam here is Tier-1 (no Godot), so the T3 view
    /// depends on the IR, never the reverse.
    /// </summary>
    public class Story710GraphEditorSeamTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> NoVars =
            new(System.StringComparer.Ordinal);
        private static readonly Dictionary<string, (DslValueType Elem, int Capacity)> NoArrays =
            new(System.StringComparer.Ordinal);

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

        // ── (a) NodeEditorAnnotation: round-trip, other-key preservation, cap reject ──────────────────────────

        [Fact]
        public void NodeEditorAnnotation_RoundTripsPosition()
        {
            var node = new ActionNode { Id = 7, Kind = "victory" };
            Assert.Null(NodeEditorAnnotation.GetPosition(node)); // no bag → no position
            NodeEditorAnnotation.SetPosition(node, 120, -40);
            var pos = NodeEditorAnnotation.GetPosition(node);
            Assert.NotNull(pos);
            Assert.Equal(120, pos!.Value.X);
            Assert.Equal(-40, pos.Value.Y);
        }

        [Fact]
        public void NodeEditorAnnotation_PreservesOtherKeys_OnReSave()
        {
            var node = new ActionNode { Id = 3, Kind = "victory" };
            using (var doc = JsonDocument.Parse("{\"note\":\"keep me\",\"x\":1,\"y\":2}"))
                node.Editor = doc.RootElement.Clone();

            NodeEditorAnnotation.SetPosition(node, 300, 400); // overwrite x/y, preserve note

            JsonElement bag = node.Editor!.Value;
            Assert.Equal("keep me", bag.GetProperty("note").GetString());
            Assert.Equal(300, bag.GetProperty("x").GetInt32());
            Assert.Equal(400, bag.GetProperty("y").GetInt32());
        }

        [Fact]
        public void NodeEditorAnnotation_OverCap_Rejects()
        {
            var node = new ActionNode { Id = 9, Kind = "victory" };
            // A pre-existing note that leaves no room for an x/y pair under the 4096 cap.
            string big = new string('z', DslBounds.MaxEditorBagBytes - 20);
            using (var doc = JsonDocument.Parse("{\"note\":\"" + big + "\"}"))
                node.Editor = doc.RootElement.Clone();

            var ex = Assert.Throws<JsonException>(() => NodeEditorAnnotation.SetPosition(node, 1, 1));
            Assert.Contains("MaxEditorBagBytes", ex.Message);
        }

        // ── (c) DataWireColorPalette: stable + all four data colors mutually distinct ─────────────────────────

        [Fact]
        public void DataWireColorPalette_IsStableAndDistinct()
        {
            string b = DataWireColorPalette.HexFor(DataWireType.Boolean);
            string i = DataWireColorPalette.HexFor(DataWireType.Int);
            string f = DataWireColorPalette.HexFor(DataWireType.Fixed);
            string p = DataWireColorPalette.HexFor(DataWireType.Point);
            var set = new HashSet<string>(System.StringComparer.Ordinal) { b, i, f, p };
            Assert.Equal(4, set.Count);                 // all four distinct
            Assert.DoesNotContain(DataWireColorPalette.ExecHex, set); // exec distinct from every data color
            // Stable: the same query returns the same value (no runtime derivation).
            Assert.Equal(b, DataWireColorPalette.HexFor(DataWireType.Boolean));
        }

        // ── (d) CheckGraphLocated: correct NodeId + load-gate string parity ───────────────────────────────────

        [Fact]
        public void CheckGraphLocated_DuplicateId_LocatesTheNode_AndStringParity()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "victory" }); // duplicate id 2

            IReadOnlyList<GraphNodeError> located = GraphStructureGate.CheckGraphLocated(g, NoVars, NoArrays);
            Assert.Single(located);
            Assert.Equal(2, located[0].NodeId);
            Assert.Contains("duplicate node ids", located[0].Message);

            // Byte-identical to the load-gate string (both project from one core).
            Assert.Equal(GraphStructureGate.Check(g, NoVars, NoArrays), located[0].Message);
        }

        [Fact]
        public void CheckGraphLocated_UnreachableExprCycle_LocatesTheNode()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ExprUnaryNode { Id = 10, Op = "neg" });
            g.Nodes.Add(new ExprUnaryNode { Id = 11, Op = "neg" });
            g.DataEdges.Add(new DataEdge(10, TriggerGraph.ExprDataOutPort, 11, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(11, TriggerGraph.ExprDataOutPort, 10, TriggerGraph.ExprOperandPort0, DataWireType.Int));

            IReadOnlyList<GraphNodeError> located = GraphStructureGate.CheckGraphLocated(g, NoVars, NoArrays);
            Assert.Single(located);
            Assert.Equal(10, located[0].NodeId);
            Assert.Contains("cyclic or mutually-consuming", located[0].Message);
            Assert.Equal(GraphStructureGate.Check(g, NoVars, NoArrays), located[0].Message);
        }

        [Fact]
        public void CheckGraphLocated_SoundGraph_IsEmpty_AndCheckIsNull()
        {
            var g = SoundGraph();
            Assert.Empty(GraphStructureGate.CheckGraphLocated(g, NoVars, NoArrays));
            Assert.Null(GraphStructureGate.Check(g, NoVars, NoArrays));
        }

        // PATCH 8 — located NodeId + Check-string parity across more error families.

        [Fact]
        public void CheckGraphLocated_DanglingExecEdge_LocatesMissingEndpoint_AndStringParity()
        {
            var g = SoundGraph();
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 999, TriggerGraph.ActionExecInPort));

            IReadOnlyList<GraphNodeError> located = GraphStructureGate.CheckGraphLocated(g, NoVars, NoArrays);
            Assert.Single(located);
            Assert.Equal(999, located[0].NodeId);
            Assert.Contains("does not exist", located[0].Message);
            Assert.Equal(GraphStructureGate.Check(g, NoVars, NoArrays), located[0].Message);
        }

        [Fact]
        public void CheckGraphLocated_ExecInPortMismatch_LocatesDst_AndStringParity()
        {
            var g = SoundGraph();
            // Action(2) exec-out into Trigger(0)'s condition-in DATA port (1) — not an exec-in port of a trigger.
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 0, TriggerGraph.TriggerConditionInPort));

            IReadOnlyList<GraphNodeError> located = GraphStructureGate.CheckGraphLocated(g, NoVars, NoArrays);
            Assert.Single(located);
            Assert.Equal(0, located[0].NodeId);
            Assert.Contains("is not an exec-in port", located[0].Message);
            Assert.Equal(GraphStructureGate.Check(g, NoVars, NoArrays), located[0].Message);
        }

        [Fact]
        public void CheckGraphLocated_DataInPortMismatch_LocatesDst_AndStringParity()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            // Condition(3) data-out into Action(2) port 5 — not a data-in port of an action (value=1 / index=2 only).
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ConditionDataOutPort, 2, 5, DataWireType.Boolean));

            IReadOnlyList<GraphNodeError> located = GraphStructureGate.CheckGraphLocated(g, NoVars, NoArrays);
            Assert.Single(located);
            Assert.Equal(2, located[0].NodeId);
            Assert.Contains("is not a data-in port", located[0].Message);
            Assert.Equal(GraphStructureGate.Check(g, NoVars, NoArrays), located[0].Message);
        }

        [Fact]
        public void CheckGraphLocated_ForkedExecOut_LocatesSrc_AndStringParity()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "victory" });
            // Trigger(0) exec-out already drives Action(2); a second edge out of the SAME port forks it.
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 3, TriggerGraph.ActionExecInPort));

            IReadOnlyList<GraphNodeError> located = GraphStructureGate.CheckGraphLocated(g, NoVars, NoArrays);
            Assert.Single(located);
            Assert.Equal(0, located[0].NodeId);
            Assert.Contains("multiple exec edges leave", located[0].Message);
            Assert.Equal(GraphStructureGate.Check(g, NoVars, NoArrays), located[0].Message);
        }

        // PATCH 1 — typed data-wire inference (all four wire types authorable).

        [Theory]
        [InlineData(DslValueType.Int,   DataWireType.Int)]
        [InlineData(DslValueType.Fixed, DataWireType.Fixed)]
        [InlineData(DslValueType.Bool,  DataWireType.Boolean)]
        [InlineData(DslValueType.Point, DataWireType.Point)]
        public void InferWire_ExprLiteralSource_MapsValueTypeToWire(DslValueType vt, DataWireType expected)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprLiteralNode { Id = 0, ValueType = vt, Raw = 0 });
            Assert.Equal(expected, DataWireInference.InferWireType(g, 0, destIsConditionSink: false, NoVars, NoArrays));
        }

        [Fact]
        public void InferWire_ConditionSink_IsBoolean_RegardlessOfSource()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprLiteralNode { Id = 0, ValueType = DslValueType.Int, Raw = 1 });
            Assert.Equal(DataWireType.Boolean,
                DataWireInference.InferWireType(g, 0, destIsConditionSink: true, NoVars, NoArrays));
        }

        [Fact]
        public void InferWire_ExprVarSource_UsesDeclaredType()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprVarNode { Id = 0, Name = "rate" });
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(System.StringComparer.Ordinal)
                { ["rate"] = (DslValueType.Fixed, VarScope.Global) };
            Assert.Equal(DataWireType.Fixed,
                DataWireInference.InferWireType(g, 0, destIsConditionSink: false, decls, NoArrays));
        }

        // PATCH 2 — single-edge validation, isolated from pre-existing graph errors.

        [Fact]
        public void TryValidateNewEdge_RejectsIllegalEdge_EvenWithPreexistingStructuralError()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "victory" }); // pre-existing: duplicate node id 2

            // An independently-illegal NEW edge: Event(1) exec-out into Trigger(0)'s condition-in DATA port (not exec-in).
            string? err = GraphStructureGate.TryValidateNewEdge(
                g, isData: false, src: 1, srcPort: TriggerGraph.EventExecOutPort,
                dst: 0, dstPort: TriggerGraph.TriggerConditionInPort, wire: default);
            Assert.NotNull(err);
        }

        [Fact]
        public void TryValidateNewEdge_AcceptsLegalEdge_EvenWithPreexistingStructuralError()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "victory" });   // pre-existing: duplicate node id 2
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" }); // a legal data source

            // A legitimate NEW data edge (Condition → Trigger condition-in) is accepted despite the unrelated dup id.
            string? err = GraphStructureGate.TryValidateNewEdge(
                g, isData: true, src: 3, srcPort: TriggerGraph.ConditionDataOutPort,
                dst: 0, dstPort: TriggerGraph.TriggerConditionInPort, wire: DataWireType.Boolean);
            Assert.Null(err);
        }

        // ── (e) graph-only classification predicate ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("raise_event")]
        [InlineData("custom_event")]
        [InlineData("expr_event_param")]
        [InlineData("for_each")]
        [InlineData("for_each_batched")]
        [InlineData("branch")]
        [InlineData("array_push")]
        [InlineData("array_set")]
        [InlineData("array_clear")]
        public void GraphOnlyKind_ClassifiesGraphOnlyConstructs(string kind)
            => Assert.True(TriggerGraph.IsGraphOnlyKind(kind));

        [Theory]
        [InlineData("match_start")]
        [InlineData("unit_dies")]
        [InlineData("always")]
        [InlineData("resource_comparison")]
        [InlineData("spawn_unit")]
        [InlineData("display_message")]
        [InlineData("victory")]
        [InlineData("set_variable")]
        public void GraphOnlyKind_DoesNotClassifyFlatEca(string kind)
            => Assert.False(TriggerGraph.IsGraphOnlyKind(kind));

        [Fact]
        public void ContainsGraphOnly_TrueForACustomEventSubscription_FalseForFlat()
        {
            // A custom_event subscription EventNode is graph-only (ToFlat fails closed on it).
            var graphOnly = new TriggerGraph();
            graphOnly.Nodes.Add(new EventNode { Id = 0, Kind = NodeKinds.CustomEvent, EventName = "wave" });
            Assert.True(graphOnly.ContainsGraphOnly());

            Assert.False(TriggerGraph.FromFlat(new[]
            {
                new TriggerDefinition
                {
                    Name = "flat",
                    Events = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[] { new TriggerAction { Type = "display_message", Text = "hi" } },
                },
            }).ContainsGraphOnly());
        }

        // ── (f) graph-channel round-trip: FromJson → position edit → ToCanonicalJson preserves ids + hash ──────

        [Fact]
        public void GraphChannel_PositionEdit_PreservesNodeIds_AndSemanticHash()
        {
            string original = TriggerGraph
                .BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)))
                .ToCanonicalJson();

            var a = ModelWithGraph(original);
            ulong hashBefore = CanonicalModelHash.Compute(a);

            // Open (FromJson), move every node, re-canonicalize (the T3 save path).
            TriggerGraph g = TriggerGraph.FromJson(original);
            var idsBefore = new HashSet<int>();
            int i = 0;
            foreach (NodeBase n in g.Nodes)
            {
                idsBefore.Add(n.Id);
                NodeEditorAnnotation.SetPosition(n, 100 + i * 40, -20 * i);
                i++;
            }
            string moved = g.ToCanonicalJson();

            // Node ids preserved by construction (a T3 save never re-ids).
            var idsAfter = new HashSet<int>();
            foreach (NodeBase n in TriggerGraph.FromJson(moved).Nodes) idsAfter.Add(n.Id);
            Assert.Equal(idsBefore, idsAfter);

            // The move is cosmetic: the canonical MP handshake hash is byte-identical before vs after.
            var b = ModelWithGraph(moved);
            Assert.Equal(hashBefore, CanonicalModelHash.Compute(b));

            // And the positions read back.
            var pos = NodeEditorAnnotation.GetPosition(TriggerGraph.FromJson(moved).Nodes[0]);
            Assert.NotNull(pos);
        }

        private static ScenarioData ModelWithGraph(string graphJson) => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0 } },
            TriggerGraphJson = graphJson,
        };

        // ══ Follow-up review pass — coverage for the seams the first pass introduced or this pass adds ══════════

        // ── DataWireInference: compiled-expression sources + the explicit unknown state ──

        [Fact]
        public void InferWire_CompiledBinarySource_OverFixedOperands_IsFixed()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprLiteralNode { Id = 0, ValueType = DslValueType.Fixed, Raw = Fixed.FromInt(2).Raw });
            g.Nodes.Add(new ExprLiteralNode { Id = 1, ValueType = DslValueType.Fixed, Raw = Fixed.FromInt(3).Raw });
            g.Nodes.Add(new ExprBinaryNode { Id = 2, Op = "add" });
            g.DataEdges.Add(new DataEdge(0, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ExprOperandPort0, DataWireType.Fixed));
            g.DataEdges.Add(new DataEdge(1, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ExprOperandPort1, DataWireType.Fixed));

            Assert.Equal(DataWireType.Fixed, DataWireInference.InferWireType(g, 2, destIsConditionSink: false, NoVars, NoArrays));
            Assert.Equal(DataWireType.Fixed, DataWireInference.TryInferSourceType(g, 2, NoVars, NoArrays));
        }

        [Fact]
        public void InferWire_ConditionNodeSource_IsBoolean()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ConditionNode { Id = 0, Kind = "always" });
            Assert.Equal(DataWireType.Boolean, DataWireInference.InferWireType(g, 0, destIsConditionSink: false, NoVars, NoArrays));
            Assert.Equal(DataWireType.Boolean, DataWireInference.TryInferSourceType(g, 0, NoVars, NoArrays));
        }

        [Fact]
        public void TryInferSourceType_WorkInProgressExpr_IsUnknown_NotInt()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprBinaryNode { Id = 0, Op = "add" }); // no operands wired → cannot compile
            g.Nodes.Add(new ExprVarNode { Id = 1, Name = "undeclared" });
            Assert.Null(DataWireInference.TryInferSourceType(g, 0, NoVars, NoArrays));
            Assert.Null(DataWireInference.TryInferSourceType(g, 1, NoVars, NoArrays));
            Assert.Null(DataWireInference.TryInferSourceType(g, 999, NoVars, NoArrays)); // missing source
        }

        // ── TryValidateNewEdge: every rejection family (fork, fan-in, wire, cycle) + sanctioned fan-in ──

        [Fact]
        public void TryValidateNewEdge_RejectsForkedExecOut()
        {
            var g = SoundGraph(); // Trigger(0) exec-out already drives Action(2)
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "victory" });
            string? err = GraphStructureGate.TryValidateNewEdge(
                g, isData: false, src: 0, srcPort: TriggerGraph.TriggerExecOutPort,
                dst: 3, dstPort: TriggerGraph.ActionExecInPort, wire: default);
            Assert.NotNull(err);
            Assert.Contains("already drives an edge", err);
        }

        [Fact]
        public void TryValidateNewEdge_RejectsDataFanIn_OnNonFanInPort()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ExprLiteralNode { Id = 10, ValueType = DslValueType.Int, Raw = 1 });
            g.Nodes.Add(new ExprLiteralNode { Id = 11, ValueType = DslValueType.Int, Raw = 2 });
            g.DataEdges.Add(new DataEdge(10, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            string? err = GraphStructureGate.TryValidateNewEdge(
                g, isData: true, src: 11, srcPort: TriggerGraph.ExprDataOutPort,
                dst: 2, dstPort: TriggerGraph.ActionValueInPort, wire: DataWireType.Int);
            Assert.NotNull(err);
            Assert.Contains("already has an incoming edge", err);
        }

        [Fact]
        public void TryValidateNewEdge_AcceptsTriggerConditionInFanIn()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            g.Nodes.Add(new ConditionNode { Id = 4, Kind = "always" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ConditionDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));

            // The trigger condition-in port's fan-in is the sanctioned multi-condition AND wiring.
            Assert.Null(GraphStructureGate.TryValidateNewEdge(
                g, isData: true, src: 4, srcPort: TriggerGraph.ConditionDataOutPort,
                dst: 0, dstPort: TriggerGraph.TriggerConditionInPort, wire: DataWireType.Boolean));
        }

        [Fact]
        public void TryValidateNewEdge_RejectsUndefinedWire()
        {
            var g = SoundGraph();
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            string? err = GraphStructureGate.TryValidateNewEdge(
                g, isData: true, src: 3, srcPort: TriggerGraph.ConditionDataOutPort,
                dst: 0, dstPort: TriggerGraph.TriggerConditionInPort, wire: (DataWireType)99);
            Assert.NotNull(err);
            Assert.Contains("not a known DataWireType", err);
        }

        [Fact]
        public void TryValidateNewEdge_RejectsExecCycle()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "victory" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "victory" });
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));

            string? err = GraphStructureGate.TryValidateNewEdge(
                g, isData: false, src: 3, srcPort: TriggerGraph.ActionExecOutPort,
                dst: 2, dstPort: TriggerGraph.ActionExecInPort, wire: default);
            Assert.NotNull(err);
            Assert.Contains("exec cycle", err);
        }

        [Fact]
        public void TryValidateNewEdge_RejectsDataCycle()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprUnaryNode { Id = 10, Op = "neg" });
            g.Nodes.Add(new ExprUnaryNode { Id = 11, Op = "neg" });
            g.DataEdges.Add(new DataEdge(10, TriggerGraph.ExprDataOutPort, 11, TriggerGraph.ExprOperandPort0, DataWireType.Int));

            string? err = GraphStructureGate.TryValidateNewEdge(
                g, isData: true, src: 11, srcPort: TriggerGraph.ExprDataOutPort,
                dst: 10, dstPort: TriggerGraph.ExprOperandPort0, wire: DataWireType.Int);
            Assert.NotNull(err);
            Assert.Contains("data cycle", err);
        }

        // ── FindExecCycle: the editor-side scan for the load path's WalkChain cycle reject ──

        [Fact]
        public void FindExecCycle_LocatesACycleNode_AndIsNullOnSoundGraph()
        {
            Assert.Null(GraphStructureGate.FindExecCycle(SoundGraph()));

            var g = new TriggerGraph();
            g.Nodes.Add(new ActionNode { Id = 0, Kind = "victory" });
            g.Nodes.Add(new ActionNode { Id = 1, Kind = "victory" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.ActionExecOutPort, 1, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.ActionExecOutPort, 0, TriggerGraph.ActionExecInPort));

            GraphNodeError? err = GraphStructureGate.FindExecCycle(g);
            Assert.NotNull(err);
            Assert.Equal(0, err!.Value.NodeId); // deterministic: ascending-id DFS finds the back edge into node 0
            Assert.Contains("exec edges form a cycle", err.Value.Message);
        }

        // ── NodePaletteFactory: the palette IS the closed NodeKinds union, and every default round-trips ──

        [Fact]
        public void PaletteFactory_EveryKind_Constructs_MatchesKind_AndSurvivesSerializeReparse()
        {
            Assert.NotEmpty(NodePaletteFactory.PaletteKinds);
            foreach (string kind in NodePaletteFactory.PaletteKinds)
            {
                NodeBase? node = NodePaletteFactory.Create(kind, 5);
                Assert.NotNull(node);
                Assert.Equal(kind, NodeKinds.KindOf(node!));
                Assert.Equal(5, node!.Id);

                // The parse-safety guarantee: a freshly-added default node serializes and RE-PARSES cleanly, so a
                // T3 save of work-in-progress nodes can never brick the stored graph channel into unparseability.
                var g = new TriggerGraph();
                g.Nodes.Add(node);
                TriggerGraph reparsed = TriggerGraph.FromJson(g.ToCanonicalJson());
                Assert.Single(reparsed.Nodes);
                Assert.Equal(5, reparsed.Nodes[0].Id);
                Assert.Equal(kind, NodeKinds.KindOf(reparsed.Nodes[0]));
            }
        }

        [Fact]
        public void PaletteFactory_UnknownKind_ReturnsNull()
            => Assert.Null(NodePaletteFactory.Create("not_a_kind", 0));

        // ── NodeEditorAnnotation: a non-object bag is refused, never clobbered ──

        [Fact]
        public void NodeEditorAnnotation_NonObjectBag_FailsClosed_AndPreservesTheBag()
        {
            var node = new ActionNode { Id = 4, Kind = "victory" };
            using (var doc = JsonDocument.Parse("\"an authored note, not an object\""))
                node.Editor = doc.RootElement.Clone();

            var ex = Assert.Throws<JsonException>(() => NodeEditorAnnotation.SetPosition(node, 1, 2));
            Assert.Contains("not a JSON object", ex.Message);
            Assert.Equal(JsonValueKind.String, node.Editor!.Value.ValueKind); // untouched
        }
    }
}
