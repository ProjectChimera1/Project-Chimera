#nullable enable
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 / 9.4 — the pure lobby start-handshake decision (<see cref="HandshakeGate"/>): a 64-bit
    /// match-agreement hash of 0 ("not computed") BLOCKS the start on EITHER side (fail-closed), a nonzero mismatch
    /// blocks with the established mismatch message, and only equal nonzero hashes allow. Story 9.4 widened the gate
    /// from the 32-bit scenario hash to the 64-bit MatchAgreementHash (formatted X16). The I/O-matrix
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
        public void NonzeroMismatch_Blocks_WithTheMismatchMessage()
        {
            string? reason = HandshakeGate.CheckStart(0x1111111111111111UL, 0x2222222222222222UL);
            Assert.NotNull(reason);
            Assert.Contains("MISMATCH", reason);
            Assert.Contains("0x1111111111111111", reason); // X16-formatted 64-bit value
            Assert.Contains("0x2222222222222222", reason);
        }

        [Fact]
        public void EqualNonzero_Allows()
        {
            Assert.Null(HandshakeGate.CheckStart(0xC0FFEEu, 0xC0FFEEu));
        }

        [Fact]
        public void EqualNonzero_FullWidth64Bit_Allows()
        {
            // Story 9.4: the widened gate carries the full 64-bit MatchAgreementHash (a value the old 32-bit gate
            // could not represent). Equal high-bit-set values still allow; a one-bit difference blocks.
            Assert.Null(HandshakeGate.CheckStart(0xDEADBEEF_CAFEF00DUL, 0xDEADBEEF_CAFEF00DUL));
            Assert.NotNull(HandshakeGate.CheckStart(0xDEADBEEF_CAFEF00DUL, 0xDEADBEEF_CAFEF00CUL));
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
