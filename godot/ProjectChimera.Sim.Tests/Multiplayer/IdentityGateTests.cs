#nullable enable
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 15-14 (DW-200) — the host-side identity gate: the Attestation codec, the LanTrust inertness
    /// guarantee (LAN never asks for identity — Alec's ruling), the fail-closed OnlineAttest posture (null
    /// verifier rejects everything), the Ready gate, and the rejoin identity bind layered on the 15-1 token.
    /// </summary>
    public class IdentityGateTests
    {
        [Fact]
        public void Attestation_RoundTrips_AndBoundsAreFailClosed()
        {
            var pkt = TickCommandPacket.MakeAttestation("user-uuid-1234", "jwt.token.payload");
            Assert.True(TickCommandPacket.TryReadAttestation(pkt, pkt.Length, out string uid, out string tok));
            Assert.Equal("user-uuid-1234", uid);
            Assert.Equal("jwt.token.payload", tok);

            Assert.False(TickCommandPacket.TryReadAttestation(pkt, pkt.Length - 1, out _, out _)); // truncated
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TickCommandPacket.MakeAttestation("", "t"));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => TickCommandPacket.MakeAttestation("u", new string('x', 5000)));
        }

        [Fact]
        public void LanTrust_IsInert_NeverStoresIdentity_AlwaysPasses()
        {
            var gate = new IdentityGate(TrustMode.LanTrust, 8);
            Assert.False(gate.RecordAttestation(0, "someone", "token")); // ignored — LAN stores nothing
            Assert.Null(gate.UserIdOf(0));
            Assert.True(gate.MayReady(0, out _));
            gate.CaptureForRejoin(2);
            Assert.True(gate.RejoinIdentityOk(0)); // the 15-1 token alone is the LAN-grade identity (D-5)
        }

        [Fact]
        public void OnlineAttest_NullVerifier_FailsClosed()
        {
            var gate = new IdentityGate(TrustMode.OnlineAttest, 8, verifier: null);
            Assert.False(gate.RecordAttestation(0, "u1", "valid-token"));
            Assert.False(gate.MayReady(0, out string? why));
            Assert.NotNull(why);
        }

        [Fact]
        public void OnlineAttest_VerifierDecides_ReadyGateFollows()
        {
            var gate = new IdentityGate(TrustMode.OnlineAttest, 8, (uid, tok) => tok == "good");
            Assert.False(gate.RecordAttestation(0, "u1", "forged"));
            Assert.False(gate.MayReady(0, out _));

            Assert.True(gate.RecordAttestation(0, "u1", "good"));
            Assert.Equal("u1", gate.UserIdOf(0));
            Assert.True(gate.MayReady(0, out _));

            gate.Reset(0); // the attestation dies with the connection (recycle discipline)
            Assert.Null(gate.UserIdOf(0));
            Assert.False(gate.MayReady(0, out _));
        }

        [Fact]
        public void RejoinBind_RequiresTheSameUserId_OnAFreshAttestation()
        {
            var gate = new IdentityGate(TrustMode.OnlineAttest, 8, (_, tok) => tok == "good");
            gate.RecordAttestation(1, "alice", "good");
            gate.CaptureForRejoin(2); // StartGame freeze: slot 1 = alice

            // The drop recycles the connection → attestation gone → rejoin refused until re-attested.
            gate.Reset(1);
            Assert.False(gate.RejoinIdentityOk(1));

            // A DIFFERENT verified account with a stolen token still cannot take the slot.
            gate.RecordAttestation(1, "mallory", "good");
            Assert.False(gate.RejoinIdentityOk(1));

            // The same account re-attesting passes.
            gate.Reset(1);
            gate.RecordAttestation(1, "alice", "good");
            Assert.True(gate.RejoinIdentityOk(1));

            // A slot that was never bound at StartGame fails closed.
            gate.RecordAttestation(0, "bob", "good");
            Assert.False(gate.RejoinIdentityOk(0));
        }
    }
}
