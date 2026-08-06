#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Economy;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-803 — DW-532's walk-stall probe read a ZERO-LENGTH step as the grid's HARD STOP.
    ///
    /// <para><b>The false inference.</b> <c>GatheringSystem.TickWalkStall</c> builds
    /// <c>desired = pos + dir.Normalized() * EffectiveMoveSpeed * dt</c> and treated
    /// <c>CheckedStep.Resolve(grid, pos, desired) == pos</c> as proof of the helper's hard stop ("the full step and both
    /// wall-slide axes were all rejected"). That reading only holds for a step with LENGTH. When the computed step is
    /// zero the probe is <c>desired == pos</c>: <c>PathabilityGrid.IsBlockedOnSegmentOutside</c> over a degenerate
    /// segment has <c>col == colEnd</c> and <c>row == rowEnd</c>, so its walk loop never runs and it returns false, and
    /// <c>Resolve</c> returns <c>desired</c> from its FIRST not-blocked branch — never reaching the hard-stop return.
    /// The probe could not distinguish "every sweep was rejected" from "there was nothing to sweep".</para>
    ///
    /// <para><b>Why it is reachable.</b> <c>ModifierSystem.RecomputeEntity</c> sets
    /// <c>EffectiveMoveSpeed = Fixed.Max(Fixed.Zero, AddSaturating(base, flatBonus))</c> — it FLOORS at zero — while
    /// <c>ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA</c> is 50 and every shipped unit's speed is ≤ 6.5, so a single
    /// gate-passing snare item or granted modifier lands the value on exactly zero. The only other gate is
    /// <c>grid.AnyBlocked</c>, i.e. any map with one painted, prop, water or slope-derived cell ANYWHERE. A worker
    /// standing on clear ground therefore latched <c>SLOT_YIELDED</c> purely because it was snared, driving the
    /// SimChecksum-folded <c>ResourceNodeStore.AssignedGatherers</c> from 1 to 0 and letting a rival faction claim the
    /// node it was still targeting — DW-207's starvation class re-entered from the opposite side.</para>
    ///
    /// <para><b>The fix under test.</b> The guard is on the COMPUTED STEP (<c>desired == pos</c>), not on
    /// <c>speed == 0</c>: the same degenerate reading fires for any speed small enough that the normalized direction
    /// times <c>speed * dt</c> truncates to <see cref="Fixed"/> zero. A zero-length step is NO EVIDENCE either way, so
    /// the probe is SKIPPED — the streak neither advances nor resets, and a genuinely confined worker that is snared
    /// mid-stall resumes its window exactly where it left off.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only, isolated stores, with a REAL <see cref="MovementSystem"/> in
    /// <c>SimulationHost</c>'s order (gathering, then movement) — the same harness shape as
    /// <c>GatheringWorkerConfinementTests</c>, because the defect lives in the gap between what the probe predicts and
    /// what the integrator actually does.</para>
    /// </summary>
    public class GatheringWalkStallZeroStepTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt; // one real sim tick (1/30s)
        private const int GS = PathabilityGrid.GRID_SIZE;

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private sealed class Harness
        {
            public EntityWorld       World = null!;
            public ResourceNodeStore Nodes = null!;
            public GatheringSystem   Gather = null!;
            public MovementSystem    Move = null!;

            /// <summary>One full sim step in <c>SimulationHost</c>'s order: GatheringSystem then MovementSystem.</summary>
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
                World  = world,
                Nodes  = nodes,
                Gather = new GatheringSystem(nodes, resources, buildings, null, world),
                Move   = new MovementSystem(),
            };
        }

        private static int SpawnWorker(EntityWorld world, Faction faction, FixedVec3 pos, int speed = 3)
        {
            int id = world.Create(pos, faction, Fixed.FromInt(50), Fixed.FromInt(speed));
            world.GatherState[id]   = GatherState.Idle;
            world.CarryCapacity[id] = Fixed.FromInt(20);
            return id;
        }

        /// <summary>
        /// ONE blocked cell in the far (row 0, col 0) corner — world XZ around (−128, −128). Enough to make
        /// <c>PathabilityGrid.AnyBlocked</c> true (the only gate on the whole DW-532 path) while leaving every cell the
        /// workers in these tests occupy or traverse completely CLEAR. That combination is the point: the defect fired
        /// on units nowhere near a wall, on any map with a single painted cell.
        /// </summary>
        private static PathabilityGrid OneBlockedCellFarAway()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            mask[0] = true; // row 0, col 0
            return new PathabilityGrid(mask);
        }

        /// <summary>The DW-532 fixture: a full-height blocked BAND spanning columns 60..70 (world X ∈ [−8, 14)), eleven
        /// cells wide, so a unit on an interior column is genuinely confined — every neighbour it could step to is a
        /// DIFFERENT blocked cell and <c>CheckedStep.Resolve</c> hard-stops it for real.</summary>
        private static PathabilityGrid BandGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
                for (int col = 60; col <= 70; col++)
                    mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        /// <summary>Several full grace windows — long enough that a failure means "yields", not "not yet".</summary>
        private const int SeveralWindows = 5 * GatheringSystem.WALK_STALL_GRACE_TICKS;

        // ── The headline: a zero-length step is not a hard stop ───────────────────────────────────────────────

        [Fact]
        public void ZeroSpeedWorker_OnClearGround_KeepsItsGatherSlot()
        {
            // The recorded closure, pinned: a worker snared to EffectiveMoveSpeed 0 on completely clear ground is not
            // stalled by the GRID — it is stalled by a debuff, which is not what DW-532's yield is for. Pre-fix the
            // probe's `desired == pos` fell through the first not-blocked branch of CheckedStep.Resolve and the streak
            // ran to SLOT_YIELDED, silently surrendering the folded reservation.
            var h = NewHarness();
            h.World.SetPathabilityGrid(OneBlockedCellFarAway());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int snared = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            h.Step(); // Idle -> MovingToResource: the slot is reserved BEFORE arrival
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[snared]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            // The snare lands (ModifierSystem.RecomputeEntity floors the effective stat at zero; BaseMoveSpeed is
            // untouched, exactly as a slow item leaves it).
            h.World.EffectiveMoveSpeed[snared] = Fixed.Zero;
            FixedVec3 frozen = h.World.Position[snared];

            h.Step(SeveralWindows);

            // Fixture assumption: it genuinely could not move (so the ONLY thing under test is the probe's reading).
            Assert.Equal(frozen, h.World.Position[snared]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[snared]);
            Assert.Equal(node, h.World.GatherTarget[snared]);

            Assert.NotEqual(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[snared]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void ZeroSpeedWorker_DoesNotEvenAccumulateAStallStreak()
        {
            // Sharper than the headline: the probe is SKIPPED, not merely prevented from latching. A streak that kept
            // creeping would latch the instant the snare expired — one tick of real walking later — which is the same
            // defect with a delay.
            var h = NewHarness();
            h.World.SetPathabilityGrid(OneBlockedCellFarAway());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int snared = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            h.Step();
            h.World.EffectiveMoveSpeed[snared] = Fixed.Zero;
            h.Step(SeveralWindows);

            Assert.Equal(0, h.World.GatherWalkStallTicks[snared]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void SubTickSpeed_WhoseStepTruncatesToFixedZero_IsAlsoNotAHardStop()
        {
            // WHY THE GUARD IS ON THE COMPUTED STEP, NOT ON `speed == 0`. With EffectiveMoveSpeed at one raw unit
            // (1/65536 u/s) the direction × speed × dt product truncates to Fixed zero, so `desired == pos` for a
            // NON-zero speed — a `speed == Fixed.Zero` test would sail straight past this and yield the slot anyway.
            var h = NewHarness();
            h.World.SetPathabilityGrid(OneBlockedCellFarAway());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int crawling = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            h.Step();
            h.World.EffectiveMoveSpeed[crawling] = Fixed.FromRaw(1); // non-zero, but speed * dt is zero
            Assert.NotEqual(Fixed.Zero, h.World.EffectiveMoveSpeed[crawling]);
            FixedVec3 frozen = h.World.Position[crawling];

            h.Step(SeveralWindows);

            Assert.Equal(frozen, h.World.Position[crawling]); // fixture: the integrator cannot move it either
            Assert.NotEqual(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[crawling]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void SnaredWorker_KeepsTheNodeAwayFromARivalFaction()
        {
            // The downstream consequence the ledger names: AssignedGatherers is SimChecksum-folded and is a real
            // capacity reservation, so surrendering it hands a 1-cap node to whoever asks next — while the snared
            // worker is still targeting it and will walk right back the moment the slow expires.
            var h = NewHarness();
            h.World.SetPathabilityGrid(OneBlockedCellFarAway());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int snared = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            h.Step();
            h.World.EffectiveMoveSpeed[snared] = Fixed.Zero;
            h.Step(SeveralWindows);

            int rival = SpawnWorker(h.World, Faction.Player2, V(25, 0));
            h.Step();

            Assert.Equal(-1, h.World.GatherTarget[rival]);                        // FindBestNode sees it saturated
            Assert.Equal(GatherState.Idle, h.World.GatherState[rival]);
            Assert.Equal(node, h.World.GatherTarget[snared]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        // ── The guard must not disarm DW-532 ──────────────────────────────────────────────────────────────────

        [Fact]
        public void ConfinedWorker_AtNormalSpeed_StillYieldsTheSlot()
        {
            // The negative control. A step with real LENGTH that the grid rejects on the full move and BOTH wall-slide
            // axes is the hard stop DW-532 was written for, and it must still fire — a guard that swallowed it would
            // trade one leak for the original one.
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0)); // interior of the band (column 61)

            h.Step();
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            h.Step(8 * GatheringSystem.WALK_STALL_GRACE_TICKS);

            Assert.Equal(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void SnaringAConfinedWorker_PAUSESItsStreak_ItNeitherAdvancesNorResets()
        {
            // The semantics of "no evidence": a zero-length step tells you NOTHING about the grid, so it must not
            // count toward the window (that is the defect) and must not clear it either — a repeating snare on a
            // genuinely confined worker would then reset the streak forever and DW-532 could never fire.
            var h = NewHarness();
            h.World.SetPathabilityGrid(BandGrid());
            int node = h.Nodes.Create(V(30, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int stuck = SpawnWorker(h.World, Faction.Player1, V(-5, 0));

            h.Step();
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            // Walk it to the cell boundary and let a real streak of 3 accumulate.
            int guard = 0;
            while (h.World.GatherWalkStallTicks[stuck] < 3 && guard++ < 300) h.Step();
            Assert.True(guard < 300, "fixture assumption broken: the confined worker never stalled at all.");
            Assert.Equal(3, h.World.GatherWalkStallTicks[stuck]);

            // Now snare it. Many windows pass with the streak FROZEN at 3.
            h.World.EffectiveMoveSpeed[stuck] = Fixed.Zero;
            h.Step(SeveralWindows);
            Assert.Equal(3, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            // Snare expires: the worker is still confined, so the window resumes from 3 and closes normally.
            h.World.EffectiveMoveSpeed[stuck] = Fixed.FromInt(3);
            h.Step(GatheringSystem.WALK_STALL_GRACE_TICKS);

            Assert.Equal(GatheringSystem.SLOT_YIELDED, h.World.GatherWalkStallTicks[stuck]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
        }

        // ── The zero-length reading itself, at the helper ─────────────────────────────────────────────────────

        [Fact]
        public void CheckedStep_ReturnsTheOriginForAZeroLengthStep_WhichIsWhyCallersMustNotReadItAsBlocked()
        {
            // Documents the shared helper's real contract so the next caller doesn't repeat the inference: Resolve's
            // "no move" answer and its HARD STOP answer are the SAME value. A degenerate segment reaches the FIRST
            // not-blocked branch even when the origin sits inside a wall.
            var wall = BandGrid();
            FixedVec3 insideTheWall = V(-5, 0);

            Assert.Equal(insideTheWall, CheckedStep.Resolve(wall, insideTheWall, insideTheWall));

            FixedVec3 onClearGround = V(30, 0);
            Assert.Equal(onClearGround, CheckedStep.Resolve(wall, onClearGround, onClearGround));
        }
    }
}
