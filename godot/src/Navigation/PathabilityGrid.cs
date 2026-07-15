#nullable enable
using System;
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Story 6.5 — a Godot-free, deterministic per-cell pathability (blocked/passable) grid: the sim-side
    /// projection of the authored "impassable terrain" paint plus any slope-derived auto-blocked cells. Built
    /// ONCE at load time by a Godot-side phase (decode the painted base64 bitset + optionally union slope-derived
    /// cells), then injected into <see cref="ProjectChimera.Core.EntityWorld"/>, <see cref="FlowFieldSystem"/>,
    /// and the editor overlay so every consumer shares ONE cell identity.
    ///
    /// <para><b>Grid identity.</b> 128×128 cells, 2 world-units/cell over ±128 — byte-identical to
    /// <see cref="FlowField.WorldToCell"/>. A painted/derived cell resolves through that EXACT mapping so the
    /// validator, the deterministic sim tick, and the flow-field BFS all agree on which cell is blocked.</para>
    ///
    /// <para><b>Determinism contract.</b> <see cref="IsBlocked"/> does a CLAMPED INTEGER CELL LOOKUP over the flat
    /// <see cref="Blocked"/> mask (via <see cref="FlowField.WorldToCell"/>) — never a floating-point / Godot
    /// <c>Image</c> interpolation, never an out-of-bounds read. All arithmetic is integer/<see cref="Fixed"/>, so
    /// the result is byte-identical across platforms. Degenerate/empty grids are safe: <see cref="Empty"/> reports
    /// <see cref="AnyBlocked"/> == false and every lookup returns false.</para>
    ///
    /// <para>The packed on-disk form is the 128²/8 = 2048-byte little-endian bitset (bit i = cell i =
    /// <c>row*128 + col</c>). Only the painted bitset + slope config persist; slope-derived cells are recomputed
    /// deterministically at load and unioned into the runtime mask.</para>
    /// </summary>
    public sealed class PathabilityGrid
    {
        /// <summary>Grid cells along each axis (mirrors <see cref="FlowField.GRID_SIZE"/>).</summary>
        public const int GRID_SIZE = FlowField.GRID_SIZE;      // 128

        /// <summary>Total cell count (mirrors <see cref="FlowField.CELL_COUNT"/>).</summary>
        public const int CELL_COUNT = FlowField.CELL_COUNT;    // 16384

        /// <summary>Packed bitset length in bytes (one bit per cell).</summary>
        public const int PACKED_BYTES = CELL_COUNT / 8;        // 2048

        /// <summary>Row-major <c>[row * GRID_SIZE + col]</c> blocked mask (true = impassable). Length = 16384.</summary>
        public readonly bool[] Blocked;

        /// <summary>True when at least one cell is blocked (the fast no-op gate for the null/flat path).</summary>
        public readonly bool AnyBlocked;

        /// <summary>The shared empty grid (no blocked cells). Reused so the flat/legacy common case allocates nothing.</summary>
        public static readonly PathabilityGrid Empty = new PathabilityGrid(new bool[CELL_COUNT]);

        /// <summary>Construct over a pre-built 16384-length blocked mask. A wrong-length array degrades to all-clear
        /// (never throws) so a corrupt load is flat, not a crash.</summary>
        public PathabilityGrid(bool[] blocked)
        {
            if (blocked == null || blocked.Length != CELL_COUNT)
            {
                Blocked = new bool[CELL_COUNT];
                AnyBlocked = false;
                return;
            }
            Blocked = blocked;
            bool any = false;
            for (int i = 0; i < blocked.Length; i++) { if (blocked[i]) { any = true; break; } }
            AnyBlocked = any;
        }

        /// <summary>
        /// Deterministically test whether world (x, z) resolves to a blocked cell — a CLAMPED INTEGER cell lookup
        /// through <see cref="FlowField.WorldToCell"/> (never interpolation, never an OOB read). An XZ outside the
        /// grid clamps to the nearest edge cell.
        /// </summary>
        public bool IsBlocked(Fixed x, Fixed z)
        {
            FlowField.WorldToCell(x, z, out int col, out int row);
            return Blocked[row * GRID_SIZE + col];
        }

        /// <summary>
        /// FNV-1a digest over the packed bitset for the <see cref="ProjectChimera.Core.Definitions.CanonicalModelHash"/>
        /// fold. Returns 0 when NO cell is blocked (so an all-clear grid is indistinguishable from an absent layer),
        /// else a non-zero fold (0→1 sentinel). Consistent with <see cref="DigestOfBase64"/>.
        /// </summary>
        public uint Digest() => FoldPacked(Pack(Blocked));

        // ── Packing ───────────────────────────────────────────────────────────

        /// <summary>Pack a 16384-length blocked mask into the 2048-byte little-endian bitset (bit i = cell i).</summary>
        public static byte[] Pack(bool[] blocked)
        {
            var packed = new byte[PACKED_BYTES];
            if (blocked == null) return packed;
            int n = Math.Min(blocked.Length, CELL_COUNT);
            for (int i = 0; i < n; i++)
                if (blocked[i]) packed[i >> 3] |= (byte)(1 << (i & 7));
            return packed;
        }

        /// <summary>Unpack a 2048-byte bitset into a fresh 16384-length blocked mask. Shorter input ⇒ trailing
        /// cells clear; longer input is ignored past the grid.</summary>
        public static bool[] Unpack(byte[] packed)
        {
            var blocked = new bool[CELL_COUNT];
            if (packed == null) return blocked;
            int bits = Math.Min(packed.Length * 8, CELL_COUNT);
            for (int i = 0; i < bits; i++)
                blocked[i] = (packed[i >> 3] & (1 << (i & 7))) != 0;
            return blocked;
        }

        /// <summary>Encode a blocked mask to base64 of its packed bitset, or null when NO cell is blocked (the
        /// all-clear→null normalization the serialize chokepoint relies on to keep flat maps byte-identical).</summary>
        public static string? ToBase64(bool[] blocked)
        {
            byte[] packed = Pack(blocked);
            return AllZero(packed) ? null : Convert.ToBase64String(packed);
        }

        /// <summary>Decode a base64 packed bitset into a blocked mask. Null/empty/malformed ⇒ an all-clear mask
        /// (never throws) so a corrupt authored layer degrades to flat, not a crash.</summary>
        public static bool[] FromBase64(string? base64)
        {
            if (string.IsNullOrEmpty(base64)) return new bool[CELL_COUNT];
            try { return Unpack(Convert.FromBase64String(base64)); }
            catch { return new bool[CELL_COUNT]; }
        }

        /// <summary>
        /// FNV-1a digest of a base64 packed bitset — the value <c>CanonicalModelHash</c> folds. Null/empty/malformed
        /// or an all-clear decoded layer ⇒ 0 (byte-identical to an absent layer), else a non-zero fold. This makes
        /// "empty layer == baseline" hold and "a real painted layer moves the handshake hash" hold.
        /// <para>The decoded bytes are CANONICALIZED (<c>Pack(Unpack(...))</c> to the fixed 2048-byte form the sim
        /// actually consumes via <see cref="FromBase64"/>) BEFORE folding, so two base64 encodings that unpack to the
        /// same blocked mask (e.g. a short/over-long blob with trailing zeros) digest EQUALLY and match
        /// <see cref="Digest"/>. Without this, a hand-authored non-canonical map would false-reject at the handshake
        /// against a tool-saved (always-2048-byte) map even though both resolve to the identical runtime mask.</para>
        /// </summary>
        public static uint DigestOfBase64(string? base64)
        {
            if (string.IsNullOrEmpty(base64)) return 0u;
            byte[] packed;
            try { packed = Convert.FromBase64String(base64); }
            catch { return 0u; }
            return FoldPacked(Pack(Unpack(packed))); // canonicalize to the sim's 2048-byte mask before folding
        }

        // ── Slope-derived blocking (deterministic, Fixed-only) ────────────────

        /// <summary>
        /// Story 6.5 — deterministically OR steep flow cells into <paramref name="mask"/> from
        /// <paramref name="elev"/>. For each 128²/2-unit flow cell (identity shared with the sim/flow field), sample
        /// the terrain height at the cell centre and at its +X / +Z neighbours (2 world units apart) and block the
        /// cell when the max neighbour rise/run reaches <paramref name="threshold"/> (world Y per world unit). Pure
        /// <see cref="Fixed"/> math over the clamped <see cref="ElevationGrid.Sample"/> lookup — byte-identical across
        /// platforms and recomputed identically on every load, so the derived cells need not persist. Returns true if
        /// any cell was newly derived. Null grid / non-positive threshold ⇒ no derivation.
        /// </summary>
        public static bool DeriveSlopeBlockedInto(bool[] mask, ElevationGrid? elev, Fixed threshold)
        {
            if (mask == null || elev == null || threshold.Raw <= 0) return false;
            Fixed run = Fixed.FromInt(FlowField.CELL_SIZE_WORLD); // 2 world units between neighbour samples
            bool any = false;
            for (int row = 0; row < GRID_SIZE; row++)
            {
                for (int col = 0; col < GRID_SIZE; col++)
                {
                    int idx = row * GRID_SIZE + col;
                    if (idx >= mask.Length || mask[idx]) continue; // already painted-blocked — nothing to derive
                    // Cell-centre world XZ mirrors FlowField.CellCenter: (col*2 + 1) - 128.
                    Fixed cx = Fixed.FromInt(col * FlowField.CELL_SIZE_WORLD + 1 - FlowField.WORLD_HALF_INT);
                    Fixed cz = Fixed.FromInt(row * FlowField.CELL_SIZE_WORLD + 1 - FlowField.WORLD_HALF_INT);
                    Fixed h0 = elev.Sample(cx, cz);
                    Fixed hx = elev.Sample(cx + run, cz);
                    Fixed hz = elev.Sample(cx, cz + run);
                    Fixed riseX = AbsFixed(hx - h0);
                    Fixed riseZ = AbsFixed(hz - h0);
                    Fixed rise = riseX.Raw >= riseZ.Raw ? riseX : riseZ;
                    // slope = rise / run ≥ threshold  ⇔  rise ≥ threshold * run (avoids a divide, same Fixed result).
                    if (rise.Raw >= (threshold * run).Raw) { mask[idx] = true; any = true; }
                }
            }
            return any;
        }

        /// <summary>
        /// Story 6.5 — resolve the load-time union grid from the authored PAINTED layer plus optional slope-derived
        /// cells. Decodes <paramref name="paintedBase64"/>, and when <paramref name="slopeAutoBlock"/> is on with a
        /// positive <paramref name="slopeThreshold"/> and an <paramref name="elev"/> grid, ORs slope-derived steep
        /// cells in. Returns null when NOTHING is blocked (the flat/legacy common case — a null grid keeps every
        /// downstream consumer a byte-identical no-op). Pure / Godot-free so the decode→derive→union decision is
        /// Tier-1 testable independent of the Godot load phase, which only fans this result out to its sim sinks.
        /// </summary>
        public static PathabilityGrid? Resolve(string? paintedBase64, bool slopeAutoBlock, Fixed slopeThreshold, ElevationGrid? elev)
        {
            bool[] mask = FromBase64(paintedBase64);
            bool anyPainted = false;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) { anyPainted = true; break; }

            bool anyDerived = false;
            if (slopeAutoBlock && elev != null && slopeThreshold.Raw > 0)
                anyDerived = DeriveSlopeBlockedInto(mask, elev, slopeThreshold);

            return (anyPainted || anyDerived) ? new PathabilityGrid(mask) : null;
        }

        private static Fixed AbsFixed(Fixed v) => v.Raw < 0 ? -v : v;

        // ── Private ───────────────────────────────────────────────────────────

        private const uint FNV_OFFSET = 2166136261u;
        private const uint FNV_PRIME  = 16777619u;

        /// <summary>FNV-1a over the packed bytes; returns 0 when every byte is zero (all-clear == absent), else a
        /// non-zero fold (0→1 sentinel so a real layer never collides with the "absent" value).</summary>
        private static uint FoldPacked(byte[] packed)
        {
            if (AllZero(packed)) return 0u;
            uint h = FNV_OFFSET;
            for (int i = 0; i < packed.Length; i++) { h ^= packed[i]; h *= FNV_PRIME; }
            return h == 0u ? 1u : h;
        }

        private static bool AllZero(byte[] bytes)
        {
            if (bytes == null) return true;
            for (int i = 0; i < bytes.Length; i++) if (bytes[i] != 0) return false;
            return true;
        }
    }
}
