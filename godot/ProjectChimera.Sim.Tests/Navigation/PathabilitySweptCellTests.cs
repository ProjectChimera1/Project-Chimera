#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-147 (grid half) — <see cref="PathabilityGrid.IsBlockedOnSegmentOutside"/>: the SWEPT-cell blocked test that
    /// replaces endpoint-only sampling.
    ///
    /// <para>Pre-fix the movement rejection asked only "is the DESTINATION cell blocked?", so a per-tick displacement
    /// at or beyond the 2-world-unit cell size tunnelled a one-cell-thick wall (both endpoints clear) and a diagonal
    /// step could clip a blocked cell's corner and come out the far side. Every test below states the pre-fix
    /// endpoint verdict alongside the swept one, so the regression is legible without running the old code.</para>
    /// </summary>
    public class PathabilitySweptCellTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;

        private static Fixed F(int v) => Fixed.FromInt(v);

        /// <summary>A full N-S wall one cell thick at column 64 (world X ∈ [0, 2)).</summary>
        private static PathabilityGrid WallAtCol64()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) mask[row * GS + 64] = true;
            return new PathabilityGrid(mask);
        }

        /// <summary>A grid whose ONLY blocked cell is <c>(col, row)</c>.</summary>
        private static PathabilityGrid SingleCell(int col, int row)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        // ── The defect itself ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void FastAxisStep_OverAOneCellWall_IsRejected_ThoughBothEndpointsAreClear()
        {
            // X=-1 (col 63, clear) → X=+4 (col 66, clear) in ONE step: 5 world units, 2.5 cells. The wall at col 64
            // lies strictly between them and the endpoint-only test cannot see it.
            PathabilityGrid wall = WallAtCol64();
            int from = PathabilityGrid.CellOf(F(-1), Fixed.Zero);

            Assert.False(wall.IsBlockedOutside(from, F(4), Fixed.Zero));                       // pre-fix: tunnels
            Assert.True(wall.IsBlockedOnSegmentOutside(from, F(-1), Fixed.Zero, F(4), Fixed.Zero));
        }

        [Fact]
        public void FastAxisStep_Westward_IsRejectedToo_TheSweepIsDirectionSymmetric()
        {
            PathabilityGrid wall = WallAtCol64();
            int from = PathabilityGrid.CellOf(F(4), Fixed.Zero);

            Assert.False(wall.IsBlockedOutside(from, F(-1), Fixed.Zero));
            Assert.True(wall.IsBlockedOnSegmentOutside(from, F(4), Fixed.Zero, F(-1), Fixed.Zero));
        }

        [Fact]
        public void DiagonalStep_ThroughABlockedCell_IsRejected_ThoughBothEndpointsAreClear()
        {
            // (-1,-1) [cell 63,63] → (5,5) [cell 66,66], a 45° segment straight through cell (64,64).
            PathabilityGrid g = SingleCell(64, 64);
            int from = PathabilityGrid.CellOf(F(-1), F(-1));

            Assert.False(g.IsBlockedOutside(from, F(5), F(5)));                       // pre-fix: corner-cut allowed
            Assert.True(g.IsBlockedOnSegmentOutside(from, F(-1), F(-1), F(5), F(5)));
        }

        [Fact]
        public void ArbitrarilyThickWall_IsNeverTunnelled_AtAnyStepLength()
        {
            // A 10-cell-thick band (cols 60..69, world X ∈ [-8, 12)). Sweeping a step of ANY length across it rejects.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
                for (int col = 60; col <= 69; col++) mask[row * GS + col] = true;
            var band = new PathabilityGrid(mask);

            int from = PathabilityGrid.CellOf(F(-30), Fixed.Zero);
            for (int toX = 12; toX <= 60; toX += 3)
                Assert.True(band.IsBlockedOnSegmentOutside(from, F(-30), Fixed.Zero, F(toX), Fixed.Zero),
                    $"a step from X=-30 to X={toX} swept straight through a 10-cell band.");
        }

        // ── The preserved arm: what must NOT change ──────────────────────────────────────────────────────────────

        [Fact]
        public void AxisAlignedSubCellSteps_DecideIdenticallyToTheEndpointCheck()
        {
            // The no-golden-movement proof. An axis-aligned step shorter than one cell visits exactly the two endpoint
            // cells, so the swept verdict must equal the endpoint verdict at every probe on both axes.
            PathabilityGrid colWall = WallAtCol64();
            PathabilityGrid rowWall = BuildRowWall();
            for (int i = -2000; i <= 2000; i += 7)
            {
                Fixed a = Fixed.FromRaw(i * 64);                        // ≈ ±1.95 world units, a fine sub-cell lattice
                for (int dRaw = -Fixed.ONE; dRaw <= Fixed.ONE; dRaw += Fixed.ONE / 8)
                {
                    Fixed b = a + Fixed.FromRaw(dRaw);                  // |step| ≤ 1 world unit = half a cell

                    // X axis, against the N-S wall column.
                    int fromX = PathabilityGrid.CellOf(a, Fixed.Zero);
                    Assert.Equal(colWall.IsBlockedOutside(fromX, b, Fixed.Zero),
                                 colWall.IsBlockedOnSegmentOutside(fromX, a, Fixed.Zero, b, Fixed.Zero));

                    // Z axis, against the mirrored E-W wall row.
                    int fromZ = PathabilityGrid.CellOf(Fixed.Zero, a);
                    Assert.Equal(rowWall.IsBlockedOutside(fromZ, Fixed.Zero, b),
                                 rowWall.IsBlockedOnSegmentOutside(fromZ, Fixed.Zero, a, Fixed.Zero, b));
                }
            }
        }

        /// <summary>A full E-W wall one cell thick at row 64 (world Z ∈ [0, 2)) — the Z-axis mirror of the fixture.</summary>
        private static PathabilityGrid BuildRowWall()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int col = 0; col < GS; col++) mask[64 * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        [Fact]
        public void SegmentInsideOneCell_IsNeverBlocking_EvenInsideABlockedCell()
        {
            // The DW-148 confinement contract survives: shuffling within one's own cell is not "entering" anything.
            PathabilityGrid wall = WallAtCol64();
            int from = PathabilityGrid.CellOf(F(1), Fixed.Zero);         // inside the blocked column
            Assert.True(wall.Blocked[from]);
            Assert.False(wall.IsBlockedOnSegmentOutside(from, F(1), Fixed.Zero,
                                                        Fixed.FromRaw(Fixed.ONE + Fixed.HALF), Fixed.Zero));
        }

        [Fact]
        public void StartCellIsExcluded_ButAFurtherBlockedCellIsNot()
        {
            // A unit standing in the wall may walk out east into the clear cell (65)…
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) { mask[row * GS + 64] = true; mask[row * GS + 66] = true; }
            var g = new PathabilityGrid(mask);

            int from = PathabilityGrid.CellOf(F(1), Fixed.Zero);         // col 64, blocked
            Assert.False(g.IsBlockedOnSegmentOutside(from, F(1), Fixed.Zero, F(3), Fixed.Zero));  // → col 65, clear
            // …but it may not sweep ONWARD through the second blocked column (66).
            Assert.True(g.IsBlockedOnSegmentOutside(from, F(1), Fixed.Zero, F(5), Fixed.Zero));
        }

        [Fact]
        public void AllClearGrid_IsAnExactNoOp()
        {
            var clear = new PathabilityGrid(new bool[PathabilityGrid.CELL_COUNT]);
            Assert.False(clear.AnyBlocked);
            Assert.False(clear.IsBlockedOnSegmentOutside(0, F(-100), F(-100), F(100), F(100)));
            Assert.False(PathabilityGrid.Empty.IsBlockedOnSegmentOutside(0, F(-100), F(-100), F(100), F(100)));
        }

        // ── Edge / robustness contract ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void EndpointCellIdentity_MatchesFlowFieldWorldToCell_AtEveryProbe()
        {
            // The DDA derives its cells by floor-dividing the raw Fixed coordinate; FlowField.WorldToCell floors the
            // world coordinate then integer-divides. Pin that they agree: with ONLY the destination's cell blocked,
            // the swept verdict must equal the endpoint verdict for a step that crosses exactly one boundary.
            for (int i = -260; i <= 260; i++)
            {
                Fixed x1 = Fixed.FromRaw(i * (Fixed.ONE / 2));           // half-unit lattice over ±130 world units
                int destCell = PathabilityGrid.CellOf(x1, Fixed.Zero);
                int destCol  = destCell % GS;
                PathabilityGrid g = SingleCell(destCol, destCell / GS);

                // Start one cell west of the destination so the segment crosses exactly one column boundary.
                Fixed x0 = x1 - Fixed.FromInt(FlowField.CELL_SIZE_WORLD);
                int from = PathabilityGrid.CellOf(x0, Fixed.Zero);
                if (from == destCell) continue;                          // both ends in the same clamped edge cell

                Assert.True(g.IsBlockedOnSegmentOutside(from, x0, Fixed.Zero, x1, Fixed.Zero),
                    $"the swept walk missed the destination cell for X={x1.ToFloat()}.");
            }
        }

        [Fact]
        public void OutOfGridSegments_ClampLikeIsBlocked_AndNeverThrow()
        {
            PathabilityGrid wall = WallAtCol64();

            // Wholly outside the grid on one side: every visited cell clamps to the same edge cell as the start, which
            // is the from-cell ⇒ never blocking (matching the endpoint check's clamp).
            int fromEast = PathabilityGrid.CellOf(F(200), Fixed.Zero);
            Assert.False(wall.IsBlockedOutside(fromEast, F(260), Fixed.Zero));
            Assert.False(wall.IsBlockedOnSegmentOutside(fromEast, F(200), Fixed.Zero, F(260), Fixed.Zero));

            int fromWest = PathabilityGrid.CellOf(F(-200), Fixed.Zero);
            Assert.False(wall.IsBlockedOnSegmentOutside(fromWest, F(-200), Fixed.Zero, F(-260), Fixed.Zero));

            // Leaving the grid from an in-bounds cell clamps onto the edge cell — no OOB read, no throw.
            int fromEdge = PathabilityGrid.CellOf(F(126), Fixed.Zero);
            Assert.False(wall.IsBlockedOnSegmentOutside(fromEdge, F(126), Fixed.Zero, F(140), Fixed.Zero));
        }

        [Fact]
        public void TeleportScaleStep_FailsClosed_RatherThanSweepingUnbounded()
        {
            // A single step spanning more than twice the map cannot be a legitimate integration; it is REFUSED (never
            // allowed to pass through walls) and the walk stays bounded.
            PathabilityGrid wall = WallAtCol64();
            int from = PathabilityGrid.CellOf(F(-10000), Fixed.Zero);
            Assert.True(wall.IsBlockedOnSegmentOutside(from, F(-10000), Fixed.Zero, F(10000), Fixed.Zero));
        }

        [Fact]
        public void RepeatedEvaluation_IsDeterministic_AndFreeOfSideEffects()
        {
            PathabilityGrid g = SingleCell(64, 64);
            int from = PathabilityGrid.CellOf(F(-1), F(-1));
            bool first = g.IsBlockedOnSegmentOutside(from, F(-1), F(-1), F(5), F(5));
            for (int k = 0; k < 50; k++)
                Assert.Equal(first, g.IsBlockedOnSegmentOutside(from, F(-1), F(-1), F(5), F(5)));
            Assert.True(first);
        }
    }
}
