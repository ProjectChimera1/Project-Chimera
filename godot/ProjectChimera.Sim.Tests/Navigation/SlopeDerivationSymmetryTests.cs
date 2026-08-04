#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-149 — <see cref="PathabilityGrid.DeriveSlopeBlockedInto"/> must sample ALL FOUR neighbours, not just the
    /// forward (+X / +Z) pair.
    ///
    /// <para>Pre-fix the derivation was directionally asymmetric in two visible ways:</para>
    /// <list type="bullet">
    ///   <item>the far EAST column and far SOUTH row could never auto-block, because
    ///   <see cref="ElevationGrid.Sample"/> CLAMPS past the last column/row so their only sampled neighbour returned
    ///   the cell's own height (rise 0) however steep the terrain there actually was;</item>
    ///   <item>a cliff blocked only the cell on its LOW side — the cell perched on the plateau edge, which is just as
    ///   unwalkable, stayed clear, so every derived wall landed one cell off.</item>
    /// </list>
    ///
    /// <para>Deterministic and <see cref="Fixed"/>-only throughout; the derived cells are recomputed at load and are
    /// NOT folded into <c>CanonicalModelHash</c> (only the slope TOGGLE + threshold are), so widening the derivation
    /// moves no handshake hash and no golden.</para>
    /// </summary>
    public class SlopeDerivationSymmetryTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;
        private const int N  = 256;   // the real Godot elevation-grid layout: 256×256 / 1 unit / ±128

        /// <summary>Build a 256×256 / 1-unit / ±128 elevation grid whose height is <c>high ? 10 : 0</c> per cell.</summary>
        private static ElevationGrid Grid(System.Func<int, int, bool> high)
        {
            var heights = new Fixed[N * N];
            for (int row = 0; row < N; row++)
                for (int col = 0; col < N; col++)
                    heights[row * N + col] = high(col, row) ? Fixed.FromInt(10) : Fixed.Zero;
            return new ElevationGrid(heights, N, N, Fixed.FromInt(-128), Fixed.FromInt(-128), Fixed.One);
        }

        private static bool[] Derive(ElevationGrid elev)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            PathabilityGrid.DeriveSlopeBlockedInto(mask, elev, Fixed.One);   // threshold 1 world-Y per world-unit
            return mask;
        }

        /// <summary>The set of derived flow COLUMNS on a given row.</summary>
        private static List<int> DerivedColumns(bool[] mask, int row)
        {
            var cols = new List<int>();
            for (int col = 0; col < GS; col++) if (mask[row * GS + col]) cols.Add(col);
            return cols;
        }

        /// <summary>The set of derived flow ROWS in a given column.</summary>
        private static List<int> DerivedRows(bool[] mask, int col)
        {
            var rows = new List<int>();
            for (int row = 0; row < GS; row++) if (mask[row * GS + col]) rows.Add(row);
            return rows;
        }

        // ── The edge-clamp half of the defect ────────────────────────────────────────────────────────────────────

        [Fact]
        public void FarEastEdgeCliff_IsDerived_ThoughTheForwardSampleClamps()
        {
            // A cliff in the last two elevation columns. Flow column 127 (centre world X=127 ⇒ elevation col 255) has
            // its +X neighbour CLAMPED back onto its own cell ⇒ pre-fix rise 0 ⇒ never blocked, however sheer.
            bool[] mask = Derive(Grid((col, _) => col >= 254));
            Assert.True(mask[70 * GS + 127], "the far-east edge column must auto-block from its WEST neighbour.");
            Assert.False(mask[70 * GS + 120], "a flat column far from the cliff must stay clear.");
        }

        [Fact]
        public void FarSouthEdgeCliff_IsDerived_ThoughTheForwardSampleClamps()
        {
            // The Z mirror: a cliff in the last two elevation ROWS. Flow row 127's +Z neighbour clamps onto itself.
            bool[] mask = Derive(Grid((_, row) => row >= 254));
            Assert.True(mask[127 * GS + 70], "the far-south edge row must auto-block from its NORTH neighbour.");
            Assert.False(mask[120 * GS + 70], "a flat row far from the cliff must stay clear.");
        }

        [Fact]
        public void FarWestAndFarNorthEdges_WereAlreadyCovered_AndStayCovered()
        {
            // The forward-only derivation happened to cover the LOW edges; the widened one must not regress them.
            Assert.True(Derive(Grid((col, _) => col <= 1))[70 * GS + 0],
                "the far-west edge column must auto-block.");
            Assert.True(Derive(Grid((_, row) => row <= 1))[0 * GS + 70],
                "the far-north edge row must auto-block.");
        }

        // ── The one-cell-off half of the defect ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Cliff_BlocksBothStraddlingColumns_NotJustTheLowSide()
        {
            // The canonical cliff at world X=0 (0 west, 10 east). Flow col 63 is the low-side cell (derived pre-fix);
            // flow col 64 sits ON the plateau edge and is exactly as unwalkable — pre-fix it stayed clear.
            bool[] mask = Derive(Grid((col, _) => col >= 128));
            Assert.Equal(new[] { 63, 64 }, DerivedColumns(mask, 70));
        }

        [Fact]
        public void AWestFacingCliff_DerivesTheSameColumnPair_AsAnEastFacingOne()
        {
            // Direction symmetry stated directly: flipping which side of world X=0 is high must not move the wall.
            Assert.Equal(DerivedColumns(Derive(Grid((col, _) => col >= 128)), 70),
                         DerivedColumns(Derive(Grid((col, _) => col <  128)), 70));
        }

        [Fact]
        public void ANorthFacingCliff_DerivesTheSameRowPair_AsASouthFacingOne()
        {
            Assert.Equal(new[] { 63, 64 }, DerivedRows(Derive(Grid((_, row) => row >= 128)), 70));
            Assert.Equal(DerivedRows(Derive(Grid((_, row) => row >= 128)), 70),
                         DerivedRows(Derive(Grid((_, row) => row <  128)), 70));
        }

        [Fact]
        public void TheXAndZ_ArmsAgree_ForTheSameCliffProfile()
        {
            // A cliff along X and the same cliff transposed along Z must derive transposed masks — no axis is special.
            bool[] alongX = Derive(Grid((col, _)   => col >= 128));
            bool[] alongZ = Derive(Grid((_,   row) => row >= 128));
            for (int row = 0; row < GS; row++)
                for (int col = 0; col < GS; col++)
                    Assert.Equal(alongX[row * GS + col], alongZ[col * GS + row]);
        }

        // ── Unchanged contracts ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void FlatTerrain_StillDerivesNothing_AtEveryEdge()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            Assert.False(PathabilityGrid.DeriveSlopeBlockedInto(mask, Grid((_, __) => false), Fixed.One));
            Assert.DoesNotContain(true, mask);
        }

        [Fact]
        public void ShallowSlope_BelowThreshold_IsStillNotDerived()
        {
            // Rise 10 over a 2-unit run is a slope of 5; a threshold of 6 must reject it from EVERY direction.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            Assert.False(PathabilityGrid.DeriveSlopeBlockedInto(mask, Grid((col, _) => col >= 128), Fixed.FromInt(6)));
            Assert.DoesNotContain(true, mask);
        }

        [Fact]
        public void Derivation_IsDeterministic_AcrossRepeatedRuns()
        {
            ElevationGrid elev = Grid((col, row) => (col + row) % 7 == 0);   // a noisy, every-direction height field
            bool[] a = Derive(elev), b = Derive(elev);
            Assert.Equal(a, b);
        }
    }
}
