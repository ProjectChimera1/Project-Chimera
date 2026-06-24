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
    }
}
