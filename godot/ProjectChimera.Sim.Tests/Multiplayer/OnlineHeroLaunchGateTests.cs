#nullable enable
using ProjectChimera.Core.Definitions; // AttestationOutcome, OnlineHeroLaunchGate, ProfileInvalidReason
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 9.12 — the pure online-launch gate. A profile may enter an online match ONLY when the attest RPC completed
    /// AND the server attested a valid stored profile; every other combination — invalid/unattested, or a
    /// failed/errored/timed-out attestation call — is refused FAIL-CLOSED. Godot-free (Tier-1), mirroring the
    /// <c>ServerLobbyPolicy</c> test idiom, so the invariant is verified without a live Nakama.
    /// </summary>
    public class OnlineHeroLaunchGateTests
    {
        [Fact]
        public void CanEnterMatch_AttestedAndValid_True()
            => Assert.True(OnlineHeroLaunchGate.CanEnterMatch(AttestationOutcome.Ok));

        [Fact]
        public void CanEnterMatch_Unattested_NoStoredObject_False()
            => Assert.False(OnlineHeroLaunchGate.CanEnterMatch(
                AttestationOutcome.Unattested(ProfileInvalidReason.None))); // call succeeded, nothing stored

        [Fact]
        public void CanEnterMatch_AttestedButInvalid_False()   // object exists but failed server validation
            => Assert.False(OnlineHeroLaunchGate.CanEnterMatch(
                AttestationOutcome.Unattested(ProfileInvalidReason.Range)));

        [Fact]
        public void CanEnterMatch_AttestationCallFailed_False()   // Nakama unreachable / RPC error / timeout → fail-closed
            => Assert.False(OnlineHeroLaunchGate.CanEnterMatch(AttestationOutcome.CallFailed));

        [Fact]
        public void CanEnterMatch_CallFailed_IsFailClosed_EvenIfFieldsLookAttested()
        {
            // Defensive: a malformed outcome that claims Attested but CallSucceeded=false must still refuse launch.
            var contradictory = new AttestationOutcome(Attested: true, CallSucceeded: false, Reason: ProfileInvalidReason.None);
            Assert.False(OnlineHeroLaunchGate.CanEnterMatch(contradictory));
        }
    }
}
