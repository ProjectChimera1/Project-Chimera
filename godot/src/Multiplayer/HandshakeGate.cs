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
    /// Pure function: no logging, no side effects; <c>LobbyUi</c> surfaces the returned reason as its status text.
    /// </summary>
    public static class HandshakeGate
    {
        /// <summary>
        /// Decide whether the match start is allowed given the local and peer scenario wire hashes
        /// (<c>CanonicalModelHash.ToWire</c> values; 0 = "not computed"). Returns <c>null</c> to ALLOW, or the
        /// human-readable BLOCK reason to surface:
        ///   • an UNPARSEABLE Ready payload (<paramref name="peerHashParsed"/> false) → the peer hash is treated
        ///     as 0, so it blocks with the not-computed reason (fail-closed — a payload we cannot read is never
        ///     proof of start-state agreement, and must not bypass the gate);
        ///   • either hash 0 → block ("scenario hash not computed" — a validated scenario was never applied);
        ///   • nonzero mismatch → block (the established map-mismatch message);
        ///   • equal nonzero → allow.
        /// </summary>
        public static string? CheckStart(uint localHash, uint peerHash, bool peerHashParsed = true)
        {
            if (!peerHashParsed) peerHash = 0u; // unparseable Ready ≡ "not computed" — routes into the block below

            if (localHash == 0u || peerHash == 0u)
                return "CANNOT START — scenario hash not computed!\n" +
                       $"Your map: 0x{localHash:X8}\n" +
                       $"Peer map:  0x{peerHash:X8}\n" +
                       "A hash of 0 means no validated scenario was applied on that peer.";

            if (peerHash != localHash)
                return "MAP MISMATCH — cannot start!\n" +
                       $"Your map: 0x{localHash:X8}\n" +
                       $"Peer map:  0x{peerHash:X8}\n" +
                       "Both players must load the same scenario file.";

            return null; // equal nonzero — start allowed
        }
    }
}
