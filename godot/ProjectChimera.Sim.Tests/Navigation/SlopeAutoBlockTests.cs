#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 6.5 — deterministic slope-auto-block derivation (<see cref="PathabilityGrid.DeriveSlopeBlockedInto"/>):
    /// steep flow cells are derived from a synthetic <see cref="ElevationGrid"/> by neighbour rise/run, unioned into
    /// the painted mask; a non-positive/huge threshold derives nothing; already-painted cells are preserved.
    /// </summary>
    public class SlopeAutoBlockTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;

        /// <summary>A 256×256 / 1-unit / ±128 elevation grid with a hard cliff at world X=0 (height 0 west, 10 east) —
        /// the same layout the real Godot elevation grid uses.</summary>
        private static ElevationGrid CliffAtX0()
        {
            const int N = 256;
            var heights = new Fixed[N * N];
            for (int row = 0; row < N; row++)
                for (int col = 0; col < N; col++)
                {
                    // World X of this elevation col ≈ col - 128; the cliff is at world X = 0 (col ≥ 128).
                    heights[row * N + col] = col >= 128 ? Fixed.FromInt(10) : Fixed.Zero;
                }
            return new ElevationGrid(heights, N, N, Fixed.FromInt(-128), Fixed.FromInt(-128), Fixed.One);
        }

        [Fact]
        public void SteepCliff_DerivesTheStraddlingColumn_AtLowThreshold()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            bool any = PathabilityGrid.DeriveSlopeBlockedInto(mask, CliffAtX0(), Fixed.One); // threshold 1 (rise/run)
            Assert.True(any);
            // Flow cell col 63 (centre world X=-1) has its +X neighbour (X=+1) up on the plateau ⇒ rise 10 / run 2 = 5 ≥ 1.
            Assert.True(mask[70 * GS + 63], "the cliff-straddling column should be slope-blocked.");
            // A cell two columns west (col 61, flat neighbours) is NOT derived.
            Assert.False(mask[70 * GS + 61]);
        }

        [Fact]
        public void HugeThreshold_DerivesNothing()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            bool any = PathabilityGrid.DeriveSlopeBlockedInto(mask, CliffAtX0(), Fixed.FromInt(1000));
            Assert.False(any);
            Assert.DoesNotContain(true, mask);
        }

        [Fact]
        public void NonPositiveThreshold_Or_NullGrid_DerivesNothing()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            Assert.False(PathabilityGrid.DeriveSlopeBlockedInto(mask, CliffAtX0(), Fixed.Zero)); // OFF-equivalent
            Assert.DoesNotContain(true, mask);
            Assert.False(PathabilityGrid.DeriveSlopeBlockedInto(mask, null, Fixed.One));          // no heightmap
            Assert.DoesNotContain(true, mask);
        }

        [Fact]
        public void FlatTerrain_DerivesNothing()
        {
            const int N = 256;
            var flat = new ElevationGrid(new Fixed[N * N], N, N, Fixed.FromInt(-128), Fixed.FromInt(-128), Fixed.One);
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            Assert.False(PathabilityGrid.DeriveSlopeBlockedInto(mask, flat, Fixed.One));
            Assert.DoesNotContain(true, mask);
        }

        [Fact]
        public void UnionsWithPaintedMask_WithoutClearingIt()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            int painted = 5 * GS + 5; // a painted cell far from the cliff
            mask[painted] = true;
            PathabilityGrid.DeriveSlopeBlockedInto(mask, CliffAtX0(), Fixed.One);
            Assert.True(mask[painted], "an already-painted cell must survive slope derivation (union, not replace).");
            Assert.True(mask[70 * GS + 63], "derived steep cells are added alongside the painted ones.");
        }
    }
}
