#nullable enable
using ProjectChimera.Multiplayer; // PacketType, TickCommandPacket, HaltReason
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 1.9a (AC4, D7) — the net-new server packet builders. DesyncAlert is 9 bytes (type + tick + canonicalHash,
    /// mirroring Checksum); Halt is 6 bytes (type + tick + reason). Both round-trip via their readers; truncated or
    /// wrong-type buffers parse to false. 32-bit width throughout — the wire is never widened (D12).
    /// </summary>
    public class ServerPacketTests
    {
        [Theory]
        [InlineData(0u, 0u)]
        [InlineData(1u, 0xDEADBEEFu)]
        [InlineData(4294967295u, 4294967295u)] // uint.MaxValue both fields
        public void DesyncAlert_RoundTrips(uint tick, uint canonical)
        {
            byte[] b = TickCommandPacket.MakeDesyncAlert(tick, canonical);
            Assert.Equal(9, b.Length);
            Assert.Equal((byte)PacketType.DesyncAlert, b[0]);

            Assert.True(TickCommandPacket.TryReadDesyncAlert(b, b.Length, out uint t, out uint c));
            Assert.Equal(tick, t);
            Assert.Equal(canonical, c);
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(12345u)]
        [InlineData(4294967295u)]
        public void Halt_RoundTrips(uint tick)
        {
            byte[] b = TickCommandPacket.MakeHalt(tick, HaltReason.NoMajority);
            Assert.Equal(6, b.Length);
            Assert.Equal((byte)PacketType.Halt, b[0]);

            Assert.True(TickCommandPacket.TryReadHalt(b, b.Length, out uint t, out HaltReason r));
            Assert.Equal(tick, t);
            Assert.Equal(HaltReason.NoMajority, r);
        }

        [Fact]
        public void TruncatedBuffers_ReturnFalse()
        {
            byte[] alert = TickCommandPacket.MakeDesyncAlert(5u, 9u);
            Assert.False(TickCommandPacket.TryReadDesyncAlert(alert, 8, out _, out _)); // len < 9

            byte[] halt = TickCommandPacket.MakeHalt(5u, HaltReason.NoMajority);
            Assert.False(TickCommandPacket.TryReadHalt(halt, 5, out _, out _)); // len < 6
        }

        [Fact]
        public void WrongType_ReturnsFalse()
        {
            // A full-length DesyncAlert is not a Halt (type byte differs) and vice-versa.
            byte[] alert = TickCommandPacket.MakeDesyncAlert(5u, 9u);
            Assert.False(TickCommandPacket.TryReadHalt(alert, alert.Length, out _, out _));

            // A 9-byte Checksum packet is not a DesyncAlert despite the matching length.
            var checksumBuf = new byte[9];
            TickCommandPacket.WriteChecksum(checksumBuf, 5u, 9u);
            Assert.False(TickCommandPacket.TryReadDesyncAlert(checksumBuf, checksumBuf.Length, out _, out _));
        }

        // ── Story 9.6: DropDirective / DropAck (type + faction + applyAtTick = 6 bytes) ──────────────────────

        [Theory]
        [InlineData((byte)0, 0u)]
        [InlineData((byte)2, 12345u)]
        [InlineData((byte)8, 4294967295u)]
        public void DropDirective_RoundTrips(byte faction, uint applyAtTick)
        {
            byte[] b = TickCommandPacket.MakeDropDirective(faction, applyAtTick);
            Assert.Equal(6, b.Length);
            Assert.Equal((byte)PacketType.DropDirective, b[0]);

            Assert.True(TickCommandPacket.TryReadDropDirective(b, b.Length, out byte f, out uint t));
            Assert.Equal(faction, f);
            Assert.Equal(applyAtTick, t);
        }

        [Theory]
        [InlineData((byte)1, 0u)]
        [InlineData((byte)3, 99u)]
        [InlineData((byte)7, 4294967295u)]
        public void DropAck_RoundTrips(byte faction, uint applyAtTick)
        {
            byte[] b = TickCommandPacket.MakeDropAck(faction, applyAtTick);
            Assert.Equal(6, b.Length);
            Assert.Equal((byte)PacketType.DropAck, b[0]);

            Assert.True(TickCommandPacket.TryReadDropAck(b, b.Length, out byte f, out uint t));
            Assert.Equal(faction, f);
            Assert.Equal(applyAtTick, t);
        }

        [Fact]
        public void Drop_TruncatedAndWrongType_ReturnFalse()
        {
            byte[] dir = TickCommandPacket.MakeDropDirective(2, 42u);
            Assert.False(TickCommandPacket.TryReadDropDirective(dir, 5, out _, out _)); // len < 6
            Assert.False(TickCommandPacket.TryReadDropAck(dir, dir.Length, out _, out _)); // DropDirective is not a DropAck

            byte[] ack = TickCommandPacket.MakeDropAck(2, 42u);
            Assert.False(TickCommandPacket.TryReadDropAck(ack, 5, out _, out _)); // len < 6
            Assert.False(TickCommandPacket.TryReadDropDirective(ack, ack.Length, out _, out _)); // DropAck is not a DropDirective

            // The 0x16/0x44 discriminators are distinct from the 0x15/0x43 delay pair (no accidental alias).
            byte[] delayDir = TickCommandPacket.MakeDelayDirective(4, 42u);
            Assert.False(TickCommandPacket.TryReadDropDirective(delayDir, delayDir.Length, out _, out _));
        }
    }
}
