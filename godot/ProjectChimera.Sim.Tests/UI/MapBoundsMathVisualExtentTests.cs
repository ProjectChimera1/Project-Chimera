#nullable enable
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Story 15.2 (Route C, DW-160) — the PRESENTATION visual half-extent formula (playable map_bounds + non-playable
    /// border_extent). This locks the feature's core POSITIVE behaviour Godot-free, the way HeightmapCellMappingTests
    /// locked the sibling DW-146 heightmap math: BorderExtentTests pins the EXCLUSION side (never folded into a hash),
    /// this pins the INCLUSION side (the camera/ground extent actually grows by the border, and only by a positive
    /// border). ScenarioLoadPhase.VisualHalfExtentOf delegates here, so this pins the FORMULA that wiring uses; the
    /// live camera-extent wiring itself (UpdateCameraVisualExtent → RtsCameraController.PanTo clamp) is Godot-coupled
    /// and is covered by the in-engine gate, not this Tier-1 unit test.
    /// </summary>
    public class MapBoundsMathVisualExtentTests
    {
        [Fact]
        public void Border32_OnLargeMap_Gives160()   // The Frontier
            => Assert.Equal(160f, MapBoundsMath.VisualHalfExtent(128f, 32f));

        [Fact]
        public void Border2_OnLargeMap_Gives130()     // Mirror Lake's original visual extent
            => Assert.Equal(130f, MapBoundsMath.VisualHalfExtent(128f, 2f));

        [Fact]
        public void ZeroBorder_EqualsMapBounds()
            => Assert.Equal(120f, MapBoundsMath.VisualHalfExtent(120f, 0f));

        [Fact]
        public void NegativeBorder_ClampsToMapBounds()
            => Assert.Equal(80f, MapBoundsMath.VisualHalfExtent(80f, -50f));

        [Theory]
        [InlineData(80f, 0f, 80f)]     // Small, no border
        [InlineData(120f, 0f, 120f)]   // Medium, no border
        [InlineData(128f, 0f, 128f)]   // Large, no border
        [InlineData(128f, 32f, 160f)]  // Large + border (Frontier)
        public void VisualExtent_IsMapBoundsPlusPositiveBorder(float mb, float be, float expected)
            => Assert.Equal(expected, MapBoundsMath.VisualHalfExtent(mb, be));
    }
}
