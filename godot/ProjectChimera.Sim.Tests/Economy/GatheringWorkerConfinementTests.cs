#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Economy;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-532 — the FIFTH way a worker stops occupying a resource node, which none of DW-207's four release paths can
    /// reach: it is CONFINED by blocked cells and can never arrive.
    ///
    /// <para><b>The interaction.</b> <c>ScenarioApplier</c> deliberately only WARNS about an authored spawn inside a
    /// blocked cell and places the unit anyway ("silently refusing to spawn authored content would be strictly worse"),
    /// while DW-148 made <c>MovementSystem</c> CONFINE such a unit — it may shuffle inside its own cell and walk out
    /// into a CLEAR neighbour, but may not traverse onward. On an interior cell of a blocked region at least three
    /// cells across, the full step and BOTH wall-slide axes therefore resolve to foreign blocked cells and
    /// <c>CheckedStep.Resolve</c> hard-stops the worker in place every tick, forever. <c>TickIdle</c> → <c>AssignToNode</c>
    /// has already incremented the node's SimChecksum-folded <c>AssignedGatherers</c>, and <c>TickMovingToResource</c>
    /// released only on "node gone" — so the stranded worker held a real capacity reservation for the whole match and
    /// <c>FindBestNode</c> skipped that node as saturated for EVERY faction. That is exactly the starvation DW-207 was
    /// written to eliminate, re-opened by a bundle that could not see it.</para>
    ///
    /// <para><b>The fix under test.</b> <see cref="GatheringSystem.WALK_STALL_GRACE_TICKS"/> consecutive ticks of "cannot
    /// advance toward the node AT ALL" make the worker hand the slot back (<see cref="GatheringSystem.SLOT_YIELDED"/>)
    /// while STAYING en route — it re-claims only if it ever actually arrives. Re-idling instead would churn: nothing
    /// makes <c>FindBestNode</c> reachability-aware, so the next <c>TickIdle</c> would re-pick the same node and
    /// re-reserve the slot for all but one tick of every window.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only, isolated stores — but with a REAL <see cref="MovementSystem"/> in the
    /// loop (in <c>SimulationHost</c>'s order: gathering, then movement), because the whole defect lives in what the
    /// movement integrator does to a confined unit.</para>
    /// </summary>
    public class GatheringWorkerConfinementTests
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

        /// <summary>A full-height blocked BAND spanning flow columns 60..70 — world X ∈ [-8, 14). Eleven cells wide, so
        /// a unit on an interior column is confined: every neighbour it could step to is a DIFFERENT blocked cell.
        /// (The shared fixture <c>MovementSystemInBlockedConfinementTests</c> uses.)</summary>
        private static PathabilityGrid BandGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
                for (int col = 60; col <= 70; col++)
                    mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        /// <summary>Long enough for a 3 u/s worker to walk the ~1 world unit to its cell boundary (~10 ticks) AND then
        /// exhaust the full grace window several times over — so a failure means "never yields", not "not yet".</summary>
        private const int LongEnoughToStrand = 8 * GatheringSystem.WALK_STALL_GRACE_TICKS;

        // ── The headline: a stranded worker must stop consuming the node's capacity ──────────────────────────

        [Fact]
        public void ConfinedWorker_EnRouteToANode_YieldsTheGatherSlot_SoTheNodeIsNotStarvedForever()
        {
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            // maxGatherers: 1 makes the leak observable exactly as DW-207's tests do — one stranded worker is enough to
            // make the node permanently un-gatherable for EVERY faction.
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0)); // interior of the band (column 61)

            h.Step(); // Idle -> MovingToResource: the slot is reserved BEFORE arrival
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[stuck]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            h.Step(LongEnoughToStrand);

            // It genuinely never got out (the DW-148 confinement is untouched by this fix)…
            Assert.True(h.World.Position[stuck].X < Fixed.FromInt(-4),
                $"fixture assumption broken: the worker escaped the band (X={h.World.Position[stuck].X.ToFloat()}).");
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[stuck]); // still trying — NOT re-idled
            Assert.Equal(node, h.World.GatherTarget[stuck]);                        // …and it kept its target

            // …but pre-DW-532 this stayed at 1 for the whole match.
            Assert.Equal(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            // And the capacity is genuinely usable again, not merely a corrected number: a worker outside the band
            // claims the node the counter was hoarding.
            int free = SpawnWorker(h.World, Faction.Player1, V(20, 0));
            h.Step();
            Assert.Equal(node, h.World.GatherTarget[free]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[free]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void ConfinedWorker_HoldsItsSlotForTheWholeGraceWindow_SoATransientBlockIsNotAnEviction()
        {
            // The window is not decorative: a worker must not lose its reservation the first tick it fails to advance
            // (units get pinned against corners transiently). Teeth on "consecutive", mirroring DW-80's grace pin.
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0));

            h.Step();
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            // Walk it up to (but not past) the boundary, then step exactly one tick short of the threshold.
            int guard = 0;
            while (h.World.GatherWalkStallTicks[stuck] == 0 && guard++ < 200) h.Step();
            Assert.True(guard < 200, "fixture assumption broken: the worker never stalled at all.");
            Assert.Equal(1, h.World.GatherWalkStallTicks[stuck]);

            h.Step(GatheringSystem.WALK_STALL_GRACE_TICKS - 2);
            Assert.Equal(GatheringSystem.WALK_STALL_GRACE_TICKS - 1, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]); // still held — the window has not closed

            h.Step(); // the threshold tick
            Assert.Equal(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
        }

        // ── The mirror-image defects a naive fix introduces ──────────────────────────────────────────────────

        [Fact]
        public void YieldedWorker_ThatLaterDies_DoesNotDecrementASecondTime()
        {
            // ReleaseGatherSlot fires on death for a MovingToResource worker, which normally DOES hold a reservation.
            // A stranded worker no longer does, so a target-only release would decrement twice and evict a live worker
            // from the node it never even reached — the same class of bug DW-207's MovingToBase exclusion closed.
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 2);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0));

            h.Step(1 + LongEnoughToStrand);
            Assert.Equal(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            h.Nodes.AssignedGatherers[node] = 1; // a real worker now occupies it, so a spurious decrement is VISIBLE
            h.World.Destroy(stuck);

            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void YieldedWorker_InterruptedByABuildOrder_DoesNotDecrementASecondTime()
        {
            // The other external ReleaseGatherSlot caller (BuildingSystem.QueueWorkerBuild, DW-207) — driven here
            // through the static path itself so the assertion is about the release contract, not about BuildingSystem.
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 2);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0));

            h.Step(1 + LongEnoughToStrand);
            h.Nodes.AssignedGatherers[node] = 1;

            GatheringSystem.ReleaseGatherSlot(h.World, h.Nodes, stuck);

            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
            Assert.Equal(-1, h.World.GatherTarget[stuck]);
            Assert.Equal(0, h.World.GatherWalkStallTicks[stuck]); // the release clears the sentinel too
        }

        // ── The fix must not fire on a worker that is simply walking ─────────────────────────────────────────

        [Fact]
        public void UnobstructedWorker_OnAMapWithBlockedCells_KeepsItsSlot_AndReachesTheNode()
        {
            // A blocked grid must not make every long walk a stall: the counter only advances on a tick the worker
            // could not advance AT ALL. Without this, the fix would randomly strip reservations on any painted map.
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());   // the band exists, but not between this worker and its node
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(1), maxGatherers: 1);
            int w = SpawnWorker(h.World, Faction.Player1, V(20, 0));

            h.Step();
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            // 10 world units at 3 u/s ⇒ ~100 ticks; well over three grace windows of walking.
            for (int t = 0; t < 200 && h.World.GatherState[w] == GatherState.MovingToResource; t++)
            {
                h.Step();
                Assert.Equal(0, h.World.GatherWalkStallTicks[w]);
            }

            Assert.Equal(GatherState.Gathering, h.World.GatherState[w]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void FlatMap_NeverTouchesTheStallCounter_EvenWhenTheWorkerCannotMoveAtAll()
        {
            // DETERMINISM PIN. Every recorded golden runs with a null pathability grid, so the whole DW-532 path must be
            // an exact no-op there — otherwise the SimChecksum-folded AssignedGatherers would move and every golden
            // with a gathering worker would have to be re-recorded. Movement is deliberately NOT ticked, so this worker
            // makes no ground whatsoever: a stall watch that ignored the grid gate would evict it.
            var h = NewHarness();                                    // no SetPathabilityGrid — Pathability == null
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            h.Gather.Tick(h.World, Dt);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            for (int t = 0; t < 3 * GatheringSystem.WALK_STALL_GRACE_TICKS; t++) h.Gather.Tick(h.World, Dt);

            Assert.Equal(0, h.World.GatherWalkStallTicks[w]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
        }

        [Fact]
        public void AllClearGrid_IsTreatedLikeNoGridAtAll()
        {
            // PathabilityGrid.AnyBlocked == false is the other "flat map" spelling (ScenarioApplier can inject one).
            // CheckedStep short-circuits on it, and so must the stall watch — otherwise a map with an empty painted
            // layer would behave differently from one with no layer.
            var h = NewHarness();
            h.World.SetPathabilityGrid(new PathabilityGrid(new bool[PathabilityGrid.CELL_COUNT]));
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            h.Gather.Tick(h.World, Dt); // assign, then freeze (no MovementSystem) for three full windows
            for (int t = 0; t < 3 * GatheringSystem.WALK_STALL_GRACE_TICKS; t++) h.Gather.Tick(h.World, Dt);

            Assert.Equal(0, h.World.GatherWalkStallTicks[w]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        // ── Self-healing: the yield is for the LEG, not for the worker ───────────────────────────────────────

        [Fact]
        public void YieldedWorker_ReClaimsASlotOnArrival_WhenTheBlockedRegionIsRebuiltAway()
        {
            // The grid is rebuilt from source on every scenario re-apply, so a stranded worker can be freed mid-match.
            // Because it stayed in MovingToResource it simply resumes — but it must CLAIM a slot on arrival, or it
            // would gather off the books and the node could exceed MaxGatherers.
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(1), maxGatherers: 1);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0));

            h.Step(1 + LongEnoughToStrand);
            Assert.Equal(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            h.World.SetPathabilityGrid(null); // the wall is gone
            for (int t = 0; t < 600 && h.World.GatherState[stuck] == GatherState.MovingToResource; t++) h.Step();

            Assert.Equal(GatherState.Gathering, h.World.GatherState[stuck]);
            Assert.Equal(0, h.World.GatherWalkStallTicks[stuck]); // the sentinel is consumed by the arrival
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);     // exactly one — re-claimed, not double-counted
        }

        [Fact]
        public void YieldedWorker_ArrivingAtANodeThatFilledUp_ReSeeks_InsteadOfGatheringOffTheBooks()
        {
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(1), maxGatherers: 1);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0));

            h.Step(1 + LongEnoughToStrand);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            h.Nodes.AssignedGatherers[node] = 1; // somebody else took the slot while it was stranded (max is 1)
            h.World.SetPathabilityGrid(null);
            for (int t = 0; t < 600 && h.World.GatherState[stuck] == GatherState.MovingToResource; t++) h.Step();

            Assert.Equal(GatherState.Idle, h.World.GatherState[stuck]); // re-seeks, exactly like a vanished node
            Assert.Equal(-1, h.World.GatherTarget[stuck]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);           // the occupant's slot is untouched
            Assert.Equal(0, h.World.GatherWalkStallTicks[stuck]);
        }

        // ── The SoA recycle trap ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void RecycledSlot_CarriesNoPriorWalkStallState()
        {
            // The 1.12/1.13/2.6 recycle defect class. Inheriting a corpse's SLOT_YIELDED sentinel would make the new
            // occupant's first ReleaseGatherSlot skip a decrement it genuinely owes — i.e. re-open DW-207's leak
            // through the very field added to close DW-532.
            var w = new EntityWorld();
            int first = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            w.GatherWalkStallTicks[first] = GatheringSystem.SLOT_YIELDED;
            w.Destroy(first);

            int reused = w.Create(V(1, 1), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            Assert.Equal(first, reused); // fixture assumption: the free list hands the same slot back
            Assert.Equal(0, w.GatherWalkStallTicks[reused]);
        }

        [Fact]
        public void WorldClear_ResetsTheWalkStallState_SoARePlayStartsLikeAFreshBoot()
        {
            var w = new EntityWorld();
            int id = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            w.GatherWalkStallTicks[id] = GatheringSystem.SLOT_YIELDED;

            w.Clear();

            Assert.Equal(0, w.GatherWalkStallTicks[id]);
        }
    }
}
