#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-658 — <see cref="BuildingStore.Destroy"/> tears the razed producer's production queue down.
    ///
    /// <para><b>The defect.</b> <c>Destroy</c> used to flip <see cref="BuildingStore.Alive"/> and push the free-list
    /// entry and nothing else. The depth-5 <see cref="BuildingStore.ProductionQueue"/> row and the head
    /// <see cref="BuildingStore.ProductionTimer"/> were zeroed only by the NEXT <see cref="BuildingStore.Create"/>
    /// that happened to recycle that slot — which may never come. Two consequences, both covered below:</para>
    ///
    /// <para>(1) <b>No refund.</b> A producer razed mid-training silently forfeited every already-paid-for order
    /// sitting in its queue. WC3 refunds them, and <see cref="BuildingSystem.CancelTrainCommand"/> already held the
    /// exact re-resolve-from-def refund machinery — so razing and cancelling now pay out identically (100%, head
    /// slot included; elapsed training time buys nothing back either way).</para>
    ///
    /// <para>(2) <b>Phantom orders in the fold.</b> <see cref="SimChecksum"/>'s building loop runs <c>0..Count</c>
    /// with NO Alive filter (deliberately — the folded SET must stay stable and slot-aligned), so a dead slot's
    /// stale queue bytes and timer stayed hashed. Deterministic on every peer, so never a desync, but the folded
    /// state described orders for a building that no longer existed. The fix zeroes the row at the moment of death;
    /// it changes folded VALUES only, never which fields are folded.</para>
    ///
    /// <para><b>Determinism.</b> The refund walks the queue in ascending slot order and re-resolves each slot's cost
    /// from the faction definition by the slot's stored encoded index (never a remembered cost — the slot stores only
    /// the index), so it is identical on every peer and in replay. The teardown itself is unconditional integer work
    /// in <see cref="BuildingStore"/>; the refund rides an UNFOLDED callback, so a store with no
    /// <c>BuildingSystem</c> wired (a golden fixture, a bare unit-test store) still clears the queue identically.</para>
    /// </summary>
    public class ProductionDestroyRefundTests
    {
        /// <summary>Ore cost of the one trainable Melee unit below — the per-order refund quantum.</summary>
        private const int GruntCostOre = 10;

        /// <summary>The Units index of the trainable Melee unit in <see cref="TestFaction"/>.</summary>
        private const int Grunt = 1;

        /// <summary>Starting ore — abundant next to the 10-ore unit cost and safely inside the 16.16 Fixed range.</summary>
        private const int StartingOre = 10000;

        /// <summary>A faction whose Melee category holds ONE unit (Units index 1) with an authored ore cost, so a
        /// refund is unambiguous. Mirrors <c>ProductionQueueCorrectnessTests.TestFaction</c>.</summary>
        private static FactionDefinition TestFaction(float trainTime = 5f, int costCrystal = 0)
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });   // 0
            f.Units.Add(new UnitDefinition                                                       // 1
            {
                Id = "grunt", Category = "Melee", Hp = 100f, CostOre = GruntCostOre,
                CostCrystal = costCrystal, TrainTime = trainTime, Supply = 0,
            });
            return f;
        }

        /// <summary>A sim harness with a huge supply cap (so nothing below is ever supply-gated) and abundant ore.</summary>
        private static (BuildingSystem sys, BuildingStore buildings, ResourceStore resources, EntityWorld world)
            Harness(float trainTime = 5f, int costCrystal = 0)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(StartingOre));
            resources.AddCrystal(Faction.Player1, Fixed.FromInt(StartingOre));
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 200 });
            var sys = new BuildingSystem(buildings, resources,
                                         TestFaction(trainTime, costCrystal), TestFaction(trainTime, costCrystal));
            sys.Tick(world, Fixed.Zero); // resolve SupplyCap from the configured starting cap
            return (sys, buildings, resources, world);
        }

        private static int Barracks(BuildingSystem sys, Faction faction = Faction.Player1) =>
            sys.PlaceBuildingDirect(BuildingType.Barracks, faction, FixedVec3.Zero, preBuilt: true);

        /// <summary>A faction's ore as a RAW Fixed, so every balance below is compared exactly (never through a
        /// float round-trip) — a refund that is off by one quantum must be visible.</summary>
        private static int OreRaw(ResourceStore r, Faction f = Faction.Player1) => r.Ore[(int)f].Raw;

        /// <summary>A faction's crystal as a RAW Fixed (see <see cref="OreRaw"/>).</summary>
        private static int CrystalRaw(ResourceStore r, Faction f = Faction.Player1) => r.Crystal[(int)f].Raw;

        /// <summary>The raw Fixed for a whole-number resource amount — the expected side of every balance assert.</summary>
        private static int Raw(int amount) => Fixed.FromInt(amount).Raw;

        // ── (1) The refund ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void RazingAProducer_RefundsEveryPaidForQueuedOrder()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);

            for (int i = 0; i < 3; i++) Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(3, buildings.QueueLength(b));
            Assert.Equal(Raw(StartingOre - 3 * GruntCostOre), OreRaw(resources));

            buildings.Destroy(b);

            // RED without the fix: Destroy flipped Alive and nothing else, so all 30 ore stayed spent forever.
            Assert.Equal(Raw(StartingOre), OreRaw(resources));
        }

        [Fact]
        public void RazingAProducer_RefundsTheHeadOrderToo_EvenPartWayThroughItsTimer()
        {
            var (sys, buildings, resources, world) = Harness(trainTime: 10f);
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt));   // head, 10s
            Assert.True(sys.TrainUnit(b, resources, Grunt));   // waiting

            sys.Tick(world, Fixed.FromInt(4));                 // 4s of the head's 10s elapsed — nothing spawned yet
            Assert.Equal(2, buildings.QueueLength(b));
            Assert.True(buildings.ProductionTimer[b] > Fixed.Zero);

            buildings.Destroy(b);

            // The head's elapsed progress buys nothing back and costs nothing extra: both orders refund in full,
            // exactly as cancelling the head discards its progress and still refunds 100%.
            Assert.Equal(Raw(StartingOre), OreRaw(resources));
        }

        [Fact]
        public void RazingAProducer_PaysOutExactlyWhatCancellingEveryOrderWouldHave()
        {
            // The two paths share one refund helper; this pins that they can never drift into different payouts.
            var (cancelSys, cancelBuildings, cancelRes, _) = Harness();
            int cb = Barracks(cancelSys);
            for (int i = 0; i < 4; i++) Assert.True(cancelSys.TrainUnit(cb, cancelRes, Grunt));
            for (int i = 0; i < 4; i++) Assert.True(cancelSys.CancelTrainCommand(cb, Faction.Player1, 0));
            Assert.Equal(0, cancelBuildings.QueueLength(cb));

            var (razeSys, razeBuildings, razeRes, _) = Harness();
            int rb = Barracks(razeSys);
            for (int i = 0; i < 4; i++) Assert.True(razeSys.TrainUnit(rb, razeRes, Grunt));
            razeBuildings.Destroy(rb);

            Assert.Equal(OreRaw(cancelRes), OreRaw(razeRes));
            Assert.Equal(Raw(StartingOre), OreRaw(razeRes));
        }

        [Fact]
        public void RazingAProducer_RefundsEveryResourceTheOrderSpent_NotOreOnly()
        {
            // The refund re-resolves the FULL ResolvedCost map, so a multi-resource unit gets its crystal back too.
            var (sys, buildings, resources, _) = Harness(costCrystal: 7);
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(Raw(StartingOre - 2 * 7), CrystalRaw(resources));

            buildings.Destroy(b);

            Assert.Equal(Raw(StartingOre), OreRaw(resources));
            Assert.Equal(Raw(StartingOre), CrystalRaw(resources));
        }

        [Fact]
        public void RazingAnIdleProducer_CreditsNothing()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            Assert.Equal(0, buildings.QueueLength(b));

            buildings.Destroy(b);

            Assert.Equal(Raw(StartingOre), OreRaw(resources)); // no queue ⇒ no refund, not a free 10 ore
        }

        [Fact]
        public void RazingTheSameProducerTwice_RefundsOnlyOnce()
        {
            // The double-free guard fires before the hook, so a second Destroy cannot mint the queue's cost again.
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.True(sys.TrainUnit(b, resources, Grunt));

            buildings.Destroy(b);
            Assert.Equal(Raw(StartingOre), OreRaw(resources));
            buildings.Destroy(b);
            Assert.Equal(Raw(StartingOre), OreRaw(resources));
        }

        [Fact]
        public void RazingAProducer_CreditsItsOwnerNotTheRazer()
        {
            var (sys, buildings, resources, _) = Harness();
            int victim = Barracks(sys, Faction.Player2);
            Assert.True(sys.TrainUnit(victim, resources, Grunt));
            Assert.True(sys.TrainUnit(victim, resources, Grunt));
            Assert.Equal(Raw(StartingOre - 2 * GruntCostOre), OreRaw(resources, Faction.Player2));
            int p1Before = OreRaw(resources, Faction.Player1);

            buildings.Destroy(victim);

            Assert.Equal(Raw(StartingOre), OreRaw(resources, Faction.Player2));
            Assert.Equal(p1Before, OreRaw(resources, Faction.Player1)); // the razer gains nothing
        }

        // ── (2) The teardown ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void RazingAProducer_ZeroesEveryQueueSlotAndTheHeadTimer_Immediately()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            for (int i = 0; i < BuildingStore.QUEUE_DEPTH; i++) Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(BuildingStore.QUEUE_DEPTH, buildings.QueueLength(b));
            Assert.True(buildings.ProductionTimer[b] > Fixed.Zero);

            buildings.Destroy(b);

            // RED without the fix: the row and the timer survived until the slot was recycled — which may never happen.
            int head = buildings.HeadIndex(b);
            for (int k = 0; k < BuildingStore.QUEUE_DEPTH; k++)
                Assert.Equal(0, buildings.ProductionQueue[head + k]);
            Assert.Equal(Fixed.Zero.Raw, buildings.ProductionTimer[b].Raw);
            Assert.Equal(0, buildings.QueueLength(b));
        }

        [Fact]
        public void TheTeardownIsUnconditional_AStoreWithNoBuildingSystemWiredStillClearsTheQueue()
        {
            // The refund rides an optional callback; the CLEARING must not. A bare store (golden fixture, editor
            // undo capture, unit test) has no hook and no ResourceStore to credit — its queue must still go to zero.
            var buildings = new BuildingStore();
            int b = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Barracks);
            int head = buildings.HeadIndex(b);
            for (int k = 0; k < BuildingStore.QUEUE_DEPTH; k++) buildings.ProductionQueue[head + k] = (byte)(k + 1);
            buildings.ProductionTimer[b] = Fixed.FromInt(3);

            buildings.Destroy(b);

            for (int k = 0; k < BuildingStore.QUEUE_DEPTH; k++)
                Assert.Equal(0, buildings.ProductionQueue[head + k]);
            Assert.Equal(Fixed.Zero.Raw, buildings.ProductionTimer[b].Raw);
        }

        [Fact]
        public void ARazedProducersQueue_LeavesTheChecksumFoldImmediately_NotOnTheNextRecycle()
        {
            // The building fold runs 0..Count with NO Alive filter, so a dead slot's bytes are still hashed. Two
            // worlds that differ ONLY in whether the (now-dead) barracks ever trained must therefore hash IDENTICALLY:
            // the queue is zeroed and the ore is refunded, so nothing observable is left behind.
            //
            // RED without the fix on BOTH counts — the trained arm kept 5 non-zero queue bytes plus a live head timer
            // in the fold, and its owner was still 50 ore poorer.
            uint trainedThenRazed = ChecksumAfter(train: BuildingStore.QUEUE_DEPTH);
            uint razedIdle        = ChecksumAfter(train: 0);

            Assert.Equal(razedIdle, trainedThenRazed);
        }

        [Fact]
        public void ARazedProducersQueue_StillMovesTheChecksumWhileItIsALIVE()
        {
            // Guard on the guard above: prove the equality it asserts is the TEARDOWN working, not the queue being
            // invisible to the hash in the first place. The same two arms, compared BEFORE the raze, must DIFFER.
            Assert.NotEqual(ChecksumAfter(train: 0, raze: false),
                            ChecksumAfter(train: BuildingStore.QUEUE_DEPTH, raze: false));
        }

        /// <summary>Build a one-barracks world, enqueue <paramref name="train"/> orders, optionally raze the
        /// barracks, and return the <see cref="SimChecksum"/> over the result. Nothing is ticked, so no order can
        /// complete and the only difference between two calls is the queue (and the ore it spent).</summary>
        private static uint ChecksumAfter(int train, bool raze = true)
        {
            var registry  = new FactionRegistry(2);
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(StartingOre));
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 200 });
            var sys = new BuildingSystem(buildings, resources, TestFaction(), TestFaction());
            sys.Tick(world, Fixed.Zero);

            int b = Barracks(sys);
            for (int i = 0; i < train; i++) Assert.True(sys.TrainUnit(b, resources, Grunt));
            if (raze) buildings.Destroy(b);

            return SimChecksum.Compute(world, buildings, resources, registry);
        }

        // ── Interactions the teardown must not break ──────────────────────────────────────────────────────

        [Fact]
        public void ARecycledSlot_StartsIdle_AndTheRefundNeverFiresTwiceForOneOrder()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            buildings.Destroy(b);
            Assert.Equal(Raw(StartingOre), OreRaw(resources));

            int recycled = Barracks(sys);            // pops the freed slot off the LIFO free-list
            Assert.Equal(b, recycled);
            Assert.Equal(0, buildings.QueueLength(recycled));
            Assert.Equal(Fixed.Zero.Raw, buildings.ProductionTimer[recycled].Raw);
            Assert.Equal(Raw(StartingOre), OreRaw(resources)); // Create's own reset cannot re-refund what Destroy already paid
        }

        [Fact]
        public void TheRefundHookSurvivesTheEditPlayReset()
        {
            // SimulationHost.ClearForReset clears the STORES in place and never reconstructs BuildingSystem, so the
            // destroy-time refund callback is ctor-lifetime WIRING that Clear() must preserve — dropping it would
            // silently disable the raze refund from the second Play onward. This is the positive pin behind the
            // "_onDestroyRefund" entry in StoreClearCompletenessTests' BuildingStore reset-sweep allowlist.
            var (sys, buildings, resources, world) = Harness();
            buildings.Clear();
            resources.Clear();
            sys.Tick(world, Fixed.Zero);

            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, Grunt));
            Assert.Equal(Raw(StartingOre - GruntCostOre), OreRaw(resources));

            buildings.Destroy(b);
            Assert.Equal(Raw(StartingOre), OreRaw(resources));
        }

        [Fact]
        public void ARazedProducersQueue_StillHoldsNoSupplyReservation()
        {
            // DW-478's dead-building skip in QueuedSupply is now belt-and-braces (the queue is empty anyway); both
            // must agree, or a razed barracks could eat its owner's supply headroom for the rest of the match.
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(StartingOre));
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 2 });
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Units.Add(new UnitDefinition { Id = "grunt", Category = "Melee", Hp = 100f, CostOre = GruntCostOre,
                                            TrainTime = 5f, Supply = 1 });
            var sys = new BuildingSystem(buildings, resources, f, f);
            sys.Tick(world, Fixed.Zero);

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
    }
}
