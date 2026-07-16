#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.4 — the CEL-shaped TEXT surface: precedence/associativity/parens, the no-floating-point Fixed
    /// literal conversion (pure integer math: "1.5" → raw 98304), name[k] reads, call syntax, located syntax
    /// errors, the text-length/depth caps, parse→compile→eval end-to-end, and byte-identical canonical-JSON
    /// round-trips of parser output (one IR, no second executable form).
    /// </summary>
    public class ExprParserTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> Vars =
            new(StringComparer.Ordinal)
            {
                ["gold"]  = (DslValueType.Int,   VarScope.Global),
                ["done"]  = (DslValueType.Bool,  VarScope.Global),
                ["score"] = (DslValueType.Int,   VarScope.PerPlayer),
                ["p"]     = (DslValueType.Point, VarScope.Global),
                ["q"]     = (DslValueType.Point, VarScope.Global),
            };

        private static int ParseCompileEval(string text, DslVarTable? table = null, IExprWorld? world = null)
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse(text, g, Vars);
            Assert.True(ExprCompiler.TryCompile(g, root, Vars, inCondition: false,
                out ExprProgram? p, out string? err), err);
            return p!.Eval(table ?? new DslVarTable(), world);
        }

        private static JsonException ParseError(string text)
        {
            var g = new TriggerGraph();
            return Assert.Throws<JsonException>(() => ExprParser.Parse(text, g, Vars));
        }

        // ── Precedence / associativity / grouping ──────────────────────────────────

        [Theory]
        [InlineData("1 + 2 * 3", 7)]
        [InlineData("(1 + 2) * 3", 9)]
        [InlineData("10 - 3 - 2", 5)]
        [InlineData("20 / 4 / 5", 1)]
        [InlineData("-3 * -2", 6)]
        [InlineData("2 * 3 % 4", 2)]
        public void PrecedenceAndAssociativity(string text, int expected) =>
            Assert.Equal(expected, ParseCompileEval(text));

        [Fact]
        public void BooleanPrecedence_AndBindsTighterThanOr()
        {
            // true || X && false → true || (X && false) → true; the (true || X) && false reading would be 0.
            Assert.Equal(1, ParseCompileEval("true || false && false"));
            Assert.Equal(0, ParseCompileEval("(true || false) && false"));
            Assert.Equal(1, ParseCompileEval("!false"));
        }

        [Fact]
        public void ComparisonPrecedence_ArithmeticBindsTighter()
        {
            Assert.Equal(1, ParseCompileEval("1 + 2 > 2"));
            Assert.Equal(0, ParseCompileEval("1 + 2 < 3"));
        }

        // ── Fixed-literal integer-math parse (no floating point anywhere) ──────────

        [Theory]
        [InlineData("1.5", 98304)]        // (1 << 16) + 32768
        [InlineData("0.5", 32768)]
        [InlineData("0.25", 16384)]
        [InlineData("2.0", 131072)]
        [InlineData("0.00002", 1)]        // (2 * 65536 + 50000) / 100000 = 1 (round-half-up)
        [InlineData("0.00001", 1)]        // (1 * 65536 + 50000) / 100000 = 1 (round-half-up)
        [InlineData("32767.99998", 2147483647)] // the 16.16 ceiling parses exactly to int.MaxValue raw
        public void FixedLiteral_ParsesViaIntegerMath(string text, int expectedRaw)
        {
            var g = new TriggerGraph();
            (int root, DataWireType wire) = ExprParser.Parse(text, g, Vars);
            Assert.Equal(DataWireType.Fixed, wire);
            var lit = Assert.IsType<ExprLiteralNode>(g.Nodes.Single(n => n.Id == root));
            Assert.Equal(DslValueType.Fixed, lit.ValueType);
            Assert.Equal(expectedRaw, lit.Raw);
        }

        [Fact]
        public void FixedLiteral_OverRange_IsRejected()
        {
            Assert.Contains("16.16", ParseError("32768.0").Message);
        }

        [Fact]
        public void FixedLiteral_TooManyFractionDigits_IsRejected()
        {
            Assert.Contains("fraction digits", ParseError("1.123456").Message);
        }

        [Fact]
        public void MultiDotNumber_IsRejected()
        {
            var ex = ParseError("1.5.2");
            Assert.Contains("pos", ex.Message);
        }

        // ── Variable + call syntax ─────────────────────────────────────────────────

        [Fact]
        public void PerPlayerSlotRead_ParsesToExprVarNodeWithFaction()
        {
            var g = new TriggerGraph();
            (int root, DataWireType wire) = ExprParser.Parse("score[3]", g, Vars);
            Assert.Equal(DataWireType.Int, wire);
            var v = Assert.IsType<ExprVarNode>(g.Nodes.Single(n => n.Id == root));
            Assert.Equal("score", v.Name);
            Assert.Equal(3, v.Faction);
        }

        [Fact]
        public void BareGlobalRead_ParsesWithNoSlot()
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse("gold", g, Vars);
            var v = Assert.IsType<ExprVarNode>(g.Nodes.Single(n => n.Id == root));
            Assert.Equal(-1, v.Faction);
        }

        [Fact]
        public void CallSyntax_ParsesBuiltins()
        {
            Assert.Equal(2, ParseCompileEval("min(2, 3)"));
            Assert.Equal(4, ParseCompileEval("abs(0 - 4)"));
            Assert.Equal(7, ParseCompileEval("max(min(7, 9), 3)"));
        }

        // ── Located errors ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData("1 +")]              // dangling operator
        [InlineData("gold ??")]          // unknown operator garbage
        [InlineData("(1 + 2")]           // unbalanced paren
        [InlineData("ghost")]            // undeclared variable
        [InlineData("foo(1)")]           // unknown function
        [InlineData("score")]            // bare PerPlayer read
        [InlineData("gold[1]")]          // slot read on a Global
        [InlineData("score[9]")]         // slot out of range
        [InlineData("done + 1")]         // Bool + Int mismatch
        [InlineData("1 + 1.5")]          // Int + Fixed (no implicit promotion)
        [InlineData("distance(1, 2)")]   // non-Point args
        [InlineData("min(1)")]           // wrong arity
        [InlineData("!5")]               // NOT on Int
        [InlineData("")]                 // empty input
        public void MalformedText_ThrowsLocatedJsonException(string text)
        {
            var ex = ParseError(text);
            Assert.Contains("expr text (pos", ex.Message);
        }

        [Fact]
        public void SlotOutOfRange_ErrorQuotesTheAuthoredNumber()
        {
            // The digit accumulator must not saturate — `score[999]` reports 999, never a stand-in "8".
            var ex = ParseError("score[999]");
            Assert.Contains("999", ex.Message);
            Assert.Contains("out of range", ex.Message);
        }

        [Fact]
        public void MostNegativeLiterals_AreUnrepresentable_PinnedReject()
        {
            // Unary '-' applies AFTER the positive-literal bounds check, so int.MinValue and the Fixed minimum
            // cannot be authored as literals — their positive magnitudes reject first (the documented exclusion).
            Assert.Contains("out of range", ParseError("-2147483648").Message);
            Assert.Contains("16.16", ParseError("-32768.0").Message);
        }

        [Fact]
        public void OverlongText_IsRejected_NamingTheCap()
        {
            string text = "1" + new string(' ', ExprBounds.MaxExprTextLength);
            Assert.Contains("MaxExprTextLength", ParseError(text).Message);
        }

        [Fact]
        public void OverDeepText_IsRejected_NamingTheCap()
        {
            // MaxExprDepth nested NOTs over a Bool leaf = depth MaxExprDepth+1 → located depth reject.
            string text = new string('!', ExprBounds.MaxExprDepth) + "done";
            Assert.Contains("MaxExprDepth", ParseError(text).Message);
        }

        // ── End-to-end + round-trip ────────────────────────────────────────────────

        [Fact]
        public void ParseCompileEval_EndToEnd_OverDeclaredVariables()
        {
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl>
            {
                new("gold", DslValueType.Int,  VarScope.Global, 12),
                new("done", DslValueType.Bool, VarScope.Global, 0),
            }, new List<DslTimerDecl>());

            // (gold >= 10 && !done) || count(2) < 5 → (true && true) || … → 1
            Assert.Equal(1, ParseCompileEval("(gold >= 10 && !done) || count(2) < 5", table));
            // (gold + 5) * 2 = 34
            Assert.Equal(34, ParseCompileEval("(gold + 5) * 2", table));
        }

        [Fact]
        public void ParserOutput_RoundTripsCanonicalJson_ByteIdentically()
        {
            var g = new TriggerGraph();
            ExprParser.Parse("(gold >= 10 && !done) || distance(p, q) < 3.5", g, Vars);
            string json1 = g.ToCanonicalJson();
            string json2 = TriggerGraph.FromJson(json1).ToCanonicalJson();
            Assert.Equal(json1, json2); // byte-identical — one IR, no second executable form

            // The data edges carry NAME-only typed wires.
            Assert.Contains("\"Int\"", json1);
            Assert.Contains("\"Boolean\"", json1);
            Assert.Contains("\"Fixed\"", json1);
            Assert.Contains("\"Point\"", json1);
        }

        [Fact]
        public void TextAndRawIr_ProduceTheSameCanonicalGraph()
        {
            // The same expression authored as TEXT and as hand-built raw IR must canonicalize identically
            // (same ids, same edges) — the parser is a projection of the one IR.
            var fromText = new TriggerGraph();
            ExprParser.Parse("gold + 1", fromText, Vars);

            var byHand = new TriggerGraph();
            byHand.Nodes.Add(new ExprVarNode { Id = 0, Name = "gold" });
            byHand.Nodes.Add(new ExprLiteralNode { Id = 1, ValueType = DslValueType.Int, Raw = 1 });
            byHand.Nodes.Add(new ExprBinaryNode { Id = 2, Op = "add" });
            byHand.DataEdges.Add(new DataEdge(0, 0, 2, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            byHand.DataEdges.Add(new DataEdge(1, 0, 2, TriggerGraph.ExprOperandPort1, DataWireType.Int));

            Assert.Equal(byHand.ToCanonicalJson(), fromText.ToCanonicalJson());
        }

        [Fact]
        public void ParserAppends_PastExistingGraphIds()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            (int root, _) = ExprParser.Parse("1 + 2", g, Vars);
            Assert.True(root >= 2); // fresh ids past the existing max
            Assert.Equal(5, g.Nodes.Count);
        }
    }
}
