#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 15.2 (DW-146) — the Godot-free, integer-only world-grid→heightmap-texel mapping. Pins that the CHOICE of
    /// raw texel each elevation cell reads is deterministic (no float, so bit-identical on x64/ARM) across the whole
    /// grid: the negative-world edge, the origin, and the positive-world edge; that a region sampled at its own
    /// resolution is the identity; that a coarser/finer grid maps by the exact floor ratio; and that off-grid indices
    /// clamp to the region rather than escaping it.
    /// </summary>
    public class HeightmapCellMappingTests
    {
        // ── 1:1 (shipped) resolution — identity, edge to edge ───────────────────

        [Theory]
        [InlineData(0, 0)]     // negative-world edge cell → texel 0
        [InlineData(1, 1)]
        [InlineData(128, 128)] // origin
        [InlineData(254, 254)]
        [InlineData(255, 255)] // positive-world edge cell → last texel
        public void SameResolution_IsIdentity(int cell, int expectedTexel)
            => Assert.Equal(expectedTexel, HeightmapCellMapping.CellToTexel(cell, 256, 256));

        // ── Coarser grid (128 cells over a 256-texel region): each cell centre lands on an odd texel ──

        [Theory]
        [InlineData(0, 1)]     // cell 0 centre = world texel 1
        [InlineData(1, 3)]
        [InlineData(63, 127)]
        [InlineData(127, 255)] // positive edge
        public void CoarserGrid_MapsByFloorRatio(int cell, int expectedTexel)
            => Assert.Equal(expectedTexel, HeightmapCellMapping.CellToTexel(cell, 128, 256));

        // ── Finer grid (512 cells over a 256-texel region): two cells share each texel ──

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(510, 255)]
        [InlineData(511, 255)] // positive edge
        public void FinerGrid_MapsByFloorRatio(int cell, int expectedTexel)
            => Assert.Equal(expectedTexel, HeightmapCellMapping.CellToTexel(cell, 512, 256));

        // ── Off-grid / degenerate inputs clamp into [0, regionSize-1] and never throw ──

        [Fact]
        public void OutOfRangeIndices_ClampToRegion()
        {
            Assert.Equal(0, HeightmapCellMapping.CellToTexel(-5, 256, 256));   // negative → first texel
            Assert.Equal(255, HeightmapCellMapping.CellToTexel(1000, 256, 256)); // past the end → last texel
        }

        [Fact]
        public void DegenerateDimensions_ReturnZero()
        {
            Assert.Equal(0, HeightmapCellMapping.CellToTexel(3, 0, 256));
            Assert.Equal(0, HeightmapCellMapping.CellToTexel(3, 256, 0));
        }

        // ── The mapping is monotonic non-decreasing across the grid (a correct floor mapping) and total ──

        [Fact]
        public void MappingIsMonotonicAndInRange_AcrossTheWholeGrid()
        {
            const int cells = 256, region = 256;
            int prev = -1;
            for (int c = 0; c < cells; c++)
            {
                int t = HeightmapCellMapping.CellToTexel(c, cells, region);
                Assert.InRange(t, 0, region - 1);
                Assert.True(t >= prev, $"cell {c} texel {t} decreased below {prev}");
                prev = t;
            }
        }
    }
}
