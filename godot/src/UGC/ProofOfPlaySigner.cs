#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UGC
{
    /// <summary>
    /// Story 9.8 — the HMAC-SHA256 signer/verifier for <see cref="ProofOfPlayToken"/>. Godot-free and deterministic
    /// (HMAC is a pure function of key + payload), so it is fully Tier-1 testable. Lives in <c>src/UGC</c> beside
    /// <see cref="ModIoService"/> (which already uses <c>System.*</c> crypto/net APIs) — off the sim tick path and
    /// outside the banned-API analyzer's globbed source set, so no float / RS0030 concern.
    ///
    /// <para>The canonical payload is order-fixed so <see cref="Verify"/> re-derives the signature byte-identically:
    /// <c>{scenario_id}|{scenario_hash}|{outcome}|{minted_at}</c>. Any hand-edit to any signed field changes the
    /// recomputed signature and fails the constant-time compare.</para>
    ///
    /// <para>NOT anti-cheat: the key is per-install and local, making the token tamper-EVIDENT within an install for
    /// trusted-friends EA only. Cross-machine forgery resistance is the 9.12 online rail (out of scope).</para>
    /// </summary>
    public static class ProofOfPlaySigner
    {
        /// <summary>
        /// Mint a signed token. The <paramref name="scenarioHash"/> is the full 64-bit
        /// <see cref="CanonicalModelHash.Compute"/> value, stored as fixed-width hex so it round-trips through JSON as a
        /// string (no ulong precision loss). <paramref name="mintedAt"/> is supplied by the caller (presentation-side
        /// wall clock) so this core stays <c>DateTime</c>-free.
        /// </summary>
        public static ProofOfPlayToken Create(ulong scenarioHash, string outcome, string mintedAt,
                                              string scenarioId, byte[] key)
        {
            var token = new ProofOfPlayToken
            {
                ScenarioHash = HashToHex(scenarioHash),
                Outcome      = outcome ?? "",
                MintedAt     = mintedAt ?? "",
                ScenarioId   = scenarioId ?? "",
            };
            token.Signature = Sign(token, key);
            return token;
        }

        /// <summary>The fixed-width hex form of a 64-bit canonical hash — the exact string stored in
        /// <see cref="ProofOfPlayToken.ScenarioHash"/> and compared by <see cref="MatchesScenario"/>.</summary>
        public static string HashToHex(ulong scenarioHash) => scenarioHash.ToString("X16");

        /// <summary>Recompute the signature over <paramref name="token"/>'s canonical payload and compare it to the
        /// stored one in constant time. Returns false for a null token, a malformed hex signature, or any edited
        /// field.</summary>
        public static bool Verify(ProofOfPlayToken? token, byte[] key)
        {
            if (token is null) return false;

            byte[] expected;
            try { expected = HexToBytes(token.Signature); }
            catch { return false; } // a hand-mangled non-hex signature can never verify

            byte[] actual = ComputeHmac(token, key);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        /// <summary>True iff the token was minted from the CURRENT canonical model hash — i.e. the scenario has not
        /// been edited since the win. A content edit moves <see cref="CanonicalModelHash.Compute"/>, so the stored
        /// hex no longer equals <paramref name="currentHash"/>'s hex and the token is stale.</summary>
        public static bool MatchesScenario(ProofOfPlayToken? token, ulong currentHash)
            => token is not null
               && string.Equals(token.ScenarioHash, HashToHex(currentHash), StringComparison.OrdinalIgnoreCase);

        // ── Internals ─────────────────────────────────────────────────────────

        /// <summary>The order-fixed canonical payload. Any change to layout here is a signing-format change that
        /// invalidates every previously-minted token — treat it as frozen.</summary>
        private static string CanonicalPayload(ProofOfPlayToken t)
            => $"{t.ScenarioId}|{t.ScenarioHash}|{t.Outcome}|{t.MintedAt}";

        private static string Sign(ProofOfPlayToken token, byte[] key)
            => Convert.ToHexString(ComputeHmac(token, key));

        private static byte[] ComputeHmac(ProofOfPlayToken token, byte[] key)
        {
            using var h = new HMACSHA256(key ?? Array.Empty<byte>());
            return h.ComputeHash(Encoding.UTF8.GetBytes(CanonicalPayload(token)));
        }

        private static byte[] HexToBytes(string hex) => Convert.FromHexString(hex ?? "");
    }
}
