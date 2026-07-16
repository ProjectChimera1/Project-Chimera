#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // ScenarioData (the director-backed count() seam test)
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.4 — the TICK-phase evaluator: arithmetic/comparison/boolean semantics per type, OR/NOT/grouping
    /// truth tables, the TOTAL runtime semantics (variable div/mod-by-zero → 0, abs(int.MinValue) → int.MaxValue,
    /// unchecked Int wrap), the closed built-ins (count via a stub IExprWorld, distance vs FixedVec3.Distance,
    /// min/max/abs), per-player name[k] reads/writes, and the Bool write normalization.
    /// </summary>
    public class ExprEvaluatorTests
    {
        private sealed class StubWorld : IExprWorld
        {
            public int CountAlive(int factionSlot) => factionSlot == 1 ? 5 : 0;
        }

        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> NoVars =
            new(StringComparer.Ordinal);

        /// <summary>Parse text (over the given declared vars), compile, and evaluate against the given table.</summary>
        private static int Eval(string text, DslVarTable? vars = null,
            Dictionary<string, (DslValueType Type, VarScope Scope)>? decls = null, IExprWorld? world = null)
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse(text, g, decls ?? NoVars);
            Assert.True(ExprCompiler.TryCompile(g, root, decls ?? NoVars, inCondition: false,
                out ExprProgram? p, out string? err), err);
            return p!.Eval(vars ?? new DslVarTable(), world);
        }

        // ── Int arithmetic ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData("(2 + 3) * 4", 20)]
        [InlineData("10 - 3 - 2", 5)]      // left-associative
        [InlineData("7 / 2", 3)]           // integer division truncates
        [InlineData("7 % 3", 1)]
        [InlineData("-2 + 3", 1)]
        [InlineData("2 - -3", 5)]
        [InlineData("1 + 2 * 3", 7)]       // precedence
        [InlineData("min(2, 3)", 2)]
        [InlineData("max(2, 3)", 3)]
        [InlineData("abs(-4)", 4)]
        public void IntArithmetic_EvaluatesCorrectly(string text, int expected) =>
            Assert.Equal(expected, Eval(text));

        [Fact]
        public void IntOverflow_WrapsUnchecked()
        {
            // int.MaxValue + 1 wraps to int.MinValue (the total, deterministic semantics).
            Assert.Equal(int.MinValue, Eval("2147483647 + 1"));
        }

        [Fact]
        public void AbsOfIntMinValue_IsIntMaxValue()
        {
            // abs(int.MinValue) is defined as int.MaxValue (never a throw). int.MinValue is reached by wrap.
            Assert.Equal(int.MaxValue, Eval("abs(0 - 2147483647 - 1)"));
        }

        // ── Fixed arithmetic (raw-exact) ───────────────────────────────────────────

        [Theory]
        [InlineData("1.5 + 2.25", 245760)]   // 3.75 * 65536
        [InlineData("1.5 * 2.0", 196608)]    // 3.0
        [InlineData("3.0 / 2.0", 98304)]     // 1.5
        [InlineData("-1.5 + 3.0", 98304)]    // 1.5
        [InlineData("min(1.5, 2.5)", 98304)]
        [InlineData("max(1.5, 2.5)", 163840)]
        [InlineData("abs(-1.5)", 98304)]
        public void FixedArithmetic_MatchesRawExpectations(string text, int expectedRaw) =>
            Assert.Equal(expectedRaw, Eval(text));

        [Fact]
        public void FixedMul_MatchesFixedOperator()
        {
            Assert.Equal((Fixed.FromRaw(98304) * Fixed.FromRaw(147456)).Raw, Eval("1.5 * 2.25"));
        }

        // ── Comparisons (exact raws, Bool results) ─────────────────────────────────

        [Theory]
        [InlineData("3 > 2", 1)]
        [InlineData("2 > 3", 0)]
        [InlineData("2 >= 2", 1)]
        [InlineData("2 <= 1", 0)]
        [InlineData("2 == 2", 1)]
        [InlineData("2 != 2", 0)]
        [InlineData("1.5 == 1.5", 1)]   // Fixed == is EXACT raw equality (no legacy epsilon)
        [InlineData("1.5 != 1.5", 0)]
        [InlineData("true == false", 0)]
        [InlineData("true != false", 1)]
        public void Comparisons_EvaluateToBool(string text, int expected) =>
            Assert.Equal(expected, Eval(text));

        [Fact]
        public void FixedEquality_IsRawExact_OneRawApartIsNotEqual()
        {
            // 0.00002 parses to raw 1; 0.0 is raw 0 — one raw apart must NOT be equal (the legacy
            // variable_comparison epsilon applies only to the untouched legacy leaf).
            Assert.Equal(0, Eval("0.00002 == 0.0"));
        }

        // ── OR/NOT/grouping truth table ────────────────────────────────────────────

        [Theory]
        [InlineData(false, false, false, 0)]
        [InlineData(true,  false, false, 1)]
        [InlineData(false, true,  false, 1)]
        [InlineData(true,  true,  false, 1)]
        [InlineData(false, false, true,  0)]
        [InlineData(true,  false, true,  0)]
        [InlineData(false, true,  true,  0)]
        [InlineData(true,  true,  true,  0)]
        public void OrNotGrouping_TruthTable(bool a, bool b, bool c, int expected)
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["a"] = (DslValueType.Bool, VarScope.Global),
                ["b"] = (DslValueType.Bool, VarScope.Global),
                ["c"] = (DslValueType.Bool, VarScope.Global),
            };
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl>
            {
                new("a", DslValueType.Bool, VarScope.Global, a ? 1 : 0),
                new("b", DslValueType.Bool, VarScope.Global, b ? 1 : 0),
                new("c", DslValueType.Bool, VarScope.Global, c ? 1 : 0),
            }, new List<DslTimerDecl>());
            Assert.Equal(expected, Eval("(a || b) && !c", table, decls));
        }

        // ── Total runtime semantics: variable zero divisors ────────────────────────

        [Fact]
        public void RuntimeVariableZeroDivisor_Int_EvaluatesToZero()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["b"] = (DslValueType.Int, VarScope.Global),
            };
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl> { new("b", DslValueType.Int, VarScope.Global, 0) },
                new List<DslTimerDecl>());
            Assert.Equal(0, Eval("7 / b", table, decls));
            Assert.Equal(0, Eval("7 % b", table, decls));
        }

        [Fact]
        public void RuntimeVariableZeroDivisor_Fixed_EvaluatesToZero()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["b"] = (DslValueType.Fixed, VarScope.Global),
            };
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl> { new("b", DslValueType.Fixed, VarScope.Global, 0) },
                new List<DslTimerDecl>());
            Assert.Equal(0, Eval("1.5 / b", table, decls));
            Assert.Equal(0, Eval("1.5 % b", table, decls));
        }

        [Fact]
        public void IntMinValueDividedByMinusOne_WrapsInsteadOfThrowing()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["m"] = (DslValueType.Int, VarScope.Global),
                ["d"] = (DslValueType.Int, VarScope.Global),
            };
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl>
            {
                new("m", DslValueType.Int, VarScope.Global, int.MinValue),
                new("d", DslValueType.Int, VarScope.Global, -1),
            }, new List<DslTimerDecl>());
            Assert.Equal(int.MinValue, Eval("m / d", table, decls)); // unchecked wrap, no OverflowException
            Assert.Equal(0, Eval("m % d", table, decls));            // remainder 0, no hardware trap
        }

        // ── Built-ins ──────────────────────────────────────────────────────────────

        [Fact]
        public void Count_ReachesTheWorldSeam_AndEmptyFactionCountsZero()
        {
            Assert.Equal(5, Eval("count(1)", world: new StubWorld()));
            Assert.Equal(0, Eval("count(0)", world: new StubWorld())); // empty faction = 0
            Assert.Equal(0, Eval("count(1)", world: null));            // no world wired = 0
        }

        [Fact]
        public void Count_ComputedOutOfRangeSlot_EvaluatesToZero_AtTheDirectorSeam()
        {
            // A COMPUTED slot escapes the compile-time literal range check, so the director's IExprWorld seam must
            // return 0 for it — without the guard, (Faction)(-1 + 1) wraps count(-1) onto Neutral and counts it.
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.LoadScenario(new ScenarioData());
            var world = new EntityWorld();
            world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(1)),
                Faction.Neutral, Fixed.FromInt(10), Fixed.One);
            director.Tick(world, Fixed.One); // pins the live world for the count() seam

            Assert.Equal(0, Eval("count(0 - 1)", world: director)); // negative computed slot — NOT the Neutral count
            Assert.Equal(0, Eval("count(4 + 4)", world: director)); // computed slot == PlayerSlots
        }

        [Fact]
        public void Distance_ComputesExactlyLikeFixedVec3Distance_OverXZ()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["p"] = (DslValueType.Point, VarScope.Global),
                ["q"] = (DslValueType.Point, VarScope.Global),
            };
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl>
            {
                new("p", DslValueType.Point, VarScope.Global, Fixed.FromRaw(98304).Raw,  Fixed.FromInt(2).Raw), // (1.5, 2)
                new("q", DslValueType.Point, VarScope.Global, Fixed.FromInt(4).Raw,      Fixed.FromInt(6).Raw), // (4, 6)
            }, new List<DslTimerDecl>());

            int expected = FixedVec3.Distance(
                new FixedVec3(Fixed.FromRaw(98304), Fixed.Zero, Fixed.FromInt(2)),
                new FixedVec3(Fixed.FromInt(4),     Fixed.Zero, Fixed.FromInt(6))).Raw;
            Assert.Equal(expected, Eval("distance(p, q)", table, decls));
        }

        // ── Per-player reads + typed raw writes ────────────────────────────────────

        [Fact]
        public void PerPlayerSlotRead_SelectsTheDeclaredSlot()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["score"] = (DslValueType.Int, VarScope.PerPlayer),
            };
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl> { new("score", DslValueType.Int, VarScope.PerPlayer, 0) },
                new List<DslTimerDecl>());
            table.SetRaw("score", 2, 42, 0);

            Assert.Equal(42, Eval("score[2]", table, decls));
            Assert.Equal(0,  Eval("score[0]", table, decls));
        }

        [Fact]
        public void BoolWrite_NormalizesToZeroOrOne()
        {
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl> { new("done", DslValueType.Bool, VarScope.Global, 0) },
                new List<DslTimerDecl>());
            table.SetRaw("done", 0, 7, 3); // a truthy raw write to a Bool slot
            table.GetRaw("done", 0, out int raw0, out int raw1);
            Assert.Equal(1, raw0); // normalized
            Assert.Equal(0, raw1);
        }

        [Fact]
        public void TryGetDecl_ResolvesInPerPlayerTriggerLocalGlobalOrder()
        {
            var table = new DslVarTable();
            table.InitFromDeclarations(new List<DslVarDecl>
            {
                new("g", DslValueType.Fixed, VarScope.Global, 0),
                new("p", DslValueType.Int,   VarScope.PerPlayer, 0),
                new("t", DslValueType.Bool,  VarScope.TriggerLocal, 0),
            }, new List<DslTimerDecl>());

            Assert.True(table.TryGetDecl("g", out DslValueType gt, out VarScope gs));
            Assert.Equal((DslValueType.Fixed, VarScope.Global), (gt, gs));
            Assert.True(table.TryGetDecl("p", out DslValueType pt, out VarScope ps));
            Assert.Equal((DslValueType.Int, VarScope.PerPlayer), (pt, ps));
            Assert.True(table.TryGetDecl("t", out DslValueType tt, out VarScope ts));
            Assert.Equal((DslValueType.Bool, VarScope.TriggerLocal), (tt, ts));
            Assert.False(table.TryGetDecl("ghost", out _, out _));
        }
    }
}
