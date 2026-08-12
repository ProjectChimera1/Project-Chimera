#nullable enable
using ProjectChimera.Core;         // Faction
using ProjectChimera.Multiplayer;  // PacketType, TickCommandPacket, HaltReason
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 9.4 — the net-new / widened wire codecs: the widened Ready packet (protocol version + 64-bit
    /// match-agreement hash), the Hello version exposure, the server-dictated <see cref="PacketType.DelayDirective"/>
    /// and client <see cref="PacketType.DelayAck"/>, and the two new <see cref="HaltReason"/> members. Every codec
    /// round-trips byte-identically; truncated / wrong-type buffers fail closed. The I/O-matrix "Ready wire
    /// round-trip" + directive/ack rows, pinned Godot-free.
    /// </summary>
    public class Story94WireTests
    {
        // ── PROTOCOL_VERSION bump ──────────────────────────────────────────────────

        [Fact]
        public void ProtocolVersion_IsCurrent()
            // Story 9.4 bumped this to 2; Story 15.11 (DW-280) bumped it to 3 for the widened 12-byte UnitOrder stride.
            => Assert.Equal(5, TickCommandPacket.PROTOCOL_VERSION); // DW-945: 4→5 (14-byte stride, packed SUBJECT ref; 15-23 took it to 4)

        // ── Widened Ready packet ───────────────────────────────────────────────────

        [Theory]
        [InlineData((ushort)2, 0UL)]
        [InlineData((ushort)2, 0xC0FFEE_C0FFEEUL)]
        [InlineData((ushort)1, 0xDEADBEEF_CAFEF00DUL)]
        [InlineData((ushort)65535, 0xFFFFFFFF_FFFFFFFFUL)]
        public void Ready_RoundTrips_VersionAndHash(ushort version, ulong hash)
        {
            byte[] b = TickCommandPacket.MakeReady(version, hash);
            Assert.Equal(11, b.Length);
            Assert.Equal((byte)PacketType.Ready, b[0]);

            Assert.True(TickCommandPacket.TryReadReady(b, b.Length, out ushort v, out ulong h));
            Assert.Equal(version, v);
            Assert.Equal(hash, h);
        }

        [Fact]
        public void Ready_ShortOrWrongType_FailsClosed()
        {
            byte[] b = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, 0xABCDUL);
            // Undersized (the old 5-byte layout) fails closed → version 0 / hash 0 (routes into the block gate).
            Assert.False(TickCommandPacket.TryReadReady(b, 10, out ushort v, out ulong h));
            Assert.Equal(0, v);
            Assert.Equal(0UL, h);
            // A wrong type byte at full length is rejected too.
            var notReady = new byte[11];
            notReady[0] = (byte)PacketType.StartGame;
            Assert.False(TickCommandPacket.TryReadReady(notReady, notReady.Length, out _, out _));
        }

        // ── Hello version exposure ─────────────────────────────────────────────────

        [Fact]
        public void Hello_ExposesProtocolVersion_AndFaction()
        {
            byte[] b = TickCommandPacket.MakeHello(Faction.Player2);
            Assert.True(TickCommandPacket.TryReadHello(b, b.Length, out Faction f, out ushort version));
            Assert.Equal(Faction.Player2, f);
            Assert.Equal(TickCommandPacket.PROTOCOL_VERSION, version);
            // The 2-arg overload still works (delegates to the version-exposing one).
            Assert.True(TickCommandPacket.TryReadHello(b, b.Length, out Faction f2));
            Assert.Equal(Faction.Player2, f2);
        }

        [Fact]
        public void Hello_ForgedOldVersion_ReadsBackTheForgedValue_ForFailClosedValidation()
        {
            // A v1 peer's Hello carries version 1 — the client's fail-closed check compares it to PROTOCOL_VERSION.
            var b = new byte[] { (byte)PacketType.Hello, 1, 0, (byte)Faction.Player1 };
            Assert.True(TickCommandPacket.TryReadHello(b, b.Length, out _, out ushort version));
            Assert.Equal(1, version);
            Assert.NotEqual(TickCommandPacket.PROTOCOL_VERSION, version);
        }

        // ── DelayDirective (server → client) ───────────────────────────────────────

        [Theory]
        [InlineData((byte)2, 0u)]
        [InlineData((byte)12, 12345u)]
        [InlineData((byte)200, 4294967295u)] // forged out-of-range byte survives the wire; the client clamps on receipt
        public void DelayDirective_RoundTrips(byte delay, uint applyAtTick)
        {
            byte[] b = TickCommandPacket.MakeDelayDirective(delay, applyAtTick);
            Assert.Equal(6, b.Length);
            Assert.Equal((byte)PacketType.DelayDirective, b[0]);

            Assert.True(TickCommandPacket.TryReadDelayDirective(b, b.Length, out byte d, out uint at));
            Assert.Equal(delay, d);
            Assert.Equal(applyAtTick, at);
        }

        // ── DelayAck (client → server) ─────────────────────────────────────────────

        [Theory]
        [InlineData((byte)2, 0u)]
        [InlineData((byte)12, 999u)]
        public void DelayAck_RoundTrips(byte delay, uint applyAtTick)
        {
            byte[] b = TickCommandPacket.MakeDelayAck(delay, applyAtTick);
            Assert.Equal(6, b.Length);
            Assert.Equal((byte)PacketType.DelayAck, b[0]);

            Assert.True(TickCommandPacket.TryReadDelayAck(b, b.Length, out byte d, out uint at));
            Assert.Equal(delay, d);
            Assert.Equal(applyAtTick, at);
        }

        [Fact]
        public void DirectiveAndAck_AreDistinctTypes_AndTruncationFailsClosed()
        {
            byte[] dir = TickCommandPacket.MakeDelayDirective(5, 100u);
            byte[] ack = TickCommandPacket.MakeDelayAck(5, 100u);
            // 0x15 vs 0x43 — a directive is not an ack and vice-versa.
            Assert.False(TickCommandPacket.TryReadDelayAck(dir, dir.Length, out _, out _));
            Assert.False(TickCommandPacket.TryReadDelayDirective(ack, ack.Length, out _, out _));
            // Truncated → false.
            Assert.False(TickCommandPacket.TryReadDelayDirective(dir, 5, out _, out _));
            Assert.False(TickCommandPacket.TryReadDelayAck(ack, 5, out _, out _));
        }

        // ── HaltReason additions ───────────────────────────────────────────────────

        [Theory]
        [InlineData(HaltReason.ProtocolMismatch)]
        [InlineData(HaltReason.StartStateDisagreement)]
        public void Halt_CarriesTheNewReasons(HaltReason reason)
        {
            byte[] b = TickCommandPacket.MakeHalt(0u, reason);
            Assert.True(TickCommandPacket.TryReadHalt(b, b.Length, out _, out HaltReason r));
            Assert.Equal(reason, r);
        }
    }
}
