#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// The two RUNTIME correctness holes the depth-5 production queue (Story 11.6) shipped with, each with a case
    /// that is demonstrably RED without its fix.
    ///
    /// <para><b>DW-478 — queued supply was never reserved.</b> <see cref="BuildingSystem.TrainUnit"/> gated every
    /// enqueue against the LIVE <see cref="ResourceStore.SupplyUsed"/>, which <see cref="SupplySystem"/> recomputes
    /// from ALIVE entities and which production only touches at SPAWN — so all five enqueues saw the same untouched
    /// headroom and a full queue could overshoot the supply cap by up to 4. The recorded owner decision (2026-07-30)
    /// is WC3-strict: count queued supply against the cap. The reservation is a gate-time PROJECTION
    /// (<see cref="BuildingSystem.QueuedSupply"/>), never a write into the checksum-folded SupplyUsed array.</para>
    ///
    /// <para><b>DW-479 — a blocked spawn discarded the paid-for order.</b> <c>TickProduction</c> called
    /// <c>SpawnTrainedUnit</c> then <c>AdvanceQueue</c> UNCONDITIONALLY, so at the <see cref="EntityWorld"/> entity
    /// cap the head order was popped with no unit and no refund — and the depth-5 queue then burned one more paid
    /// slot every tick the world stayed full. The queue now advances only on a successful spawn.</para>
    ///
    /// <para><b>Determinism.</b> Both fixes are integer/queue reads in a fixed ascending order and write no new sim
    /// state: DW-478 adds a read-only projection at a REJECT gate, DW-479 removes a state mutation on a path that
    /// only runs at the entity cap. No <see cref="SimChecksum"/> input's VALUE changes on any path a recorded golden
    /// exercises (no golden scenario trains at the entity cap or over its supply cap), so no golden moves.</para>
    /// </summary>
    public class ProductionQueueCorrectnessTests
    {
        /// <summary>A faction whose Melee category holds ONE unit (Units index 1) with an authored per-unit supply
        /// cost and train time, so a reservation is unambiguous.</summary>
        private static FactionDefinition TestFaction(int supply, float trainTime)
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });                    // 0
            f.Units.Add(new UnitDefinition                                                                       // 1
            {
                Id = "grunt", Category = "Melee", Hp = 100f, CostOre = 10,
                TrainTime = trainTime, Supply = supply,
            });
            return f;
        }

        /// <summary>The Units index of the trainable Melee unit in <see cref="TestFaction"/>.</summary>
        private const int Grunt = 1;

        /// <summary>
        /// A sim harness whose supply cap is CONFIGURED (not poked into <see cref="ResourceStore.SupplyCap"/>), so
        /// <c>RecalculateSupplyCaps</c> reproduces it on every <see cref="BuildingSystem.Tick"/> instead of resetting
        /// it to the compile default. Both player slots share the same faction def. Ore is deliberately abundant so
        /// every rejection in these tests is unambiguously the SUPPLY gate, never affordability.
        /// </summary>
        private static (BuildingSystem sys, BuildingStore buildings, ResourceStore resources, EntityWorld world)
            Harness(int startingCap, int supply = 1, float trainTime = 5f)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            // 10000 starting ore — abundant next to the 10-ore unit cost, and safely inside the 16.16 Fixed range
            // (FromInt(100000) would overflow it and hand every faction NEGATIVE ore).
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.ConfigureSupply(new SupplyConfig { StartingCap = startingCap });
            var sys = new BuildingSystem(buildings, resources,
                                         TestFaction(supply, trainTime), TestFaction(supply, trainTime));
            sys.Tick(world, Fixed.Zero); // resolve SupplyCap from the configured starting cap
            return (sys, buildings, resources, world);
        }

        private static int Barracks(BuildingSystem sys, Faction faction = Faction.Player1) =>
            sys.PlaceBuildingDirect(BuildingType.Barracks, faction, FixedVec3.Zero, preBuilt: true);

        // ── DW-478: queued orders reserve supply at ENQUEUE ────────────────────────────────────────────────

        [Fact]
        public void Enqueue_CountsQueuedSupplyAgainstTheCap_SoTheQueueCannotOvershoot()
        {
            var (sys, buildings, resources, _) = Harness(startingCap: 3);
            int b = Barracks(sys);
            Assert.Equal(3, resources.SupplyCap[(int)Faction.Player1]);
            Assert.Equal(0, resources.SupplyUsed[(int)Faction.Player1]); // nothing alive yet — the whole point

            Assert.True(sys.TrainUnit(b, resources, Grunt)); // reserves 1 of 3
            Assert.True(sys.TrainUnit(b, resources, Grunt)); // reserves 2 of 3
            Assert.True(sys.TrainUnit(b, resources, Grunt)); // reserves 3 of 3 — cap reached
            Assert.Equal(3, sys.QueuedSupply(Faction.Player1));

            // RED without DW-478: SupplyUsed is still 0 (supply is consumed at SPAWN), so orders 4 and 5 saw the
            // same untouched headroom and BOTH were accepted — a depth-5 queue overshooting the cap by 2.
            Fixed oreAtCap = resources.Ore[(int)Faction.Player1];
            Assert.False(sys.TrainUnit(b, resources, Grunt));
            Assert.False(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(3, buildings.QueueLength(b));
            Assert.Equal(oreAtCap.Raw, resources.Ore[(int)Faction.Player1].Raw); // a rejected order spends nothing
        }

        [Fact]
        public void Enqueue_ReservesTheUnitsFullSupplyCost_NotOnePerOrder()
        {
            // A supply-3 unit must reserve 3, not 1: with cap 5 exactly ONE fits, and the second order is denied
            // (3 + 3 = 6 > 5) even though the raw ORDER count is only two.
            var (sys, buildings, resources, _) = Harness(startingCap: 5, supply: 3);
            int b = Barracks(sys);

            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(3, sys.QueuedSupply(Faction.Player1));
            Assert.False(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(1, buildings.QueueLength(b));
        }

        [Fact]
        public void QueuedSupply_IsPerFaction_AnEnemyQueueNeverEatsYourHeadroom()
        {
            var (sys, buildings, resources, _) = Harness(startingCap: 2);
            int p1 = Barracks(sys, Faction.Player1);
            int p2 = Barracks(sys, Faction.Player2);

            Assert.True(sys.TrainUnit(p2, resources, Grunt));
            Assert.True(sys.TrainUnit(p2, resources, Grunt));
            Assert.Equal(2, sys.QueuedSupply(Faction.Player2));
            Assert.Equal(0, sys.QueuedSupply(Faction.Player1)); // the scan is faction-filtered, not global

            // Player1 still gets its OWN full cap despite Player2's queue being full.
            Assert.True(sys.TrainUnit(p1, resources, Grunt));
            Assert.True(sys.TrainUnit(p1, resources, Grunt));
            Assert.False(sys.TrainUnit(p1, resources, Grunt));
            Assert.Equal(2, buildings.QueueLength(p1));
        }

        [Fact]
        public void SpawnedOrder_MovesFromReservedToLiveSupply_NeverDoubleCounted()
        {
            // The reservation must hand off cleanly to the live count as each order spawns: total headroom consumed
            // is (live + queued), so a completed order must not be charged twice (once as a queued slot, once as a
            // live unit) — that would make the queue progressively harder to refill for no reason.
            var (sys, buildings, resources, world) = Harness(startingCap: 2);
            var supplySys = new SupplySystem(resources);
            int b = Barracks(sys);

            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.False(sys.TrainUnit(b, resources, Grunt)); // 2 reserved == cap

            sys.Tick(world, Fixed.FromInt(100));       // head completes → one unit spawned, one order still queued
            supplySys.Tick(world, Fixed.FromInt(100)); // live SupplyUsed recomputed from alive entities

            Assert.Equal(1, resources.SupplyUsed[(int)Faction.Player1]);
            Assert.Equal(1, sys.QueuedSupply(Faction.Player1));
            Assert.False(sys.TrainUnit(b, resources, Grunt)); // 1 live + 1 reserved == cap 2 → still full

            sys.Tick(world, Fixed.FromInt(100));       // the promoted head completes too
            supplySys.Tick(world, Fixed.FromInt(100));

            Assert.Equal(2, resources.SupplyUsed[(int)Faction.Player1]);
            Assert.Equal(0, sys.QueuedSupply(Faction.Player1)); // the queue is empty — reservation fully released
            Assert.False(sys.TrainUnit(b, resources, Grunt));   // now blocked purely by the 2 LIVE units
        }

        [Fact]
        public void CancellingAQueuedOrder_ReleasesItsSupplyReservation()
        {
            var (sys, buildings, resources, _) = Harness(startingCap: 2);
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.False(sys.TrainUnit(b, resources, Grunt)); // reserved to the cap

            Assert.True(sys.CancelTrainCommand(b, Faction.Player1, 1)); // drop the waiting order
            Assert.Equal(1, sys.QueuedSupply(Faction.Player1));

            // The freed headroom is immediately re-usable — no bookkeeping needed, because the reservation is
            // re-derived from the queue on every gate call.
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(2, buildings.QueueLength(b));
        }

        [Fact]
        public void DestroyedBuildingsQueue_HoldsNoReservation()
        {
            // DW-848 — the rationale, corrected. This assertion originally read "a razed producer's slots are not
            // cleared by BuildingStore.Destroy, so the scan MUST skip dead buildings", which stopped being true at
            // DW-658: Destroy now refunds and ZEROES the whole ProductionQueue row plus the head ProductionTimer, so
            // a razed barracks holds nothing to scan in the first place. DW-478's dead-building skip in QueuedSupply
            // is therefore DEFENCE IN DEPTH, not the load-bearing mechanism — the two must agree, and this test is
            // what says so from the queue side. The invariant itself is pinned positively from the destroy side by
            // ProductionDestroyRefundTests.ARazedProducersQueue_StillHoldsNoSupplyReservation.
            var (sys, buildings, resources, _) = Harness(startingCap: 2);
            int dead = Barracks(sys);
            Assert.True(sys.TrainUnit(dead, resources, Grunt));
            Assert.True(sys.TrainUnit(dead, resources, Grunt));
            Assert.Equal(2, sys.QueuedSupply(Faction.Player1));

            buildings.Destroy(dead);
            Assert.Equal(0, sys.QueuedSupply(Faction.Player1));

            int live = Barracks(sys);
            Assert.True(sys.TrainUnit(live, resources, Grunt));
            Assert.True(sys.TrainUnit(live, resources, Grunt));
        }

        [Fact]
        public void SupplyGatingDisabled_QueuedReservationIsBypassedToo()
        {
            // The reservation rides the SAME ResourceStore.HasSupply gate, so a scenario that disables supply gating
            // stays entirely ungated — the DW-478 term must not sneak a cap back in.
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            // 10000 starting ore — abundant next to the 10-ore unit cost, and safely inside the 16.16 Fixed range
            // (FromInt(100000) would overflow it and hand every faction NEGATIVE ore).
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 1, Enabled = false });
            var sys = new BuildingSystem(buildings, resources, TestFaction(1, 5f));
            sys.Tick(world, Fixed.Zero);

            int b = Barracks(sys);
            for (int i = 0; i < BuildingStore.QUEUE_DEPTH; i++)
                Assert.True(sys.TrainUnit(b, resources, Grunt), $"order {i} must be accepted with gating disabled");
            Assert.Equal(BuildingStore.QUEUE_DEPTH, buildings.QueueLength(b));
        }

        // ── DW-479: a spawn refused at the entity cap must NOT consume the paid-for order ─────────────────

        [Fact]
        public void HeadSpawnBlockedAtTheEntityCap_KeepsThePaidOrder_ThenSpawnsItWhenSpaceFrees()
        {
            var (sys, buildings, resources, world) = Harness(startingCap: 500);
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt)); // head
            Assert.True(sys.TrainUnit(b, resources, Grunt)); // waiting
            Assert.Equal(2, buildings.QueueLength(b));

            // Fill EntityWorld to its hard cap so the completion's world.Create must fail.
            int filler = -1;
            for (int i = 0; i < EntityWorld.MAX_ENTITIES; i++)
            {
                int e = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(1));
                if (e < 0) break;
                filler = e;
            }
            Assert.True(filler >= 0);
            Assert.True(world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(1)) < 0);

            // RED without DW-479: AdvanceQueue ran regardless, so the head was popped with no unit and no refund —
            // QueueLength would be 1 here and 0 after the second tick (one paid slot burned PER TICK).
            sys.Tick(world, Fixed.FromInt(100));
            Assert.Equal(2, buildings.QueueLength(b));
            Assert.Equal((byte)(Grunt + 1), buildings.ProductionQueue[buildings.HeadIndex(b)]); // same head order
            Assert.Equal(0, buildings.TrainedCount[b]); // a blocked spawn burns no spawn-offset slot either

            sys.Tick(world, Fixed.FromInt(100)); // still full — the order parks, it does not drain
            Assert.Equal(2, buildings.QueueLength(b));
            Assert.Equal(0, buildings.TrainedCount[b]);

            // Free one entity slot: the parked order finally spawns and the queue advances exactly once.
            world.Destroy(filler);
            sys.Tick(world, Fixed.FromInt(100));
            Assert.Equal(1, buildings.QueueLength(b));
            Assert.Equal(1, buildings.TrainedCount[b]);
            Assert.Equal((byte)(Grunt + 1), buildings.ProductionQueue[buildings.HeadIndex(b)]); // the promoted order
        }

        [Fact]
        public void NormalCompletion_StillAdvancesExactlyOneOrderPerTick()
        {
            // The DW-479 restructure must leave the ordinary path untouched: one completion per building per tick,
            // the promoted head starting from its FULL train time.
            var (sys, buildings, resources, world) = Harness(startingCap: 500);
            int b = Barracks(sys);
            for (int i = 0; i < BuildingStore.QUEUE_DEPTH; i++)
                Assert.True(sys.TrainUnit(b, resources, Grunt));

            sys.Tick(world, Fixed.FromInt(100));
            Assert.Equal(BuildingStore.QUEUE_DEPTH - 1, buildings.QueueLength(b));
            Assert.Equal(Fixed.FromFloat(5f).Raw, buildings.ProductionTimer[b].Raw);
            Assert.Equal(1, buildings.TrainedCount[b]);

            sys.Tick(world, Fixed.FromInt(100));
            Assert.Equal(BuildingStore.QUEUE_DEPTH - 2, buildings.QueueLength(b));
            Assert.Equal(2, buildings.TrainedCount[b]);
        }

        [Fact]
        public void PartialTick_DoesNotCompleteTheHead()
        {
            // Guards the restructured decrement: a tick that does not exhaust the timer must leave the order in
            // production (a naive "spawn whenever the timer is not positive" rewrite would fire early).
            var (sys, buildings, resources, world) = Harness(startingCap: 500);
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt));

            sys.Tick(world, Fixed.FromInt(2));
            Assert.Equal(1, buildings.QueueLength(b));
            Assert.Equal(Fixed.FromFloat(3f).Raw, buildings.ProductionTimer[b].Raw);
            Assert.Equal(0, buildings.TrainedCount[b]);
            Assert.Equal(0, world.AliveCount);
        }
    }
}
