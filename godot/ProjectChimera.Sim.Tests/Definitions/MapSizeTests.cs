#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.7 — the supported <see cref="MapSize"/> set is the single Godot-free source of truth for authored map
    /// extents. Every size must map to a positive half-extent ≤ the fixed grid coverage (128), and the
    /// bounds↔size round-trip must be stable so the picker/factory/validator never disagree on a size's identity.
    /// </summary>
    public class MapSizeTests
    {
        [Fact]
        public void All_IsNonEmpty()
            => Assert.NotEmpty(MapSizes.All);

        [Fact]
        public void EverySupportedSize_HasPositiveBoundsWithinGridCoverage()
        {
            foreach (MapSize s in MapSizes.All)
            {
                float b = MapSizes.ToBounds(s);
                Assert.True(b > 0f, $"{s} bounds must be > 0 (was {b}).");
                Assert.True(b <= MapSizes.MaxHalfExtent, $"{s} bounds {b} exceeds the fixed grid coverage {MapSizes.MaxHalfExtent}.");
            }
        }

        [Fact]
        public void ToBounds_ThenFromBounds_RoundTrips()
        {
            foreach (MapSize s in MapSizes.All)
                Assert.Equal(s, MapSizes.FromBounds(MapSizes.ToBounds(s)));
        }

        [Fact]
        public void IsSupportedBounds_MatchesTheSet()
        {
            foreach (MapSize s in MapSizes.All)
                Assert.True(MapSizes.IsSupportedBounds(MapSizes.ToBounds(s)));
            Assert.False(MapSizes.IsSupportedBounds(37f)); // an arbitrary non-supported extent
        }

        [Fact]
        public void FromBounds_UnknownExtent_FallsBackToMedium()
            => Assert.Equal(MapSize.Medium, MapSizes.FromBounds(999f));

        [Fact]
        public void MediumBounds_MatchesHistoricalDefault()
            // A legacy scenario's implicit MapBounds default (120) must read back as "Medium" so an existing map's
            // size label is stable.
            => Assert.Equal(120f, MapSizes.ToBounds(MapSize.Medium));

        [Fact]
        public void Label_IsNonEmptyForEverySize()
        {
            foreach (MapSize s in MapSizes.All)
                Assert.False(string.IsNullOrWhiteSpace(MapSizes.Label(s)));
        }
    }
}
