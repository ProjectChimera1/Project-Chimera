#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.12 — the result of a server hero-profile attestation (from <c>rpc_attest_hero_profile</c>). Godot-free /
    /// SDK-free (integer/enum only) so BOTH the Godot-coupled <c>NakamaService</c> (which produces it from the RPC reply)
    /// and the pure <see cref="OnlineHeroLaunchGate"/> (which consumes it) can name it.
    ///
    /// <para><see cref="CallSucceeded"/> = the attest RPC/read actually completed (false ⇒ Nakama unreachable, timeout,
    /// or an exception — the FAIL-CLOSED path). <see cref="Attested"/> = the server found the stored profile object AND
    /// it passed the server-side validator. <see cref="Reason"/> carries the reason class when it did not attest.</para>
    /// </summary>
    public readonly record struct AttestationOutcome(bool Attested, bool CallSucceeded, ProfileInvalidReason Reason)
    {
        /// <summary>A clean pass — the RPC completed and the server attested a valid stored profile.</summary>
        public static readonly AttestationOutcome Ok = new(true, true, ProfileInvalidReason.None);

        /// <summary>The RPC could not complete (Nakama unreachable / RPC error / timeout). FAIL-CLOSED: never enter a
        /// match. <see cref="CallSucceeded"/> is false so the gate refuses launch regardless of the other fields.</summary>
        public static readonly AttestationOutcome CallFailed = new(false, false, ProfileInvalidReason.None);

        /// <summary>The RPC completed but the server did NOT attest (no stored object, or the object failed validation).
        /// <paramref name="reason"/> is the surfaceable reason class.</summary>
        public static AttestationOutcome Unattested(ProfileInvalidReason reason) => new(false, true, reason);
    }

    /// <summary>
    /// Story 9.12 (FR-7c / AR-12) — the pure launch predicate the online hero picker gates on at Ready/launch. Mirrors
    /// the <c>ServerLobbyPolicy</c> idiom: a Godot-free static decision, Tier-1 unit-testable, so the "an unattested,
    /// invalid, or unreachable-server profile can never enter online play" invariant is verified without a live Nakama.
    /// A profile may enter a match ONLY when the attest call SUCCEEDED and the server ATTESTED it — every other
    /// combination (invalid, absent, or a failed/errored/timed-out call) refuses launch FAIL-CLOSED.
    /// </summary>
    public static class OnlineHeroLaunchGate
    {
        /// <summary>True iff the attest call completed AND the server attested a valid stored profile — the single
        /// online-launch gate. A false <see cref="AttestationOutcome.CallSucceeded"/> is fail-closed (never fail-open on
        /// a network error).</summary>
        public static bool CanEnterMatch(AttestationOutcome outcome) => outcome.CallSucceeded && outcome.Attested;
    }
}
