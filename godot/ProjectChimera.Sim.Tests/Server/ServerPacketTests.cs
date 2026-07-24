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

        // ── Story 9.7: pre-match LobbyChat packet ──────────────────────────────────

        [Theory]
        [InlineData((byte)ProjectChimera.Core.Faction.Player1, "gg")]
        [InlineData((byte)ProjectChimera.Core.Faction.Player4, "hello there, 4-player lobby")]
        [InlineData((byte)ProjectChimera.Core.Faction.Neutral, "")]
        public void LobbyChat_RoundTrips(byte factionByte, string msg)
        {
            var faction = (ProjectChimera.Core.Faction)factionByte;
            byte[] b = TickCommandPacket.MakeLobbyChat(faction, msg);
            Assert.Equal((byte)PacketType.LobbyChat, b[0]);

            Assert.True(TickCommandPacket.TryReadLobbyChat(b, b.Length, out var f, out string m));
            Assert.Equal(faction, f);
            Assert.Equal(msg, m);
        }

        [Fact]
        public void LobbyChat_TruncatedOrWrongType_ParsesFalse()
        {
            byte[] b = TickCommandPacket.MakeLobbyChat(ProjectChimera.Core.Faction.Player2, "hi");
            Assert.False(TickCommandPacket.TryReadLobbyChat(b, 2, out _, out _)); // truncated (header needs 4)

            // A Chat packet is NOT a LobbyChat (distinct discriminator 0x20 vs 0x21).
            byte[] chat = TickCommandPacket.MakeChat(ProjectChimera.Core.Faction.Player1, "hi");
            Assert.False(TickCommandPacket.TryReadLobbyChat(chat, chat.Length, out _, out _));

            // P9: a valid packet whose DECLARED msgLen exceeds the PASSED len — the OOB guard for the GetString read.
            byte[] valid = TickCommandPacket.MakeLobbyChat(ProjectChimera.Core.Faction.Player1, "0123456789"); // 10 msg bytes → 14 total
            Assert.Equal(14, valid.Length);
            Assert.False(TickCommandPacket.TryReadLobbyChat(valid, 8, out _, out _)); // 8 < 4 + 10 → false, no OOB read
            Assert.True(TickCommandPacket.TryReadLobbyChat(valid, valid.Length, out _, out _)); // full len → ok
        }

        // ── Story 9.7 (P2): server→client LobbyRoster snapshot ─────────────────────

        [Fact]
        public void LobbyRoster_RoundTrips()
        {
            var occ = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];
            var rdy = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];
            occ[0] = true; rdy[0] = true;   // slot 0 occupied + ready
            occ[1] = true; rdy[1] = false;  // slot 1 occupied, not ready
            occ[2] = false;                 // slot 2 open
            byte[] b = TickCommandPacket.MakeLobbyRoster(3, occ, rdy);
            Assert.Equal((byte)PacketType.LobbyRoster, b[0]);

            var occOut = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];
            var rdyOut = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];
            Assert.True(TickCommandPacket.TryReadLobbyRoster(b, b.Length, out int n, occOut, rdyOut));
            Assert.Equal(3, n);
            Assert.True(occOut[0]);  Assert.True(rdyOut[0]);
            Assert.True(occOut[1]);  Assert.False(rdyOut[1]);
            Assert.False(occOut[2]); Assert.False(rdyOut[2]);
        }

        [Fact]
        public void LobbyRoster_TruncatedOrWrongType_ParsesFalse()
        {
            var occ = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];
            var rdy = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];
            byte[] b = TickCommandPacket.MakeLobbyRoster(4, occ, rdy);
            Assert.False(TickCommandPacket.TryReadLobbyRoster(b, 3, out _, occ, rdy)); // declared 4 slots, len too short
            byte[] chat = TickCommandPacket.MakeLobbyChat(ProjectChimera.Core.Faction.Player1, "x");
            Assert.False(TickCommandPacket.TryReadLobbyRoster(chat, chat.Length, out _, occ, rdy)); // wrong type
        }

        [Fact]
        public void LobbyChat_ClampsOverlongMessage()
        {
            string big = new string('x', 500);
            byte[] b = TickCommandPacket.MakeLobbyChat(ProjectChimera.Core.Faction.Player1, big);
            Assert.True(TickCommandPacket.TryReadLobbyChat(b, b.Length, out _, out string m));
            Assert.Equal(TickCommandPacket.MAX_CHAT_BYTES, m.Length);
        }
    }
}
