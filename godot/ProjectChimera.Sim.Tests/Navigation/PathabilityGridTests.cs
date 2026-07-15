#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 6.5 — the Godot-free <see cref="PathabilityGrid"/>: clamped integer-cell lookup that mirrors
    /// <see cref="FlowField.WorldToCell"/> exactly, edge/degenerate safety, and the packing / digest round-trip the
    /// persistence + hash layers depend on.
    /// </summary>
    public class PathabilityGridTests
    {
        [Fact]
        public void IsBlocked_MatchesFlowFieldWorldToCell_ForBlockedCell()
        {
            // Block a single known cell; IsBlocked at any world XZ that maps to that cell (via the SAME WorldToCell)
            // must be true, and a neighbouring cell false — proving validator/sim/flow-field share one cell identity.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            const int GS = PathabilityGrid.GRID_SIZE;
            int col = 64, row = 70;
            mask[row * GS + col] = true;
            var grid = new PathabilityGrid(mask);

            // Cell (64,70) centre world = (col*2+1-128, row*2+1-128) = (1, 13).
            Assert.True(grid.IsBlocked(Fixed.FromInt(1), Fixed.FromInt(13)));
            // Parity check: the same mapping FlowField uses lands on the blocked index.
            FlowField.WorldToCell(Fixed.FromInt(1), Fixed.FromInt(13), out int c, out int r);
            Assert.Equal(col, c);
            Assert.Equal(row, r);
            // A world position one cell over (X≈3 ⇒ col 65) is NOT blocked.
            Assert.False(grid.IsBlocked(Fixed.FromInt(3), Fixed.FromInt(13)));
        }

        [Fact]
        public void IsBlocked_ClampsOutOfBounds_NoThrow()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            // Block the far corner cell (127,127); an XZ well beyond +128 clamps there and reads blocked.
            mask[127 * PathabilityGrid.GRID_SIZE + 127] = true;
            var grid = new PathabilityGrid(mask);
            Assert.True(grid.IsBlocked(Fixed.FromInt(9999), Fixed.FromInt(9999)));   // clamps to (127,127)
            // Block cell (0,0); a very negative XZ clamps to it.
            var mask2 = new bool[PathabilityGrid.CELL_COUNT];
            mask2[0] = true;
            Assert.True(new PathabilityGrid(mask2).IsBlocked(Fixed.FromInt(-9999), Fixed.FromInt(-9999)));
        }

        [Fact]
        public void Empty_And_Degenerate_AreAllClear()
        {
            Assert.False(PathabilityGrid.Empty.AnyBlocked);
            Assert.False(PathabilityGrid.Empty.IsBlocked(Fixed.Zero, Fixed.Zero));
            // Wrong-length input degrades to all-clear (never throws).
            var bad = new PathabilityGrid(new bool[10]);
            Assert.False(bad.AnyBlocked);
            Assert.Equal(PathabilityGrid.CELL_COUNT, bad.Blocked.Length);
        }

        [Fact]
        public void AnyBlocked_ReflectsMask()
        {
            Assert.False(new PathabilityGrid(new bool[PathabilityGrid.CELL_COUNT]).AnyBlocked);
            var m = new bool[PathabilityGrid.CELL_COUNT];
            m[500] = true;
            Assert.True(new PathabilityGrid(m).AnyBlocked);
        }

        [Fact]
        public void Pack_Unpack_RoundTrips()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            mask[0] = mask[7] = mask[8] = mask[63] = mask[64] = mask[16383] = true;
            byte[] packed = PathabilityGrid.Pack(mask);
            Assert.Equal(PathabilityGrid.PACKED_BYTES, packed.Length);
            bool[] back = PathabilityGrid.Unpack(packed);
            Assert.Equal(mask, back);
        }

        [Fact]
        public void ToBase64_AllClear_IsNull_ForKeyOmission()
        {
            Assert.Null(PathabilityGrid.ToBase64(new bool[PathabilityGrid.CELL_COUNT]));
            var m = new bool[PathabilityGrid.CELL_COUNT];
            m[100] = true;
            string? b64 = PathabilityGrid.ToBase64(m);
            Assert.NotNull(b64);
            Assert.Equal(m, PathabilityGrid.FromBase64(b64));
        }

        [Fact]
        public void FromBase64_NullOrMalformed_IsAllClear_NoThrow()
        {
            Assert.All(PathabilityGrid.FromBase64(null), b => Assert.False(b));
            Assert.All(PathabilityGrid.FromBase64(""), b => Assert.False(b));
            Assert.All(PathabilityGrid.FromBase64("not valid base64 %%%"), b => Assert.False(b));
        }

        [Fact]
        public void Digest_ZeroForAllClear_NonZeroForPainted_And_ConsistentAcrossForms()
        {
            Assert.Equal(0u, PathabilityGrid.DigestOfBase64(null));
            Assert.Equal(0u, PathabilityGrid.DigestOfBase64(""));
            Assert.Equal(0u, new PathabilityGrid(new bool[PathabilityGrid.CELL_COUNT]).Digest());

            var m = new bool[PathabilityGrid.CELL_COUNT];
            m[42] = m[9000] = true;
            var grid = new PathabilityGrid(m);
            string? b64 = PathabilityGrid.ToBase64(m);
            Assert.NotEqual(0u, grid.Digest());
            Assert.Equal(grid.Digest(), PathabilityGrid.DigestOfBase64(b64)); // instance ⇔ base64 digest agree
        }

        /// <summary>A 256×256 / 1-unit / ±128 elevation grid with a hard cliff at world X=0 (0 west, 10 east).</summary>
        private static ElevationGrid CliffAtX0()
        {
            const int N = 256;
            var heights = new Fixed[N * N];
            for (int row = 0; row < N; row++)
                for (int col = 0; col < N; col++)
                    heights[row * N + col] = col >= 128 ? Fixed.FromInt(10) : Fixed.Zero;
            return new ElevationGrid(heights, N, N, Fixed.FromInt(-128), Fixed.FromInt(-128), Fixed.One);
        }

        [Fact]
        public void Resolve_PaintedOnly_FlatNull_SlopeOnly_And_Both()
        {
            const int GS = PathabilityGrid.GRID_SIZE;
            int paintedIdx = 70 * GS + 10;   // a flat-terrain cell, away from the cliff column
            int cliffIdx   = 70 * GS + 63;   // the cliff-straddling column derived by slope

            // Flat/legacy: no paint, slope off ⇒ null grid (a byte-identical no-op for every downstream consumer).
            Assert.Null(PathabilityGrid.Resolve(null, false, Fixed.Zero, null));
            // Slope toggle on but a zero threshold is inert ⇒ still null.
            Assert.Null(PathabilityGrid.Resolve(null, true, Fixed.Zero, CliffAtX0()));

            var painted = new bool[PathabilityGrid.CELL_COUNT];
            painted[paintedIdx] = true;
            string b64 = PathabilityGrid.ToBase64(painted)!;

            // Painted-only: the painted cell is set and NOTHING is derived (no cliff cell) even with no elevation grid.
            var pg = PathabilityGrid.Resolve(b64, false, Fixed.Zero, null);
            Assert.NotNull(pg);
            Assert.True(pg!.Blocked[paintedIdx]);
            Assert.False(pg.Blocked[cliffIdx]);

            // Slope-only: no paint, slope on + cliff ⇒ derived cells only (the painted cell is NOT set).
            var slope = PathabilityGrid.Resolve(null, true, Fixed.One, CliffAtX0());
            Assert.NotNull(slope);
            Assert.True(slope!.Blocked[cliffIdx]);
            Assert.False(slope.Blocked[paintedIdx]);

            // Both: painted ∪ slope-derived — the union carries BOTH the painted cell and the derived cliff column.
            var both = PathabilityGrid.Resolve(b64, true, Fixed.One, CliffAtX0());
            Assert.NotNull(both);
            Assert.True(both!.Blocked[paintedIdx]);
            Assert.True(both.Blocked[cliffIdx]);
        }

        [Fact]
        public void DigestOfBase64_CanonicalizesNonCanonicalEncodings()
        {
            // Same logical mask, two byte-encodings: the canonical 2048-byte packed form and a 1-byte blob that
            // Unpack zero-extends to the identical mask. Both must digest EQUALLY and equal the instance Digest —
            // so a hand-authored non-canonical map never false-rejects at the handshake against a tool-saved one.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            mask[5] = true;                                                  // cell 5 → byte 0, bit 5 (0x20)
            var grid = new PathabilityGrid(mask);
            string canonical = PathabilityGrid.ToBase64(mask)!;             // 2048 bytes
            string shortBlob = System.Convert.ToBase64String(new byte[] { 0x20 }); // 1 byte, same cell 5
            Assert.Equal(grid.Digest(), PathabilityGrid.DigestOfBase64(canonical));
            Assert.Equal(grid.Digest(), PathabilityGrid.DigestOfBase64(shortBlob));
        }
    }
}
