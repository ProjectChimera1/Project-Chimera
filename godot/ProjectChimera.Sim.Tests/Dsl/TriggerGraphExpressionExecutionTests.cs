#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.4 — director-driven execution of the expression layer: an expression-gated trigger fires per its
    /// truth table (ANDed with legacy ConditionNodes), a set_variable RHS expression assigns typed Int/Fixed/Bool
    /// values through the folded DslVarTable, two headless runs at HOST altitude with live expression triggers
    /// produce byte-identical SimChecksum sequences, and an expression-free legacy scenario stays two-run
    /// deterministic (the committed goldens — unmoved by this story — pin pre-7.4 parity; the Block-If net).
    /// </summary>
    public class TriggerGraphExpressionExecutionTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> BoolAbc =
            new(StringComparer.Ordinal)
            {
                ["a"] = (DslValueType.Bool, VarScope.Global),
                ["b"] = (DslValueType.Bool, VarScope.Global),
                ["c"] = (DslValueType.Bool, VarScope.Global),
                ["fired"] = (DslValueType.Int, VarScope.Global),
            };

        private static ScenarioVariable BoolVar(string name, bool v) =>
            new() { Name = name, Type = DslValueType.Bool, Scope = VarScope.Global, Initial = v ? Fixed.One : Fixed.Zero };

        private static (ScenarioDirector Director, DslVarTable Vars) Build(ScenarioData scenario)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(scenario);
            return (director, vars);
        }

        // ── AC1: expression-gated trigger fires per boolean semantics ──────────────

        [Theory]
        [InlineData(false, false, false, 0)]
        [InlineData(true,  false, false, 1)]
        [InlineData(false, true,  false, 1)]
        [InlineData(true,  true,  false, 1)]
        [InlineData(false, false, true,  0)]
        [InlineData(true,  false, true,  0)]
        [InlineData(false, true,  true,  0)]
        [InlineData(true,  true,  true,  0)]
        public void ExpressionGatedTrigger_FiresPerTruthTable(bool a, bool b, bool c, int expectedFired)
        {
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "gate", "match_start", "(a || b) && !c", "fired", 0, "1", BoolAbc);
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    BoolVar("a", a), BoolVar("b", b), BoolVar("c", c),
                    new ScenarioVariable { Name = "fired", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(expectedFired, vars.GetInt("fired", 0));
        }

        [Fact]
        public void ExpressionCondition_ANDsWithLegacyConditionNodes()
        {
            // A trigger gated by BOTH a legacy variable_comparison (gate == 1) AND an expression root (flag):
            // it fires only when BOTH pass — matching multi-condition semantics.
            static ScenarioData Scenario(int gate, bool flag)
            {
                var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
                {
                    ["gate"]  = (DslValueType.Int,  VarScope.Global),
                    ["flag"]  = (DslValueType.Bool, VarScope.Global),
                    ["fired"] = (DslValueType.Int,  VarScope.Global),
                };
                var g = new TriggerGraph();
                g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
                g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
                g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
                g.Nodes.Add(new ConditionNode { Id = 2, Kind = "variable_comparison", Variable = "gate", Operator = "==", Value = 1 });
                g.DataEdges.Add(new DataEdge(2, TriggerGraph.ConditionDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
                (int exprRoot, _) = ExprParser.Parse("flag", g, decls);
                g.DataEdges.Add(new DataEdge(exprRoot, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
                int actionId = exprRoot + 1;
                g.Nodes.Add(new ActionNode { Id = actionId, Kind = "set_variable", Variable = "fired", Value = 1 });
                g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, actionId, TriggerGraph.ActionExecInPort));

                return new ScenarioData
                {
                    Variables = new[]
                    {
                        new ScenarioVariable { Name = "gate",  Type = DslValueType.Int,  Scope = VarScope.Global, Initial = Fixed.FromInt(gate) },
                        BoolVar("flag", flag),
                        new ScenarioVariable { Name = "fired", Type = DslValueType.Int,  Scope = VarScope.Global, Initial = Fixed.Zero },
                    },
                    TriggerGraphJson = g.ToCanonicalJson(),
                };
            }

            foreach ((int gate, bool flag, int expected) in new[] { (1, true, 1), (1, false, 0), (0, true, 0), (0, false, 0) })
            {
                (ScenarioDirector director, DslVarTable vars) = Build(Scenario(gate, flag));
                director.Tick(new EntityWorld(), Fixed.One);
                Assert.Equal(expected, vars.GetInt("fired", 0));
            }
        }

        // ── AC1: computed set_variable RHS per target type ─────────────────────────

        [Fact]
        public void ComputedSetVariable_AssignsInt()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["gold"]  = (DslValueType.Int, VarScope.Global),
                ["score"] = (DslValueType.Int, VarScope.Global),
            };
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "calc", "match_start", null, "score", 0, "(gold + 5) * 2", decls);
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "gold",  Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(12) },
                    new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(34, vars.GetInt("score", 0));
        }

        [Fact]
        public void ComputedSetVariable_AssignsFixed_RawExact()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["speed"] = (DslValueType.Fixed, VarScope.Global),
            };
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "calc", "match_start", null, "speed", 0, "1.5 + 0.25", decls);
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "speed", Type = DslValueType.Fixed, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            vars.GetRaw("speed", 0, out int raw0, out _);
            Assert.Equal(Fixed.FromRaw(98304 + 16384).Raw, raw0); // 1.75, raw-exact
        }

        [Fact]
        public void ComputedSetVariable_AssignsBool_Normalized()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["gold"] = (DslValueType.Int,  VarScope.Global),
                ["rich"] = (DslValueType.Bool, VarScope.Global),
            };
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "calc", "match_start", null, "rich", 0, "gold > 5", decls);
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "gold", Type = DslValueType.Int,  Scope = VarScope.Global, Initial = Fixed.FromInt(12) },
                    new ScenarioVariable { Name = "rich", Type = DslValueType.Bool, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            vars.GetRaw("rich", 0, out int raw0, out int raw1);
            Assert.Equal(1, raw0); // Bool normalizes to 0/1
            Assert.Equal(0, raw1);
        }

        [Theory]
        [InlineData(true,  true,  1)]
        [InlineData(true,  false, 0)]
        [InlineData(false, true,  0)]
        [InlineData(false, false, 0)]
        public void TwoExpressionRoots_OnOneConditionInPort_ANDTogether(bool a, bool b, int expectedFired)
        {
            // TWO expression roots wired into the SAME trigger's condition-in port AND together (multi-condition
            // semantics) — the trigger fires only when BOTH compiled programs evaluate true.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            (int rootA, _) = ExprParser.Parse("a", g, BoolAbc);
            (int rootB, _) = ExprParser.Parse("b", g, BoolAbc);
            g.DataEdges.Add(new DataEdge(rootA, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(rootB, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            int actionId = rootB + 1;
            g.Nodes.Add(new ActionNode { Id = actionId, Kind = "set_variable", Variable = "fired", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, actionId, TriggerGraph.ActionExecInPort));

            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    BoolVar("a", a), BoolVar("b", b),
                    new ScenarioVariable { Name = "fired", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(expectedFired, vars.GetInt("fired", 0));
        }

        [Fact]
        public void ComputedSetVariable_ToUndeclaredTarget_AppendsGlobalInt_AndValidatorAccepts()
        {
            // Undeclared WRITE targets are legal by design (Global/Int append — legacy SetVariable parity): the
            // compiler rejects undeclared READS, not write targets. Both gates accept, and the write lands in
            // DslVarTable.SetRaw's undeclared-append branch.
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "calc", "match_start", null, "mystery", 0, "1 + 2",
                new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal));
            var scenario = new ScenarioData
            {
                MapBounds = 120f,
                WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                    new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX = 45f, BaseZ = 0f },
                },
                ResourceNodes = Array.Empty<ScenarioResourceNode>(),
                Buildings = Array.Empty<ScenarioBuilding>(),
                Units = Array.Empty<ScenarioUnit>(),
                Triggers = Array.Empty<TriggerDefinition>(),
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            ValidationResult r = new ScenarioValidator().Validate(scenario);
            Assert.True(r.Ok, r.Error);

            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(3, vars.GetInt("mystery", 0));
        }

        [Fact]
        public void ForkedValueInGraph_FailsClosed_AtLoadScenario()
        {
            // TWO expression edges into ONE set_variable value-in port: the validator gate rejects this, and the
            // LoadScenario backstop must too (BuildExecutionOrder's lowest-src pick must never silently resolve
            // the fork for a direct caller that skipped the gate).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "x" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 1 });
            g.Nodes.Add(new ExprLiteralNode { Id = 4, ValueType = DslValueType.Int, Raw = 2 });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(4, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("forked", ex.Message);
        }

        [Fact]
        public void MalformedExpressionGraph_FailsClosed_AtLoadScenario()
        {
            // A type-mismatched raw-IR expression (Bool && Int) reaching LoadScenario directly (no validator gate)
            // still fails closed with a located JsonException — the compile-once backstop.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Bool, Raw = 1 });
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 1 });
            g.Nodes.Add(new ExprBinaryNode { Id = 4, Op = "and" });
            g.DataEdges.Add(new DataEdge(2, 0, 4, TriggerGraph.ExprOperandPort0, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(3, 0, 4, TriggerGraph.ExprOperandPort1, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(4, 0, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
        }

        // ── AC4: determinism at host altitude ──────────────────────────────────────

        /// <summary>A scenario with LIVE expression triggers evaluated every tick: a resource_threshold event
        /// (polled per tick) fires a trigger whose condition expression and computed set_variable RHS (including
        /// the count() world built-in) run each tick, mutating the FOLDED DslVarTable so the checksum evolves.</summary>
        private static ScenarioData LiveExpressionScenario()
        {
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["gold"] = (DslValueType.Int,  VarScope.Global),
                ["cap"]  = (DslValueType.Int,  VarScope.Global),
                ["done"] = (DslValueType.Bool, VarScope.Global),
            };
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "accumulate", "resource_threshold", "(gold < cap && !done) || count(1) < 0",
                "gold", 0, "gold + count(0) + 1", decls);
            return new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "gold", Type = DslValueType.Int,  Scope = VarScope.Global, Initial = Fixed.Zero },
                    new ScenarioVariable { Name = "cap",  Type = DslValueType.Int,  Scope = VarScope.Global, Initial = Fixed.FromInt(1000) },
                    new ScenarioVariable { Name = "done", Type = DslValueType.Bool, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
        }

        private static List<(uint Tick, uint Hash)> RunHost(int ticks)
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;
            // A live unit so count(0) is non-zero and entity state folds too.
            host.World.Create(new FixedVec3(Fixed.FromInt(4), Fixed.Zero, Fixed.FromInt(4)),
                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            host.ScenarioDirector.LoadScenario(LiveExpressionScenario());

            var seq = new List<(uint, uint)>(ticks);
            host.SetChecksumSink((tick, hash) => seq.Add((tick, hash)));
            for (int i = 0; i < ticks; i++)
                host.StepOnce();
            return seq;
        }

        [Fact]
        public void TwoHeadlessRuns_WithLiveExpressionTriggers_ProduceByteIdenticalChecksumSequences()
        {
            List<(uint Tick, uint Hash)> run1 = RunHost(60);
            List<(uint Tick, uint Hash)> run2 = RunHost(60);
            Assert.Equal(60, run1.Count);
            Assert.Equal(run1, run2);

            // Sanity: the expression trigger actually ran and mutated folded state (the sequence is not flat).
            Assert.Contains(run1, s => s.Hash != run1[0].Hash);
        }

        [Fact]
        public void ExpressionTrigger_ActuallyMutatesState_EachTick()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.World.Create(new FixedVec3(Fixed.FromInt(4), Fixed.Zero, Fixed.FromInt(4)),
                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            host.ScenarioDirector.LoadScenario(LiveExpressionScenario());
            for (int i = 0; i < 10; i++)
                host.StepOnce();
            // gold += count(0) + 1 = 2 per tick for 10 ticks = 20 (one P1 unit alive; count(faction slot 0) = 1).
            Assert.Equal(20, host.Vars.GetInt("gold", 0));
        }

        // ── Block-If net: expression-free legacy behavior parity ──────────────────

        // ── Review pass 2 — PerPlayer slot routing, index contract, backstop parity, atomicity ──

        [Fact]
        public void ComputedSetVariable_ToPerPlayerTarget_LandsInDeclaredSlot()
        {
            // The action's Faction field selects the player slot for a PerPlayer target — a regression hardcoding
            // slot 0 (or dropping a.Faction from the SetRaw call) passed every prior test because they all used
            // Global targets, whose writes ignore the faction argument. PerPlayer slots fold into SimChecksum,
            // so a wrong-slot write would be silently checksummed on every peer.
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["score"] = (DslValueType.Int, VarScope.PerPlayer),
            };
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "calc", "match_start", null, "score", 2, "40 + 2", decls);
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.PerPlayer, Initial = Fixed.FromInt(7) },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(42, vars.GetInt("score", 2)); // the action's declared slot got the computed value
            Assert.Equal(7,  vars.GetInt("score", 0)); // slot 0 keeps its initial — no hardcoded-slot write
        }

        [Fact]
        public void MultiActionChain_ValueProgramAppliesToTheCorrectAction()
        {
            // display_message → set_variable(expression) → set_variable(literal): ActionValueExprRoots is
            // POSITIONALLY parallel to Actions, and every prior execution test used a single-action chain where
            // positional and compact indexing coincide — a filtered/compact-indexing regression would silently
            // attach the RHS to the wrong action (or drop it so the literal Value wins).
            var none = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "hi" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "x", Value = 999 }); // literal must LOSE to the RHS
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "set_variable", Variable = "y", Value = 5 });   // literal path stays literal
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort));
            (int rhsRoot, _) = ExprParser.Parse("10 * 2", g, none);
            g.DataEdges.Add(new DataEdge(rhsRoot, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(20, vars.GetInt("x", 0)); // the middle action's compiled RHS, not its literal 999
            Assert.Equal(5,  vars.GetInt("y", 0)); // the sibling literal action untouched by the RHS program
        }

        [Fact]
        public void NonExpressionSrcOnValueIn_FailsClosed_AtLoadScenario()
        {
            // A SINGLE value-in edge whose src is not an expression node: BuildExecutionOrder maps it to root -1,
            // so pre-patch the backstop silently ignored it and the literal Value won against the authored wiring
            // (the gate rejects the identical graph). Parity demands the backstop fail closed too.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "x", Value = 9 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            g.DataEdges.Add(new DataEdge(3, 0, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("not an expression node", ex.Message);
        }

        [Fact]
        public void OrphanActionValueInEdge_FailsClosed_AtLoadScenario()
        {
            // A value-in edge onto an action OUTSIDE every exec chain: the per-exec compile loop never visits it,
            // so only the per-edge parity scan can reject it (the gate rejects it per-edge regardless of
            // reachability — a non-set_variable dst here).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "orphan" }); // NO exec edge to it
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 7 });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("set_variable", ex.Message);
        }

        [Fact]
        public void WrongWireOnConditionInEdge_FailsClosed_AtLoadScenario()
        {
            // A Bool-rooted condition expression whose condition-in edge carries a non-Boolean wire — the
            // backstop check the gate mirrors (wire color = type); previously covered at the gate only.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Bool, Raw = 1 });
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Int));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("Boolean wire", ex.Message);
        }

        [Fact]
        public void ValueInWireMismatch_FailsClosed_AtLoadScenario()
        {
            // An Int expression into an Int-declared target over a Fixed wire: wire ≠ WireOf(target).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "gold" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 7 });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Fixed));

            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "gold", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("does not match target", ex.Message);
        }

        [Fact]
        public void StraySrcPortOnConsumerEdge_FailsClosed_AtLoadScenario()
        {
            // Expression nodes emit only on ExprDataOutPort (= 0): a consumer edge leaving src_port 7 previously
            // compiled, validated, and round-tripped untouched — now a located reject at gate AND backstop.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Bool, Raw = 1 });
            g.DataEdges.Add(new DataEdge(2, 7, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("emit only on port", ex.Message);
        }

        [Fact]
        public void DuplicateVariableDeclarations_WithExpressions_FailClosed_AtLoadScenario()
        {
            // Duplicate names across scopes are gate-rejected; the backstop's declared map must not silently
            // type expressions against the LAST declaration while DslVarTable.Resolve reads PerPlayer-first.
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["x"] = (DslValueType.Int, VarScope.Global),
            };
            TriggerGraph g = TriggerGraph.BuildExpressionTrigger("t", "match_start", "x > 0", "x", 0, "1 + 1", decls);
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "x", Type = DslValueType.Int,   Scope = VarScope.PerPlayer, Initial = Fixed.Zero },
                    new ScenarioVariable { Name = "x", Type = DslValueType.Fixed, Scope = VarScope.Global,    Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("declared more than once", ex.Message);
        }

        [Fact]
        public void FailedLoadScenario_LeavesPriorScenarioIntact()
        {
            // LoadScenario is failure-atomic: a located compile throw must leave the PREVIOUS scenario's runtime
            // state coherent — pre-patch, half-replaced trigger state with null program rows NRE'd on the next Tick.
            TriggerGraph good = TriggerGraph.BuildExpressionTrigger(
                "gate", "match_start", "a || b", "fired", 0, "1", BoolAbc);
            var goodScenario = new ScenarioData
            {
                Variables = new[]
                {
                    BoolVar("a", true), BoolVar("b", false), BoolVar("c", false),
                    new ScenarioVariable { Name = "fired", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = good.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(goodScenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(1, vars.GetInt("fired", 0));

            // A malformed load attempt (type-mismatched expression) throws located…
            var bad = new TriggerGraph();
            bad.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            bad.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            bad.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            bad.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Bool, Raw = 1 });
            bad.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 1 });
            bad.Nodes.Add(new ExprBinaryNode { Id = 4, Op = "and" });
            bad.DataEdges.Add(new DataEdge(2, 0, 4, TriggerGraph.ExprOperandPort0, DataWireType.Boolean));
            bad.DataEdges.Add(new DataEdge(3, 0, 4, TriggerGraph.ExprOperandPort1, DataWireType.Int));
            bad.DataEdges.Add(new DataEdge(4, 0, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            Assert.ThrowsAny<System.Text.Json.JsonException>(
                () => director.LoadScenario(new ScenarioData { TriggerGraphJson = bad.ToCanonicalJson() }));

            // …and the director still ticks the ORIGINAL scenario without crashing, state untouched.
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(1, vars.GetInt("fired", 0));
        }

        [Fact]
        public void MergedExpressionTriggers_BothExecute()
        {
            // The editor's accumulation path: a SECOND expression trigger merges into an existing graph channel
            // with every node id and expression DATA edge offset — both triggers' programs must survive the offset
            // and execute (no prior test exercised Merge over expression data edges).
            var declsA = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["gold"] = (DslValueType.Int, VarScope.Global),
            };
            var declsB = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["done"] = (DslValueType.Bool, VarScope.Global),
                ["mark"] = (DslValueType.Int,  VarScope.Global),
            };
            TriggerGraph a = TriggerGraph.BuildExpressionTrigger("t1", "match_start", null, "gold", 0, "1 + 2", declsA);
            TriggerGraph b = TriggerGraph.BuildExpressionTrigger("t2", "match_start", "!done", "mark", 0, "5 * 2", declsB);
            a.Merge(b);

            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "gold", Type = DslValueType.Int,  Scope = VarScope.Global, Initial = Fixed.Zero },
                    new ScenarioVariable { Name = "done", Type = DslValueType.Bool, Scope = VarScope.Global, Initial = Fixed.Zero },
                    new ScenarioVariable { Name = "mark", Type = DslValueType.Int,  Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = a.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(3,  vars.GetInt("gold", 0)); // first trigger's RHS
            Assert.Equal(10, vars.GetInt("mark", 0)); // merged trigger's condition passed and RHS assigned
        }

        [Theory]
        [InlineData(true,  false, 1)]
        [InlineData(false, false, 0)]
        [InlineData(true,  true,  0)]
        public void RawIrAuthoredExpression_GatesTrigger_ThroughDirector(bool a, bool c, int expectedFired)
        {
            // The I/O matrix's "authored as raw-IR" half of the happy path: hand-built expression NODES (no
            // parser anywhere) drive the director directly — a && !c.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprVarNode { Id = 2, Name = "a" });
            g.Nodes.Add(new ExprVarNode { Id = 3, Name = "c" });
            g.Nodes.Add(new ExprUnaryNode { Id = 4, Op = "not" });
            g.Nodes.Add(new ExprBinaryNode { Id = 5, Op = "and" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 4, TriggerGraph.ExprOperandPort0, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 5, TriggerGraph.ExprOperandPort0, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(4, TriggerGraph.ExprDataOutPort, 5, TriggerGraph.ExprOperandPort1, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(5, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            g.Nodes.Add(new ActionNode { Id = 6, Kind = "set_variable", Variable = "fired", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 6, TriggerGraph.ActionExecInPort));

            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    BoolVar("a", a), BoolVar("c", c),
                    new ScenarioVariable { Name = "fired", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(expectedFired, vars.GetInt("fired", 0));
        }

        [Fact]
        public void BuildExpressionTrigger_WhitespaceOnlyText_ThrowsInsteadOfSilentlyDropping()
        {
            // "Absent" is spelled null: whitespace-only condition text used to silently produce an UNCONDITIONAL
            // trigger (the silent-condition-drop class), and whitespace value text silently fell back to the
            // literal path. Both now fail closed for non-editor callers (the editor trims first).
            var ex1 = Assert.ThrowsAny<System.Text.Json.JsonException>(() => TriggerGraph.BuildExpressionTrigger(
                "t", "match_start", "   ", "fired", 0, "1", BoolAbc));
            Assert.Contains("blank", ex1.Message);
            var ex2 = Assert.ThrowsAny<System.Text.Json.JsonException>(() => TriggerGraph.BuildExpressionTrigger(
                "t", "match_start", null, "fired", 0, "  \t", BoolAbc));
            Assert.Contains("blank", ex2.Message);
        }

        // ── Review P10: arrayDecls plumbing through BuildExpressionTrigger ──────────

        [Fact]
        public void BuildExpressionTrigger_ArrayLengthCondition_CompilesAndLoads_WhenArrayDeclsArePassed()
        {
            // Before P10 the helper never forwarded arrayDecls to Parse/TryCompile, so an author who declared
            // an Array variable and typed `length(arr) >= 2` in the editor's manual condition field got a false
            // "no array declaration is available" reject.
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["arr"]   = (DslValueType.Array, VarScope.Global),
                ["fired"] = (DslValueType.Int,   VarScope.Global),
            };
            var arrayDecls = new Dictionary<string, (DslValueType Elem, int Capacity)>(StringComparer.Ordinal)
            {
                ["arr"] = (DslValueType.Int, 8),
            };

            TriggerGraph g = TriggerGraph.BuildExpressionTrigger(
                "arr-gate", "match_start", "length(arr) >= 2", "fired", 0, "1", decls,
                arrayDecls: arrayDecls);
            Assert.Contains(g.Nodes, n => n is ExprArrayLenNode);

            // The built trigger LOADS and ticks (a Global array read is legal in the inCondition:true trigger
            // condition): the declared array seeds EMPTY, so length(arr) >= 2 is false and the action must not fire.
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "arr",   Type = DslValueType.Array, Scope = VarScope.Global, ElementType = DslValueType.Int, Capacity = 8 },
                    new ScenarioVariable { Name = "fired", Type = DslValueType.Int,   Scope = VarScope.Global },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(0, vars.GetInt("fired", 0));
        }

        [Fact]
        public void BuildExpressionTrigger_WithoutArrayDecls_ScalarCallsAreUnchanged_AndArrayReadsStillReject()
        {
            // Omitting the trailing optional parameter keeps the exact 7.4 semantics: a scalar-only call
            // compiles as before…
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["x"] = (DslValueType.Int, VarScope.Global),
            };
            TriggerGraph scalar = TriggerGraph.BuildExpressionTrigger("t", "match_start", "x > 0", "x", 0, "1 + 1", decls);
            Assert.Contains(scalar.Nodes, n => n is TriggerNode);

            // …and an array read WITHOUT the map still rejects located (fail-closed default, never a silent 0).
            var declsWithArr = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["arr"] = (DslValueType.Array, VarScope.Global),
            };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => TriggerGraph.BuildExpressionTrigger(
                "t2", "match_start", "length(arr) >= 2", null, 0, null, declsWithArr));
            Assert.Contains("length(arr) requires a declared Array variable", ex.Message);
        }

        [Fact]
        public void LegacyExpressionFreeScenario_TwoRuns_AreByteIdentical()
        {
            // The committed goldens (unmoved by 7.4 — checked by the golden suite + `git diff` over Golden/) pin
            // pre-7.4 behavior; this adds the fresh two-run net over a flat legacy trigger scenario.
            static List<(uint, uint)> Run()
            {
                var host = SimulationHost.Create(
                    NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
                host.ChecksumInterval = 1;
                host.ScenarioDirector.LoadScenario(new ScenarioData
                {
                    Triggers = new[]
                    {
                        new TriggerDefinition
                        {
                            Name    = "legacy",
                            Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                            Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "v", Value = 7 } },
                        },
                    },
                });
                var seq = new List<(uint, uint)>(30);
                host.SetChecksumSink((tick, hash) => seq.Add((tick, hash)));
                for (int i = 0; i < 30; i++)
                    host.StepOnce();
                return seq;
            }

            Assert.Equal(Run(), Run());
        }
    }
}
