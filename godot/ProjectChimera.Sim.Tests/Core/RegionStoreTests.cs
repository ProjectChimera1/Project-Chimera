#nullable enable
using System;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 6.4 — <see cref="RegionStore"/> is the Godot-free resolved-region store the <c>unit_in_region</c>
    /// condition scans. Pins id→index resolution (ordinal, first-match), containment delegation to
    /// <see cref="FixedRect"/>, and the degenerate/empty-store safety (no OOB, no throw) the I/O matrix requires.
    /// </summary>
    public class RegionStoreTests
    {
        private static FixedRect Rect(int minX, int minZ, int maxX, int maxZ) =>
            new FixedRect(Fixed.FromInt(minX), Fixed.FromInt(minZ), Fixed.FromInt(maxX), Fixed.FromInt(maxZ));

        private static FixedVec3 P(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static RegionStore TwoRegions() => new RegionStore(
            new[] { "base", "hill" },
            new[] { Rect(-50, -50, -30, -30), Rect(-10, -10, 10, 10) });

        [Fact]
        public void EmptyStore_HasZeroCount_AndEveryLookupFailsCleanly()
        {
            var s = RegionStore.Empty;
            Assert.Equal(0, s.Count);
            Assert.False(s.TryGetIndex("anything", out int idx));
            Assert.Equal(-1, idx);
            Assert.False(s.Contains(0, P(0, 0)));   // OOB index → false, no throw
            Assert.False(s.Contains(-1, P(0, 0)));
        }

        [Fact]
        public void TryGetIndex_ResolvesKnownIds_AndRejectsUnknownNullEmpty()
        {
            var s = TwoRegions();
            Assert.Equal(2, s.Count);
            Assert.True(s.TryGetIndex("base", out int i0)); Assert.Equal(0, i0);
            Assert.True(s.TryGetIndex("hill", out int i1)); Assert.Equal(1, i1);
            Assert.False(s.TryGetIndex("missing", out int im)); Assert.Equal(-1, im);
            Assert.False(s.TryGetIndex(null, out _));
            Assert.False(s.TryGetIndex("", out _));
        }

        [Fact]
        public void TryGetIndex_IsCaseSensitiveOrdinal()
        {
            var s = TwoRegions();
            Assert.False(s.TryGetIndex("BASE", out _)); // ordinal, not case-insensitive
        }

        [Fact]
        public void Contains_DelegatesToTheResolvedRect_Inclusive()
        {
            var s = TwoRegions();
            s.TryGetIndex("hill", out int idx);
            Assert.True(s.Contains(idx, P(0, 0)));    // interior
            Assert.True(s.Contains(idx, P(10, 10)));  // inclusive corner
            Assert.False(s.Contains(idx, P(11, 0)));  // just outside
        }

        [Fact]
        public void Contains_OutOfRangeIndex_ReturnsFalse_NoThrow()
        {
            var s = TwoRegions();
            Assert.False(s.Contains(2, P(0, 0)));   // == Count, OOB
            Assert.False(s.Contains(99, P(0, 0)));
            Assert.False(s.Contains(-1, P(0, 0)));
        }

        [Fact]
        public void Ctor_RejectsMismatchedArrayLengths()
        {
            Assert.Throws<ArgumentException>(() =>
                new RegionStore(new[] { "a", "b" }, new[] { Rect(0, 0, 1, 1) }));
        }
    }
}
