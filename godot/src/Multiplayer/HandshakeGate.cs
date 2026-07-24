#nullable enable
namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 7.7 — the PURE lobby start-handshake decision (Godot-free; single-file-included into the Tier-1 test
    /// assembly like <c>DelayMath</c>). Extracted from <c>LobbyUi</c>'s Ready-packet handler so the policy is
    /// unit-testable and so the hash-0 posture is INVERTED from fail-open to fail-closed: before 7.7 a scenario
    /// hash of 0 ("not computed") SKIPPED the map-mismatch check entirely — two peers could start a match no one
    /// had proven start-state agreement for. Now either side's 0 BLOCKS the start, a nonzero mismatch blocks with
    /// the established message, and only equal nonzero hashes allow.
    ///
    /// Story 9.4 — widened from the 32-bit scenario hash to the 64-bit <c>MatchAgreementHash</c> (ruleset +
    /// initial-delay + roster + faction-count + start-state), so the P2P start gate now covers the full agreement
    /// surface, not just map content. Formatted as <c>X16</c>. The fail-closed hash-0 posture is unchanged.
    ///
    /// Story 9.16 — the <c>MatchAgreementHash</c> now also folds a content-definitions fingerprint, so a content-byte
    /// mismatch blocks here. On a block, the caller's LOCAL per-domain content fingerprint (ruleset-caps / factions /
    /// abilities / items / damage-table) is appended so a human can COMPARE it line-by-line with the peer's. It is NOT
    /// automatic remote-domain naming: the wire carries one combined value (no sub-hash exchange), and it covers only
    /// the 5 CONTENT domains — a mismatch caused by a non-content component (roster / teams / start-state / scenario /
    /// initial-delay) will show all-matching content here.
    ///
    /// Pure function: no logging, no side effects; <c>LobbyUi</c> surfaces the returned reason as its status text.
    /// </summary>
    public static class HandshakeGate
    {
        /// <summary>
        /// Decide whether the match start is allowed given the local and peer 64-bit match-agreement hashes
        /// (<c>MatchAgreementHash.Compute</c> values; 0 = "not computed"). Returns <c>null</c> to ALLOW, or the
        /// human-readable BLOCK reason to surface:
        ///   • an UNPARSEABLE Ready payload (<paramref name="peerHashParsed"/> false) → the peer hash is treated
        ///     as 0, so it blocks with the not-computed reason (fail-closed — a payload we cannot read is never
        ///     proof of start-state agreement, and must not bypass the gate);
        ///   • either hash 0 → block ("start-state hash not computed" — a validated scenario was never applied);
        ///   • nonzero mismatch → block (the established mismatch message);
        ///   • equal nonzero → allow.
        /// <paramref name="localBreakdown"/> (Story 9.16) is the LOCAL per-domain content fingerprint, appended to any
        /// block reason for line-by-line comparison with the peer's (NOT automatic remote-domain naming; content-only
        /// — a non-content mismatch shows all-matching content); null/empty appends nothing.
        /// </summary>
        public static string? CheckStart(ulong localHash, ulong peerHash, bool peerHashParsed = true,
            string? localBreakdown = null)
        {
            if (!peerHashParsed) peerHash = 0UL; // unparseable Ready ≡ "not computed" — routes into the block below

            if (localHash == 0UL || peerHash == 0UL)
                return WithBreakdown(
                    "CANNOT START — start-state hash not computed!\n" +
                    $"Your match: 0x{localHash:X16}\n" +
                    $"Peer match: 0x{peerHash:X16}\n" +
                    "A hash of 0 means no validated scenario was applied on that peer.", localBreakdown);

            if (peerHash != localHash)
                return WithBreakdown(
                    "START-STATE MISMATCH — cannot start!\n" +
                    $"Your match: 0x{localHash:X16}\n" +
                    $"Peer match: 0x{peerHash:X16}\n" +
                    "Both players must run the same game build/version and load the same scenario, ruleset, and roster.\n" +
                    "(A different build's match-agreement algorithm always mismatches here even with identical content.)", localBreakdown);

            return null; // equal nonzero — start allowed
        }

        /// <summary>Append the LOCAL content fingerprint to a block reason for line-by-line peer comparison (Story
        /// 9.16). Framed as a comparison aid, not an automatic cause: it is this side's OWN fingerprint and covers only
        /// the content domains, so the block may still originate in a non-content component (roster/teams/start-state/
        /// scenario/delay), which shows all-matching content. Null/empty appends nothing.</summary>
        private static string WithBreakdown(string reason, string? localBreakdown) =>
            string.IsNullOrEmpty(localBreakdown)
                ? reason
                : reason + "\nYour LOCAL content fingerprint (compare with your peer's line; the mismatch may instead " +
                           "be in a non-content component — roster/teams/start-state/scenario):\n  " + localBreakdown;
    }
}
