#nullable enable
using ProjectChimera.Core;         // Faction
using ProjectChimera.Multiplayer;  // LobbyVersionGate, TickCommandPacket, PacketType
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-402 / DW-403 — the client-side PROTOCOL_VERSION gate (<see cref="LobbyVersionGate"/>), extracted from
    /// LobbyUi's inbound-Hello and peer-Ready version clauses so the D3.8 client-side closure finally has Tier-1
    /// coverage (the server-side twin, ServerLobbyPolicy.CheckStartStateAgreement, has been pinned since 9.4).
    /// Deleting either version clause in the gate now fails THIS suite instead of reopening the gap (a v1 client
    /// proceeding against a v2 peer) with a green run.
    ///
    /// DW-403 — the mismatch LATCHES: while blocked, peer Ready packets are refused fail-closed even with a
    /// matching version, and a SUBSEQUENTLY VALID Hello on the same connection recovers the lobby
    /// (<see cref="LobbyVersionGate.HelloVerdict.Recovered"/> — LobbyUi restores ready-ability and restarts the
    /// ready handshake from a clean slate). Reset (disconnect/cancel/close) clears the latch without counting as
    /// a recovery.
    /// </summary>
    public class LobbyVersionGateTests
    {
        private const ushort LOCAL = 2; // stands in for TickCommandPacket.PROTOCOL_VERSION (injectable ctor)

        private static LobbyVersionGate NewGate() => new(LOCAL);

        // ── DW-402: the inbound-Hello version clause ───────────────────────────────

        [Fact]
        public void Hello_MatchingVersion_Allows_AndDoesNotBlock()
        {
            var gate = NewGate();
            var v = gate.EvaluateHello(parsed: true, helloVersion: LOCAL);
            Assert.True(v.Allowed);
            Assert.False(v.Recovered);
            Assert.Null(v.BlockReason);
            Assert.False(gate.Blocked);
        }

        [Theory]
        [InlineData((ushort)1)]      // the archetypal old build (the D3.8 scenario)
        [InlineData((ushort)3)]      // a NEWER peer must block symmetrically
        [InlineData((ushort)65535)]
        public void Hello_MismatchedVersion_Blocks_WithBothVersionsInTheReason(ushort peerVer)
        {
            var gate = NewGate();
            var v = gate.EvaluateHello(parsed: true, helloVersion: peerVer);
            Assert.False(v.Allowed);
            Assert.NotNull(v.BlockReason);
            Assert.Contains("protocol version mismatch", v.BlockReason);
            Assert.Contains($"v{peerVer}", v.BlockReason);   // the server's version, surfaced for the human
            Assert.Contains($"v{LOCAL}", v.BlockReason);     // ours, beside it
            Assert.True(gate.Blocked);                       // DW-403: the mismatch LATCHES
        }

        [Fact]
        public void Hello_Unparseable_Blocks_FailClosed()
        {
            // TryReadHello fails on a short/wrong-type buffer and reads version back as 0 — a payload we cannot
            // read is never proof of a matching protocol, so it must block exactly like a mismatch.
            var gate = NewGate();
            var v = gate.EvaluateHello(parsed: false, helloVersion: 0);
            Assert.False(v.Allowed);
            Assert.NotNull(v.BlockReason);
            Assert.True(gate.Blocked);
        }

        // ── DW-402: the peer-Ready version clause ──────────────────────────────────

        [Fact]
        public void PeerReady_MatchingVersion_ProceedsToTheHashGate()
        {
            Assert.Null(NewGate().CheckPeerReady(parsed: true, peerVersion: LOCAL));
        }

        [Theory]
        [InlineData((ushort)1)]
        [InlineData((ushort)3)]
        public void PeerReady_MismatchedVersion_Blocks_WithBothVersionsInTheReason(ushort peerVer)
        {
            var gate = NewGate();
            string? reason = gate.CheckPeerReady(parsed: true, peerVersion: peerVer);
            Assert.NotNull(reason);
            Assert.Contains("peer protocol version mismatch", reason);
            Assert.Contains($"v{peerVer}", reason);
            Assert.Contains($"v{LOCAL}", reason);
            Assert.True(gate.Blocked); // DW-403: the Ready-clause mismatch latches too
        }

        [Fact]
        public void PeerReady_Unparseable_ProceedsToTheFailClosedHashGate()
        {
            // Pre-extraction flow, preserved: an UNPARSEABLE Ready is not version-blocked here — LobbyUi routes it
            // into HandshakeGate.CheckStart with peerHashParsed:false, which blocks fail-closed on the hash-0 path
            // (HandshakeGateTests.UnparseableReadyPayload_Blocks_EvenWithAMatchingResidualHash pins that half).
            var gate = NewGate();
            Assert.Null(gate.CheckPeerReady(parsed: false, peerVersion: 0));
            Assert.False(gate.Blocked);
        }

        // ── DW-403: the latch — a Ready cannot out-run a version-blocked lobby ─────

        [Fact]
        public void PeerReady_WhileVersionBlocked_IsRefused_EvenWithAMatchingVersion()
        {
            var gate = NewGate();
            gate.EvaluateHello(parsed: true, helloVersion: 1); // latch via a mismatched Hello
            string? reason = gate.CheckPeerReady(parsed: true, peerVersion: LOCAL);
            Assert.NotNull(reason);
            Assert.Contains("version-blocked", reason);
            Assert.True(gate.Blocked); // still latched — only a valid Hello (or Reset) clears it
        }

        [Fact]
        public void RepeatedMismatchedHellos_StayBlocked_AndKeepReporting()
        {
            var gate = NewGate();
            Assert.False(gate.EvaluateHello(parsed: true, helloVersion: 1).Allowed);
            Assert.False(gate.EvaluateHello(parsed: true, helloVersion: 1).Allowed);
            Assert.True(gate.Blocked);
        }

        // ── DW-403: the recovery — a subsequently valid Hello restores ready-ability ──

        [Fact]
        public void ValidHelloAfterMismatchedHello_Recovers_AndUnblocks()
        {
            var gate = NewGate();
            gate.EvaluateHello(parsed: true, helloVersion: 1);          // block + latch
            var v = gate.EvaluateHello(parsed: true, helloVersion: LOCAL); // the same-connection valid Hello
            Assert.True(v.Allowed);
            Assert.True(v.Recovered);   // LobbyUi resets the stale ready handshake on this signal
            Assert.Null(v.BlockReason);
            Assert.False(gate.Blocked);
        }

        [Fact]
        public void ValidHelloAfterMismatchedPeerReady_AlsoRecovers()
        {
            var gate = NewGate();
            gate.CheckPeerReady(parsed: true, peerVersion: 1);          // latch via the Ready clause
            Assert.True(gate.Blocked);
            var v = gate.EvaluateHello(parsed: true, helloVersion: LOCAL);
            Assert.True(v.Allowed);
            Assert.True(v.Recovered);
            // …and the recovered lobby accepts peer Readies again (the ready handshake can restart).
            Assert.Null(gate.CheckPeerReady(parsed: true, peerVersion: LOCAL));
        }

        [Fact]
        public void ValidHelloWithoutAPriorBlock_IsNotARecovery()
        {
            // The Recovered signal must fire ONLY on an actual block→valid transition, so LobbyUi never resets a
            // healthy handshake on an ordinary (first) Hello.
            var v = NewGate().EvaluateHello(parsed: true, helloVersion: LOCAL);
            Assert.True(v.Allowed);
            Assert.False(v.Recovered);
        }

        [Fact]
        public void ValidHelloAfterUnparseableHello_Recovers()
        {
            var gate = NewGate();
            gate.EvaluateHello(parsed: false, helloVersion: 0); // garbled Hello latched the block
            var v = gate.EvaluateHello(parsed: true, helloVersion: LOCAL);
            Assert.True(v.Allowed);
            Assert.True(v.Recovered);
        }

        // ── Reset (disconnect / cancel / close) ────────────────────────────────────

        [Fact]
        public void Reset_ClearsTheLatch_AndIsNotARecovery()
        {
            var gate = NewGate();
            gate.EvaluateHello(parsed: true, helloVersion: 1);
            gate.Reset(); // disconnect/cancel/close — a new connection never inherits the block
            Assert.False(gate.Blocked);
            Assert.Null(gate.CheckPeerReady(parsed: true, peerVersion: LOCAL));
            var v = gate.EvaluateHello(parsed: true, helloVersion: LOCAL);
            Assert.True(v.Allowed);
            Assert.False(v.Recovered); // the block died with the old connection — nothing to recover
        }

        // ── Wire pairing: the gate over the REAL codec (the production call shape) ─

        [Fact]
        public void RealWire_ForgedV1Hello_Blocks_ThenARealHello_Recovers()
        {
            // The exact LobbyUi call shape: TryReadHello feeds (parsed, version) into the gate. A forged v1 Hello
            // (the Story94WireTests forged buffer) blocks; the codec's own MakeHello (which stamps the compiled
            // PROTOCOL_VERSION) then recovers the same gate on the same "connection".
            var gate = new LobbyVersionGate(TickCommandPacket.PROTOCOL_VERSION);

            var forged = new byte[] { (byte)PacketType.Hello, 1, 0, (byte)Faction.Player1 };
            bool parsedForged = TickCommandPacket.TryReadHello(forged, forged.Length, out _, out ushort forgedVer);
            Assert.False(gate.EvaluateHello(parsedForged, forgedVer).Allowed);

            byte[] real = TickCommandPacket.MakeHello(Faction.Player1);
            bool parsedReal = TickCommandPacket.TryReadHello(real, real.Length, out _, out ushort realVer);
            var v = gate.EvaluateHello(parsedReal, realVer);
            Assert.True(v.Allowed);
            Assert.True(v.Recovered);
        }

        [Fact]
        public void RealWire_TruncatedHello_Blocks_FailClosed()
        {
            // A 2-byte fragment fails TryReadHello (version reads back 0) — the gate must block, not allow.
            var gate = new LobbyVersionGate(TickCommandPacket.PROTOCOL_VERSION);
            byte[] full = TickCommandPacket.MakeHello();
            bool parsed = TickCommandPacket.TryReadHello(full, 2, out _, out ushort ver);
            Assert.False(parsed);
            Assert.False(gate.EvaluateHello(parsed, ver).Allowed);
            Assert.True(gate.Blocked);
        }

        [Fact]
        public void RealWire_CurrentBuildReady_PassesTheVersionClause()
        {
            // The production Ready (MakeReady stamped with the compiled PROTOCOL_VERSION) proceeds to the hash
            // gate — the version clause only ever stops a DIFFERENT build.
            var gate = new LobbyVersionGate(TickCommandPacket.PROTOCOL_VERSION);
            byte[] b = TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, 0xC0FFEEUL);
            bool parsed = TickCommandPacket.TryReadReady(b, b.Length, out ushort ver, out _);
            Assert.True(parsed);
            Assert.Null(gate.CheckPeerReady(parsed, ver));
        }
    }
}
