#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 6.7 — the cross-system grid-consistency guard the AC asks for (absent before this story). It pins THREE
    /// invariants so a supported map size can never silently place authored content outside a deterministic grid:
    ///   1. fog ↔ flow-field ↔ pathability agree on cell identity (same GRID_SIZE, world half-extent, cell size, and
    ///      the SAME integer world→cell mapping) — they are three views of one ±128/2-unit grid.
    ///   2. every supported <see cref="MapSize"/> half-extent ≤ <see cref="FlowField.WORLD_HALF_INT"/> (128), so no
    ///      authored edge position falls outside fog/flow/pathability coverage.
    ///   3. the spatial-hash coverage encloses that ±128 extent (its ±160 window ⊇ ±128).
    /// A future size (or a grid re-parameterization) that breaks any of these turns this test red rather than
    /// shipping units that fall off the deterministic grid.
    /// </summary>
    public class GridDimensionConsistencyTests
    {
        [Fact]
        public void Fog_Flow_Pathability_AgreeOnGridDimensions()
        {
            // Same cell count per axis.
            Assert.Equal(FlowField.GRID_SIZE, FogOfWarSystem.GRID_SIZE);
            Assert.Equal(FlowField.GRID_SIZE, PathabilityGrid.GRID_SIZE);

            // Same world half-extent (fog stores it as a float; flow as an int).
            Assert.Equal((float)FlowField.WORLD_HALF_INT, FogOfWarSystem.WORLD_HALF_EXTENT);

            // Same cell size.
            Assert.Equal((float)FlowField.CELL_SIZE_WORLD, FogOfWarSystem.CELL_SIZE);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(-127f, -127f)]
        [InlineData(63.5f, -42.25f)]
        [InlineData(127.9f, 127.9f)]
        public void FogAndFlow_MapTheSameWorldPointToTheSameCell(float x, float z)
        {
            // Fog's integer cell mapping (FogOfWarSystem.WorldToCell) must equal FlowField's — otherwise a unit's
            // vision cell and its pathing cell would disagree at the same world position. Call the real fog mapping
            // directly (not a reimplemented formula) so this stays honest if fog's mapping changes.
            FlowField.WorldToCell(Fixed.FromFloat(x), Fixed.FromFloat(z), out int flowCol, out int flowRow);

            var (fogCol, fogRow) = FogOfWarSystem.WorldToCell(x, z);

            Assert.Equal(flowCol, fogCol);
            Assert.Equal(flowRow, fogRow);

            // PathabilityGrid has no separate public world→cell mapping: it resolves every cell through
            // FlowField.WorldToCell by construction (see PathabilityGrid.cs), so it agrees with flow by definition.
        }

        [Fact]
        public void EverySupportedMapSize_FitsInsideTheFixedGridCoverage()
        {
            foreach (MapSize s in MapSizes.All)
            {
                float bounds = MapSizes.ToBounds(s);
                Assert.True(bounds <= FlowField.WORLD_HALF_INT,
                    $"{s} bounds {bounds} exceeds flow/fog/pathability coverage ±{FlowField.WORLD_HALF_INT}.");
            }
            // The helper's advertised ceiling must match the real grid constant.
            Assert.Equal((float)FlowField.WORLD_HALF_INT, MapSizes.MaxHalfExtent);
        }

        [Fact]
        public void RouteC_BorderExtent_NeitherDerivesNorConstrainsAnySimGrid()
        {
            // Story 15.2 (Route C, DW-160): border_extent is presentation-only. The fixed sim grids do not derive from
            // map_bounds OR border_extent (they are compile-time constants), and the ONLY sim constraint the validator
            // enforces is map_bounds <= MaxHalfExtent — a visual border is unbounded by the grids.
            var validator = new ScenarioValidator();

            // A huge visual border on a playable-ceiling map is legal: the border never touches a grid dimension.
            var bordered = ScenarioData.CreateBlank("m", size: MapSize.Large); // map_bounds 128
            bordered.BorderExtent = 500f;
            var borderedResult = validator.Validate(bordered);
            Assert.True(borderedResult.Ok, borderedResult.Error);

            // map_bounds past the grid coverage still fails regardless of border_extent — the constraint is on
            // map_bounds alone, and it is fail-closed.
            var oversize = ScenarioData.CreateBlank("m", size: MapSize.Large);
            oversize.MapBounds = FlowField.WORLD_HALF_INT + 1f; // 129
            oversize.BorderExtent = 0f;
            Assert.False(validator.Validate(oversize).Ok);

            // The fixed grids are unchanged constants — equal to the pinned coverage no matter what a scenario authors.
            Assert.Equal(FlowField.GRID_SIZE, FogOfWarSystem.GRID_SIZE);
            Assert.Equal(FlowField.GRID_SIZE, PathabilityGrid.GRID_SIZE);
            Assert.Equal((float)FlowField.WORLD_HALF_INT, MapSizes.MaxHalfExtent);
        }

        [Fact]
        public void SpatialHashCoverage_EnclosesTheFixedGridExtent()
        {
            // Spatial-hash covers [ORIGIN, ORIGIN + GRID_DIM*CELL_SIZE) on each axis; it must ⊇ [-128, 128].
            float min = SpatialHash.ORIGIN_F;
            float max = SpatialHash.ORIGIN_F + SpatialHash.GRID_DIM * SpatialHash.CELL_SIZE_F;
            Assert.True(min <= -FlowField.WORLD_HALF_INT,
                $"spatial-hash origin {min} does not enclose -{FlowField.WORLD_HALF_INT}.");
            Assert.True(max >= FlowField.WORLD_HALF_INT,
                $"spatial-hash max {max} does not enclose +{FlowField.WORLD_HALF_INT}.");
        }
    }
}
