#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;

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
        /// DW-148 — the CELL-RELATIVE blocked test the sim's movement rejection needs: true when world (x, z)
        /// resolves to a blocked cell that is NOT <paramref name="fromCell"/> (the flat index of the cell the unit
        /// already occupies, from <see cref="CellOf"/>). Staying inside one's OWN cell is therefore never "entering
        /// a blocked cell", so a unit that somehow starts inside a blocked cell can still shuffle within it and walk
        /// out into a CLEAR neighbour — but it may no longer traverse ONWARD into a different blocked cell (pre-fix,
        /// any unit already standing in a blocked cell was exempt from blocking entirely and walked straight through
        /// walls). Identical to <see cref="IsBlocked"/> whenever <paramref name="fromCell"/> is a CLEAR cell (a
        /// blocked destination is then necessarily a different cell), so the validated-map path is byte-identical.
        /// Same clamped integer cell lookup — no floating point, no OOB read.
        /// </summary>
        public bool IsBlockedOutside(int fromCell, Fixed x, Fixed z)
        {
            int idx = CellOf(x, z);
            return idx != fromCell && Blocked[idx];
        }

        /// <summary>The clamped flat <c>[row * GRID_SIZE + col]</c> cell index for world (x, z) — the grid's shared
        /// cell identity (<see cref="FlowField.WorldToIndex"/>), exposed so a caller can name the cell a unit
        /// currently occupies for <see cref="IsBlockedOutside"/> without duplicating the mapping.</summary>
        public static int CellOf(Fixed x, Fixed z) => FlowField.WorldToIndex(x, z);

        /// <summary>
        /// DW-147 — the SWEPT-CELL blocked test: true when the STRAIGHT SEGMENT (<paramref name="x0"/>,
        /// <paramref name="z0"/>) → (<paramref name="x1"/>, <paramref name="z1"/>) crosses ANY blocked cell other than
        /// <paramref name="fromCell"/> — not merely when its ENDPOINT lands in one.
        ///
        /// <para><b>Why.</b> <see cref="IsBlockedOutside"/> samples the two endpoints only, so a per-tick displacement
        /// at or beyond the 2-world-unit cell size (move speed ≳ 60 u/s at 30 tps) TUNNELS a one-cell-thick wall: both
        /// endpoints are clear, the wall in between is never sampled. A diagonal step can likewise clip the corner of a
        /// blocked cell and come out the far side. This walks the cells the segment actually enters (an Amanatides–Woo
        /// DDA) and rejects on the FIRST blocked one.</para>
        ///
        /// <para><b>Cell identity.</b> The traversal derives its cell indices by FLOOR-DIVIDING the raw
        /// <see cref="Fixed"/> coordinate — provably the SAME mapping as <see cref="FlowField.WorldToCell"/> (which
        /// floors the world coordinate then integer-divides by the cell size, then clamps), so the swept walk and every
        /// other consumer of the grid can never disagree about which cell a point is in. Out-of-grid cells clamp to the
        /// nearest edge cell exactly as <see cref="IsBlocked"/> does.</para>
        ///
        /// <para><b>Determinism.</b> Integer/<see cref="Fixed"/>-raw arithmetic only — the "which boundary comes first"
        /// ordering is decided by CROSS-MULTIPLYING the two boundary distances (<c>ax*|dz|</c> vs <c>az*|dx|</c>, both
        /// non-negative), so there is no division and no rounding in the comparison. An exact corner crossing (a tie)
        /// deterministically advances the X axis first, which visits one extra cell and is therefore the CONSERVATIVE
        /// resolution: a unit can never thread a perfect diagonal corner gap in a wall. Byte-identical across platforms
        /// and same-seed replays.</para>
        ///
        /// <para><b>AXIS-ALIGNED sub-cell steps are byte-identical to <see cref="IsBlockedOutside"/>.</b> A segment
        /// that moves along one axis only and crosses at most one boundary visits exactly the two endpoint cells, so
        /// for a mover under one cell per tick moving on an axis this decides identically. A DIAGONAL sub-cell step
        /// additionally visits the one shared-edge cell the segment genuinely passes through — the deliberate
        /// tightening (a unit may no longer cut the corner of an obstacle it visibly clips).</para>
        ///
        /// <para>A step spanning more than <see cref="MAX_SWEPT_CELLS"/> cells (over twice the map, unreachable by
        /// integration at any sane speed) is refused outright rather than swept: that bounds the walk, keeps the
        /// cross-multiply inside <see cref="long"/> range, and fails CLOSED (a teleport-scale displacement may not
        /// pass through walls).</para>
        /// </summary>
        public bool IsBlockedOnSegmentOutside(int fromCell, Fixed x0, Fixed z0, Fixed x1, Fixed z1)
        {
            if (!AnyBlocked) return false;

            // Grid space: shift world XZ so the grid's low corner is 0, then a cell is CELL_RAW wide.
            long gx0 = (long)x0.Raw + GRID_ORIGIN_RAW, gz0 = (long)z0.Raw + GRID_ORIGIN_RAW;
            long gx1 = (long)x1.Raw + GRID_ORIGIN_RAW, gz1 = (long)z1.Raw + GRID_ORIGIN_RAW;

            long col = FloorDiv(gx0, CELL_RAW), row = FloorDiv(gz0, CELL_RAW);
            long colEnd = FloorDiv(gx1, CELL_RAW), rowEnd = FloorDiv(gz1, CELL_RAW);

            long spanC = colEnd - col; if (spanC < 0) spanC = -spanC;
            long spanR = rowEnd - row; if (spanR < 0) spanR = -spanR;
            if (spanC + spanR > MAX_SWEPT_CELLS) return true; // teleport-scale step — refuse, never sweep unbounded

            long dx = gx1 - gx0, dz = gz1 - gz0;
            int stepC = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int stepR = dz > 0 ? 1 : (dz < 0 ? -1 : 0);
            long adx = dx < 0 ? -dx : dx;
            long adz = dz < 0 ? -dz : dz;

            // Distance from the segment START to the next cell boundary on each axis (non-negative; 0 when the start
            // sits exactly on a boundary and travel is toward the lower cell — it leaves immediately, as it should).
            long ax = stepC > 0 ? (col + 1) * CELL_RAW - gx0 : (stepC < 0 ? gx0 - col * CELL_RAW : 0L);
            long az = stepR > 0 ? (row + 1) * CELL_RAW - gz0 : (stepR < 0 ? gz0 - row * CELL_RAW : 0L);

            // The START cell is never tested: a unit is already standing there (the DW-148 confinement contract).
            while (col != colEnd || row != rowEnd)
            {
                // Advance whichever axis reaches its next boundary first — but NEVER an axis that has already
                // ARRIVED at its destination index. That axis's next boundary lies at or beyond the segment end, so
                // taking it steps PAST colEnd/rowEnd, and because an index only ever moves in its own step direction
                // it can never come back: the loop would spin forever (MAX_SWEPT_CELLS bounds only the initial span,
                // not the walk). Only X could overshoot, because the tie at `<=` resolves in its favour: a −X/+Z
                // segment whose BOTH endpoint coordinates land exactly on a cell boundary (an even world integer)
                // produces a spurious X crossing at t == 1 — floor() puts an on-boundary endpoint in the UPPER cell,
                // so travelling −X leaves one unused crossing behind — which ties with Z's genuine final crossing at
                // t == 1. Pure Fixed/integer state, so every lockstep peer and every same-seed replay froze on the
                // same tick. `col != colEnd` also subsumes the old `stepC != 0` guard (dx == 0 ⇒ colEnd == col) and
                // `row == rowEnd` subsumes `stepR == 0`.
                //
                // Every segment that TERMINATED before picks the same axis it picked before, so no behaviour (and no
                // checksum/golden) moves: with X arrived the Z boundary was already strictly nearer in every
                // non-hanging case, and with Z arrived the remaining X boundary is at t ≤ 1 ≤ t_z, so X already won
                // the comparison. The walk now provably ends after exactly spanC + spanR steps.
                bool advanceX = col != colEnd && (row == rowEnd || ax * adz <= az * adx);
                if (advanceX) { col += stepC; ax += CELL_RAW; }
                else          { row += stepR; az += CELL_RAW; }

                int c = col < 0 ? 0 : (col > GRID_SIZE - 1 ? GRID_SIZE - 1 : (int)col);
                int r = row < 0 ? 0 : (row > GRID_SIZE - 1 ? GRID_SIZE - 1 : (int)row);
                int idx = r * GRID_SIZE + c;
                if (idx != fromCell && Blocked[idx]) return true;
            }
            return false;
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
        /// the terrain height at the cell centre and at ALL FOUR of its −X / +X / −Z / +Z neighbours (2 world units
        /// apart) and block the cell when the max neighbour rise/run reaches <paramref name="threshold"/> (world Y per
        /// world unit). Pure <see cref="Fixed"/> math over the clamped <see cref="ElevationGrid.Sample"/> lookup —
        /// byte-identical across platforms and recomputed identically on every load, so the derived cells need not
        /// persist. Returns true if any cell was newly derived. Null grid / non-positive threshold ⇒ no derivation.
        ///
        /// <para><b>DW-149 — why all four neighbours.</b> Sampling only the FORWARD (+X / +Z) neighbours made the
        /// derivation directionally asymmetric: the far EAST column and far SOUTH row could never auto-block, because
        /// <see cref="ElevationGrid.Sample"/> CLAMPS past the last column/row so their forward neighbour returns the
        /// cell's own height (rise 0) no matter how steep the terrain actually is there. It also landed every derived
        /// cliff wall ONE CELL to the low side — only the cell whose forward neighbour was up on the plateau blocked,
        /// never the cell perched on the plateau edge itself. Taking the MAX over all four neighbours makes a cliff
        /// block symmetrically from both sides and gives the edge cells a neighbour that is genuinely off-cell.</para>
        ///
        /// <para>A neighbour whose sample CLAMPS back onto the centre's own elevation cell contributes rise 0, which
        /// can never win a max — so the "skip clamp-equal neighbours" refinement is a no-op under this formulation and
        /// is deliberately not coded (it would only matter for a central-difference average). A neighbour that clamps
        /// to a nearer-than-<c>run</c> cell divides its rise by the full run, i.e. UNDER-estimates the slope: the
        /// conservative direction (it never fabricates a blocked cell at the map edge).</para>
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
                    // DW-149: max rise over all FOUR neighbours (fixed order −X, +X, −Z, +Z — a max is
                    // order-independent, but the fixed order keeps the read byte-identical for a reviewer).
                    Fixed rise = AbsFixed(elev.Sample(cx - run, cz) - h0);
                    rise = MaxFixed(rise, AbsFixed(elev.Sample(cx + run, cz) - h0));
                    rise = MaxFixed(rise, AbsFixed(elev.Sample(cx, cz - run) - h0));
                    rise = MaxFixed(rise, AbsFixed(elev.Sample(cx, cz + run) - h0));
                    // slope = rise / run ≥ threshold  ⇔  rise ≥ threshold * run (avoids a divide, same Fixed result).
                    if (rise.Raw >= (threshold * run).Raw) { mask[idx] = true; any = true; }
                }
            }
            return any;
        }

        /// <summary>
        /// Story 6.5 — resolve the load-time union grid from the authored PAINTED layer plus optional slope-derived
        /// cells and (Story 6.6) an optional pre-built <paramref name="extraBlocked"/> mask (blocking-prop + water
        /// footprint cells). Decodes <paramref name="paintedBase64"/>, when <paramref name="slopeAutoBlock"/> is on
        /// with a positive <paramref name="slopeThreshold"/> and an <paramref name="elev"/> grid ORs slope-derived
        /// steep cells in, then ORs <paramref name="extraBlocked"/> in. Returns null when NOTHING is blocked (the
        /// flat/legacy common case — a null grid keeps every downstream consumer a byte-identical no-op). Pure /
        /// Godot-free so the decode→derive→union decision is Tier-1 testable independent of the Godot load phase,
        /// which only fans this result out to its sim sinks.
        /// </summary>
        public static PathabilityGrid? Resolve(string? paintedBase64, bool slopeAutoBlock, Fixed slopeThreshold, ElevationGrid? elev, bool[]? extraBlocked = null)
        {
            bool[] mask = FromBase64(paintedBase64);
            bool anyPainted = false;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) { anyPainted = true; break; }

            bool anyDerived = false;
            if (slopeAutoBlock && elev != null && slopeThreshold.Raw > 0)
                anyDerived = DeriveSlopeBlockedInto(mask, elev, slopeThreshold);

            // Story 6.6: OR the blocking-prop + water footprint mask into the SAME union so its cells route the flow
            // field, block the sim tick, and fold into the hash identically to painted cells. A moved/deleted prop or
            // removed water volume un-stamps for free because this whole grid is rebuilt from source every load.
            bool anyExtra = false;
            if (extraBlocked != null)
            {
                int n = Math.Min(extraBlocked.Length, mask.Length);
                for (int i = 0; i < n; i++) if (extraBlocked[i]) { mask[i] = true; anyExtra = true; }
            }

            return (anyPainted || anyDerived || anyExtra) ? new PathabilityGrid(mask) : null;
        }

        // ── Story 6.6: blocking-prop / water footprint derivation (the ONE Godot-free derivation shared by the
        //    load-time union, the CanonicalModelHash fold, and the ScenarioValidator blocked-cell check) ──────────

        /// <summary>
        /// Story 6.6 — stamp a blocking prop's single-cell footprint (the cell containing world (<paramref name="x"/>,
        /// <paramref name="z"/>) via <see cref="FlowField.WorldToCell"/>) into <paramref name="mask"/>. Clamped integer
        /// cell — an out-of-grid coordinate clamps to the nearest edge cell, never throws.
        /// </summary>
        public static void StampPropInto(bool[] mask, Fixed x, Fixed z)
        {
            if (mask == null || mask.Length != CELL_COUNT) return;
            FlowField.WorldToCell(x, z, out int col, out int row);
            mask[row * GRID_SIZE + col] = true;
        }

        /// <summary>
        /// Story 6.6 — stamp a water volume's rectangular footprint into <paramref name="mask"/>: every cell whose
        /// centre-domain overlaps the axis-aligned rect [<paramref name="x"/>, x+<paramref name="w"/>] ×
        /// [<paramref name="z"/>, z+<paramref name="h"/>], resolved through <see cref="FlowField.WorldToCell"/> (the
        /// SAME 128²/2-unit/±128 mapping the sim enforces). Both corners clamp into the grid — a degenerate/negative
        /// extent stamps at least the origin cell, never throws.
        /// </summary>
        public static void StampWaterInto(bool[] mask, Fixed x, Fixed z, Fixed w, Fixed h)
        {
            if (mask == null || mask.Length != CELL_COUNT) return;
            Fixed maxX = w.Raw > 0 ? x + w : x;
            Fixed maxZ = h.Raw > 0 ? z + h : z;
            FlowField.WorldToCell(x, z, out int c0, out int r0);
            FlowField.WorldToCell(maxX, maxZ, out int c1, out int r1);
            if (c1 < c0) { (c0, c1) = (c1, c0); }
            if (r1 < r0) { (r0, r1) = (r1, r0); }
            for (int row = r0; row <= r1; row++)
                for (int col = c0; col <= c1; col++)
                    mask[row * GRID_SIZE + col] = true;
        }

        /// <summary>
        /// Story 6.6 (review V1) — THE single Godot-free derivation of the blocking-prop + water footprint mask (or
        /// <c>null</c> when nothing blocks). Every <c>blocks_pathing</c> prop stamps one cell and every water volume
        /// stamps its rect, both through <see cref="StampPropInto"/>/<see cref="StampWaterInto"/>. All three consumers
        /// call THIS method so the runtime <see cref="PathabilityGrid"/> the sim routes on, the
        /// <see cref="ProjectChimera.Core.Definitions.CanonicalModelHash"/> handshake fold, and the
        /// <see cref="ProjectChimera.Core.Definitions.ScenarioValidator"/> fail-closed check can never disagree on
        /// which cells are blocked (the pre-fix bug: three hand-copies of this loop that could silently drift, shipping
        /// a wrong-but-deterministic map). Order-independent (a mask union); iterates props then water.
        /// </summary>
        public static bool[]? BuildBlockingFootprint(ScenarioProp[]? props, ScenarioWater[]? water)
        {
            bool[]? mask = null;
            if (props != null)
                foreach (ScenarioProp p in props)
                    if (p is { BlocksPathing: true })
                    {
                        mask ??= new bool[CELL_COUNT];
                        StampPropInto(mask, Fixed.FromFloat(p.X), Fixed.FromFloat(p.Z));
                    }
            if (water != null)
                foreach (ScenarioWater w in water)
                    if (w != null)
                    {
                        mask ??= new bool[CELL_COUNT];
                        StampWaterInto(mask, Fixed.FromFloat(w.X), Fixed.FromFloat(w.Z), Fixed.FromFloat(w.W), Fixed.FromFloat(w.H));
                    }
            return mask;
        }

        private static Fixed AbsFixed(Fixed v) => v.Raw < 0 ? -v : v;

        private static Fixed MaxFixed(Fixed a, Fixed b) => a.Raw >= b.Raw ? a : b;

        // ── Private ───────────────────────────────────────────────────────────

        // ── DW-147 swept-cell traversal constants ────────────────────────────

        /// <summary>Raw-<see cref="Fixed"/> shift that maps world XZ into non-negative grid space (world −128 ⇒ 0),
        /// mirroring <see cref="FlowField.WorldToCell"/>'s <c>+ WORLD_HALF_INT</c>.</summary>
        private const long GRID_ORIGIN_RAW = (long)FlowField.WORLD_HALF_INT << Fixed.FRACTIONAL_BITS;

        /// <summary>Raw-<see cref="Fixed"/> width of one grid cell (<see cref="FlowField.CELL_SIZE_WORLD"/> world units).</summary>
        private const long CELL_RAW = (long)FlowField.CELL_SIZE_WORLD << Fixed.FRACTIONAL_BITS;

        /// <summary>Cell budget for ONE swept step. A straight segment crosses at most <see cref="GRID_SIZE"/> column
        /// plus <see cref="GRID_SIZE"/> row boundaries, so anything beyond this spans more than the whole map in a
        /// single tick — not a legitimate integration step. Bounds the walk AND keeps the cross-multiplied ordering
        /// comparison inside <see cref="long"/> range.</summary>
        private const int MAX_SWEPT_CELLS = 2 * GRID_SIZE;

        /// <summary>Integer FLOOR division (C# <c>/</c> truncates toward zero, which would mis-place negative grid
        /// coordinates by one cell). <paramref name="divisor"/> is always positive here.</summary>
        private static long FloorDiv(long value, long divisor)
        {
            long q = value / divisor;
            if (value % divisor != 0 && (value < 0) != (divisor < 0)) q--;
            return q;
        }

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
