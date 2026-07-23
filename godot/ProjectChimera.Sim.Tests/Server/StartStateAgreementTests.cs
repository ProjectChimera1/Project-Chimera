#nullable enable
using ProjectChimera.Multiplayer;          // HaltReason, TickCommandPacket.PROTOCOL_VERSION
using ProjectChimera.Multiplayer.Server;    // ServerLobbyPolicy
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.4 — the Godot-free server-attested start-state-agreement gate
    /// (<see cref="ServerLobbyPolicy.CheckStartStateAgreement"/>): the match may start ONLY when every player slot
    /// reported the same non-zero match-agreement hash AND every slot runs <see cref="TickCommandPacket.PROTOCOL_VERSION"/>.
    /// Any 0 hash, per-slot disagreement, or version skew fails closed with the corresponding <see cref="HaltReason"/>.
    /// </summary>
    public class StartStateAgreementTests
    {
        private const ushort V = TickCommandPacket.PROTOCOL_VERSION;

        [Fact]
        public void AllEqualNonZeroHashes_AndMatchingVersions_Allow()
        {
            var hashes   = new ulong[]  { 0xABCDEF01UL, 0xABCDEF01UL };
            var versions = new ushort[] { V, V };
            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, 2));
        }

        [Fact]
        public void AnyZeroHash_Blocks_StartStateDisagreement()
        {
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(
                    new ulong[] { 0UL, 0xABCDUL }, new ushort[] { V, V }, 2));
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(
                    new ulong[] { 0xABCDUL, 0UL }, new ushort[] { V, V }, 2));
        }

        [Fact]
        public void NonZeroMismatch_Blocks_StartStateDisagreement()
        {
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(
                    new ulong[] { 0x1111UL, 0x2222UL }, new ushort[] { V, V }, 2));
        }

        [Fact]
        public void VersionSkew_Blocks_ProtocolMismatch_CheckedBeforeHash()
        {
            // A version skew is reported as ProtocolMismatch even when the hashes also disagree (version first).
            Assert.Equal(HaltReason.ProtocolMismatch,
                ServerLobbyPolicy.CheckStartStateAgreement(
                    new ulong[] { 0x1111UL, 0x2222UL }, new ushort[] { V, (ushort)(V + 1) }, 2));
            // …and even when the hashes agree.
            Assert.Equal(HaltReason.ProtocolMismatch,
                ServerLobbyPolicy.CheckStartStateAgreement(
                    new ulong[] { 0xABCDUL, 0xABCDUL }, new ushort[] { (ushort)(V + 1), V }, 2));
        }

        [Fact]
        public void ZeroExpected_Blocks()
        {
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(new ulong[] { 0xABCDUL }, new ushort[] { V }, 0));
        }

        [Fact]
        public void OnlyTheFirstExpectedSlots_AreConsidered()
        {
            // A stale/garbage entry in a slot beyond `expected` must not affect the verdict.
            var hashes   = new ulong[]  { 0xABCDUL, 0xABCDUL, 0xDEADUL, 0UL };
            var versions = new ushort[] { V, V, (ushort)(V + 9), V };
            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, 2));
        }
    }
}
