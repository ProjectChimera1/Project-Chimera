#nullable enable

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 15.2 (DW-146) — the deterministic, Godot-free core of the Godot→sim elevation read.
    ///
    /// <para>The elevation build formerly sampled Terrain3D via <c>get_height</c>, which BILINEARLY interpolates the
    /// four texels around a world position; that interpolated float's last bit can differ across x64 and ARM, so
    /// <c>Fixed.FromFloat</c> of it would quantize differently per platform — a prospective cross-platform desync the
    /// moment a sculpted map ships (elevation feeds the per-entity <c>Elevation[]</c> SoA folded into
    /// <c>SimChecksum</c>). Route: read the RAW nearest texel instead (Terrain3D's <c>get_pixel(TYPE_HEIGHT, …)</c>,
    /// which does no interpolation) and convert that ONE stored value with a single <c>Fixed.FromFloat</c>. A raw
    /// stored texel read back is bit-identical on every runtime, so the only thing that must be deterministic is the
    /// CHOICE of texel — which is exactly what this helper pins with integer-only math.</para>
    ///
    /// <para>Pure C# — no <c>using Godot;</c>. Unit-tested in <c>HeightmapCellMappingTests</c>.</para>
    /// </summary>
    public static class HeightmapCellMapping
    {
        /// <summary>
        /// Map a sim elevation-grid cell index (the integer world-grid coordinate on one axis, running
        /// <c>0..cellCount-1</c> from the negative world edge through the origin to the positive world edge) to the raw
        /// heightmap texel index (<c>0..regionSize-1</c>) whose stored value that cell samples: the texel the cell
        /// CENTRE falls in.
        ///
        /// <para>Computed as <c>(cellIndex + 0.5) · regionSize / cellCount</c> but evaluated ENTIRELY in integers —
        /// <c>((2·cellIndex + 1)·regionSize) / (2·cellCount)</c> — so the choice is bit-identical on every platform (no
        /// float rounding). C# integer division truncates toward zero; for the NON-NEGATIVE cell indices this is called
        /// with (<c>0..cellCount-1</c>) the numerator is non-negative, so truncation == floor exactly. A negative
        /// <c>cellIndex</c> (not a real elevation cell) truncates toward zero and is then caught by the negative guard,
        /// clamping to the first texel. <c>long</c> guards the multiply against <c>int</c> overflow at large grids. The
        /// result is clamped into <c>[0, regionSize-1]</c> so an off-grid cell reads the nearest edge texel rather than
        /// indexing out of the region. Degenerate <c>cellCount ≤ 0</c> / <c>regionSize ≤ 0</c> return 0.</para>
        ///
        /// <para>When the grid resolution equals the region resolution (the shipped 256-cell ↔ 256-texel case) this is
        /// the identity <c>cellIndex → cellIndex</c>; the ratio math only bites when a future grid samples a region at
        /// a different resolution.</para>
        /// </summary>
        public static int CellToTexel(int cellIndex, int cellCount, int regionSize)
        {
            if (cellCount <= 0 || regionSize <= 0) return 0;

            long texel = ((2L * cellIndex + 1L) * regionSize) / (2L * cellCount);

            if (texel < 0L) return 0;
            if (texel > regionSize - 1) return regionSize - 1;
            return (int)texel;
        }
    }
}
