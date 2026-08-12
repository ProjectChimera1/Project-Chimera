#nullable enable
using System;
using ProjectChimera.Core;        // Fixed
using ProjectChimera.Navigation;  // FlowField

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// DW-150 — the Godot-free bridge between the EDITOR's float world coordinates (a camera raycast onto the ground
    /// plane) and the SIM's pathability grid cells. Extracted from <c>PathabilityTool</c>, which used to re-implement
    /// <see cref="FlowField.WorldToCell"/> and <see cref="FlowField.CellCenter"/> locally with
    /// <c>Mathf.FloorToInt</c>/<c>Mathf.Clamp</c> (the EditorHistory / StartSlotMath / TriggerVarPickerPolicy
    /// extraction pattern: pull the decision core out of the Godot node so it is Tier-1 testable).
    ///
    /// <para><b>The defect this closes.</b> The two mappings agreed by inspection — <c>Mathf.FloorToInt</c> and
    /// <see cref="Fixed.ToInt"/> (<c>Raw &gt;&gt; 16</c>, an arithmetic shift, i.e. FLOOR not truncate-toward-zero)
    /// land on the same integer world unit — but nothing PINNED that agreement. A future change to the sim's cell
    /// mapping (a different grid size, a re-centred origin, a rounding change) would silently desync what the author
    /// paints from what the sim blocks: the red overlay would show one wall and units would walk through it into a
    /// different one. There is now exactly ONE implementation — this type DELEGATES to <see cref="FlowField"/> rather
    /// than mirroring it, so the two cannot drift at all, and <c>PathabilityCellMappingTests</c> additionally pins the
    /// float-side entry point against the Fixed-side one across the full grid.</para>
    ///
    /// <para><b>Why the float half stays out of the sim.</b> The input is a raycast hit — inherently float,
    /// presentation-side, never folded into any checksum. This file is therefore deliberately OUTSIDE
    /// <c>SimSources.props</c> (and so outside the determinism analyzer's globbed set); it is compiled into the
    /// Tier-1 test assembly as a single-file include, the precedent set by <c>StartSlotMath</c> /
    /// <c>MapBoundsMath</c> / <c>TriggerVarPickerPolicy</c>. Pure C# — no <c>using Godot;</c>.</para>
    /// </summary>
    public static class PathabilityCellMapping
    {
        /// <summary>The lowest integer world coordinate the grid covers (the negative edge of cell 0): −128.</summary>
        public const int MinWorldInt = -FlowField.WORLD_HALF_INT;

        /// <summary>The highest integer world coordinate the grid covers (inside the last cell): +127. Anything
        /// beyond it maps to the same edge cell, so clamping HERE — before the Fixed conversion — is behaviour-
        /// identical to the sim's own <c>Math.Clamp(ix / CELL_SIZE_WORLD, 0, GRID_SIZE - 1)</c> while keeping
        /// <see cref="Fixed.FromInt"/> (a <c>value &lt;&lt; 16</c>) safely inside <c>int</c> range for a wild ray.</summary>
        public const int MaxWorldInt = FlowField.GRID_SIZE * FlowField.CELL_SIZE_WORLD - FlowField.WORLD_HALF_INT - 1;

        /// <summary>
        /// Quantize one float world axis to the integer world unit the sim would land on: FLOOR (matching
        /// <see cref="Fixed.ToInt"/>'s arithmetic shift — <c>-0.5</c> must become <c>-1</c>, not <c>0</c>, or every
        /// negative-side cell would be off by one), then clamp into the grid's covered range.
        ///
        /// <para>Non-finite input is folded onto the near edge deliberately: <c>NaN</c> fails BOTH comparisons and
        /// falls through to <see cref="MinWorldInt"/>, so a degenerate ray yields a defined cell instead of an
        /// unspecified <c>float</c>→<c>int</c> cast. A miss is a wrong cell at worst, never an exception.</para>
        /// </summary>
        public static int QuantizeWorldAxis(float world)
        {
            float floored = MathF.Floor(world);
            if (floored > MaxWorldInt) return MaxWorldInt;   // +∞ and anything past the far edge
            if (floored > MinWorldInt) return (int)floored;  // inside the grid — exact
            return MinWorldInt;                              // −∞, NaN, and anything past the near edge
        }

        /// <summary>
        /// Editor world XZ → the sim's grid (col, row). Routes through <see cref="FlowField.WorldToCell"/> itself —
        /// the cell the author paints IS the cell the validator and the sim enforce, by construction rather than by
        /// two implementations agreeing.
        /// </summary>
        public static void WorldToCell(float wx, float wz, out int col, out int row)
            => FlowField.WorldToCell(
                Fixed.FromInt(QuantizeWorldAxis(wx)),
                Fixed.FromInt(QuantizeWorldAxis(wz)),
                out col, out row);

        /// <summary>The flat <c>row * GRID_SIZE + col</c> index for an editor world position — the index into the
        /// painted/derived masks and into <c>PathabilityGrid.Blocked</c>.</summary>
        public static int WorldToIndex(float wx, float wz)
        {
            WorldToCell(wx, wz, out int col, out int row);
            return row * FlowField.GRID_SIZE + col;
        }

        /// <summary>The world X of cell <paramref name="col"/>'s centre, from <see cref="FlowField.CellCenter"/> —
        /// so the overlay quad sits exactly where the sim thinks the cell is. Always integral, so the Fixed→float
        /// conversion is exact.</summary>
        public static float CellCenterX(int col) => FlowField.CellCenter(col, 0).X.ToFloat();

        /// <summary>The world Z of cell <paramref name="row"/>'s centre — see <see cref="CellCenterX"/>.</summary>
        public static float CellCenterZ(int row) => FlowField.CellCenter(0, row).Z.ToFloat();
    }
}
