#nullable enable
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 — the pure lobby start-handshake decision (<see cref="HandshakeGate"/>): a scenario wire hash of
    /// 0 ("not computed") BLOCKS the start on EITHER side (inverting the old fail-open skip), a nonzero mismatch
    /// blocks with the established map-mismatch message, and only equal nonzero hashes allow. The I/O-matrix
    /// lobby-handshake row, pinned Godot-free.
    /// </summary>
    public class HandshakeGateTests
    {
        [Fact]
        public void LocalHashZero_Blocks_WithNotComputedReason()
        {
            string? reason = HandshakeGate.CheckStart(0u, 0xDEADBEEFu);
            Assert.NotNull(reason);
            Assert.Contains("not computed", reason);
        }

        [Fact]
        public void PeerHashZero_Blocks_WithNotComputedReason()
        {
            string? reason = HandshakeGate.CheckStart(0xDEADBEEFu, 0u);
            Assert.NotNull(reason);
            Assert.Contains("not computed", reason);
        }

        [Fact]
        public void BothHashesZero_Block()
        {
            Assert.NotNull(HandshakeGate.CheckStart(0u, 0u));
        }

        [Fact]
        public void NonzeroMismatch_Blocks_WithTheMapMismatchMessage()
        {
            string? reason = HandshakeGate.CheckStart(0x11111111u, 0x22222222u);
            Assert.NotNull(reason);
            Assert.Contains("MAP MISMATCH", reason);
            Assert.Contains("0x11111111", reason);
            Assert.Contains("0x22222222", reason);
        }

        [Fact]
        public void EqualNonzero_Allows()
        {
            Assert.Null(HandshakeGate.CheckStart(0xC0FFEEu, 0xC0FFEEu));
        }

        [Fact]
        public void UnparseableReadyPayload_Blocks_EvenWithAMatchingResidualHash()
        {
            // Review follow-up: LobbyUi routes a Ready packet whose payload failed TryReadReady through the gate
            // with peerHashParsed: false — the gate treats the peer hash as 0 ("not computed") and BLOCKS, so an
            // unreadable payload can never mark the peer ready and bypass the check (fail-closed). The matching
            // nonzero peerHash argument here proves the parsed flag, not the residual value, decides.
            string? reason = HandshakeGate.CheckStart(0xC0FFEEu, 0xC0FFEEu, peerHashParsed: false);
            Assert.NotNull(reason);
            Assert.Contains("not computed", reason);
        }

        [Fact]
        public void ParsedFlagTrue_IsTheDefault_AndKeepsTheAllowPath()
        {
            Assert.Null(HandshakeGate.CheckStart(0xC0FFEEu, 0xC0FFEEu, peerHashParsed: true));
        }
    }
}
