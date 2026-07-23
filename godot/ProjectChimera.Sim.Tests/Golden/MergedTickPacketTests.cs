#nullable enable
using ProjectChimera.Core;         // Faction, UnitOrder, UnitCommand, Fixed
using ProjectChimera.Multiplayer;  // PacketType, TickCommandPacket, MergedTickPacket
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.3 — the <see cref="MergedTickPacket"/> wire codec: round-trip of multi-faction sub-bundles, and
    /// the read-side DROP-NOT-CLAMP ceilings (a breach of the sub-bundle-count / per-sub-bundle order-count /
    /// total-byte ceiling rejects the WHOLE packet, never truncates). Also pins the chat re-stamp round-trip (the
    /// server rebuilds chat from the slot's authoritative faction, so a spoofed byte can never survive).
    /// </summary>
    public class MergedTickPacketTests
    {
        private static UnitOrder Move(int unitId, int rx, int rz) =>
            new UnitOrder(unitId, UnitCommand.Move, Fixed.FromRaw(rx), Fixed.FromRaw(rz));

        // Scratch sized to the codec's contract.
        private static (Faction[] f, int[] c, UnitOrder[] o) Scratch() => (
            new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
            new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
            new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS]);

        [Fact]
        public void RoundTrips_TwoAscendingSubBundles()
        {
            var factions = new[] { Faction.Player1, Faction.Player2 };
            var counts   = new[] { 2, 1 };
            var orders   = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            orders[0 * TickCommandPacket.MAX_ORDERS + 0] = Move(10, 100, -50);
            orders[0 * TickCommandPacket.MAX_ORDERS + 1] = Move(11, 7, 8);
            orders[1 * TickCommandPacket.MAX_ORDERS + 0] = Move(20, -3, 4);

            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            int len = MergedTickPacket.Write(buf, 4242u, factions, counts, orders, 2);

            Assert.Equal((byte)PacketType.TickCommandsMerged, buf[0]);

            var (of, oc, oo) = Scratch();
            Assert.True(MergedTickPacket.TryRead(buf, len, out uint tick, of, oc, oo, out int n));
            Assert.Equal(4242u, tick);
            Assert.Equal(2, n);
            Assert.Equal(Faction.Player1, of[0]);
            Assert.Equal(Faction.Player2, of[1]);
            Assert.Equal(2, oc[0]);
            Assert.Equal(1, oc[1]);
            Assert.Equal((ushort)10, oo[0].UnitId);
            Assert.Equal(100, oo[0].TargetX);
            Assert.Equal(-50, oo[0].TargetZ);
            Assert.Equal((ushort)20, oo[TickCommandPacket.MAX_ORDERS + 0].UnitId);
        }

        [Fact]
        public void EmptyMerged_RoundTrips_ZeroSubBundles()
        {
            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            int len = MergedTickPacket.Write(buf, 0u, new Faction[1], new int[1], new UnitOrder[TickCommandPacket.MAX_ORDERS], 0);
            Assert.Equal(MergedTickPacket.HEADER_BYTES, len);

            var (of, oc, oo) = Scratch();
            Assert.True(MergedTickPacket.TryRead(buf, len, out _, of, oc, oo, out int n));
            Assert.Equal(0, n);
        }

        [Fact]
        public void TryRead_RejectsWrongType()
        {
            var (of, oc, oo) = Scratch();
            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            buf[0] = (byte)PacketType.TickCommands; // not a merged packet
            Assert.False(MergedTickPacket.TryRead(buf, MergedTickPacket.HEADER_BYTES, out _, of, oc, oo, out int n));
            Assert.Equal(0, n);
        }

        [Fact]
        public void TryRead_RejectsTruncatedHeader()
        {
            var (of, oc, oo) = Scratch();
            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            buf[0] = (byte)PacketType.TickCommandsMerged;
            Assert.False(MergedTickPacket.TryRead(buf, MergedTickPacket.HEADER_BYTES - 1, out _, of, oc, oo, out _));
        }

        [Fact]
        public void TryRead_RejectsSubBundleCountCeiling_DropNotClamp()
        {
            // Hand-craft a header claiming MORE sub-bundles than the ceiling → whole packet rejected.
            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            buf[0] = (byte)PacketType.TickCommandsMerged;
            buf[5] = (byte)(MergedTickPacket.MERGED_MAX_SUBBUNDLES + 1);

            var (of, oc, oo) = Scratch();
            Assert.False(MergedTickPacket.TryRead(buf, MergedTickPacket.MERGED_MAX_BYTES, out _, of, oc, oo, out int n));
            Assert.Equal(0, n);
        }

        [Fact]
        public void TryRead_RejectsPerSubBundleOverCount_DropNotClamp()
        {
            // One sub-bundle claiming orderCount > MAX_ORDERS → reject (never clamp to MAX_ORDERS).
            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            buf[0] = (byte)PacketType.TickCommandsMerged;
            buf[5] = 1;                                   // one sub-bundle
            buf[6] = (byte)Faction.Player1;              // its faction
            buf[7] = (byte)(TickCommandPacket.MAX_ORDERS + 1); // over-count

            var (of, oc, oo) = Scratch();
            Assert.False(MergedTickPacket.TryRead(buf, MergedTickPacket.MERGED_MAX_BYTES, out _, of, oc, oo, out _));
        }

        [Fact]
        public void TryRead_RejectsLengthPastByteCeiling()
        {
            var (of, oc, oo) = Scratch();
            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES + 8];
            buf[0] = (byte)PacketType.TickCommandsMerged;
            // A declared len beyond MERGED_MAX_BYTES is rejected up front.
            Assert.False(MergedTickPacket.TryRead(buf, MergedTickPacket.MERGED_MAX_BYTES + 1, out _, of, oc, oo, out _));
        }

        [Fact]
        public void FullCapacity_FitsExactlyInByteCeiling()
        {
            // MERGED_MAX_SUBBUNDLES full (MAX_ORDERS) bundles must serialise to exactly MERGED_MAX_BYTES — proving
            // legitimate worst-case N=8 traffic is never dropped by the byte ceiling.
            int n = MergedTickPacket.MERGED_MAX_SUBBUNDLES;
            var factions = new Faction[n];
            var counts   = new int[n];
            for (int b = 0; b < n; b++) { factions[b] = (Faction)(b + 1); counts[b] = TickCommandPacket.MAX_ORDERS; }
            var orders = new UnitOrder[n * TickCommandPacket.MAX_ORDERS];

            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            int len = MergedTickPacket.Write(buf, 1u, factions, counts, orders, n);
            Assert.Equal(MergedTickPacket.MERGED_MAX_BYTES, len);

            var (of, oc, oo) = Scratch();
            Assert.True(MergedTickPacket.TryRead(buf, len, out _, of, oc, oo, out int rn));
            Assert.Equal(n, rn);
        }

        [Fact]
        public void TryPeekTick_ReadsSameTickAsFullDecode()
        {
            // TryPeekTick is the client ring KEY (LockstepManager slots merged bytes by its return). A byte-order
            // slip or a too-short accept here would mis-key the ring → stall/mis-apply, with nothing else catching
            // it. Pin: the cheap header peek agrees with the full decode, and rejects wrong-type / truncated.
            var factions = new[] { Faction.Player1, Faction.Player2 };
            var counts   = new[] { 1, 1 };
            var orders   = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            orders[0 * TickCommandPacket.MAX_ORDERS + 0] = Move(10, 1, 2);
            orders[1 * TickCommandPacket.MAX_ORDERS + 0] = Move(20, 3, 4);

            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            int len = MergedTickPacket.Write(buf, 0xDEADBEEFu, factions, counts, orders, 2);

            Assert.True(MergedTickPacket.TryPeekTick(buf, len, out uint peeked));
            Assert.Equal(0xDEADBEEFu, peeked);

            var (of, oc, oo) = Scratch();
            Assert.True(MergedTickPacket.TryRead(buf, len, out uint full, of, oc, oo, out _));
            Assert.Equal(full, peeked); // peek and full decode agree on the ring key

            // Rejects: wrong packet type, and a buffer shorter than the header.
            buf[0] = (byte)PacketType.TickCommands;
            Assert.False(MergedTickPacket.TryPeekTick(buf, len, out _));
            buf[0] = (byte)PacketType.TickCommandsMerged;
            Assert.False(MergedTickPacket.TryPeekTick(buf, MergedTickPacket.HEADER_BYTES - 1, out _));
        }

        [Fact]
        public void ChatReStamp_RoundTrips_FromSlotFaction()
        {
            // The server re-encodes chat from the slot's authoritative faction; a client's spoofed byte is discarded.
            // Simulate: a client claims Player5 but sits at slot 1 → server stamps SLOT_FACTION[1] == Player2.
            var slotFaction = new[] { Faction.Player1, Faction.Player2 };
            byte[] spoofed = TickCommandPacket.MakeChat(Faction.Player5, "gg");

            Assert.True(TickCommandPacket.TryReadChat(spoofed, spoofed.Length, out Faction claimed, out string msg));
            Assert.Equal(Faction.Player5, claimed); // the spoof as sent

            byte[] reStamped = TickCommandPacket.MakeChat(slotFaction[1], msg);
            Assert.True(TickCommandPacket.TryReadChat(reStamped, reStamped.Length, out Faction stamped, out string msg2));
            Assert.Equal(Faction.Player2, stamped); // authoritative slot faction, NOT the spoof
            Assert.Equal("gg", msg2);
        }
    }
}
