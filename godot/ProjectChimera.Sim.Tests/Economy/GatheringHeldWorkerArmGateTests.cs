#nullable enable
using ProjectChimera.Core;        // EntityWorld, Fixed, FixedVec3, Faction, ResourceStore, SimulationLoop, GatherState
using ProjectChimera.Economy;     // GatheringSystem, ResourceNodeStore
using ProjectChimera.Effects;     // StatusFlags
using ProjectChimera.Navigation;  // MovementSystem, PathabilityGrid
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-834 — DW-619's stun/root gate covered ONE of <see cref="GatheringSystem.Tick"/>'s FOUR arms, so two of the
    /// invariants it claimed were false as written. The gate now sits above the dispatch switch; these tests are the
    /// behavioural oracle for the three arms it was missing.
    ///
    /// <list type="bullet">
    ///   <item><b>MovingToBase.</b> The delivery arm's arrival test is purely POSITIONAL (3.0 world units), so a worker
    ///         stunned or rooted while already standing inside the drop-off radius needs no movement to finish the leg —
    ///         it banked its whole carry on the very next tick. "A held worker PRODUCES NOTHING" and "a Streaming node is
    ///         the only way a held worker could still feed its faction" were both false because of this arm.</item>
    ///   <item><b>Idle.</b> No movement is required to CLAIM a node either: a held worker beside a free node ran
    ///         <c>FindBestNode</c> + <c>AssignToNode</c> and consumed one of its SimChecksum-folded
    ///         <c>AssignedGatherers</c> slots while anchored, taking capacity from a worker that could use it.</item>
    ///   <item><b>MovingToResource.</b> DW-532's walk-stall probe kept running against a worker <c>MovementSystem</c> is
    ///         deliberately NOT integrating (its own DW-266 status anchor), i.e. it counted a stall the GRID never
    ///         caused, and surrendered the reservation about a second later. (DW-805 lists that anchor as one of the
    ///         things the probe does not model.)</item>
    /// </list>
    ///
    /// <para>Every case is PAUSE-not-cancel: the held worker keeps its state, its target, its carry and its reservation,
    /// and resumes exactly where it stood the tick the status clears. The final test pins the gate as an EXACT no-op for
    /// every flag OUTSIDE the mask, which is what keeps every recorded golden still (they all leave
    /// <c>StatusFlagsOf</c> at None for every entity).</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only, ascending id, isolated stores — but with a REAL
    /// <see cref="MovementSystem"/> in <c>SimulationHost</c>'s order (gathering, then movement) wherever the case depends
    /// on what the integrator does.</para>
    /// </summary>
    public class GatheringHeldWorkerArmGateTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt; // one real sim tick (1/30s)
        private const int GS = PathabilityGrid.GRID_SIZE;

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private sealed class Harness
        {
            public EntityWorld       World     = null!;
            public ResourceNodeStore Nodes     = null!;
            public ResourceStore     Resources = null!;
            public BuildingStore     Buildings = null!;
            public GatheringSystem   Gather    = null!;
            public MovementSystem    Move      = null!;

            /// <summary>One full sim step in <c>SimulationHost</c>'s order: GatheringSystem [2] then MovementSystem [3].</summary>
            public void Step(int ticks = 1)
            {
                for (int i = 0; i < ticks; i++)
                {
                    Gather.Tick(World, Dt);
                    Move.Tick(World, Dt);
                }
            }
        }

        private static Harness NewHarness()
        {
            var world     = new EntityWorld();
            var nodes     = new ResourceNodeStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            return new Harness
            {
                World = world, Nodes = nodes, Resources = resources, Buildings = buildings,
                Gather = new GatheringSystem(nodes, resources, buildings, null, world),
                Move   = new MovementSystem(),
            };
        }

        private static int SpawnWorker(EntityWorld world, Faction faction, FixedVec3 pos)
        {
            int id = world.Create(pos, faction, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[id]   = GatherState.Idle;
            world.CarryCapacity[id] = Fixed.FromInt(20);
            return id;
        }

        /// <summary>A worker mid-DELIVERY: carrying a full load, already inside its faction's drop-off radius.</summary>
        private static int SpawnCarrierAtBase(EntityWorld world, Faction faction, FixedVec3 pos, Fixed carry)
        {
            int id = SpawnWorker(world, faction, pos);
            world.GatherState[id]        = GatherState.MovingToBase;
            world.CarryAmount[id]        = carry;
            world.CarryResourceType[id]  = ResourceKind.Ore;
            return id;
        }

        /// <summary>The shared full-height blocked BAND spanning flow columns 60..70 — world X ∈ [-8, 14). Eleven cells
        /// wide, so a unit on an interior column is confined: every neighbour it could step to is a DIFFERENT blocked
        /// cell. (Same fixture as <c>GatheringWorkerConfinementTests</c>.)</summary>
        private static PathabilityGrid BandGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
                for (int col = 60; col <= 70; col++)
                    mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        // ── (1) The delivery arm: a held worker inside the drop-off radius must bank NOTHING ─────────────────

        [Theory]
        [InlineData((byte)StatusFlags.Stunned)]
        [InlineData((byte)StatusFlags.Rooted)]
        public void HeldWorker_StandingInsideTheDropOffRadius_BanksNothing_WhileAnUnheldTwinBanksItsWholeLoad(byte statusRaw)
        {
            var h = NewHarness();
            // Two factions so each side's balance is separately observable; each worker starts 2.0 from its OWN base,
            // i.e. already inside ARRIVE_AT_BASE_SQR (3.0) — no movement is needed to finish the leg, which is the
            // whole point: the DW-266 anchor cannot stop a leg that requires no steps.
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            h.Resources.FactionBase[(int)Faction.Player2] = V(50, 0);

            int held = SpawnCarrierAtBase(h.World, Faction.Player1, V(2, 0),  Fixed.FromInt(10));
            int twin = SpawnCarrierAtBase(h.World, Faction.Player2, V(52, 0), Fixed.FromInt(10));
            h.World.StatusFlagsOf[held] = (StatusFlags)statusRaw;

            h.Step(30);

            // Exact Raw equality — "banked nothing", not "banked less".
            Assert.Equal(Fixed.Zero.Raw, h.Resources.Ore[(int)Faction.Player1].Raw);
            // The identical twin proves the scenario really does deposit for a healthy worker.
            Assert.Equal(Fixed.FromInt(10).Raw, h.Resources.Ore[(int)Faction.Player2].Raw);

            // PAUSE, NOT CANCEL — the leg, the load and the carried kind all survived the hold…
            Assert.Equal(GatherState.MovingToBase, h.World.GatherState[held]);
            Assert.Equal(Fixed.FromInt(10).Raw, h.World.CarryAmount[held].Raw);
            Assert.Equal(ResourceKind.Ore, h.World.CarryResourceType[held]);

            // …so the very next tick after it clears completes the delivery it was standing in.
            h.World.StatusFlagsOf[held] = StatusFlags.None;
            h.Step();
            Assert.Equal(Fixed.FromInt(10).Raw, h.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.Zero.Raw, h.World.CarryAmount[held].Raw);
            Assert.Equal(GatherState.Idle, h.World.GatherState[held]);
        }

        // ── (2) The idle arm: a held worker must take NO node reservation ────────────────────────────────────

        [Theory]
        [InlineData((byte)StatusFlags.Stunned)]
        [InlineData((byte)StatusFlags.Rooted)]
        public void HeldIdleWorker_TakesNoNodeReservation_SoAFreeWorkerCanStillClaimTheSlot(byte statusRaw)
        {
            var h = NewHarness();
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            h.Resources.FactionBase[(int)Faction.Player2] = V(0, 0);

            // maxGatherers: 1 makes the theft observable — one reservation is the whole node.
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int held = SpawnWorker(h.World, Faction.Player1, V(2, 0)); // standing ON the node it would claim
            h.World.StatusFlagsOf[held] = (StatusFlags)statusRaw;

            h.Step(30);

            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
            Assert.Equal(GatherState.Idle, h.World.GatherState[held]);
            Assert.Equal(-1, h.World.GatherTarget[held]);

            // The capacity is genuinely LEFT USABLE, not merely a corrected number: a free worker takes it.
            int free = SpawnWorker(h.World, Faction.Player2, V(2, 0));
            h.Step();
            Assert.Equal(node, h.World.GatherTarget[free]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            // And releasing the hold does not retroactively hand the slot back to the worker that was anchored —
            // it simply re-enters the normal race and finds the node saturated (no double-claim).
            h.World.StatusFlagsOf[held] = StatusFlags.None;
            h.Step();
            Assert.Equal(-1, h.World.GatherTarget[held]);
            Assert.Equal(GatherState.Idle, h.World.GatherState[held]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        // ── (3) The walk arm: a held worker must accrue no DW-532 stall streak ───────────────────────────────

        [Theory]
        [InlineData((byte)StatusFlags.Stunned)]
        [InlineData((byte)StatusFlags.Rooted)]
        public void HeldWorkerEnRoute_FreezesTheWalkStallStreak_AndKeepsItsReservation_ThenResumesWhenTheHoldEnds(byte statusRaw)
        {
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node  = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0)); // interior of the band (column 61)

            h.Step(); // Idle -> MovingToResource: the slot is reserved BEFORE arrival
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[stuck]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            // Let it walk up to its cell boundary so a REAL streak is running — otherwise this would prove nothing
            // (inside its own cell the probe correctly reads "making ground").
            int guard = 0;
            while (h.World.GatherWalkStallTicks[stuck] < 3 && guard++ < 300) h.Step();
            Assert.Equal(3, h.World.GatherWalkStallTicks[stuck]);

            // HOLD IT. MovementSystem's DW-266 anchor now skips this worker entirely, so the position it is pinned at
            // says nothing about the grid — the streak must FREEZE, not advance, and the reservation must survive
            // arbitrarily many grace windows.
            h.World.StatusFlagsOf[stuck] = (StatusFlags)statusRaw;
            h.Step(6 * GatheringSystem.WALK_STALL_GRACE_TICKS);
            Assert.Equal(3, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[stuck]);

            // NEGATIVE CONTROL — DW-532 is not disarmed: released, the streak resumes from 3 and closes normally.
            h.World.StatusFlagsOf[stuck] = StatusFlags.None;
            h.Step(GatheringSystem.WALK_STALL_GRACE_TICKS);
            Assert.Equal(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
        }

        // ── (4) The no-op half: outside the mask NOTHING may change ──────────────────────────────────────────

        [Fact]
        public void NonBlockingStatuses_LeaveTheWholeFourArmCycleBitIdentical()
        {
            // The gate is now read for EVERY arm, so its no-op-outside-the-mask property has to be re-proved over the
            // whole cycle (Idle → MovingToResource → Gathering → MovingToBase → deposit), not just the mining tick.
            // This is the property that keeps every recorded golden still.
            static (int assigned, int carryRaw, int supplyRaw, int oreRaw, GatherState state) Run(StatusFlags s)
            {
                var h = NewHarness();
                h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
                int node   = h.Nodes.Create(V(6, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 2);
                int worker = SpawnWorker(h.World, Faction.Player1, V(0, 0));
                h.World.CarryCapacity[worker] = Fixed.FromInt(2); // small, so several full round trips complete
                h.World.StatusFlagsOf[worker] = s;
                h.Step(400);
                return (h.Nodes.AssignedGatherers[node], h.World.CarryAmount[worker].Raw,
                        h.Nodes.SupplyRemaining[node].Raw, h.Resources.Ore[(int)Faction.Player1].Raw,
                        h.World.GatherState[worker]);
            }

            var baseline = Run(StatusFlags.None);
            var buffed   = Run(StatusFlags.Invulnerable | StatusFlags.Silenced | StatusFlags.Disarmed);

            Assert.Equal(baseline.assigned,  buffed.assigned);
            Assert.Equal(baseline.carryRaw,  buffed.carryRaw);
            Assert.Equal(baseline.supplyRaw, buffed.supplyRaw);
            Assert.Equal(baseline.oreRaw,    buffed.oreRaw);
            Assert.Equal(baseline.state,     buffed.state);
            Assert.True(baseline.oreRaw > 0, "the control run must actually complete a delivery, or this proves nothing");
        }
    }
}
