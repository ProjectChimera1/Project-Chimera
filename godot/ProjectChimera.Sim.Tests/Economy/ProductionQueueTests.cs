#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Multiplayer; // OrderApplier / UnitOrder (wire-path parity)
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// Story 11.6 (FR-74) — depth-5 production queue with cancel/refund (WC3 model). Godot-free Tier-1 coverage of
    /// the I/O matrix: enqueue-to-5, queue-full reject, head advance on completion, cancel-waiting refund,
    /// cancel-head promote+refund, and the enemy/null/empty-slot/out-of-range deterministic no-ops. Supply is never
    /// refunded (only the ore/crystal cost is).
    /// </summary>
    public class ProductionQueueTests
    {
        // A faction whose Melee category has three siblings (indices 1,2,3), each a DISTINCT cost + train time so a
        // cancel's per-unit refund and a promoted head's fresh timer are unambiguous.
        private static FactionDefinition TestFaction()
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker",  Category = "Worker", Hp = 50f });                                // 0
            f.Units.Add(new UnitDefinition { Id = "melee_a", Category = "Melee",  Hp = 100f, CostOre = 50, TrainTime = 5f }); // 1
            f.Units.Add(new UnitDefinition { Id = "melee_b", Category = "Melee",  Hp = 110f, CostOre = 60, TrainTime = 7f }); // 2
            f.Units.Add(new UnitDefinition { Id = "melee_c", Category = "Melee",  Hp = 120f, CostOre = 70, TrainTime = 9f }); // 3
            return f;
        }

        private static (BuildingSystem sys, BuildingStore buildings, ResourceStore resources, EntityWorld world) Harness()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, TestFaction());
            return (sys, buildings, resources, world);
        }

        private static int Barracks(BuildingSystem sys) =>
            sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

        // ── Enqueue up to 5, then reject the 6th ────────────────────────────────────────────────────

        [Fact]
        public void Enqueue_FillsAllFiveSlots_ThenRejectsSixth_NoSpend()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);

            // Five accepted orders occupy slots 0-4, each spends its own 50 ore.
            Fixed ore0 = resources.Ore[(int)Faction.Player1];
            for (int i = 0; i < BuildingStore.QUEUE_DEPTH; i++)
                Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1), $"order {i} should be accepted");
            Assert.Equal(BuildingStore.QUEUE_DEPTH, buildings.QueueLength(b));
            Assert.Equal((ore0 - Fixed.FromInt(50 * BuildingStore.QUEUE_DEPTH)).Raw, resources.Ore[(int)Faction.Player1].Raw);

            // The 6th is rejected (queue full) — no spend, nothing else queued.
            Fixed oreFull = resources.Ore[(int)Faction.Player1];
            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Assert.Equal(BuildingStore.QUEUE_DEPTH, buildings.QueueLength(b));
            Assert.Equal(oreFull.Raw, resources.Ore[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void Enqueue_OnlyHeadStartsATimer_WaitingSlotsDoNot()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // head → timer starts at 5s
            Assert.Equal(Fixed.FromFloat(5f).Raw, buildings.ProductionTimer[b].Raw);

            // Appending a second order must NOT restart/alter the running head timer.
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 2)); // waiting slot 1
            Assert.Equal(Fixed.FromFloat(5f).Raw, buildings.ProductionTimer[b].Raw);
            Assert.Equal((byte)(1 + 1), buildings.ProductionQueue[buildings.HeadIndex(b) + 0]); // head is melee_a
            Assert.Equal((byte)(2 + 1), buildings.ProductionQueue[buildings.HeadIndex(b) + 1]); // waiting is melee_b
        }

        // ── Head completes → shift down + new head timer from full ──────────────────────────────────

        [Fact]
        public void HeadCompletes_SpawnsHead_ShiftsDown_NewHeadTimerFromFull()
        {
            var (sys, buildings, resources, world) = Harness();
            int b = Barracks(sys);

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // head: melee_a, 5s
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 2)); // waiting: melee_b, 7s
            Assert.Equal(2, buildings.QueueLength(b));

            // One big-dt tick completes exactly the head (a building completes at most one order per tick).
            sys.Tick(world, Fixed.FromInt(100));

            // The head (melee_a) spawned; the queue shifted down; the new head (melee_b) starts from its FULL 7s.
            Assert.Equal(1, buildings.QueueLength(b));
            Assert.Equal((byte)(2 + 1), buildings.ProductionQueue[buildings.HeadIndex(b) + 0]); // melee_b promoted
            Assert.Equal(Fixed.FromFloat(7f).Raw, buildings.ProductionTimer[b].Raw);            // full, not carried over

            int spawned = -1;
            for (int i = 0; i < world.HighWaterMark; i++) if (world.IsAlive(i)) { spawned = i; break; }
            Assert.True(spawned >= 0);
            Assert.Equal(Fixed.FromFloat(100f).Raw, world.EffectiveMaxHealth[spawned].Raw); // melee_a's Hp
        }

        [Fact]
        public void LastOrderCompletes_BuildingGoesIdle()
        {
            var (sys, buildings, resources, world) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1));

            sys.Tick(world, Fixed.FromInt(100)); // completes the only order

            Assert.Equal(0, buildings.QueueLength(b));
            Assert.Equal((byte)0, buildings.ProductionQueue[buildings.HeadIndex(b)]);
            Assert.Equal(Fixed.Zero.Raw, buildings.ProductionTimer[b].Raw);
        }

        // ── Cancel a WAITING slot → full refund + shift, head untouched ─────────────────────────────

        [Fact]
        public void CancelWaitingSlot_RefundsFullCost_ShiftsDown_HeadTimerUntouched()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // head:  melee_a (50 ore, 5s)
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 2)); // slot1: melee_b (60 ore, 7s)
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 3)); // slot2: melee_c (70 ore, 9s)
            Fixed headTimer = buildings.ProductionTimer[b];
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            // Cancel slot 1 (melee_b) → refund exactly 60 ore; slot 2 (melee_c) shifts down to slot 1; head untouched.
            Assert.True(sys.CancelTrainCommand(b, Faction.Player1, 1));
            Assert.Equal((oreBefore + Fixed.FromInt(60)).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(2, buildings.QueueLength(b));
            Assert.Equal((byte)(1 + 1), buildings.ProductionQueue[buildings.HeadIndex(b) + 0]); // head still melee_a
            Assert.Equal((byte)(3 + 1), buildings.ProductionQueue[buildings.HeadIndex(b) + 1]); // melee_c promoted into slot 1
            Assert.Equal(headTimer.Raw, buildings.ProductionTimer[b].Raw);                       // head timer untouched
        }

        // ── Cancel the HEAD → full refund, progress discarded, next promoted with full timer ─────────

        [Fact]
        public void CancelHead_RefundsFullCost_DiscardsProgress_PromotesNextWithFullTimer()
        {
            var (sys, buildings, resources, world) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // head:  melee_a (50 ore, 5s)
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 2)); // slot1: melee_b (60 ore, 7s)

            // Advance the head timer partway (progress that a head-cancel must DISCARD).
            sys.Tick(world, Fixed.FromFloat(2f));
            Assert.True(buildings.ProductionTimer[b] < Fixed.FromFloat(5f));
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            // Cancel the head (slot 0) → refund melee_a's 50 ore; melee_b promoted, its timer starts at FULL 7s.
            Assert.True(sys.CancelTrainCommand(b, Faction.Player1, 0));
            Assert.Equal((oreBefore + Fixed.FromInt(50)).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(1, buildings.QueueLength(b));
            Assert.Equal((byte)(2 + 1), buildings.ProductionQueue[buildings.HeadIndex(b)]); // melee_b promoted
            Assert.Equal(Fixed.FromFloat(7f).Raw, buildings.ProductionTimer[b].Raw);         // full, progress discarded
        }

        [Fact]
        public void CancelSoleHead_GoesIdle_TimerZero()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            Assert.True(sys.CancelTrainCommand(b, Faction.Player1, 0));
            Assert.Equal((oreBefore + Fixed.FromInt(50)).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.QueueLength(b));
            Assert.Equal(Fixed.Zero.Raw, buildings.ProductionTimer[b].Raw);
        }

        // ── Supply is NEVER refunded (only ore/crystal) ─────────────────────────────────────────────

        [Fact]
        public void Cancel_RefundsOreButNeverSupply()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            // A NON-ZERO supply baseline (simulating live-unit supply). Without this the assert is vacuous: SupplyUsed
            // is recomputed from live units, so with zero units it is 0 before AND after regardless of what cancel does.
            resources.SupplyUsed[(int)Faction.Player1] = 3;
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // spends 50 ore
            Assert.True(sys.CancelTrainCommand(b, Faction.Player1, 0));   // refunds 50 ore, must touch ONLY ore

            // The cancel actually ran a refund (ore fully restored) — so the supply assert below is exercising a live
            // refund path, not a no-op — and it credited ONLY ore: the non-zero supply baseline is untouched.
            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(3, resources.SupplyUsed[(int)Faction.Player1]);
        }

        // ── No-op cases: enemy building, out-of-range slot, empty slot, null store ───────────────────

        [Fact]
        public void CancelEnemyBuilding_IsSilentNoOp_NoRefund()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            // Player2 cancels a Player1 building → anti-cheat silent no-op: nothing removed, nothing refunded.
            Assert.False(sys.CancelTrainCommand(b, Faction.Player2, 0));
            Assert.Equal(1, buildings.QueueLength(b));
            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void CancelOutOfRangeOrEmptySlot_IsNoOp_NoRefund()
        {
            var (sys, buildings, resources, _) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // only slot 0 filled
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            Assert.False(sys.CancelTrainCommand(b, Faction.Player1, 3));  // empty waiting slot → no-op
            Assert.False(sys.CancelTrainCommand(b, Faction.Player1, 99)); // out-of-range slot → no-op
            Assert.False(sys.CancelTrainCommand(b, Faction.Player1, -1)); // negative slot → no-op
            Assert.Equal(1, buildings.QueueLength(b));
            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void CancelTrain_NullBuildings_IsDeterministicNoOp()
        {
            // The headless / golden / replay-without-buildings path passes no BuildingSystem → CancelTrain must no-op
            // without throwing (goldens never cancel via the wire).
            var world = new EntityWorld();
            var ex = Record.Exception(() =>
                OrderApplier.Apply(world, new UnitOrder(0, UnitCommand.CancelTrain, Fixed.FromRaw(0), Fixed.Zero),
                                   Faction.Player1)); // buildings defaults to null
            Assert.Null(ex);
        }

        // ── Wire parity: CancelTrain through OrderApplier matches the direct call ────────────────────

        [Fact]
        public void OrderApplier_CancelTrain_RefundsIdenticallyToDirectCall()
        {
            // Route A — direct CancelTrainCommand.
            var a = Harness();
            int ba = Barracks(a.sys);
            Assert.True(a.sys.TrainUnit(ba, a.resources, chosenUnitIndex: 2));
            Fixed aOre = a.resources.Ore[(int)Faction.Player1];
            Assert.True(a.sys.CancelTrainCommand(ba, Faction.Player1, 0));

            // Route B — the SAME cancel issued as a UnitCommand.CancelTrain through the shared OrderApplier (slot in TargetX).
            var c = Harness();
            int bc = Barracks(c.sys);
            Assert.True(c.sys.TrainUnit(bc, c.resources, chosenUnitIndex: 2));
            Fixed cOre = c.resources.Ore[(int)Faction.Player1];
            OrderApplier.Apply(c.world, new UnitOrder(bc, UnitCommand.CancelTrain, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, buildings: c.sys);

            // Identical refund (both credit melee_b's 60 ore) and both queues emptied.
            Assert.Equal(a.resources.Ore[(int)Faction.Player1].Raw - aOre.Raw,
                         c.resources.Ore[(int)Faction.Player1].Raw - cOre.Raw);
            Assert.Equal(a.buildings.QueueLength(ba), c.buildings.QueueLength(bc));
        }

        [Fact]
        public void OrderApplier_CancelTrain_WrongFaction_RejectedByOwnershipGuard()
        {
            var (sys, buildings, resources, world) = Harness();
            int b = Barracks(sys);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            // Player2 tries to cancel a Player1 building's order → rejected (anti-cheat), no refund, nothing removed.
            OrderApplier.Apply(world, new UnitOrder(b, UnitCommand.CancelTrain, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player2, buildings: sys);

            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(1, buildings.QueueLength(b));
        }
    }
}
