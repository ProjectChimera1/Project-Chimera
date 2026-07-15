#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 6.3 — the Tier-1-testable core of the elevation feature: <see cref="ElevationGrid.Sample"/> is a
    /// deterministic clamped integer cell lookup over a <see cref="Fixed"/> grid. These tests pin the lookup math,
    /// the edge/OOB clamp (nearest valid cell, no OOB read / NaN / exception), and the flat/degenerate → Zero
    /// contract — all with small hand-built grids so the behaviour is legible and general over resolution.
    /// </summary>
    public class ElevationGridTests
    {
        // A 2×2 grid over world [0,2)×[0,2) at 1 unit/cell. Row-major [row*Width+col]:
        //   (col0,row0)=10  (col1,row0)=20
        //   (col0,row1)=30  (col1,row1)=40
        private static ElevationGrid TwoByTwo() => new ElevationGrid(
            new[] { Fixed.FromInt(10), Fixed.FromInt(20), Fixed.FromInt(30), Fixed.FromInt(40) },
            width: 2, height: 2,
            worldMinX: Fixed.Zero, worldMinZ: Fixed.Zero, cellSize: Fixed.One);

        [Theory]
        // Cell-centre samples land squarely in each cell.
        [InlineData(0.5, 0.5, 10)]
        [InlineData(1.5, 0.5, 20)]
        [InlineData(0.5, 1.5, 30)]
        [InlineData(1.5, 1.5, 40)]
        // Cell low-edge (inclusive) still lands in that cell (floor).
        [InlineData(0.0, 0.0, 10)]
        [InlineData(1.0, 0.0, 20)]
        public void Sample_InBounds_ReturnsTheContainingCell(double x, double z, int expected)
        {
            var grid = TwoByTwo();
            Assert.Equal(Fixed.FromInt(expected).Raw,
                grid.Sample(Fixed.FromFloat((float)x), Fixed.FromFloat((float)z)).Raw);
        }

        [Theory]
        // Far below the low corner → clamps to (col0,row0).
        [InlineData(-100.0, -100.0, 10)]
        // Far past the high corner → clamps to (col1,row1).
        [InlineData(100.0, 100.0, 40)]
        // Exactly ON the high edge (world 2.0 → cell index 2, out of [0,1]) → clamps to the last cell.
        [InlineData(2.0, 2.0, 40)]
        // Mixed: X in-bounds, Z out-high → clamps only the Z row.
        [InlineData(0.5, 50.0, 30)]
        public void Sample_OutsideOrAtEdge_ClampsToNearestValidCell(double x, double z, int expected)
        {
            var grid = TwoByTwo();
            Assert.Equal(Fixed.FromInt(expected).Raw,
                grid.Sample(Fixed.FromFloat((float)x), Fixed.FromFloat((float)z)).Raw);
        }

        [Fact]
        public void Sample_FlatGrid_ReturnsZeroEverywhere()
        {
            // All-zero heights (a flat/legacy heightmap) ⇒ every sample is Fixed.Zero, in and out of bounds.
            var flat = new ElevationGrid(new Fixed[16], width: 4, height: 4,
                worldMinX: Fixed.FromInt(-2), worldMinZ: Fixed.FromInt(-2), cellSize: Fixed.One);
            Assert.Equal(Fixed.Zero.Raw, flat.Sample(Fixed.Zero, Fixed.Zero).Raw);
            Assert.Equal(Fixed.Zero.Raw, flat.Sample(Fixed.FromInt(-99), Fixed.FromInt(99)).Raw);
        }

        [Fact]
        public void Sample_NegativeOriginGrid_MapsWorldToCellCorrectly()
        {
            // Mirrors the production ±extent layout: worldMin at -2, 4×4 at 1 unit/cell covers [-2, 2).
            // Row-major fill so world (0,0) → col=floor((0-(-2))/1)=2, row=2 → index 2*4+2 = 10.
            var heights = new Fixed[16];
            for (int i = 0; i < 16; i++) heights[i] = Fixed.FromInt(i);
            var grid = new ElevationGrid(heights, width: 4, height: 4,
                worldMinX: Fixed.FromInt(-2), worldMinZ: Fixed.FromInt(-2), cellSize: Fixed.One);

            Assert.Equal(Fixed.FromInt(10).Raw, grid.Sample(Fixed.Zero, Fixed.Zero).Raw);          // col2,row2
            Assert.Equal(Fixed.FromInt(0).Raw,  grid.Sample(Fixed.FromInt(-2), Fixed.FromInt(-2)).Raw); // col0,row0 (low corner)
            Assert.Equal(Fixed.FromInt(15).Raw, grid.Sample(Fixed.FromInt(9), Fixed.FromInt(9)).Raw);   // clamps to col3,row3
        }

        [Fact]
        public void Sample_DegenerateGrid_ReturnsZeroInsteadOfThrowing()
        {
            // An unsized grid must read as flat, never throw / OOB (the load-time build could hand a 0×0 grid).
            var empty = new ElevationGrid(System.Array.Empty<Fixed>(), width: 0, height: 0,
                worldMinX: Fixed.Zero, worldMinZ: Fixed.Zero, cellSize: Fixed.One);
            Assert.Equal(Fixed.Zero.Raw, empty.Sample(Fixed.FromInt(5), Fixed.FromInt(5)).Raw);
        }
    }
}
