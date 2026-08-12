#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;
using ProjectChimera.CreationSuite;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.CreationSuite
{
    /// <summary>
    /// DW-150 — "the cell the author paints IS the cell the sim blocks".
    ///
    /// <para>The defect: <c>PathabilityTool</c> carried its OWN <c>WorldToCell</c> (<c>Mathf.FloorToInt</c> +
    /// <c>Mathf.Clamp</c>) and its own inline cell-centre expression, duplicating
    /// <see cref="FlowField.WorldToCell"/> / <see cref="FlowField.CellCenter"/>. The two agreed by inspection, and
    /// NOTHING pinned that — so a later change to the sim's mapping would have silently desynced the red overlay the
    /// author paints from the cells the sim actually blocks (paint a wall here, units walk through it there), with a
    /// green suite the whole way. The tool now delegates to <see cref="PathabilityCellMapping"/>, which delegates to
    /// <see cref="FlowField"/>; these tests pin the float→Fixed entry point against the sim's own mapping across the
    /// whole grid, and <see cref="PathabilityToolDelegatesItsCellMapping"/> pins that the duplicate cannot come back.</para>
    /// </summary>
    public class PathabilityCellMappingTests
    {
        // ── The DW-150 contract: editor float mapping == sim Fixed mapping, everywhere ────────────────────────

        [Fact]
        public void EveryCellCentre_RoundTripsToItsOwnCell_AndMatchesTheSimMapping()
        {
            // Sweep the ENTIRE 128x128 grid. For each cell: its centre must map back to itself through the editor's
            // float path, and that path must agree with FlowField's Fixed path exactly.
            for (int row = 0; row < FlowField.GRID_SIZE; row++)
            {
                for (int col = 0; col < FlowField.GRID_SIZE; col++)
                {
                    float cx = PathabilityCellMapping.CellCenterX(col);
                    float cz = PathabilityCellMapping.CellCenterZ(row);

                    PathabilityCellMapping.WorldToCell(cx, cz, out int backCol, out int backRow);
                    Assert.Equal(col, backCol);
                    Assert.Equal(row, backRow);

                    // The sim reads the same world point as Fixed — it must land on the same cell.
                    FlowField.WorldToCell(Fixed.FromInt((int)cx), Fixed.FromInt((int)cz), out int simCol, out int simRow);
                    Assert.Equal(simCol, backCol);
                    Assert.Equal(simRow, backRow);

                    // And the flat index the painted mask / PathabilityGrid.Blocked is keyed by.
                    Assert.Equal(row * FlowField.GRID_SIZE + col, PathabilityCellMapping.WorldToIndex(cx, cz));
                }
            }
        }

        [Fact]
        public void CellCentres_AreExactlyFlowFieldsCellCentres()
        {
            // The overlay quad sits at the cell centre; if this drifted from FlowField.CellCenter the red square
            // would be drawn off the cell it represents.
            for (int i = 0; i < FlowField.GRID_SIZE; i++)
            {
                FixedVec3 simCentre = FlowField.CellCenter(i, i);
                Assert.Equal(simCentre.X.ToFloat(), PathabilityCellMapping.CellCenterX(i));
                Assert.Equal(simCentre.Z.ToFloat(), PathabilityCellMapping.CellCenterZ(i));
            }
        }

        [Fact]
        public void SubCellSweep_AgreesWithTheSimMapping_AcrossTheWholeCoveredRange()
        {
            // Quarter-unit steps through the whole covered range (and a margin past both edges), i.e. every position
            // a ground raycast can plausibly return. The NEGATIVE half is the half that matters: Fixed.ToInt() is
            // Raw >> 16 — an arithmetic shift, so it FLOORS — and a truncate-toward-zero editor mapping would agree
            // on every non-negative coordinate and be one cell off on every negative one.
            for (float w = -140f; w <= 140f; w += 0.25f)
            {
                PathabilityCellMapping.WorldToCell(w, w, out int col, out int row);

                FlowField.WorldToCell(Fixed.FromFloat(w), Fixed.FromFloat(w), out int simCol, out int simRow);
                Assert.Equal(simCol, col);
                Assert.Equal(simRow, row);
                Assert.Equal(col, row); // both axes are quantized by the identical rule
            }
        }

        [Theory]
        [InlineData(-0.5f, -1)]   // FLOOR, not truncate-toward-zero: the classic off-by-one-cell trap
        [InlineData(-0.0001f, -1)]
        [InlineData(0f, 0)]
        [InlineData(0.9999f, 0)]
        [InlineData(1f, 1)]
        [InlineData(-128f, -128)]
        [InlineData(127.75f, 127)]
        public void QuantizeWorldAxis_FloorsTowardNegativeInfinity(float world, int expected)
            => Assert.Equal(expected, PathabilityCellMapping.QuantizeWorldAxis(world));

        [Theory]
        [InlineData(-1e9f)]
        [InlineData(-129f)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(float.NaN)]
        public void OffGridNegative_ClampsToTheFirstCell_NeverThrows(float world)
        {
            // A near-parallel camera ray can return an enormous (or non-finite) ground point. The pre-extraction code
            // fed that straight into `Mathf.FloorToInt(wx) + HALF`; the extracted path clamps BEFORE the Fixed
            // conversion (Fixed.FromInt is `value << 16` and would wrap), so an absurd hit lands on the edge cell
            // instead of a wrapped one somewhere in the middle of the map.
            Assert.Equal(PathabilityCellMapping.MinWorldInt, PathabilityCellMapping.QuantizeWorldAxis(world));

            PathabilityCellMapping.WorldToCell(world, world, out int col, out int row);
            Assert.Equal(0, col);
            Assert.Equal(0, row);
        }

        [Theory]
        [InlineData(1e9f)]
        [InlineData(128f)]
        [InlineData(float.PositiveInfinity)]
        public void OffGridPositive_ClampsToTheLastCell_NeverThrows(float world)
        {
            Assert.Equal(PathabilityCellMapping.MaxWorldInt, PathabilityCellMapping.QuantizeWorldAxis(world));

            PathabilityCellMapping.WorldToCell(world, world, out int col, out int row);
            Assert.Equal(FlowField.GRID_SIZE - 1, col);
            Assert.Equal(FlowField.GRID_SIZE - 1, row);
        }

        [Fact]
        public void CoveredRangeConstants_TrackTheSimGrid()
        {
            // If the sim grid is ever re-sized or re-centred these must move with it — they are derived from
            // FlowField's constants, never re-typed. Pins the derivation itself (the DW-150 desync vector).
            Assert.Equal(-FlowField.WORLD_HALF_INT, PathabilityCellMapping.MinWorldInt);
            Assert.Equal(FlowField.GRID_SIZE * FlowField.CELL_SIZE_WORLD - FlowField.WORLD_HALF_INT - 1,
                         PathabilityCellMapping.MaxWorldInt);

            // The extremes really are the first and last cell of the sim's own mapping.
            FlowField.WorldToCell(Fixed.FromInt(PathabilityCellMapping.MinWorldInt),
                                  Fixed.FromInt(PathabilityCellMapping.MaxWorldInt), out int col, out int row);
            Assert.Equal(0, col);
            Assert.Equal(FlowField.GRID_SIZE - 1, row);
        }

        // ── The structural half: the duplicate implementation must not come back ─────────────────────────────

        /// <summary>
        /// DW-150's actual defect was DUPLICATION, not a wrong value — so the guard has to be a source check.
        /// A value test alone cannot see the regression: re-adding a private <c>WorldToCell</c> to the tool would
        /// keep every assertion above green (they exercise the helper, which the tool would no longer call) while
        /// re-opening the exact drift the entry describes. This FAILS against the pre-fix
        /// <c>PathabilityTool.cs</c>, which declared both.
        /// </summary>
        [Fact]
        public void PathabilityToolDelegatesItsCellMapping()
        {
            string toolPath = Path.Combine(SrcRoot(), "CreationSuite", "PathabilityTool.cs");
            Assert.True(File.Exists(toolPath),
                $"DW-150 guard could not find PathabilityTool.cs at '{toolPath}'. If the file moved, update this guard.");

            string code = ProjectChimera.Sim.Tests.Meta.CSharpSourceScan
                .StripCommentsAndLiterals(File.ReadAllText(toolPath));

            Assert.Contains("PathabilityCellMapping.WorldToCell", code, StringComparison.Ordinal);
            Assert.Contains("PathabilityCellMapping.CellCenterX", code, StringComparison.Ordinal);
            Assert.Contains("PathabilityCellMapping.CellCenterZ", code, StringComparison.Ordinal);

            var offenders = new List<string>();
            if (code.Contains("Mathf.FloorToInt", StringComparison.Ordinal))
                offenders.Add("Mathf.FloorToInt — the tool re-quantizing world coordinates itself");
            if (code.Contains("WORLD_HALF_INT", StringComparison.Ordinal))
                offenders.Add("FlowField.WORLD_HALF_INT — the tool re-deriving the grid origin itself");
            if (code.Contains("private static void WorldToCell", StringComparison.Ordinal))
                offenders.Add("a private WorldToCell — the duplicate DW-150 removed");

            Assert.True(offenders.Count == 0,
                "PathabilityTool.cs is re-implementing the sim's cell mapping again (DW-150): " +
                string.Join("; ", offenders) + ". Route it through PathabilityCellMapping (which delegates to " +
                "FlowField) so the painted cell and the blocked cell can never drift apart.");
        }

        private static string SrcRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"DW-150 guard could not locate the shipping source tree. Resolved path: '{root}'. " +
                    "This path is derived from [CallerFilePath]; if the project layout moved, update this guard.");
            return root;
        }
    }
}
