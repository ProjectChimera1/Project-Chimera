#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 6.6 — a blocking prop's single-cell footprint and a water volume's rect footprint union into the
    /// resolved <see cref="PathabilityGrid"/> at the SAME <see cref="FlowField.WorldToCell"/> cell identity the sim
    /// enforces; a non-blocking prop contributes nothing; and removing the footprint source clears the grid (the
    /// load rebuilds from source, so un-stamp is inherent).
    /// </summary>
    public class PathabilityUnionPropsWaterTests
    {
        private static bool[] PropMask(float x, float z)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            PathabilityGrid.StampPropInto(mask, Fixed.FromFloat(x), Fixed.FromFloat(z));
            return mask;
        }

        [Fact]
        public void PropFootprint_CellDomain_MatchesFlowFieldWorldToCell()
        {
            // A prop at world (1, 1) maps to cell (col 64, row 64) — byte-identical to the sim's mapping.
            FlowField.WorldToCell(Fixed.FromFloat(1f), Fixed.FromFloat(1f), out int col, out int row);
            var mask = PropMask(1f, 1f);
            Assert.True(mask[row * PathabilityGrid.GRID_SIZE + col]);
            // Exactly one cell is stamped.
            int count = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) count++;
            Assert.Equal(1, count);
        }

        [Fact]
        public void BlockingPropFootprint_UnionsIntoResolvedGrid()
        {
            bool[] footprint = PropMask(1f, 1f);
            PathabilityGrid? grid = PathabilityGrid.Resolve(null, false, Fixed.Zero, null, footprint);
            Assert.NotNull(grid);
            Assert.True(grid!.AnyBlocked);
            Assert.True(grid.IsBlocked(Fixed.FromFloat(1f), Fixed.FromFloat(1f)));
        }

        [Fact]
        public void NoFootprint_ResolvesToNull()
        {
            // A non-blocking-only scenario passes a null extra mask (nothing stamped) — the flat/legacy no-op grid.
            Assert.Null(PathabilityGrid.Resolve(null, false, Fixed.Zero, null, null));
            // And an all-clear extra mask still resolves to null (nothing blocked anywhere).
            Assert.Null(PathabilityGrid.Resolve(null, false, Fixed.Zero, null, new bool[PathabilityGrid.CELL_COUNT]));
        }

        [Fact]
        public void RemovingFootprint_ClearsGrid_OnRebuild()
        {
            // "Un-stamp for free": with the footprint present the grid blocks; rebuilt WITHOUT it, the grid is null.
            bool[] footprint = PropMask(1f, 1f);
            Assert.NotNull(PathabilityGrid.Resolve(null, false, Fixed.Zero, null, footprint));
            Assert.Null(PathabilityGrid.Resolve(null, false, Fixed.Zero, null, null)); // source removed on next load
        }

        [Fact]
        public void WaterFootprint_StampsEveryCellInRect()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            // A 4×4 world rect starting at (0, 0): spans cells [col 64..66] × [row 64..66] (2 units/cell).
            PathabilityGrid.StampWaterInto(mask, Fixed.FromFloat(0f), Fixed.FromFloat(0f), Fixed.FromFloat(4f), Fixed.FromFloat(4f));
            var grid = new PathabilityGrid(mask);
            Assert.True(grid.IsBlocked(Fixed.FromFloat(1f), Fixed.FromFloat(1f)));  // inside
            Assert.True(grid.IsBlocked(Fixed.FromFloat(3f), Fixed.FromFloat(3f)));  // inside
            Assert.False(grid.IsBlocked(Fixed.FromFloat(-5f), Fixed.FromFloat(-5f))); // outside
        }

        [Fact]
        public void PropUnionsWith_PaintedLayer()
        {
            // A painted cell AND a prop footprint both survive into the union grid.
            var painted = new bool[PathabilityGrid.CELL_COUNT];
            painted[0] = true; // cell (0,0) — world corner
            string b64 = PathabilityGrid.ToBase64(painted)!;
            bool[] footprint = PropMask(1f, 1f); // cell (64,64)
            PathabilityGrid? grid = PathabilityGrid.Resolve(b64, false, Fixed.Zero, null, footprint);
            Assert.NotNull(grid);
            Assert.True(grid!.Blocked[0]);                                   // painted cell
            Assert.True(grid.IsBlocked(Fixed.FromFloat(1f), Fixed.FromFloat(1f))); // prop footprint cell
        }

        // ── Review fix (V1): the ONE shared BuildBlockingFootprint derivation ──────────────────────────────────
        // load (ScenarioLoadPhase), hash (CanonicalModelHash) and validator (ScenarioValidator) all route through this
        // exact method now, so the runtime grid can never block a different cell set than the handshake/validator
        // certified. These tests pin that single derivation directly.

        [Fact]
        public void BuildBlockingFootprint_BlockingProp_And_Water_StampExpectedCells()
        {
            var props = new[]
            {
                new ScenarioProp { PropId = "rock", X = 1f, Z = 1f, BlocksPathing = true },   // cell (64,64)
                new ScenarioProp { PropId = "bush", X = 5f, Z = 5f, BlocksPathing = false },  // NON-blocking → ignored
            };
            var water = new[] { new ScenarioWater { X = -2f, Z = -2f, W = 2f, H = 2f } };     // rect footprint

            bool[]? mask = PathabilityGrid.BuildBlockingFootprint(props, water);
            Assert.NotNull(mask);
            var grid = new PathabilityGrid(mask!);
            Assert.True(grid.IsBlocked(Fixed.FromFloat(1f), Fixed.FromFloat(1f)));   // blocking prop cell
            Assert.False(grid.IsBlocked(Fixed.FromFloat(5f), Fixed.FromFloat(5f)));  // non-blocking prop NOT stamped
            Assert.True(grid.IsBlocked(Fixed.FromFloat(-1f), Fixed.FromFloat(-1f))); // inside the water rect
        }

        [Fact]
        public void BuildBlockingFootprint_NothingBlocking_ReturnsNull()
        {
            Assert.Null(PathabilityGrid.BuildBlockingFootprint(null, null));
            Assert.Null(PathabilityGrid.BuildBlockingFootprint(
                new[] { new ScenarioProp { PropId = "tree", X = 0f, Z = 0f, BlocksPathing = false } }, null));
        }

        [Fact]
        public void BuildBlockingFootprint_DrivesResolve_SameCellsAsDirectStamp()
        {
            // The mask the load path feeds Resolve equals the direct StampPropInto derivation — proving the three
            // consumers share one cell identity (a drift in any copy would fail here).
            var props = new[] { new ScenarioProp { PropId = "crystal", X = 1f, Z = 1f, BlocksPathing = true } };
            bool[]? shared = PathabilityGrid.BuildBlockingFootprint(props, null);
            Assert.NotNull(shared);
            Assert.Equal(PropMask(1f, 1f), shared);
            PathabilityGrid? grid = PathabilityGrid.Resolve(null, false, Fixed.Zero, null, shared);
            Assert.NotNull(grid);
            Assert.True(grid!.IsBlocked(Fixed.FromFloat(1f), Fixed.FromFloat(1f)));
        }
    }
}
