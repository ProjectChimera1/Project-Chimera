#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-648 — the SHARED checked-step helper.
    ///
    /// <para>Before this, the swept blocked-cell rejection (Story 6.5 / DW-147 / DW-148) existed only as four inline
    /// branches inside <c>MovementSystem.Tick</c>, so it protected exactly one writer of <c>EntityWorld.Position</c>
    /// and a second movement writer would have had to hand-copy it. <see cref="CheckedStep.Resolve"/> is that
    /// sequence as a callable contract; these tests pin the contract INDEPENDENTLY of the integrator (so a future
    /// caller — blink, knockback, snap-to-waypoint — inherits proven behaviour), and the last test pins that the
    /// production integrator really does route through it.</para>
    ///
    /// <para>Grid identity (<c>FlowField.WorldToCell</c>): 2 world units per cell over ±128, so column <c>c</c> spans
    /// world X ∈ [2c−128, 2c−126) and row <c>r</c> spans world Z ∈ [2r−128, 2r−126). Column 64 is therefore
    /// X ∈ [0, 2), column 65 is X ∈ [2, 4), row 64 is Z ∈ [0, 2), row 65 is Z ∈ [2, 4).</para>
    /// </summary>
    public class CheckedStepTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        /// <summary>A full N–S wall exactly one cell thick at <paramref name="col"/>.</summary>
        private static PathabilityGrid ColumnWall(int col)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        /// <summary>A full E–W wall exactly one cell thick at <paramref name="row"/>.</summary>
        private static PathabilityGrid RowWall(int row)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int col = 0; col < GS; col++) mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        /// <summary>Both a column wall and a row wall — the inside corner that leaves NEITHER slide axis open.</summary>
        private static PathabilityGrid CornerWall(int col, int row)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int r = 0; r < GS; r++) mask[r * GS + col] = true;
            for (int c = 0; c < GS; c++) mask[row * GS + c] = true;
            return new PathabilityGrid(mask);
        }

        private static void AssertPos(FixedVec3 expected, FixedVec3 actual, string what)
        {
            Assert.True(expected.X.Raw == actual.X.Raw && expected.Y.Raw == actual.Y.Raw && expected.Z.Raw == actual.Z.Raw,
                $"{what}: expected ({expected.X.ToFloat()}, {expected.Y.ToFloat()}, {expected.Z.ToFloat()}) " +
                $"but got ({actual.X.ToFloat()}, {actual.Y.ToFloat()}, {actual.Z.ToFloat()}).");
        }

        // ── the flat / legacy no-op ──────────────────────────────────────────────

        [Fact]
        public void NullGrid_ReturnsTheDesiredPosition_Exactly()
        {
            // The whole flat-map fence: with no pathability layer the helper must be an identity function, Y included,
            // or every legacy map's per-tick checksum would move the moment a writer routed through it.
            FixedVec3 from = V(-1, 7, 3), desired = V(40, 9, -12);
            AssertPos(desired, CheckedStep.Resolve(null, from, desired), "null grid");
        }

        [Fact]
        public void AllClearGrid_ReturnsTheDesiredPosition_Exactly()
        {
            FixedVec3 from = V(-1, 7, 3), desired = V(40, 9, -12);
            AssertPos(desired, CheckedStep.Resolve(PathabilityGrid.Empty, from, desired), "all-clear grid");
            Assert.False(PathabilityGrid.Empty.AnyBlocked);
        }

        [Fact]
        public void ClearSegment_IsAccepted_EvenWhenTheGridHasWallsElsewhere()
        {
            // The rejection must be about the SEGMENT, not about the map merely having walls: a step that never
            // touches the wall keeps its exact desired position.
            PathabilityGrid wall = ColumnWall(64);           // X ∈ [0, 2)
            FixedVec3 from = V(-20, 0, 0), desired = V(-10, 3, 6);
            AssertPos(desired, CheckedStep.Resolve(wall, from, desired), "clear segment west of the wall");
        }

        // ── the swept rejection (DW-147) ─────────────────────────────────────────

        [Fact]
        public void StepWhoseEndpointsAreBothClear_ButWhichCrossesAThinWall_IsRejected()
        {
            // THE DW-147 case, now pinned at the helper: −1 (col 63, clear) → +4 (col 66, clear) sweeps straight
            // through the one-cell wall at col 64. The negative control below proves it is the SWEEP doing the work.
            PathabilityGrid wall = ColumnWall(64);
            FixedVec3 from = V(-1, 0, 0), desired = V(4, 0, 0);

            FixedVec3 got = CheckedStep.Resolve(wall, from, desired);
            Assert.True(got.X.Raw == from.X.Raw,
                $"the step tunnelled the wall (X={got.X.ToFloat()}, expected to stay at {from.X.ToFloat()}).");
            Assert.False(wall.IsBlocked(got.X, got.Z), "the resolved position landed inside a blocked cell.");
        }

        [Fact]
        public void NegativeControl_TheEndpointOnlyPredicate_WouldHaveAcceptedTheTunnellingStep()
        {
            // Guards against a vacuous version of the test above: if the destination cell were itself blocked, the
            // step would be rejected by the OLD endpoint check too and the sweep would be proving nothing.
            PathabilityGrid wall = ColumnWall(64);
            FixedVec3 from = V(-1, 0, 0), desired = V(4, 0, 0);
            int fromCell = PathabilityGrid.CellOf(from.X, from.Z);

            Assert.False(wall.IsBlockedOutside(fromCell, desired.X, desired.Z),
                "the destination cell is blocked, so this fixture does not actually exercise the swept check.");
            Assert.True(wall.IsBlockedOnSegmentOutside(fromCell, from.X, from.Z, desired.X, desired.Z),
                "the swept check failed to see the wall between the two clear endpoints.");
        }

        [Fact]
        public void StepEndingInsideAWall_IsRejected()
        {
            PathabilityGrid wall = ColumnWall(64);
            FixedVec3 from = V(-1, 0, 0), desired = V(1, 0, 0);   // X=1 ⇒ col 64 ⇒ blocked
            FixedVec3 got = CheckedStep.Resolve(wall, from, desired);
            Assert.True(got.X.Raw == from.X.Raw, $"the step entered the wall (X={got.X.ToFloat()}).");
        }

        // ── wall-slide + hard stop ───────────────────────────────────────────────

        [Fact]
        public void WhenOnlyTheZAxisIsBlocked_TheStepSlidesAlongX_AndKeepsTheDesiredY()
        {
            // Wall across row 65 (Z ∈ [2, 4)). The diagonal (0,1) → (3,3) enters it; the X-only candidate
            // (0,1) → (3,1) stays in row 64 and is clear, so X survives and Z is dropped to the pre-step value.
            PathabilityGrid wall = RowWall(65);
            FixedVec3 from = V(0, 7, 1), desired = V(3, 9, 3);

            FixedVec3 got = CheckedStep.Resolve(wall, from, desired);
            AssertPos(new FixedVec3(desired.X, desired.Y, from.Z), got, "slide along X");
        }

        [Fact]
        public void WhenOnlyTheXAxisIsBlocked_TheStepSlidesAlongZ_AndKeepsTheDesiredY()
        {
            // Wall down column 65 (X ∈ [2, 4)). The X-only candidate crosses it, so the Z-only candidate wins.
            PathabilityGrid wall = ColumnWall(65);
            FixedVec3 from = V(0, 7, 0), desired = V(3, 9, 3);

            FixedVec3 got = CheckedStep.Resolve(wall, from, desired);
            AssertPos(new FixedVec3(from.X, desired.Y, desired.Z), got, "slide along Z");
        }

        [Fact]
        public void WhenNeitherAxisSurvives_TheStepHardStops_AtTheWholePreStepPosition()
        {
            // Inside corner: column 65 AND row 65 blocked. Full step, X-only and Z-only candidates all cross a wall,
            // so the mover keeps its ENTIRE pre-step position — Y included (it did not move at all this tick).
            PathabilityGrid corner = CornerWall(65, 65);
            FixedVec3 from = V(0, 7, 0), desired = V(3, 9, 3);

            AssertPos(from, CheckedStep.Resolve(corner, from, desired), "hard stop");
        }

        [Fact]
        public void XIsTriedBeforeZ_WhenBothSingleAxisSlidesAreOpen()
        {
            // Only a single blocked CELL (65,65): the diagonal clips it, but both single-axis candidates are clear.
            // The fixed X-first order is what makes the outcome caller-independent and replay-identical, so it is
            // pinned rather than left to chance.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            mask[65 * GS + 65] = true;                        // X ∈ [2,4) × Z ∈ [2,4)
            var spot = new PathabilityGrid(mask);

            FixedVec3 from = V(0, 0, 0), desired = V(3, 0, 3);
            Assert.True(spot.IsBlockedOnSegmentOutside(PathabilityGrid.CellOf(from.X, from.Z), from.X, from.Z, desired.X, desired.Z),
                "fixture error: the diagonal does not actually clip the blocked cell.");

            FixedVec3 got = CheckedStep.Resolve(spot, from, desired);
            AssertPos(new FixedVec3(desired.X, desired.Y, from.Z), got, "X-first tie-break");
        }

        // ── DW-148 confinement, inherited by every caller ────────────────────────

        [Fact]
        public void AMoverAlreadyInsideABlockedCell_MayShuffleWithinIt()
        {
            PathabilityGrid wall = ColumnWall(64);            // X ∈ [0, 2)
            FixedVec3 from = new FixedVec3(Fixed.Half, Fixed.Zero, Fixed.Zero);              // X=0.5 ⇒ col 64
            FixedVec3 desired = new FixedVec3(Fixed.One + Fixed.Half, Fixed.Zero, Fixed.Zero); // X=1.5 ⇒ still col 64
            AssertPos(desired, CheckedStep.Resolve(wall, from, desired), "shuffle inside the start cell");
        }

        [Fact]
        public void AMoverAlreadyInsideABlockedCell_MayWalkOutIntoAClearNeighbour()
        {
            PathabilityGrid wall = ColumnWall(64);
            FixedVec3 from = new FixedVec3(Fixed.Half, Fixed.Zero, Fixed.Zero);   // col 64 (blocked)
            FixedVec3 desired = V(-1, 0, 0);                                       // col 63 (clear)
            AssertPos(desired, CheckedStep.Resolve(wall, from, desired), "walk out into a clear neighbour");
        }

        [Fact]
        public void AMoverAlreadyInsideABlockedCell_MayNotTraverseOnwardIntoAnotherBlockedCell()
        {
            // Two-cell-thick wall (columns 63 and 64). Starting in col 64 the mover is confined: col 63 is a
            // DIFFERENT blocked cell, so the onward step is rejected and X does not advance.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) { mask[row * GS + 63] = true; mask[row * GS + 64] = true; }
            var thick = new PathabilityGrid(mask);

            FixedVec3 from = new FixedVec3(Fixed.Half, Fixed.Zero, Fixed.Zero);   // col 64
            FixedVec3 got = CheckedStep.Resolve(thick, from, V(-3, 0, 0));        // aiming for col 62
            Assert.True(got.X.Raw == from.X.Raw,
                $"a confined mover traversed onward through the wall (X={got.X.ToFloat()}).");
        }

        // ── determinism ──────────────────────────────────────────────────────────

        [Fact]
        public void Resolve_IsPure_SameInputsGiveByteIdenticalResults()
        {
            PathabilityGrid a = CornerWall(65, 65), b = CornerWall(65, 65);
            FixedVec3 from = V(0, 0, 0), desired = V(3, 0, 3);

            FixedVec3 first = CheckedStep.Resolve(a, from, desired);
            FixedVec3 second = CheckedStep.Resolve(a, from, desired);
            FixedVec3 onAFreshGrid = CheckedStep.Resolve(b, from, desired);

            AssertPos(first, second, "repeat call");
            AssertPos(first, onAFreshGrid, "equivalent grid instance");
        }

        // ── the production integrator really routes through the helper ───────────

        [Fact]
        public void MovementSystem_PositionAfterEachTick_EqualsTheHelperAppliedToItsOwnIntegratedStep()
        {
            // The extraction's proof: MovementSystem's post-tick Position is EXACTLY
            // CheckedStep.Resolve(grid, prePos, prePos + <the step it integrated>) on every tick — including the ticks
            // where the wall actually rejects the step. If the integrator ever grew a second, divergent copy of the
            // rejection (the DW-648 failure mode), this parity breaks.
            //
            // DW-732 — the integrated step is read off a MIRROR world holding the identical mover on a NULL grid, not
            // reconstructed from EntityWorld.Velocity. Velocity used to be a faithful record of the DESIRED step;
            // DW-732 now zeroes it whenever the guard refuses the step, so reconstructing `integrated` from it would
            // make this parity trivially true on exactly the ticks that matter (both sides collapsing to the pre-step
            // position). The mirror is also strictly STRONGER than the old reading: with a null grid Resolve is the
            // identity, so the mirror's post-tick Position IS the unrejected step, and the comparison now proves the
            // two runs agree on the whole VELOCITY SOLUTION as well as on the rejection.
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid wall = ColumnWall(64);
            w.SetPathabilityGrid(wall);

            var mirrorWorld = new EntityWorld();   // no SetPathabilityGrid ⇒ Pathability == null ⇒ Resolve is identity
            var mirrorMove = new MovementSystem();

            int u = w.Create(V(-20, 0, 0), Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(150));
            w.MoveTarget[u] = V(40, 0, 0);
            w.Flags[u] |= EntityFlags.Moving;

            int m = mirrorWorld.Create(V(-20, 0, 0), Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(150));
            mirrorWorld.MoveTarget[m] = V(40, 0, 0);
            mirrorWorld.Flags[m] |= EntityFlags.Moving;

            int rejections = 0, refusedSteps = 0;
            for (int t = 0; t < 200; t++)
            {
                FixedVec3 pre = w.Position[u];
                // Re-seed the mirror from the guarded world's REAL pre-step state every tick, so the two integrate the
                // same step and can never drift apart once the wall stops one of them.
                mirrorWorld.Position[m] = pre;
                mirrorWorld.Flags[m] = w.Flags[u];

                move.Tick(w, Dt);
                mirrorMove.Tick(mirrorWorld, Dt);

                FixedVec3 post = w.Position[u];
                FixedVec3 integrated = mirrorWorld.Position[m];   // the unrejected step this very tick
                FixedVec3 expected = CheckedStep.Resolve(wall, pre, integrated);
                AssertPos(expected, post, $"tick {t + 1}");

                if (post.X.Raw != integrated.X.Raw || post.Z.Raw != integrated.Z.Raw) rejections++;

                if (post.X.Raw == pre.X.Raw && post.Z.Raw == pre.Z.Raw)
                {
                    refusedSteps++;
                    // DW-732 — a step the guard refused must not leave the seek velocity standing. Pre-fix the mover
                    // reported a non-zero Velocity forever while its Position never moved a raw tick, so a save taken
                    // here recorded a wall-stuck unit as travelling at full speed.
                    Assert.True(w.Velocity[u] == FixedVec3.Zero,
                        $"tick {t + 1}: the guard refused the step (Position unchanged) but Velocity is still " +
                        $"({w.Velocity[u].X.ToFloat()}, {w.Velocity[u].Z.ToFloat()}) — DW-732.");
                    // …and the control proves that is the GUARD's doing, not the mover giving up: the unguarded
                    // mirror, from the identical state, is still seeking at full speed on this same tick.
                    Assert.False(mirrorWorld.Velocity[m] == FixedVec3.Zero,
                        $"tick {t + 1}: the unguarded mirror also reported zero velocity, so the assertion above " +
                        "proves nothing about the guard.");
                }
            }

            Assert.True(rejections > 0,
                "the mover never reached the wall, so this parity run never exercised a rejected step.");
            Assert.True(refusedSteps > 0,
                "no tick was refused outright, so the DW-732 zero-velocity assertion above never ran.");
            Assert.True(w.Position[u].X.Raw < 0,
                $"the mover ended east of the wall (X={w.Position[u].X.ToFloat()}) — the guard did not hold.");
        }
    }
}
