#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Combat;   // DamageType (review P11 — mid-loop death via the real death sequence)
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.6 — director-driven <c>for_each</c> execution: an array loop iterates its entry-snapshotted
    /// elements in ascending index through a TriggerLocal loop variable (sum = 15 on the I/O-matrix fixture); an
    /// entity loop iterates alive units in ascending id, anchoring body <c>run_effect</c>s at the CURRENT entity;
    /// <c>up_to</c> caps iteration at the lowest ids; and two headless runs with a live loop are byte-identical.
    /// </summary>
    public class ForEachExecutionTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> Decls =
            new(StringComparer.Ordinal)
            {
                ["arr"]  = (DslValueType.Array, VarScope.Global),
                ["sum"]  = (DslValueType.Int, VarScope.Global),
                ["last"] = (DslValueType.Int, VarScope.Global),
                ["v"]    = (DslValueType.Int, VarScope.TriggerLocal),
            };

        private static readonly Dictionary<string, (DslValueType Elem, int Capacity)> ArrayDecls =
            new(StringComparer.Ordinal) { ["arr"] = (DslValueType.Int, 8) };

        private static ScenarioVariable[] StandardVariables() => new[]
        {
            new ScenarioVariable { Name = "arr",  Type = DslValueType.Array, Scope = VarScope.Global, ElementType = DslValueType.Int, Capacity = 8 },
            new ScenarioVariable { Name = "sum",  Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "last", Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "v",    Type = DslValueType.Int,   Scope = VarScope.TriggerLocal },
        };

        private static (ScenarioDirector Director, DslVarTable Vars, DslLoopState Loop) Build(ScenarioData scenario)
        {
            var vars = new DslVarTable();
            var loop = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop);
            director.LoadScenario(scenario);
            return (director, vars, loop);
        }

        /// <summary>Wire a parsed value expression into an action's value-in port.</summary>
        private static void WireValue(TriggerGraph g, string text, int actionId, DataWireType wire)
        {
            (int root, DataWireType w) = ExprParser.Parse(text, g, Decls, ArrayDecls);
            Assert.Equal(wire, w);
            g.DataEdges.Add(new DataEdge(root, TriggerGraph.ExprDataOutPort, actionId, TriggerGraph.ActionValueInPort, wire));
        }

        // ── I/O-matrix row: array ForEach happy path (sum = 15) ─────────────────

        [Fact]
        public void ArrayForEach_SumsElements_ThroughTriggerLocalLoopVar()
        {
            // One trigger on match_start: push 3/5/7 into arr, then for_each arr (loop var v) summing into sum.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "sum-wave" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ForEachNode { Id = 5, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.Nodes.Add(new ActionNode { Id = 6, Kind = "set_variable", Variable = "sum" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.ActionExecOutPort, 5, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(5, TriggerGraph.ForEachBodyOutPort, 6, TriggerGraph.ActionExecInPort));
            WireValue(g, "3", 2, DataWireType.Int);
            WireValue(g, "5", 3, DataWireType.Int);
            WireValue(g, "7", 4, DataWireType.Int);
            WireValue(g, "sum + v", 6, DataWireType.Int);

            var scenario = new ScenarioData
            {
                Variables = StandardVariables(),
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(15, vars.GetInt("sum", 0));
            Assert.Equal(3, vars.ArrayLen("arr"));
        }

        // ── Review P11: array-loop snapshot isolation + array-source up_to (previously untested) ──

        [Fact]
        public void ArrayForEach_BodyMutationOfTheIteratedArray_DoesNotAffectTheEntrySnapshot()
        {
            // Push [3,5,7]; the loop body BOTH sums (sum += v) AND clears the iterated array. The loop iterates
            // the ENTRY snapshot (all three elements → sum = 15, never a live re-read), while the mutation lands
            // on the live array (empty after the loop).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "snap-iso" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ForEachNode { Id = 5, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.Nodes.Add(new ActionNode { Id = 6, Kind = "set_variable", Variable = "sum" });
            g.Nodes.Add(new ActionNode { Id = 7, Kind = "array_clear", Variable = "arr" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.ActionExecOutPort, 5, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(5, TriggerGraph.ForEachBodyOutPort, 6, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(6, TriggerGraph.ActionExecOutPort, 7, TriggerGraph.ActionExecInPort));
            WireValue(g, "3", 2, DataWireType.Int);
            WireValue(g, "5", 3, DataWireType.Int);
            WireValue(g, "7", 4, DataWireType.Int);
            WireValue(g, "sum + v", 6, DataWireType.Int);

            var scenario = new ScenarioData { Variables = StandardVariables(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(15, vars.GetInt("sum", 0)); // all three snapshot elements visited despite the clear
            Assert.Equal(0, vars.ArrayLen("arr"));   // the body mutation DID take effect on the live array
        }

        [Fact]
        public void ArrayForEach_UpTo_CapsIterationAtTheFirstElements()
        {
            // Push [3,5,7] but cap the ARRAY-source loop at up_to = 2: only the first two snapshot elements are
            // visited (sum = 8). The entity-source up_to is covered elsewhere; the array branch's cap comes purely
            // from the `iter` expression (min(up_to, snapshotCount)) and was previously unpinned.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "arr-upto" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ForEachNode { Id = 5, Source = "array", ArrayName = "arr", LoopVar = "v", UpTo = 2 });
            g.Nodes.Add(new ActionNode { Id = 6, Kind = "set_variable", Variable = "sum" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.ActionExecOutPort, 5, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(5, TriggerGraph.ForEachBodyOutPort, 6, TriggerGraph.ActionExecInPort));
            WireValue(g, "3", 2, DataWireType.Int);
            WireValue(g, "5", 3, DataWireType.Int);
            WireValue(g, "7", 4, DataWireType.Int);
            WireValue(g, "sum + v", 6, DataWireType.Int);

            var scenario = new ScenarioData { Variables = StandardVariables(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(8, vars.GetInt("sum", 0));  // 3 + 5 — the third element is beyond up_to
            Assert.Equal(3, vars.ArrayLen("arr"));   // the array itself is untouched
        }

        // ── I/O-matrix row: entity ForEach + run_effect anchored at the CURRENT entity ──

        // Review (7.6, P10): built via the SPEC-mandated TriggerGraph.BuildForEachTrigger helper — the tests
        // below prove the helper assembles a loop trigger the gate admits and the director executes (its id
        // layout 0=trigger / 1=event / 2=for_each / 3..=body matches the former hand-built fixture exactly).
        private static TriggerGraph EntityLoopGraph(int factionSlot, int upTo, string? loopVar, NodeBase body) =>
            TriggerGraph.BuildForEachTrigger("loop", "match_start", "faction_units", arrayName: null,
                faction: factionSlot, regionId: null, upTo: upTo, loopVar: loopVar, bodyActions: new[] { body });

        [Fact]
        public void EntityForEach_DamagesEachMatchingUnit_AnchoredAtCurrentEntity()
        {
            var world = new EntityWorld();
            int p1a = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int p2a = world.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(10), Fixed.One);
            int p2b = world.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(10), Fixed.One);
            int p2c = world.Create(new FixedVec3(Fixed.FromInt(4), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(10), Fixed.One);

            TriggerGraph g = EntityLoopGraph(factionSlot: 1, upTo: 64, loopVar: null,
                new EffectActionNode { Effect = new DirectHpDeltaEffect(Fixed.FromInt(-5)) });
            var scenario = new ScenarioData { Variables = StandardVariables(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, _, _) = Build(scenario);
            director.Tick(world, Fixed.One);

            // Every alive P2 unit took the effect at ITSELF (the anchor override) — ascending id; P1 untouched.
            Assert.Equal(Fixed.FromInt(10).Raw, world.Health[p1a].Raw);
            Assert.Equal(Fixed.FromInt(5).Raw,  world.Health[p2a].Raw);
            Assert.Equal(Fixed.FromInt(5).Raw,  world.Health[p2b].Raw);
            Assert.Equal(Fixed.FromInt(5).Raw,  world.Health[p2c].Raw);
        }

        [Fact]
        public void UpTo_CapsIterationAtTheLowestIds()
        {
            var world = new EntityWorld();
            int a = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int b = world.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int c = world.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);

            TriggerGraph g = EntityLoopGraph(factionSlot: 0, upTo: 2, loopVar: null,
                new EffectActionNode { Effect = new DirectHpDeltaEffect(Fixed.FromInt(-1)) });
            var scenario = new ScenarioData { Variables = StandardVariables(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, _, _) = Build(scenario);
            director.Tick(world, Fixed.One);

            Assert.Equal(Fixed.FromInt(9).Raw,  world.Health[a].Raw);
            Assert.Equal(Fixed.FromInt(9).Raw,  world.Health[b].Raw);
            Assert.Equal(Fixed.FromInt(10).Raw, world.Health[c].Raw); // beyond up_to — untouched
        }

        [Fact]
        public void EntityLoopVar_ReceivesTheEntityId_InAscendingOrder()
        {
            var world = new EntityWorld();
            world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            world.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int hi = world.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);

            // Body: sum = sum + 1 (count) then last = v (the ascending-order tail = the highest id).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "ids" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachNode { Id = 2, Source = "faction_units", Faction = 0, UpTo = 64, LoopVar = "v" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "sum" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "set_variable", Variable = "last" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort));
            WireValue(g, "sum + 1", 3, DataWireType.Int);
            WireValue(g, "v", 4, DataWireType.Int);

            var scenario = new ScenarioData { Variables = StandardVariables(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One);

            Assert.Equal(3, vars.GetInt("sum", 0));
            Assert.Equal(hi, vars.GetInt("last", 0));
        }

        [Fact]
        public void LoopContinuation_RunsAfterTheLoop()
        {
            // trigger → for_each(arr) [body: sum += v] → (port 0 continuation) last = 42.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "cont" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ForEachNode { Id = 3, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "set_variable", Variable = "sum" });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "last", Value = 42 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ForEachBodyOutPort, 4, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 5, TriggerGraph.ActionExecInPort)); // port 0 = continuation
            WireValue(g, "9", 2, DataWireType.Int);
            WireValue(g, "sum + v", 4, DataWireType.Int);

            var scenario = new ScenarioData { Variables = StandardVariables(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(9, vars.GetInt("sum", 0));
            Assert.Equal(42, vars.GetInt("last", 0));
        }

        [Fact]
        public void BodyChainRejoiningAncestor_IsALocatedCycleReject()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "cycle" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachNode { Id = 2, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "sum", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 2, TriggerGraph.ActionExecInPort)); // body → its own loop

            var ex = Assert.Throws<System.Text.Json.JsonException>(() => g.BuildExecutionOrder());
            Assert.Contains("cycle", ex.Message);
        }

        // ── Review P11: mid-loop death — the verbatim-dead-anchor contract (non-batched entity loop) ──

        [Fact]
        public void EntityForEach_MemberKilledMidLoop_BodyStillRuns_AndItsRunEffectNoOpsAtTheDeadAnchor()
        {
            // Pins the RunEffect anchorOverride contract EXACTLY as implemented (the override is used VERBATIM;
            // never silently re-anchored elsewhere). Three P1 units close together: A(id0,hp100), B(id1,hp5),
            // C(id2,hp100), pairwise within radius 5. Body per iteration: sum += 1, then run_effect
            // SearchArea(r=5, Ally, Damage 10 Normal) anchored at the CURRENT entity.
            //   iter A: allies of A = {B, C} → B (hp 5) dies through the full death sequence; C takes ONE hit;
            //   iter B: B is a DEAD snapshot member — its body iteration STILL RUNS (sum reaches 3) and its
            //           run_effect anchors at dead B verbatim (SearchArea returns 0 targets for a dead center).
            //           Re-anchoring onto an alive unit instead (e.g. the legacy lowest-id-alive fallback → A)
            //           would hit C a SECOND time — the byte-equality assert below is the teeth;
            //   iter C: allies of C = {A} → A takes ONE hit.
            var world = new EntityWorld();
            int a = world.Create(new FixedVec3(Fixed.Zero,         Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(100), Fixed.One);
            int b = world.Create(new FixedVec3(Fixed.FromInt(2),   Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(5),   Fixed.One);
            int c = world.Create(new FixedVec3(Fixed.FromInt(4),   Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(100), Fixed.One);

            TriggerGraph g = TriggerGraph.BuildForEachTrigger("mid-death", "match_start", "faction_units",
                arrayName: null, faction: 0, regionId: null, upTo: 64, loopVar: null,
                bodyActions: new NodeBase[]
                {
                    new ActionNode { Kind = "set_variable", Variable = "sum" }, // id 3 (helper layout)
                    new EffectActionNode                                        // id 4
                    {
                        Effect = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,
                            new DamageEffect(Fixed.FromInt(10), DamageType.Normal)),
                    },
                });
            WireValue(g, "sum + 1", 3, DataWireType.Int);

            var scenario = new ScenarioData { Variables = StandardVariables(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars, _) = Build(scenario);
            director.Tick(world, Fixed.One);

            Assert.Equal(3, vars.GetInt("sum", 0));                  // the dead member's iteration still ran
            Assert.False(world.IsAlive(b));                          // killed by iteration A's fan-out
            Assert.True(world.Health[a] < Fixed.FromInt(100));       // C's iteration landed A's single hit
            Assert.Equal(world.Health[a].Raw, world.Health[c].Raw);  // C took NO extra hit from B's dead-anchor iteration
        }

        // ── region_units runtime execution (review P4 — previously gate-only coverage) ──

        /// <summary>The declared region [-10,-10 → 10,10] as scenario declaration + resolved runtime store.</summary>
        private static ScenarioRegion[] ZoneRegions() => new[]
        {
            new ScenarioRegion { Id = "zone", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
        };

        private static RegionStore ZoneStore() => new RegionStore(
            new[] { "zone" },
            new[] { new FixedRect(Fixed.FromInt(-10), Fixed.FromInt(-10), Fixed.FromInt(10), Fixed.FromInt(10)) });

        [Theory]
        [InlineData(0)]   // faction-filtered: only in-region P1 units
        [InlineData(-1)]  // any-faction: every in-region unit
        public void RegionForEach_TouchesOnlyInRegionMatchingUnits(int factionFilter)
        {
            var world = new EntityWorld();
            int inP1  = world.Create(new FixedVec3(Fixed.Zero,         Fixed.Zero, Fixed.Zero),        Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int outP1 = world.Create(new FixedVec3(Fixed.FromInt(50),  Fixed.Zero, Fixed.Zero),        Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int inP2  = world.Create(new FixedVec3(Fixed.FromInt(1),   Fixed.Zero, Fixed.FromInt(1)),  Faction.Player2, Fixed.FromInt(10), Fixed.One);

            TriggerGraph g = TriggerGraph.BuildForEachTrigger("rgn", "match_start", "region_units",
                arrayName: null, faction: factionFilter, regionId: "zone", upTo: 64, loopVar: null,
                bodyActions: new NodeBase[] { new EffectActionNode { Effect = new DirectHpDeltaEffect(Fixed.FromInt(-5)) } });
            var scenario = new ScenarioData
            {
                Variables = StandardVariables(),
                Regions = ZoneRegions(),
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            (ScenarioDirector director, _, _) = Build(scenario);
            director.SetRegionStore(ZoneStore());
            director.Tick(world, Fixed.One);

            // In-region + faction-matching units took the body effect; the OUT-OF-REGION same-faction unit is
            // the region-filter teeth (dropping the Contains check in SnapshotForEach damages it → red).
            Assert.Equal(Fixed.FromInt(5).Raw,  world.Health[inP1].Raw);
            Assert.Equal(Fixed.FromInt(10).Raw, world.Health[outP1].Raw);
            Assert.Equal(factionFilter == -1 ? Fixed.FromInt(5).Raw : Fixed.FromInt(10).Raw, world.Health[inP2].Raw);
        }

        // ── Two-run determinism at checksum altitude (live loop) ────────────────

        [Fact]
        public void LiveForEach_TwoHeadlessRuns_AreByteIdentical()
        {
            static List<uint> Run()
            {
                var world = new EntityWorld();
                for (int i = 0; i < 6; i++)
                    world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero),
                        i % 2 == 0 ? Faction.Player1 : Faction.Player2, Fixed.FromInt(100), Fixed.One);

                var g = EntityLoopGraph(factionSlot: 1, upTo: 64, loopVar: null,
                    new EffectActionNode { Effect = new DirectHpDeltaEffect(Fixed.FromInt(-1)) });
                // Re-fire every tick: swap the one-shot match_start for a polled threshold event.
                ((EventNode)g.Nodes.Find(n => n is EventNode)!).Kind = "unit_count_threshold";
                ((EventNode)g.Nodes.Find(n => n is EventNode)!).Faction = 1;
                ((EventNode)g.Nodes.Find(n => n is EventNode)!).Count = 1;
                ((EventNode)g.Nodes.Find(n => n is EventNode)!).Operator = ">=";

                var scenario = new ScenarioData
                {
                    Variables = StandardVariables(),
                    TriggerGraphJson = g.ToCanonicalJson(),
                };
                var vars = new DslVarTable();
                var loop = new DslLoopState();
                var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop);
                director.LoadScenario(scenario);

                var buildings = new BuildingStore();
                var resources = new ResourceStore(Fixed.Zero);
                var registry  = new FactionRegistry(2);
                var seq = new List<uint>();
                for (int t = 0; t < 12; t++)
                {
                    director.Tick(world, Fixed.One);
                    seq.Add(SimChecksum.Compute(world, buildings, resources, registry,
                        null, null, null, null, null, vars, loop));
                }
                return seq;
            }

            Assert.Equal(Run(), Run());
        }
    }
}
