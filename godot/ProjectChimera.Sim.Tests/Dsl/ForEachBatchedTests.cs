#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.6 — the <c>for_each_batched</c> drip (the I/O-matrix row): a fired batched loop snapshots the
    /// entity set into its checksummed continuation row and drains <c>batch_size</c> per tick at the start of
    /// the director tick (25 units / batch 10 → 10/10/5 across three drain ticks); the trigger is suppressed in
    /// the sweep while draining; a unit killed mid-drain is skipped; the continuation chain runs on the
    /// completion tick; the fold moves the checksum; two headless runs are byte-identical.
    /// </summary>
    public class ForEachBatchedTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> Decls =
            new(StringComparer.Ordinal)
            {
                ["fires"] = (DslValueType.Int, VarScope.Global),
                ["done"]  = (DslValueType.Int, VarScope.Global),
            };

        private static ScenarioVariable[] Variables() => new[]
        {
            new ScenarioVariable { Name = "fires", Type = DslValueType.Int, Scope = VarScope.Global },
            new ScenarioVariable { Name = "done",  Type = DslValueType.Int, Scope = VarScope.Global },
        };

        /// <summary>
        /// trigger (unit_count_threshold, slot <paramref name="eventFaction"/>, ≥ 1) → set_variable fires += 1 →
        /// for_each_batched(P1, batch <paramref name="batchSize"/>) [body: DirectHpDelta −1 at the current
        /// entity] → continuation: set_variable done += 1. The LOOP always filters P1; the empty-snapshot test
        /// fires the EVENT off P2 so the P1 snapshot is genuinely empty.
        /// </summary>
        private static ScenarioData Scenario(int batchSize = 10, int eventFaction = 0)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "drip" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "unit_count_threshold", Faction = eventFaction, Count = 1, Operator = ">=" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "fires" });
            g.Nodes.Add(new ForEachBatchedNode { Id = 3, Source = "faction_units", Faction = 0, BatchSize = batchSize });
            g.Nodes.Add(new EffectActionNode { Id = 4, Effect = new DirectHpDeltaEffect(Fixed.FromInt(-1)) });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "done" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ForEachBodyOutPort, 4, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 5, TriggerGraph.ActionExecInPort)); // continuation

            (int firesRoot, _) = ExprParser.Parse("fires + 1", g, Decls);
            g.DataEdges.Add(new DataEdge(firesRoot, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));
            (int doneRoot, _) = ExprParser.Parse("done + 1", g, Decls);
            g.DataEdges.Add(new DataEdge(doneRoot, TriggerGraph.ExprDataOutPort, 5, TriggerGraph.ActionValueInPort, DataWireType.Int));

            return new ScenarioData { Variables = Variables(), TriggerGraphJson = g.ToCanonicalJson() };
        }

        private static (ScenarioDirector Director, DslVarTable Vars, DslLoopState Loop, EntityWorld World, int[] Units)
            Build(int unitCount, int batchSize = 10, int eventFaction = 0)
        {
            var world = new EntityWorld();
            var units = new int[unitCount];
            for (int i = 0; i < unitCount; i++)
                units[i] = world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero),
                    Faction.Player1, Fixed.FromInt(10), Fixed.One);

            var vars = new DslVarTable();
            var loop = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop);
            director.LoadScenario(Scenario(batchSize, eventFaction));
            return (director, vars, loop, world, units);
        }

        private static int CountDamaged(EntityWorld world, int[] units)
        {
            int n = 0;
            foreach (int u in units)
                if (world.IsAlive(u) && world.Health[u].Raw < Fixed.FromInt(10).Raw) n++;
            return n;
        }

        [Fact]
        public void Drip_Drains10_10_5_AcrossThreeTicks_WithContinuationOnTheCompletionTick()
        {
            (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world, int[] units) = Build(25);

            // Fire tick: the sweep snapshots 25 ids into the row — NO body work yet (the drain phase runs at the
            // START of the director tick, and it ran before this fire).
            director.Tick(world, Fixed.One);
            Assert.Equal(1, vars.GetInt("fires", 0));
            Assert.Equal(0, CountDamaged(world, units));
            Assert.True(loop.RowActive(0));
            Assert.Equal(25, loop.RowLength(0));
            Assert.Equal(0, loop.RowCursor(0));

            // Drain tick 1: 10 lowest ids damaged; the trigger is SUPPRESSED (fires stays 1).
            director.Tick(world, Fixed.One);
            Assert.Equal(10, CountDamaged(world, units));
            Assert.Equal(1, vars.GetInt("fires", 0));
            Assert.Equal(0, vars.GetInt("done", 0));

            // Drain tick 2: 10 more.
            director.Tick(world, Fixed.One);
            Assert.Equal(20, CountDamaged(world, units));
            Assert.Equal(0, vars.GetInt("done", 0));

            // Drain tick 3 (completion): the last 5 drain and the CONTINUATION runs THIS tick. With the drain
            // complete, the same tick's sweep is no longer suppressed, so the trigger may re-fire (fires = 2).
            director.Tick(world, Fixed.One);
            Assert.Equal(25, CountDamaged(world, units));
            Assert.Equal(1, vars.GetInt("done", 0));
            Assert.Equal(2, vars.GetInt("fires", 0));
        }

        [Fact]
        public void UnitKilledMidDrain_IsSkipped_AndTheDripStillCompletes()
        {
            (ScenarioDirector director, DslVarTable vars, _, EntityWorld world, int[] units) = Build(25);

            director.Tick(world, Fixed.One); // fire + snapshot
            director.Tick(world, Fixed.One); // drain 0..9

            int victim = units[12];          // in the NEXT batch
            Fixed victimHealth = world.Health[victim];
            world.Destroy(victim);

            director.Tick(world, Fixed.One); // drain 10..19 — the dead id 12 is skipped at drain time
            director.Tick(world, Fixed.One); // drain 20..24 → completion

            Assert.Equal(1, vars.GetInt("done", 0));
            Assert.False(world.IsAlive(victim));
            // 24 alive units damaged (the victim was skipped, not "damaged while dead").
            Assert.Equal(24, CountDamaged(world, units));
            Assert.Equal(victimHealth.Raw, world.Health[victim].Raw); // its health row was never touched by the drip
        }

        [Fact]
        public void BatchedRow_SlotRecycledBetweenFireAndDrain_IsSkipped_NotRunAgainstTheOccupant()
        {
            // Story 15-23 (DW-775): the row snapshots PACKED refs and the drain resolves them via TryResolveRef.
            // The kill-mid-drain test above covers plain death; this is the ABA half — a snapshotted unit dies AND
            // its slot recycles into a brand-new unit between director ticks, so the pre-15-23 IsAlive-only guard
            // would pass and run the trigger body against the slot's NEW occupant. The generation mismatch must
            // skip the row instead.
            (ScenarioDirector director, DslVarTable vars, _, EntityWorld world, int[] units) = Build(25);

            director.Tick(world, Fixed.One); // fire + snapshot (25 packed refs)
            director.Tick(world, Fixed.One); // drain 0..9

            int victim = units[12];          // in the NEXT batch
            world.Destroy(victim);
            int occupant = world.Create(new FixedVec3(Fixed.FromInt(12), Fixed.Zero, Fixed.Zero),
                Faction.Player1, Fixed.FromInt(10), Fixed.One);
            Assert.Equal(victim, occupant);  // LIFO recycle — same id, generation bumped; the slot IS alive again

            director.Tick(world, Fixed.One); // drain 10..19 — the recycled ref is skipped at drain time
            director.Tick(world, Fixed.One); // drain 20..24 → completion

            Assert.Equal(1, vars.GetInt("done", 0));                          // the drip still completed
            Assert.True(world.IsAlive(occupant));
            // The body (DirectHpDelta −1) never touched the occupant: 10 HP intact. RED pre-15-23 (9 HP).
            Assert.Equal(Fixed.FromInt(10).Raw, world.Health[occupant].Raw);
            Assert.Equal(24, CountDamaged(world, units));                     // every OTHER snapshotted unit drained
        }

        [Fact]
        public void ContinuationRowFold_MovesTheChecksum_WhileDraining()
        {
            (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world, _) = Build(25);
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var registry  = new FactionRegistry(2);

            director.Tick(world, Fixed.One); // fire + snapshot (row active)
            uint withRow = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars, loop);
            uint without = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars, null);
            Assert.NotEqual(without, withRow); // the live continuation row is checksummed state
        }

        [Fact]
        public void LiveBatchedDrip_TwoHeadlessRuns_AreByteIdentical()
        {
            static List<uint> Run()
            {
                (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world, _) = Build(25);
                var buildings = new BuildingStore();
                var resources = new ResourceStore(Fixed.Zero);
                var registry  = new FactionRegistry(2);
                var seq = new List<uint>();
                for (int t = 0; t < 10; t++)
                {
                    director.Tick(world, Fixed.One);
                    seq.Add(SimChecksum.Compute(world, buildings, resources, registry,
                        null, null, null, null, null, vars, loop));
                }
                return seq;
            }

            Assert.Equal(Run(), Run());
        }

        [Fact]
        public void EmptySnapshot_CompletesOnTheNextDrainTick_AndLiftsTheSuppression()
        {
            // Review (P2): the trigger genuinely FIRES with ZERO matching entities — the event polls the P2
            // count (slot 1) while the batched loop filters P1, so the fire snapshots an EMPTY row.
            (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world, _) =
                Build(unitCount: 0, batchSize: 10, eventFaction: 1);
            world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(10), Fixed.One);

            // Fire tick: a ZERO-LENGTH row is ACTIVE (no body work possible, but the row must not be stuck).
            director.Tick(world, Fixed.One);
            Assert.Equal(1, vars.GetInt("fires", 0));
            Assert.True(loop.RowActive(0));
            Assert.Equal(0, loop.RowLength(0));
            Assert.Equal(0, vars.GetInt("done", 0));

            // Next drain tick: the empty row COMPLETES (cursor 0 ≥ length 0), its continuation chain runs
            // (done = 1), and — the suppression teeth — the SAME tick's sweep sees the row inactive, so the
            // still-true polled event re-fires the trigger (fires = 2, a fresh empty row activates).
            director.Tick(world, Fixed.One);
            Assert.Equal(1, vars.GetInt("done", 0));   // continuation ran on the completion tick
            Assert.Equal(2, vars.GetInt("fires", 0));  // re-fired ⇒ the row WAS inactive at sweep time (not suppressed)
            Assert.True(loop.RowActive(0));            // the re-fire began a fresh (again empty) snapshot
            Assert.Equal(0, loop.RowLength(0));
        }

        // ── region_units runtime execution for the batched drip (review P4) ─────

        [Theory]
        [InlineData(0)]   // faction-filtered: only in-region P1 units
        [InlineData(-1)]  // any-faction: every in-region unit
        public void RegionBatched_DrainsOnlyInRegionMatchingUnits(int factionFilter)
        {
            var world = new EntityWorld();
            int inP1  = world.Create(new FixedVec3(Fixed.Zero,        Fixed.Zero, Fixed.Zero),       Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int outP1 = world.Create(new FixedVec3(Fixed.FromInt(50), Fixed.Zero, Fixed.Zero),       Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int inP2  = world.Create(new FixedVec3(Fixed.FromInt(1),  Fixed.Zero, Fixed.FromInt(1)), Faction.Player2, Fixed.FromInt(10), Fixed.One);

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "rgn-drip" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachBatchedNode { Id = 2, Source = "region_units", RegionId = "zone", Faction = factionFilter, BatchSize = 64 });
            g.Nodes.Add(new EffectActionNode { Id = 3, Effect = new DirectHpDeltaEffect(Fixed.FromInt(-5)) });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));

            var scenario = new ScenarioData
            {
                Regions = new[] { new ScenarioRegion { Id = "zone", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f } },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
            var vars = new DslVarTable();
            var loop = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop);
            director.LoadScenario(scenario);
            director.SetRegionStore(new RegionStore(
                new[] { "zone" },
                new[] { new FixedRect(Fixed.FromInt(-10), Fixed.FromInt(-10), Fixed.FromInt(10), Fixed.FromInt(10)) }));

            director.Tick(world, Fixed.One); // fire + snapshot (region + faction filters apply AT SNAPSHOT)
            director.Tick(world, Fixed.One); // drain the whole batch

            // Region-filter teeth: dropping the Contains check in SnapshotBatched damages the out-of-region unit.
            Assert.Equal(Fixed.FromInt(5).Raw,  world.Health[inP1].Raw);
            Assert.Equal(Fixed.FromInt(10).Raw, world.Health[outP1].Raw);
            Assert.Equal(factionFilter == -1 ? Fixed.FromInt(5).Raw : Fixed.FromInt(10).Raw, world.Health[inP2].Raw);
        }

        // ── MaxBatchSnapshot truncation (review P12) ────────────────────────────

        [Fact]
        public void SnapshotBeyondMaxBatchSnapshot_TruncatesToTheLowestIds()
        {
            // 2100 alive P1 units (the SoA world creates them cheaply) against the 2048-id row storage: the
            // ascending-id scan appends until SnapshotAppend saturates, so the LOWEST 2048 ids win.
            int over = DslBounds.MaxBatchSnapshot + 52;
            (ScenarioDirector director, _, DslLoopState loop, EntityWorld world, int[] units) = Build(over);

            director.Tick(world, Fixed.One); // fire + snapshot
            Assert.True(loop.RowActive(0));
            Assert.Equal(DslBounds.MaxBatchSnapshot, loop.RowLength(0));
            Assert.Equal(units[0], loop.RowId(0, 0));
            Assert.Equal(units[DslBounds.MaxBatchSnapshot - 1], loop.RowId(0, DslBounds.MaxBatchSnapshot - 1));
        }

        // ── SetCursor defensive floor (review P13) ──────────────────────────────

        [Fact]
        public void SetCursor_ClampsBothBelowZeroAndAboveLength()
        {
            var loop = new DslLoopState();
            loop.ConfigureRows(new[] { 7 });
            loop.BeginSnapshot(0);
            Assert.True(loop.SnapshotAppend(0, 5));
            loop.SetCursor(0, -3);
            Assert.Equal(0, loop.RowCursor(0)); // negative floors to 0 (deterministic defensive clamp)
            loop.SetCursor(0, 99);
            Assert.Equal(1, loop.RowCursor(0)); // high side still clamps to the live length
        }
    }
}
