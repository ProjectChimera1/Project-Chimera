#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.6 — the array substrate: DslVarTable storage/ops with TOTAL runtime semantics (push at capacity =
    /// deterministic no-op; OOB set = no-op; OOB read = 0 — the div-by-zero precedent), the two array expression
    /// forms (<c>arr[expr]</c> / <c>length(arr)</c>) through parser + compiler + evaluator, the closed-registry
    /// converter round-trip for every new node kind, and the fail-closed parse of unknown sources.
    /// </summary>
    public class DslArrayTests
    {
        private static DslVarTable NewTableWithIntArray(string name = "arr", int capacity = 4)
        {
            var vars = new DslVarTable();
            vars.InitFromDeclarations(new[]
            {
                new DslVarDecl(name, DslValueType.Array, VarScope.Global, 0, 0, elementType: DslValueType.Int, capacity: capacity),
            }, Array.Empty<DslTimerDecl>());
            return vars;
        }

        // ── DslVarTable total-semantics ops ─────────────────────────────────────

        [Fact]
        public void ArrayPush_AppendsUntilCapacity_ThenNoOps()
        {
            DslVarTable vars = NewTableWithIntArray(capacity: 2);
            vars.ArrayPush("arr", 3);
            vars.ArrayPush("arr", 5);
            Assert.Equal(2, vars.ArrayLen("arr"));
            vars.ArrayPush("arr", 7); // at capacity → deterministic no-op
            Assert.Equal(2, vars.ArrayLen("arr"));
            Assert.Equal(3, vars.ArrayGet("arr", 0));
            Assert.Equal(5, vars.ArrayGet("arr", 1));
        }

        [Fact]
        public void ArraySetAndGet_AreTotal_OobIsNoOpAndZero()
        {
            DslVarTable vars = NewTableWithIntArray();
            vars.ArrayPush("arr", 3);
            vars.ArraySet("arr", 0, 9);
            Assert.Equal(9, vars.ArrayGet("arr", 0));
            vars.ArraySet("arr", -1, 1);  // OOB → no-op
            vars.ArraySet("arr", 99, 1);  // OOB → no-op
            vars.ArraySet("arr", 1, 1);   // beyond LIVE count → no-op
            Assert.Equal(1, vars.ArrayLen("arr"));
            Assert.Equal(0, vars.ArrayGet("arr", 99)); // OOB read → 0
            Assert.Equal(0, vars.ArrayGet("arr", -1));
            Assert.Equal(0, vars.ArrayGet("nope", 0)); // unknown name → 0
            Assert.Equal(0, vars.ArrayLen("nope"));
        }

        [Fact]
        public void ArrayClear_ResetsCount()
        {
            DslVarTable vars = NewTableWithIntArray();
            vars.ArrayPush("arr", 3);
            vars.ArrayClear("arr");
            Assert.Equal(0, vars.ArrayLen("arr"));
            Assert.Equal(0, vars.ArrayGet("arr", 0)); // cleared elements are unreadable
        }

        [Fact]
        public void BoolElementArray_NormalizesWritesToZeroOne()
        {
            var vars = new DslVarTable();
            vars.InitFromDeclarations(new[]
            {
                new DslVarDecl("flags", DslValueType.Array, VarScope.Global, 0, 0, elementType: DslValueType.Bool, capacity: 4),
            }, Array.Empty<DslTimerDecl>());
            vars.ArrayPush("flags", 7); // truthy → normalized 1 (the central Bool 0/1 rule)
            Assert.Equal(1, vars.ArrayGet("flags", 0));
            vars.ArraySet("flags", 0, 0);
            Assert.Equal(0, vars.ArrayGet("flags", 0));
        }

        [Fact]
        public void TryGetDecl_ResolvesArrayNames()
        {
            DslVarTable vars = NewTableWithIntArray();
            Assert.True(vars.TryGetDecl("arr", out DslValueType t, out VarScope s));
            Assert.Equal(DslValueType.Array, t);
            Assert.Equal(VarScope.Global, s);
            Assert.True(vars.TryGetArrayDecl("arr", out DslValueType elem, out int cap));
            Assert.Equal(DslValueType.Int, elem);
            Assert.Equal(4, cap);
        }

        // ── Parser + compiler + evaluator: arr[expr] / length(arr) ─────────────

        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> Decls =
            new(StringComparer.Ordinal)
            {
                ["arr"] = (DslValueType.Array, VarScope.Global),
                ["i"]   = (DslValueType.Int, VarScope.Global),
                ["pp"]  = (DslValueType.Int, VarScope.PerPlayer),
            };

        private static readonly Dictionary<string, (DslValueType Elem, int Capacity)> ArrayDecls =
            new(StringComparer.Ordinal) { ["arr"] = (DslValueType.Int, 4) };

        private static ExprProgram Compile(string text)
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse(text, g, Decls, ArrayDecls);
            Assert.True(ExprCompiler.TryCompile(g, root, Decls, inCondition: false, out ExprProgram? p, out string? err, ArrayDecls), err);
            return p!;
        }

        [Fact]
        public void ArrayGetExpression_ReadsElements_AndOobReadsZero()
        {
            DslVarTable vars = NewTableWithIntArray();
            vars.ArrayPush("arr", 3);
            vars.ArrayPush("arr", 5);
            Assert.Equal(3, Compile("arr[0]").Eval(vars, null));
            Assert.Equal(5, Compile("arr[0 + 1]").Eval(vars, null)); // FULL index expression, not a literal slot
            Assert.Equal(0, Compile("arr[99]").Eval(vars, null));    // OOB → 0 (total semantics)
            Assert.Equal(0, Compile("arr[0 - 1]").Eval(vars, null)); // negative → 0
        }

        [Fact]
        public void LengthExpression_ReturnsLiveCount()
        {
            DslVarTable vars = NewTableWithIntArray();
            Assert.Equal(0, Compile("length(arr)").Eval(vars, null));
            vars.ArrayPush("arr", 3);
            vars.ArrayPush("arr", 5);
            Assert.Equal(2, Compile("length(arr)").Eval(vars, null));
        }

        [Fact]
        public void ArrayGetResultType_IsElementType_AndComposes()
        {
            ExprProgram p = Compile("arr[0] + 1"); // element Int + Int literal
            Assert.Equal(DslValueType.Int, p.ResultType);
            ExprProgram cmp = Compile("length(arr) >= 2");
            Assert.Equal(DslValueType.Bool, cmp.ResultType);
        }

        [Fact]
        public void BareArrayRead_IsRejected_DirectingToTheLegalForms()
        {
            var g = new TriggerGraph();
            var ex = Assert.Throws<JsonException>(() => ExprParser.Parse("arr + 1", g, Decls, ArrayDecls));
            Assert.Contains("Array-typed", ex.Message);
        }

        [Fact]
        public void NonIntIndex_IsRejected()
        {
            var g = new TriggerGraph();
            var ex = Assert.Throws<JsonException>(() => ExprParser.Parse("arr[1.5]", g, Decls, ArrayDecls));
            Assert.Contains("index must be Int", ex.Message);
        }

        [Fact]
        public void LengthOfNonArray_IsRejected()
        {
            var g = new TriggerGraph();
            var ex = Assert.Throws<JsonException>(() => ExprParser.Parse("length(i)", g, Decls, ArrayDecls));
            Assert.Contains("not one", ex.Message);
        }

        [Fact]
        public void PerPlayerSlotRead_KeepsLiteralOnlyForm()
        {
            // The 7.4 disambiguation is DECLARED-SCOPE-driven: PerPlayer keeps the literal-only name[k] form —
            // a non-literal slot still rejects exactly as before.
            var g = new TriggerGraph();
            var ex = Assert.Throws<JsonException>(() => ExprParser.Parse("pp[i]", g, Decls, ArrayDecls));
            Assert.Contains("integer-literal player slot", ex.Message);
            var ex2 = Assert.Throws<JsonException>(() => ExprParser.Parse("pp[0 + 1]", new TriggerGraph(), Decls, ArrayDecls));
            Assert.Contains("expected ']'", ex2.Message); // the literal form consumes digits only
        }

        [Fact]
        public void CompilerRejects_UndeclaredArrayNodes()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprArrayLenNode { Id = 0, Name = "ghost" });
            Assert.False(ExprCompiler.TryCompile(g, 0, Decls, inCondition: false, out _, out string? err, ArrayDecls));
            Assert.Contains("undeclared array 'ghost'", err);
        }

        // ── Converter round-trip (IR round-trip I/O row) ────────────────────────

        [Fact]
        public void LoopBranchArraySubgraph_RoundTripsByteIdentically()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachNode { Id = 2, Source = "array", ArrayName = "arr", LoopVar = "v", UpTo = 4 });
            g.Nodes.Add(new ForEachNode { Id = 3, Source = "region_units", Faction = -1, RegionId = "zone", UpTo = 8 });
            g.Nodes.Add(new ForEachBatchedNode { Id = 4, Source = "faction_units", Faction = 1, BatchSize = 10 });
            g.Nodes.Add(new BranchNode { Id = 5 });
            g.Nodes.Add(new ActionNode { Id = 6, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 7, Kind = "array_set", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 8, Kind = "array_clear", Variable = "arr" });
            g.Nodes.Add(new ExprArrayGetNode { Id = 9, Name = "arr" });
            g.Nodes.Add(new ExprArrayLenNode { Id = 10, Name = "arr" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 6, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(5, TriggerGraph.BranchThenOutPort, 7, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(5, TriggerGraph.BranchElseOutPort, 8, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(9, TriggerGraph.ExprDataOutPort, 7, TriggerGraph.ActionValueInPort, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(10, TriggerGraph.ExprDataOutPort, 7, TriggerGraph.ActionIndexInPort, DataWireType.Int));

            string json = g.ToCanonicalJson();
            TriggerGraph back = TriggerGraph.FromJson(json);
            Assert.Equal(json, back.ToCanonicalJson()); // byte-identical canonical round-trip

            var fe = Assert.IsType<ForEachNode>(back.Nodes.Find(n => n.Id == 2));
            Assert.Equal("array", fe.Source);
            Assert.Equal("arr", fe.ArrayName);
            Assert.Equal("v", fe.LoopVar);
            Assert.Equal(4, fe.UpTo);
            var fb = Assert.IsType<ForEachBatchedNode>(back.Nodes.Find(n => n.Id == 4));
            Assert.Equal(10, fb.BatchSize);
            Assert.Equal(1, fb.Faction);
        }

        [Fact]
        public void UnknownForEachSource_FailsClosedAtParse()
        {
            string json = """
                { "nodes": [ { "id": 0, "kind": "for_each", "source": "all_units", "up_to": 4 } ],
                  "exec_edges": [], "data_edges": [] }
                """;
            var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("not a known for_each source", ex.Message);
        }

        [Fact]
        public void BatchedArraySource_FailsClosedAtParse()
        {
            string json = """
                { "nodes": [ { "id": 0, "kind": "for_each_batched", "source": "array", "batch_size": 4 } ],
                  "exec_edges": [], "data_edges": [] }
                """;
            var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("arrays never need batching", ex.Message);
        }

        [Fact]
        public void StrayPropertyOnForEach_FailsClosed()
        {
            string json = """
                { "nodes": [ { "id": 0, "kind": "for_each", "source": "array", "array_name": "a", "while": true } ],
                  "exec_edges": [], "data_edges": [] }
                """;
            var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("unknown property", ex.Message);
        }

        // ── Grammar closure: no While/recursion/goto form exists ────────────────

        [Fact]
        public void NoWhileRecursionOrGotoKind_ExistsInTheRegistry()
        {
            foreach (string kind in new[] { "while", "loop", "goto", "recurse", "call_trigger" })
            {
                string json = $$"""
                    { "nodes": [ { "id": 0, "kind": "{{kind}}" } ], "exec_edges": [], "data_edges": [] }
                    """;
                var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
                Assert.Contains("unknown node kind", ex.Message);
            }
        }
    }
}
