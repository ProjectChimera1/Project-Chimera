#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.8 — the locally-signed PROOF-OF-PLAY artifact: evidence that the creator beat their own scenario
    /// before publishing it. A Godot-free, data-only POCO (no crypto, no <c>DateTime</c>) so it can be embedded in
    /// <see cref="ContentPackageManifest"/> without a <c>Definitions → UGC</c> reference and round-trips through the
    /// same <c>System.Text.Json</c> path as the rest of the manifest. The crypto lives in
    /// <c>ProjectChimera.UGC.ProofOfPlaySigner</c>; persistence in <c>ProjectChimera.UGC.ProofOfPlayStore</c>.
    ///
    /// <para><see cref="ScenarioHash"/> is the HEX form of the full 64-bit <see cref="CanonicalModelHash.Compute"/>
    /// value (NOT the 32-bit <c>ToWire</c> fold, NOT the file-byte <c>ScenarioSerializer.ComputeFileHash</c>) — stored
    /// as a string, not a JSON number, so no ulong precision is lost across mod.io/JSON interop. It binds the token to
    /// the canonical MODEL identity: any content edit re-derives to a different hash and the publish gate treats the
    /// token as stale.</para>
    ///
    /// <para>This is a trusted-friends EA tamper-EVIDENCE artifact within a single install (the HMAC key is
    /// per-install and local), NOT anti-cheat: cross-machine forgery resistance / server attestation is the 9.12
    /// online rail, explicitly out of scope here.</para>
    /// </summary>
    public sealed class ProofOfPlayToken
    {
        /// <summary>Hex of the 64-bit <see cref="CanonicalModelHash.Compute"/> value the token was minted from.</summary>
        [JsonPropertyName("scenario_hash")]
        public string ScenarioHash { get; set; } = "";

        /// <summary>The recorded outcome — currently only <c>"win"</c> is ever minted (a loss mints nothing).</summary>
        [JsonPropertyName("outcome")]
        public string Outcome { get; set; } = "";

        /// <summary>ISO-8601 UTC timestamp of when the token was minted (provisioned presentation-side, off the tick path).</summary>
        [JsonPropertyName("minted_at")]
        public string MintedAt { get; set; } = "";

        /// <summary>Hex HMAC-SHA256 signature over the canonical payload — recomputed and compared by <c>Verify</c>.</summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = "";

        /// <summary>The scenario identity this token was minted for (the persistence key; file-safe-sanitized by the store).</summary>
        [JsonPropertyName("scenario_id")]
        public string ScenarioId { get; set; } = "";
    }
}
