#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Multiplayer;   // OrderApplier / UnitCommand parity
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.13 (Arm A) — the checksum-neutral vocabulary slice: the seven state-read expression built-ins, the
    /// sim-side <c>order_units</c> action leaf (via <c>OrderApplier.ApplyActiveOrder</c>), and the presentation-only
    /// <c>move_camera</c>/<c>cinematic_mode</c>/<c>play_vfx</c> leaves. Each I/O-matrix row: a read returns the
    /// correct typed value from live stores; a dead/out-of-range read yields the sentinel without throwing; an
    /// arity/type/selector mismatch rejects at load with one located error; order_units gives each selected unit
    /// (ascending id) the SAME state a hand-issued command would; the presentation leaves fire their delegate with
    /// the SimChecksum byte-identical whether the delegate is wired or not.
    /// </summary>
    public class StateReadAndActionLeafTests
    {
        // ── Direct-director harness (the TriggerGraphExpressionExecutionTests pattern) ──

        private static (ScenarioDirector Director, DslVarTable Vars, ResourceStore Res) Build(
            ScenarioData scenario, RegionStore? regions = null, ResourceStore? res = null)
        {
            var vars = new DslVarTable();
            res ??= new ResourceStore(Fixed.Zero);
            var director = new ScenarioDirector(new BuildingStore(), res, vars);
            if (regions != null) director.SetRegionStore(regions);
            director.LoadScenario(scenario);
            return (director, vars, res);
        }

        private static ScenarioVariable IntVar(string name)  => new() { Name = name, Type = DslValueType.Int,   Scope = VarScope.Global };
        private static ScenarioVariable FixVar(string name)  => new() { Name = name, Type = DslValueType.Fixed, Scope = VarScope.Global };
        private static ScenarioVariable BoolVar(string name) => new() { Name = name, Type = DslValueType.Bool,  Scope = VarScope.Global };

        /// <summary>Build a single-trigger graph: match_start → set_variable(target) whose RHS is the given call
        /// (an ExprCallNode fed by a single Int-literal operand — the entity/faction handle).</summary>
        private static TriggerGraph OneCallToVar(string fn, int operandLiteral, string selector,
            DataWireType resultWire, string targetVar, bool operandIsFaction = false)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Int, Raw = operandLiteral });
            g.Nodes.Add(new ExprCallNode { Id = 3, Fn = fn, Selector = selector });
            // region_unit_count has arity 0 (no operand edge); every other read takes the literal operand.
            if (fn != "region_unit_count")
                g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "set_variable", Variable = targetVar });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 4, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 4, TriggerGraph.ActionValueInPort, resultWire));
            return g;
        }

        // ── State reads: correct typed value from live stores ──────────────────────

        [Fact]
        public void EntityHp_ReturnsLiveHealth_AsFixed()
        {
            var world = new EntityWorld();
            int id = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(1)),
                Faction.Player1, Fixed.FromInt(73), Fixed.FromInt(3));

            var scenario = new ScenarioData
            {
                Variables = new[] { FixVar("hp") },
                TriggerGraphJson = OneCallToVar("entity_hp", id, "", DataWireType.Fixed, "hp").ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One);
            vars.GetRaw("hp", 0, out int raw, out _);
            Assert.Equal(Fixed.FromInt(73).Raw, raw);
        }

        [Fact]
        public void EntityHp_DeadOrOutOfRange_YieldsFixedZero_WithoutThrowing()
        {
            var world = new EntityWorld(); // NO entity 5 exists
            var scenario = new ScenarioData
            {
                Variables = new[] { FixVar("hp") },
                TriggerGraphJson = OneCallToVar("entity_hp", 5, "", DataWireType.Fixed, "hp").ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One); // must not throw
            vars.GetRaw("hp", 0, out int raw, out _);
            Assert.Equal(0, raw); // Fixed.Zero sentinel
        }

        [Fact]
        public void EntityOwner_ReturnsZeroBasedSlot_ComparableAsFactionRef()
        {
            var world = new EntityWorld();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // slot 0

            // set owned = (entity_owner(id) == 0)  → FactionRef vs Int literal 0 → Bool
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Int, Raw = id });
            g.Nodes.Add(new ExprCallNode { Id = 3, Fn = "entity_owner" });
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            g.Nodes.Add(new ExprLiteralNode { Id = 4, ValueType = DslValueType.Int, Raw = 0 });
            g.Nodes.Add(new ExprBinaryNode { Id = 5, Op = "eq" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 5, TriggerGraph.ExprOperandPort0, DataWireType.Int)); // FactionRef → Int wire
            g.DataEdges.Add(new DataEdge(4, TriggerGraph.ExprDataOutPort, 5, TriggerGraph.ExprOperandPort1, DataWireType.Int));
            g.Nodes.Add(new ActionNode { Id = 6, Kind = "set_variable", Variable = "owned" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 6, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(5, TriggerGraph.ExprDataOutPort, 6, TriggerGraph.ActionValueInPort, DataWireType.Boolean));

            var scenario = new ScenarioData
            {
                Variables = new[] { BoolVar("owned") },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One);
            Assert.Equal(1, vars.GetInt("owned", 0)); // entity_owner == 0 (Player1 slot) is true
        }

        [Fact]
        public void EntityPosition_FlowsIntoDistance_AsPoint()
        {
            var world = new EntityWorld();
            int a = world.Create(new FixedVec3(Fixed.FromInt(0), Fixed.Zero, Fixed.FromInt(0)), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int b = world.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.FromInt(4)), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));

            // set dist = distance(entity_position(a), entity_position(b))  → 5 (3-4-5)
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Int, Raw = a });
            g.Nodes.Add(new ExprCallNode { Id = 3, Fn = "entity_position" });
            g.DataEdges.Add(new DataEdge(2, 0, 3, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            g.Nodes.Add(new ExprLiteralNode { Id = 4, ValueType = DslValueType.Int, Raw = b });
            g.Nodes.Add(new ExprCallNode { Id = 5, Fn = "entity_position" });
            g.DataEdges.Add(new DataEdge(4, 0, 5, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            g.Nodes.Add(new ExprCallNode { Id = 6, Fn = "distance" });
            g.DataEdges.Add(new DataEdge(3, 0, 6, TriggerGraph.ExprOperandPort0, DataWireType.Point));
            g.DataEdges.Add(new DataEdge(5, 0, 6, TriggerGraph.ExprOperandPort1, DataWireType.Point));
            g.Nodes.Add(new ActionNode { Id = 7, Kind = "set_variable", Variable = "dist" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 7, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(6, 0, 7, TriggerGraph.ActionValueInPort, DataWireType.Fixed));

            var scenario = new ScenarioData { Variables = new[] { FixVar("dist") }, TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One);
            vars.GetRaw("dist", 0, out int raw, out _);
            var expected = FixedVec3.Distance(
                new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.FromInt(4)));
            Assert.Equal(expected.Raw, raw);
        }

        [Fact]
        public void UnitCountTag_CountsTaggedUnitsOfFaction()
        {
            var world = new EntityWorld();
            int a = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int b = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int c = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int d = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            world.TagsOf[a] = UnitTag.Organic;
            world.TagsOf[b] = UnitTag.Organic;
            world.TagsOf[c] = UnitTag.Mechanical; // not organic
            world.TagsOf[d] = UnitTag.Organic;    // wrong faction

            var scenario = new ScenarioData
            {
                Variables = new[] { IntVar("n") },
                TriggerGraphJson = OneCallToVar("unit_count_tag", 0, "organic", DataWireType.Int, "n", operandIsFaction: true).ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One);
            Assert.Equal(2, vars.GetInt("n", 0)); // a and b (Player1 slot 0, organic)
        }

        [Fact]
        public void UnitCountCategory_CountsCategoryUnitsOfFaction()
        {
            var world = new EntityWorld();
            int a = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int b = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            world.CategoryOf[a] = UnitCategory.Ranged;
            world.CategoryOf[b] = UnitCategory.Worker; // Create defaults Melee; force distinct

            var scenario = new ScenarioData
            {
                Variables = new[] { IntVar("n") },
                TriggerGraphJson = OneCallToVar("unit_count_category", 0, "ranged", DataWireType.Int, "n", operandIsFaction: true).ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One);
            Assert.Equal(1, vars.GetInt("n", 0)); // only a is Ranged
        }

        [Fact]
        public void PlayerResource_ReturnsOreBalance_AsFixed()
        {
            var res = new ResourceStore(Fixed.Zero);
            res.AddOre(Faction.Player1, Fixed.FromInt(250));

            var scenario = new ScenarioData
            {
                Variables = new[] { FixVar("ore") },
                TriggerGraphJson = OneCallToVar("player_resource", 0, "ore", DataWireType.Fixed, "ore", operandIsFaction: true).ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario, res: res);
            director.Tick(new EntityWorld(), Fixed.One);
            vars.GetRaw("ore", 0, out int raw, out _);
            Assert.Equal(Fixed.FromInt(250).Raw, raw);
        }

        [Fact]
        public void RegionUnitCount_CountsUnitsInRegion()
        {
            var world = new EntityWorld();
            world.Create(new FixedVec3(Fixed.FromInt(1),  Fixed.Zero, Fixed.FromInt(1)),  Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // inside
            world.Create(new FixedVec3(Fixed.FromInt(2),  Fixed.Zero, Fixed.FromInt(2)),  Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // inside
            world.Create(new FixedVec3(Fixed.FromInt(99), Fixed.Zero, Fixed.FromInt(99)), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // outside

            var regions = new RegionStore(
                new[] { "r" },
                new[] { new FixedRect(Fixed.FromInt(0), Fixed.FromInt(0), Fixed.FromInt(10), Fixed.FromInt(10)) });

            var scenario = new ScenarioData
            {
                Variables = new[] { IntVar("n") },
                TriggerGraphJson = OneCallToVar("region_unit_count", 0, "r", DataWireType.Int, "n").ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario, regions: regions);
            director.Tick(world, Fixed.One);
            Assert.Equal(2, vars.GetInt("n", 0)); // two units inside the region (any faction)
        }

        // ── Located load rejects (arity / type / selector) ─────────────────────────

        [Fact]
        public void EntityHp_WithBoolOperand_RejectsAtLoad_Located()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Bool, Raw = 1 });
            g.Nodes.Add(new ExprCallNode { Id = 3, Fn = "entity_hp" });
            g.DataEdges.Add(new DataEdge(2, 0, 3, TriggerGraph.ExprOperandPort0, DataWireType.Boolean));
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "set_variable", Variable = "hp" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 4, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(3, 0, 4, TriggerGraph.ActionValueInPort, DataWireType.Fixed));

            var scenario = new ScenarioData { Variables = new[] { FixVar("hp") }, TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("entity_hp", ex.Message);
        }

        [Fact]
        public void RegionUnitCount_WithAnOperandEdge_RejectsAtLoad()
        {
            // region_unit_count has arity 0 — any operand edge is a wrong-arg-count located reject.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Int, Raw = 0 });
            g.Nodes.Add(new ExprCallNode { Id = 3, Fn = "region_unit_count", Selector = "r" });
            g.DataEdges.Add(new DataEdge(2, 0, 3, TriggerGraph.ExprOperandPort0, DataWireType.Int));
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "set_variable", Variable = "n" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 4, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(3, 0, 4, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var scenario = new ScenarioData { Variables = new[] { IntVar("n") }, TriggerGraphJson = g.ToCanonicalJson() };
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
        }

        [Fact]
        public void UnknownTagSelector_RejectsAtLoad_Located()
        {
            var scenario = new ScenarioData
            {
                Variables = new[] { IntVar("n") },
                TriggerGraphJson = OneCallToVar("unit_count_tag", 0, "bogus", DataWireType.Int, "n", operandIsFaction: true).ToCanonicalJson(),
            };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("tag", ex.Message);
        }

        [Fact]
        public void UnknownResourceSelector_RejectsAtLoad_Located()
        {
            var scenario = new ScenarioData
            {
                Variables = new[] { FixVar("v") },
                TriggerGraphJson = OneCallToVar("player_resource", 0, "gold", DataWireType.Fixed, "v", operandIsFaction: true).ToCanonicalJson(),
            };
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => Build(scenario));
            Assert.Contains("resource", ex.Message);
        }

        [Fact]
        public void UnknownCamera_RejectsAtValidatorGate_Located()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new MoveCameraNode { Id = 2, CameraName = "ghost" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));

            var scenario = MinimalScenario();
            scenario.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = new ScenarioValidator().Validate(scenario);
            Assert.False(r.Ok);
            Assert.Contains("no declared camera", r.Error);
        }

        [Fact]
        public void UnknownRegionSelector_RejectsAtValidatorGate_Located()
        {
            var scenario = MinimalScenario();
            scenario.Variables = new[] { IntVar("n") };
            scenario.TriggerGraphJson = OneCallToVar("region_unit_count", 0, "nope", DataWireType.Int, "n").ToCanonicalJson();
            ValidationResult r = new ScenarioValidator().Validate(scenario);
            Assert.False(r.Ok);
            Assert.Contains("no declared region", r.Error);
        }

        private static ScenarioData MinimalScenario() => new()
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
        };

        // ── order_units: parity with a hand-issued OrderApplier.ApplyActiveOrder ────

        [Fact]
        public void OrderUnits_GivesEachSelectedUnit_TheSameOrderAsHandIssued()
        {
            var target = new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.FromInt(30));

            // World A: order_units via the director (faction 0 = Player1, cmd = attack_move).
            var worldA = new EntityWorld();
            int a0 = worldA.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int a1 = worldA.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int a2 = worldA.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // not selected

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new OrderUnitsNode { Id = 2, Command = "attack_move", Faction = 0, X = target.X, Z = target.Z });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));

            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, _, _) = Build(scenario);
            director.Tick(worldA, Fixed.One);

            // World B: identical units, the order hand-issued ascending-id via the applier directly.
            var worldB = new EntityWorld();
            int b0 = worldB.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            int b1 = worldB.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            worldB.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            OrderApplier.ApplyActiveOrder(worldB, b0, UnitCommand.AttackMove, target.X.Raw, target.Z.Raw);
            OrderApplier.ApplyActiveOrder(worldB, b1, UnitCommand.AttackMove, target.X.Raw, target.Z.Raw);

            foreach (int id in new[] { a0, a1 })
            {
                Assert.Equal(worldB.CommandState[id], worldA.CommandState[id]);
                Assert.Equal(worldB.CommandGoal[id],  worldA.CommandGoal[id]);
                Assert.Equal(worldB.MoveTarget[id],   worldA.MoveTarget[id]);
                Assert.Equal(worldB.Flags[id],        worldA.Flags[id]);
                Assert.Equal(worldB.AttackTarget[id], worldA.AttackTarget[id]);
            }
            // The Player2 unit was not selected — no order applied (default CommandState).
            Assert.Equal(worldB.CommandState[a2], worldA.CommandState[a2]);
        }

        [Fact]
        public void OrderUnits_EmptySelection_IsNoOp_NoThrow()
        {
            var world = new EntityWorld(); // no units
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new OrderUnitsNode { Id = 2, Command = "move", Faction = 3, X = Fixed.Zero, Z = Fixed.Zero });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));

            (ScenarioDirector director, _, _) = Build(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() });
            director.Tick(world, Fixed.One); // must not throw
        }

        // ── Presentation leaves: fire the delegate, checksum byte-identical either way ──

        private static ScenarioData PresentationLeafScenario()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "cine" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new MoveCameraNode { Id = 2, CameraName = "cam" });
            g.Nodes.Add(new CinematicModeNode { Id = 3, Enabled = true });
            g.Nodes.Add(new PlayVfxNode { Id = 4, VfxId = "boom", X = Fixed.FromInt(5), Z = Fixed.FromInt(6) });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort));
            return new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
        }

        private static List<(uint Tick, uint Hash)> RunPresentation(bool wireDelegates, out int fires)
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;
            host.World.Create(new FixedVec3(Fixed.FromInt(4), Fixed.Zero, Fixed.FromInt(4)),
                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int f = 0;
            if (wireDelegates)
            {
                host.ScenarioDirector.OnMoveCamera    = _ => f++;
                host.ScenarioDirector.OnCinematicMode = _ => f++;
                host.ScenarioDirector.OnPlayVfx       = (_, _, _) => f++;
            }
            host.ScenarioDirector.LoadScenario(PresentationLeafScenario());

            var seq = new List<(uint, uint)>(30);
            host.SetChecksumSink((tick, hash) => seq.Add((tick, hash)));
            for (int i = 0; i < 30; i++)
                host.StepOnce();
            fires = f;
            return seq;
        }

        [Fact]
        public void PresentationLeaves_DriveDelegates_WithChecksumByteIdenticalWhetherWiredOrNot()
        {
            List<(uint, uint)> unwired = RunPresentation(wireDelegates: false, out int fires0);
            List<(uint, uint)> wired   = RunPresentation(wireDelegates: true,  out int fires1);

            Assert.Equal(0, fires0);               // no delegates → no fires
            Assert.True(fires1 >= 3);              // all three leaves fired at match_start
            Assert.Equal(unwired, wired);          // firing the presentation leaves did NOT move the SimChecksum
        }
    }
}
