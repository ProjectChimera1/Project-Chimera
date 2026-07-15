#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 6.4 — <see cref="FixedRect"/> is the Tier-1 geometry core for named regions: a pure <see cref="Fixed"/>
    /// point-in-rect with INCLUSIVE edges. These pin the containment contract (interior true, exterior false, all
    /// four boundaries inclusive, corners inclusive) and the degenerate-rect safety the I/O matrix requires.
    /// </summary>
    public class FixedRectTests
    {
        private static FixedRect Rect(int minX, int minZ, int maxX, int maxZ) =>
            new FixedRect(Fixed.FromInt(minX), Fixed.FromInt(minZ), Fixed.FromInt(maxX), Fixed.FromInt(maxZ));

        private static FixedVec3 P(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        [Fact]
        public void InteriorPoint_IsInside()
        {
            var r = Rect(-10, -10, 10, 10);
            Assert.True(r.Contains(P(0, 0)));
            Assert.True(r.Contains(P(5, -5)));
        }

        [Fact]
        public void ExteriorPoint_IsOutside()
        {
            var r = Rect(-10, -10, 10, 10);
            Assert.False(r.Contains(P(11, 0)));
            Assert.False(r.Contains(P(0, -11)));
            Assert.False(r.Contains(P(20, 20)));
        }

        [Fact]
        public void PointsExactlyOnEachEdge_AreInclusive()
        {
            var r = Rect(-10, -10, 10, 10);
            Assert.True(r.Contains(Fixed.FromInt(-10), Fixed.Zero)); // on MinX
            Assert.True(r.Contains(Fixed.FromInt(10),  Fixed.Zero)); // on MaxX
            Assert.True(r.Contains(Fixed.Zero, Fixed.FromInt(-10))); // on MinZ
            Assert.True(r.Contains(Fixed.Zero, Fixed.FromInt(10)));  // on MaxZ
        }

        [Fact]
        public void AllFourCorners_AreInclusive()
        {
            var r = Rect(-10, -10, 10, 10);
            Assert.True(r.Contains(P(-10, -10)));
            Assert.True(r.Contains(P(-10,  10)));
            Assert.True(r.Contains(P( 10, -10)));
            Assert.True(r.Contains(P( 10,  10)));
        }

        [Fact]
        public void JustPastEdgeByOneRaw_IsOutside()
        {
            // The tightest boundary check: one raw unit beyond MaxX is outside (inclusive edge, not a fuzzy band).
            var r = Rect(-10, -10, 10, 10);
            Fixed justOver = Fixed.FromRaw(Fixed.FromInt(10).Raw + 1);
            Assert.False(r.Contains(justOver, Fixed.Zero));
        }

        [Fact]
        public void DegenerateRect_MinEqualsMax_AdmitsOnlyThatLineAndCornerPoint()
        {
            // A zero-width rect (MinX == MaxX) still admits the boundary line — no crash, inclusive by construction.
            var line = new FixedRect(Fixed.Zero, Fixed.FromInt(-5), Fixed.Zero, Fixed.FromInt(5));
            Assert.True(line.Contains(P(0, 0)));   // on the line
            Assert.False(line.Contains(P(1, 0)));  // off the line
            var point = new FixedRect(Fixed.Zero, Fixed.Zero, Fixed.Zero, Fixed.Zero);
            Assert.True(point.Contains(P(0, 0)));
            Assert.False(point.Contains(P(0, 1)));
        }

        [Fact]
        public void Contains_XZOverload_MatchesFixedVec3Overload_IgnoringY()
        {
            var r = Rect(-10, -10, 10, 10);
            var p = new FixedVec3(Fixed.FromInt(3), Fixed.FromInt(9999), Fixed.FromInt(4)); // Y is irrelevant
            Assert.Equal(r.Contains(Fixed.FromInt(3), Fixed.FromInt(4)), r.Contains(p));
            Assert.True(r.Contains(p));
        }
    }
}
