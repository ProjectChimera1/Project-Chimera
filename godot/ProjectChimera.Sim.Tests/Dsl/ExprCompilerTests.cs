#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.4 — the LOAD-TIME expression compile phase: the strict-typing matrix (no implicit Int↔Fixed
    /// promotion; Bool+Fixed etc.), wire-type = inferred-type, undeclared/mis-scoped variable reads,
    /// TriggerLocal-in-condition, ref-typed reads, literal-zero divisors, forked/missing operand edges,
    /// the ExprBounds caps, and located-error content ("expr node &lt;id&gt;: …").
    /// </summary>
    public class ExprCompilerTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> Vars =
            new(StringComparer.Ordinal)
            {
                ["gold"]  = (DslValueType.Int,        VarScope.Global),
                ["rate"]  = (DslValueType.Fixed,      VarScope.Global),
                ["flag"]  = (DslValueType.Bool,       VarScope.Global),
                ["p"]     = (DslValueType.Point,      VarScope.Global),
                ["q"]     = (DslValueType.Point,      VarScope.Global),
                ["score"] = (DslValueType.Int,        VarScope.PerPlayer),
                ["tl"]    = (DslValueType.Int,        VarScope.TriggerLocal),
                ["ent"]   = (DslValueType.EntityRef,  VarScope.Global),
            };

        // ── Graph-building helpers (raw IR, ids assigned ascending) ───────────────

        private static int Add(TriggerGraph g, NodeBase n)
        {
            int id = g.Nodes.Count == 0 ? 0 : g.Nodes[^1].Id + 1;
            n.Id = id;
            g.Nodes.Add(n);
            return id;
        }

        private static void Wire(TriggerGraph g, int src, int dst, int port, DataWireType wire) =>
            g.DataEdges.Add(new DataEdge(src, TriggerGraph.ExprDataOutPort, dst, port, wire));

        private static int IntLit(TriggerGraph g, int value) =>
            Add(g, new ExprLiteralNode { ValueType = DslValueType.Int, Raw = value });

        private static int FixedLit(TriggerGraph g, int raw) =>
            Add(g, new ExprLiteralNode { ValueType = DslValueType.Fixed, Raw = raw });

        private static int BoolLit(TriggerGraph g, bool v) =>
            Add(g, new ExprLiteralNode { ValueType = DslValueType.Bool, Raw = v ? 1 : 0 });

        /// <summary>A binary op over two already-added operands, wires colored per the given types.</summary>
        private static int Binary(TriggerGraph g, string op, int left, DataWireType lw, int right, DataWireType rw)
        {
            int id = Add(g, new ExprBinaryNode { Op = op });
            Wire(g, left, id, TriggerGraph.ExprOperandPort0, lw);
            Wire(g, right, id, TriggerGraph.ExprOperandPort1, rw);
            return id;
        }

        private static string CompileError(TriggerGraph g, int root, bool inCondition = false)
        {
            bool ok = ExprCompiler.TryCompile(g, root, Vars, inCondition, out _, out string? error);
            Assert.False(ok, "expected a compile reject");
            Assert.NotNull(error);
            return error!;
        }

        // ── Strict-typing matrix ───────────────────────────────────────────────────

        [Fact]
        public void IntPlusFixed_IsRejected_NamingNodeAndBothTypes()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "add", IntLit(g, 1), DataWireType.Int, FixedLit(g, 98304), DataWireType.Fixed);
            string err = CompileError(g, root);
            Assert.Contains($"expr node {root}", err);
            Assert.Contains("Int", err);
            Assert.Contains("Fixed", err);
        }

        [Fact]
        public void BoolPlusFixed_IsRejected()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "add", BoolLit(g, true), DataWireType.Boolean, FixedLit(g, 98304), DataWireType.Fixed);
            string err = CompileError(g, root);
            Assert.Contains("Bool", err);
            Assert.Contains("Fixed", err);
        }

        [Fact]
        public void AndOnNumericOperands_IsRejected()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "and", IntLit(g, 1), DataWireType.Int, IntLit(g, 2), DataWireType.Int);
            Assert.Contains("Bool", CompileError(g, root));
        }

        [Fact]
        public void ComparisonAcrossIntAndFixed_IsRejected_NoImplicitPromotion()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "gt", IntLit(g, 1), DataWireType.Int, FixedLit(g, 65536), DataWireType.Fixed);
            Assert.Contains($"expr node {root}", CompileError(g, root));
        }

        [Fact]
        public void EqOnBools_IsAccepted_ProducingBool()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "eq", BoolLit(g, true), DataWireType.Boolean, BoolLit(g, false), DataWireType.Boolean);
            Assert.True(ExprCompiler.TryCompile(g, root, Vars, false, out ExprProgram? p, out string? err), err);
            Assert.Equal(DslValueType.Bool, p!.ResultType);
        }

        [Fact]
        public void NotOnInt_IsRejected()
        {
            var g = new TriggerGraph();
            int lit = IntLit(g, 1);
            int root = Add(g, new ExprUnaryNode { Op = "not" });
            Wire(g, lit, root, TriggerGraph.ExprOperandPort0, DataWireType.Int);
            Assert.Contains("Bool", CompileError(g, root));
        }

        [Fact]
        public void WireTypeMismatch_IsRejected()
        {
            // The operand's inferred type is Int, but the authored edge claims a Fixed wire — wire color = type.
            var g = new TriggerGraph();
            int root = Binary(g, "add", IntLit(g, 1), DataWireType.Fixed, IntLit(g, 2), DataWireType.Int);
            string err = CompileError(g, root);
            Assert.Contains("wire", err);
        }

        // ── Variable reads ─────────────────────────────────────────────────────────

        [Fact]
        public void UndeclaredVariable_IsRejected()
        {
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = "ghost" });
            Assert.Contains("undeclared", CompileError(g, root));
        }

        [Fact]
        public void TriggerLocalRead_InConditionExpression_IsRejected_ButAllowedInActionExpression()
        {
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = "tl" });
            string err = CompileError(g, root, inCondition: true);
            Assert.Contains("TriggerLocal", err);

            var g2 = new TriggerGraph();
            int root2 = Add(g2, new ExprVarNode { Name = "tl" });
            Assert.True(ExprCompiler.TryCompile(g2, root2, Vars, inCondition: false, out _, out string? e2), e2);
        }

        [Fact]
        public void SlottedRead_OnGlobalVariable_IsRejected()
        {
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = "gold", Faction = 2 }); // gold[2] on a Global var
            Assert.Contains("PerPlayer", CompileError(g, root));
        }

        [Fact]
        public void BareRead_OnPerPlayerVariable_IsRejected()
        {
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = "score" }); // bare `score` on a PerPlayer var
            Assert.Contains("PerPlayer", CompileError(g, root));
        }

        [Fact]
        public void PerPlayerSlotOutOfRange_IsRejected()
        {
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = "score", Faction = 8 });
            Assert.Contains("out of range", CompileError(g, root));
        }

        [Fact]
        public void BoolFalseDivisor_ReportsTypeMismatch_NotLiteralZero()
        {
            // `x / false`: the Bool literal ALSO has Raw 0, but the authoring error is the operand type — blaming
            // "the literal 0" would misquote something the author never wrote (review, 7.4 pass 2).
            var g = new TriggerGraph();
            int root = Binary(g, "div", IntLit(g, 6), DataWireType.Int, BoolLit(g, false), DataWireType.Boolean);
            string err = CompileError(g, root);
            Assert.DoesNotContain("literal-zero", err);
            Assert.Contains("Bool", err); // the real error: a Bool operand on a numeric operator
        }

        [Fact]
        public void StraySrcPortOnOperandEdge_IsRejected()
        {
            // Expression nodes emit only on ExprDataOutPort (= 0) — an operand edge leaving src_port 3 is a
            // non-canonical encoding the layer previously tolerated silently (review, 7.4 pass 2).
            var g = new TriggerGraph();
            int left  = IntLit(g, 1);
            int right = IntLit(g, 2);
            int id = Add(g, new ExprBinaryNode { Op = "add" });
            g.DataEdges.Add(new DataEdge(left, 3, id, TriggerGraph.ExprOperandPort0, DataWireType.Int)); // stray src_port
            g.DataEdges.Add(new DataEdge(right, TriggerGraph.ExprDataOutPort, id, TriggerGraph.ExprOperandPort1, DataWireType.Int));
            string err = CompileError(g, id);
            Assert.Contains("emit only on port", err);
        }

        [Theory]
        [InlineData(DslValueType.FactionRef, "fac")]
        [InlineData(DslValueType.TimerRef,   "tim")]
        [InlineData(DslValueType.Array,      "arr")]
        public void EveryRefAndArrayTypedVariableRead_IsRejected(DslValueType type, string name)
        {
            // The EntityRef reject was pinned; FactionRef/TimerRef/Array ride the same branch — pin each so a
            // per-type regression can't hide behind the single covered case (review, 7.4 pass 2).
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                [name] = (type, VarScope.Global),
            };
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = name });
            bool ok = ExprCompiler.TryCompile(g, root, decls, inCondition: false, out _, out string? error);
            Assert.False(ok);
            Assert.Contains(type.ToString(), error!);
        }

        [Fact]
        public void RefTypedVariableRead_IsRejected()
        {
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = "ent" }); // EntityRef
            Assert.Contains("EntityRef", CompileError(g, root));
        }

        // ── Divisors ───────────────────────────────────────────────────────────────

        [Fact]
        public void LiteralZeroDivisor_Div_IsRejectedAtCompile()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "div", IntLit(g, 6), DataWireType.Int, IntLit(g, 0), DataWireType.Int);
            Assert.Contains("literal-zero divisor", CompileError(g, root));
        }

        [Fact]
        public void LiteralZeroDivisor_Mod_IsRejectedAtCompile()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "mod", IntLit(g, 6), DataWireType.Int, IntLit(g, 0), DataWireType.Int);
            Assert.Contains("literal-zero divisor", CompileError(g, root));
        }

        [Fact]
        public void FixedLiteralZeroDivisor_IsRejectedAtCompile()
        {
            var g = new TriggerGraph();
            int root = Binary(g, "div", FixedLit(g, 98304), DataWireType.Fixed, FixedLit(g, 0), DataWireType.Fixed);
            Assert.Contains("literal-zero divisor", CompileError(g, root));
        }

        // ── Operand-edge structure ─────────────────────────────────────────────────

        [Fact]
        public void MissingOperandEdge_IsRejected()
        {
            var g = new TriggerGraph();
            int left = IntLit(g, 1);
            int root = Add(g, new ExprBinaryNode { Op = "add" });
            Wire(g, left, root, TriggerGraph.ExprOperandPort0, DataWireType.Int);
            // port 1 has no edge
            Assert.Contains("no incoming data edge", CompileError(g, root));
        }

        [Fact]
        public void ForkedOperandEdge_IsRejected()
        {
            var g = new TriggerGraph();
            int a = IntLit(g, 1);
            int b = IntLit(g, 2);
            int c = IntLit(g, 3);
            int root = Add(g, new ExprBinaryNode { Op = "add" });
            Wire(g, a, root, TriggerGraph.ExprOperandPort0, DataWireType.Int);
            Wire(g, b, root, TriggerGraph.ExprOperandPort1, DataWireType.Int);
            Wire(g, c, root, TriggerGraph.ExprOperandPort1, DataWireType.Int); // fork on port 1
            Assert.Contains("forked", CompileError(g, root));
        }

        [Fact]
        public void NonExprNodeOperand_IsRejected()
        {
            var g = new TriggerGraph();
            int cond = Add(g, new ConditionNode { Kind = "always" });
            int root = Add(g, new ExprUnaryNode { Op = "not" });
            Wire(g, cond, root, TriggerGraph.ExprOperandPort0, DataWireType.Boolean);
            Assert.Contains("not an expression node", CompileError(g, root));
        }

        // ── Built-in misuse ────────────────────────────────────────────────────────

        [Fact]
        public void CountWithNonIntArgument_IsRejected()
        {
            var g = new TriggerGraph();
            int arg = FixedLit(g, 65536);
            int root = Add(g, new ExprCallNode { Fn = "count" });
            Wire(g, arg, root, 0, DataWireType.Fixed);
            Assert.Contains("count", CompileError(g, root));
        }

        [Fact]
        public void CountWithExtraArgument_IsRejected()
        {
            var g = new TriggerGraph();
            int a0 = IntLit(g, 1);
            int a1 = IntLit(g, 2);
            int root = Add(g, new ExprCallNode { Fn = "count" });
            Wire(g, a0, root, 0, DataWireType.Int);
            Wire(g, a1, root, 1, DataWireType.Int); // count takes 1 argument
            Assert.Contains("argument", CompileError(g, root));
        }

        [Fact]
        public void CountWithLiteralFactionOutOfRange_IsRejected()
        {
            // A LITERAL count() slot outside [0, PlayerSlots) is statically knowable — rejected at compile
            // (mirroring the literal-zero divisor); a COMPUTED out-of-range slot instead evaluates to 0.
            var g = new TriggerGraph();
            int arg = IntLit(g, 99);
            int root = Add(g, new ExprCallNode { Fn = "count" });
            Wire(g, arg, root, 0, DataWireType.Int);
            string err = CompileError(g, root);
            Assert.Contains("out of range", err);
            Assert.Contains("99", err);

            var g2 = new TriggerGraph();
            int argNeg = IntLit(g2, -1);
            int root2 = Add(g2, new ExprCallNode { Fn = "count" });
            Wire(g2, argNeg, root2, 0, DataWireType.Int);
            Assert.Contains("out of range", CompileError(g2, root2));
        }

        [Fact]
        public void DistanceWithNonPointArguments_IsRejected()
        {
            var g = new TriggerGraph();
            int a0 = IntLit(g, 1);
            int a1 = IntLit(g, 2);
            int root = Add(g, new ExprCallNode { Fn = "distance" });
            Wire(g, a0, root, 0, DataWireType.Int);
            Wire(g, a1, root, 1, DataWireType.Int);
            Assert.Contains("Point", CompileError(g, root));
        }

        [Fact]
        public void MinAcrossIntAndFixed_IsRejected()
        {
            var g = new TriggerGraph();
            int a0 = IntLit(g, 1);
            int a1 = FixedLit(g, 65536);
            int root = Add(g, new ExprCallNode { Fn = "min" });
            Wire(g, a0, root, 0, DataWireType.Int);
            Wire(g, a1, root, 1, DataWireType.Fixed);
            Assert.Contains("min", CompileError(g, root));
        }

        // ── Root-type + literal-normalization rules ────────────────────────────────

        [Fact]
        public void PointRootedExpression_IsRejected()
        {
            // ExprProgram's contract says a root can never be Point (Eval returns ONE scalar raw — the Z half
            // would silently drop). No consumer accepts a Point root, so compile rejects it located.
            var g = new TriggerGraph();
            int root = Add(g, new ExprVarNode { Name = "p" }); // Point-typed global
            Assert.Contains("Point", CompileError(g, root));
        }

        [Fact]
        public void BoolLiteral_NonBinaryRaw_NormalizesAtCompile()
        {
            // An in-memory Bool literal with Raw=7 must compare equal to true (7 normalizes to 1 at compile —
            // the same 0/1 rule DslVarTable applies to Bool writes).
            var g = new TriggerGraph();
            int seven = Add(g, new ExprLiteralNode { ValueType = DslValueType.Bool, Raw = 7 });
            int root = Binary(g, "eq", seven, DataWireType.Boolean, BoolLit(g, true), DataWireType.Boolean);
            Assert.True(ExprCompiler.TryCompile(g, root, Vars, false, out ExprProgram? p, out string? err), err);
            Assert.Equal(1, p!.Eval(new DslVarTable(), null));
        }

        // ── Caps ───────────────────────────────────────────────────────────────────

        [Fact]
        public void OverMaxExprOps_IsRejected_NamingTheCap()
        {
            // A BALANCED add tree over 64 Int literals: 64 pushes + 63 adds = 127 ops (> MaxExprOps=64) at
            // depth 7 (≤ MaxExprDepth), so the ops cap — not the depth cap — is what trips.
            var g = new TriggerGraph();
            var layer = new List<int>();
            for (int i = 0; i < 64; i++) layer.Add(IntLit(g, 1));
            while (layer.Count > 1)
            {
                var next = new List<int>();
                for (int i = 0; i < layer.Count; i += 2)
                    next.Add(Binary(g, "add", layer[i], DataWireType.Int, layer[i + 1], DataWireType.Int));
                layer = next;
            }
            Assert.Contains("MaxExprOps", CompileError(g, layer[0]));
        }

        [Fact]
        public void OverMaxExprDepth_IsRejected_NamingTheCap()
        {
            // A chain of MaxExprDepth nested negs over a literal = depth MaxExprDepth+1 → depth reject.
            var g = new TriggerGraph();
            int cur = IntLit(g, 1);
            for (int i = 0; i < ExprBounds.MaxExprDepth; i++)
            {
                int neg = Add(g, new ExprUnaryNode { Op = "neg" });
                Wire(g, cur, neg, TriggerGraph.ExprOperandPort0, DataWireType.Int);
                cur = neg;
            }
            Assert.Contains("MaxExprDepth", CompileError(g, cur));
        }

        [Fact]
        public void CyclicExpressionSubgraph_IsRejected_WithACycleDiagnostic_NotAHang()
        {
            // A self-referential operand cycle (a → a) must reject deterministically, not spin — and the located
            // error names the CYCLE, not the depth cap (the walk-path guard fires long before depth accumulates).
            var g = new TriggerGraph();
            int a = Add(g, new ExprUnaryNode { Op = "neg" });
            Wire(g, a, a, TriggerGraph.ExprOperandPort0, DataWireType.Int);
            Assert.Contains("cycle detected at node", CompileError(g, a));

            // A two-node cycle (x → y → x) reports the same diagnostic.
            var g2 = new TriggerGraph();
            int x = Add(g2, new ExprUnaryNode { Op = "neg" });
            int y = Add(g2, new ExprUnaryNode { Op = "neg" });
            Wire(g2, y, x, TriggerGraph.ExprOperandPort0, DataWireType.Int);
            Wire(g2, x, y, TriggerGraph.ExprOperandPort0, DataWireType.Int);
            Assert.Contains("cycle detected at node", CompileError(g2, x));
        }

        [Fact]
        public void AtDepthCap_IsAccepted()
        {
            // Depth exactly MaxExprDepth (literal at depth MaxExprDepth via MaxExprDepth-1 negs) compiles.
            var g = new TriggerGraph();
            int cur = IntLit(g, 1);
            for (int i = 0; i < ExprBounds.MaxExprDepth - 1; i++)
            {
                int neg = Add(g, new ExprUnaryNode { Op = "neg" });
                Wire(g, cur, neg, TriggerGraph.ExprOperandPort0, DataWireType.Int);
                cur = neg;
            }
            Assert.True(ExprCompiler.TryCompile(g, cur, Vars, false, out _, out string? err), err);
        }
    }
}
