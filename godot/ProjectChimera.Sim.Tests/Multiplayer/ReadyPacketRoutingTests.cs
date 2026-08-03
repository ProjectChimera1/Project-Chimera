#nullable enable
using ProjectChimera.Multiplayer; // ReadyPacketRouting, ReadyRouteDecision, TickCommandPacket
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-360 — the Godot-free Ready-packet ROUTING (<see cref="ReadyPacketRouting"/>) extracted from
    /// <c>LobbyUi</c>'s Ready handler. <c>HandshakeGateTests</c> pins the pure <c>CheckStart</c> decision; these
    /// pin the WIRING around it that was previously verified only by diff inspection: the parsed flag must carry
    /// <c>TryReadReady</c>'s real verdict (never a constant true), the local/peer hash argument slots must not be
    /// swapped, a version-mismatched peer blocks before the hash gate, and a blocked decision can never mark the
    /// peer ready. Every packet here is built with the REAL <see cref="TickCommandPacket.MakeReady"/> codec, so
    /// the route is exercised end-to-end from raw bytes.
    /// </summary>
    public class ReadyPacketRoutingTests
    {
        private const ulong Hash = 0xDEADBEEF_CAFEF00DUL;

        private static ReadyRouteDecision Route(byte[] pkt, int len, ulong localHash,
            string? breakdown = null, bool isHost = true)
            => ReadyPacketRouting.Route(pkt, len, localHash, breakdown, isHost);

        // ── The allow path ────────────────────────────────────────────────────────

        [Fact]
        public void MatchingHashes_RightVersion_ReadiesThePeer_AtSlot1FromTheHostView()
        {
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, Hash);
            ReadyRouteDecision d = Route(pkt, pkt.Length, Hash, isHost: true);

            Assert.True(d.PeerReady);
            Assert.Equal(1, d.PeerSlot); // the ready peer is slot 1 from the host's view
            Assert.Null(d.BlockReason);
        }

        [Fact]
        public void MatchingHashes_RightVersion_ReadiesThePeer_AtSlot0FromTheJoinerView()
        {
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, Hash);
            ReadyRouteDecision d = Route(pkt, pkt.Length, Hash, isHost: false);

            Assert.True(d.PeerReady);
            Assert.Equal(0, d.PeerSlot); // the ready peer is slot 0 (the host) from the joiner's view
            Assert.Null(d.BlockReason);
        }

        // ── The parsed flag must carry the REAL parse verdict (fail-closed) ───────

        [Fact]
        public void TruncatedReadyPayload_DoesNotReadyThePeer()
        {
            // The regression this pins: a handler that passes peerHashParsed:true unconditionally would let an
            // unreadable payload through the gate. A one-byte-short Ready must route parsed:false → the hash-0
            // fail-closed block — even though the residual bytes carry a perfectly matching hash.
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, Hash);
            ReadyRouteDecision d = Route(pkt, pkt.Length - 1, Hash);

            Assert.False(d.PeerReady);
            Assert.Equal(-1, d.PeerSlot);
            Assert.NotNull(d.BlockReason);
            Assert.Contains("not computed", d.BlockReason);
        }

        [Fact]
        public void WrongTypePacket_DoesNotReadyThePeer()
        {
            // A non-Ready packet routed here (e.g. a Hello) parses false and blocks — never readies.
            byte[] hello = TickCommandPacket.MakeHello();
            ReadyRouteDecision d = Route(hello, hello.Length, Hash);

            Assert.False(d.PeerReady);
            Assert.Contains("not computed", d.BlockReason!);
        }

        // ── The hash argument slots must not be swapped ───────────────────────────

        [Fact]
        public void HashArgumentSlots_AreNotSwapped_LocalIsYours_PeerIsTheirs()
        {
            // CheckStart's block text attributes its FIRST argument as "Your match" and its second as "Peer
            // match". Feed distinct values through the route and assert each lands in its own line — a swapped
            // marshalling (CheckStart(peerHash, localHash, …)) attributes them backwards and fails here.
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, 0x00000000000000BBUL);
            ReadyRouteDecision d = Route(pkt, pkt.Length, 0x00000000000000AAUL);

            Assert.False(d.PeerReady);
            Assert.Contains("Your match: 0x00000000000000AA", d.BlockReason!);
            Assert.Contains("Peer match: 0x00000000000000BB", d.BlockReason!);
        }

        // ── Fail-closed hash-0 posture survives the routing ───────────────────────

        [Fact]
        public void LocalHashZero_Blocks_EvenWithAValidPeerPayload()
        {
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, Hash);
            ReadyRouteDecision d = Route(pkt, pkt.Length, 0UL);

            Assert.False(d.PeerReady);
            Assert.Contains("not computed", d.BlockReason!);
        }

        [Fact]
        public void PeerHashZero_Blocks()
        {
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, 0UL);
            ReadyRouteDecision d = Route(pkt, pkt.Length, Hash);

            Assert.False(d.PeerReady);
            Assert.Contains("not computed", d.BlockReason!);
        }

        // ── The protocol-version gate fires before the hash gate ──────────────────

        [Fact]
        public void VersionMismatch_Blocks_EvenWhenTheHashesMatch()
        {
            ushort wrongVersion = (ushort)(TickCommandPacket.PROTOCOL_VERSION + 1);
            byte[] pkt = TickCommandPacket.MakeReady(wrongVersion, Hash);
            ReadyRouteDecision d = Route(pkt, pkt.Length, Hash);

            Assert.False(d.PeerReady);
            Assert.Equal(-1, d.PeerSlot);
            Assert.Contains("protocol version mismatch", d.BlockReason!);
            Assert.Contains($"Peer protocol: v{wrongVersion}", d.BlockReason!);
            Assert.Contains($"Your protocol: v{TickCommandPacket.PROTOCOL_VERSION}", d.BlockReason!);
        }

        // ── Story 9.16: the local content breakdown rides the routed block ────────

        [Fact]
        public void LocalBreakdown_IsSurfacedOnARoutedBlock()
        {
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, 0x2222UL);
            ReadyRouteDecision d = Route(pkt, pkt.Length, 0x1111UL, breakdown: "factions=0x1 items=0x2");

            Assert.False(d.PeerReady);
            Assert.Contains("LOCAL content fingerprint", d.BlockReason!);
            Assert.Contains("factions=", d.BlockReason!);
        }

        [Fact]
        public void MatchingHashes_WithABreakdownProvided_StillAllow()
        {
            byte[] pkt = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, Hash);
            ReadyRouteDecision d = Route(pkt, pkt.Length, Hash, breakdown: "factions=0x1");

            Assert.True(d.PeerReady);
            Assert.Null(d.BlockReason);
        }
    }
}
