#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 8.1 — the canonical secret ids used across the bootstrap wiring. The seed site
    /// (<c>SettingsPhase</c> migration) and the read sites (<c>TriggerEditorPhase</c>,
    /// <c>ContentBrowserPhase</c>) MUST reference these constants rather than bare string literals, so a typo can
    /// never silently decouple where a key is written from where it is read (the single point most likely to turn
    /// the Godot-coupled — and therefore unit-untestable — phase wiring into a silent "configured key ignored"
    /// regression). Godot-free so both the phases and the Tier-1 harness reference the identical values.
    ///
    /// <para>Each id maps to a <c>&lt;id&gt;.key</c> file under <c>user://secrets</c> (see
    /// <see cref="FileSecretStore"/>), so the value must satisfy the store's key-id rule <c>^[a-z0-9_-]+$</c>.</para>
    /// </summary>
    public static class SecretIds
    {
        /// <summary>LEGACY (DW-368): the pre-8.2 SHARED LLM key id — backs <c>user://secrets/llm.key</c>. One id was
        /// read for EVERY cloud provider, so a key stored while Anthropic was selected would be sent verbatim to the
        /// OpenRouter endpoint after a provider switch. Keys are now stored per-provider (see
        /// <see cref="ForLlmProvider"/>); this constant is retained ONLY so the one-time
        /// <c>LlmProviderFactory.MigrateLegacySharedKey</c> migration can find and move an existing shared key.
        /// NO read/write site other than that migration may reference it.</summary>
        public const string Llm = "llm";

        /// <summary>Prefix for the per-provider LLM key ids (see <see cref="ForLlmProvider"/>).</summary>
        public const string LlmProviderPrefix = "llm_";

        /// <summary>
        /// DW-368 — the PER-PROVIDER LLM API key id: <c>llm_&lt;providerId&gt;</c> (e.g. <c>llm_anthropic</c> →
        /// <c>user://secrets/llm_anthropic.key</c>, <c>llm_openrouter</c> → <c>llm_openrouter.key</c>). Every site
        /// that stores or reads an LLM key MUST key it by the provider it belongs to, so a key entered for one
        /// provider can never be sent to another provider's endpoint. <paramref name="providerId"/> is a curated
        /// <see cref="LlmProviderCatalog"/> id — those are lowercase <c>^[a-z0-9_-]+$</c> tokens, so the produced id
        /// always satisfies the store's key-id rule (a future catalog id violating that rule fails loudly:
        /// <see cref="FileSecretStore"/> throws <see cref="System.ArgumentException"/>).
        /// </summary>
        public static string ForLlmProvider(string? providerId) => LlmProviderPrefix + (providerId ?? "");

        /// <summary>The mod.io read-only API key id — backs <c>user://secrets/modio.key</c>.</summary>
        public const string ModIo = "modio";

        /// <summary>Story 9.8 — the per-install proof-of-play HMAC signing key id — backs
        /// <c>user://secrets/proof_of_play.key</c>. Provisioned presentation-side (32 random bytes, hex-encoded) on
        /// first self-victory; never stored in <c>SettingsData</c> or a Godot <c>[Export]</c>.</summary>
        public const string ProofOfPlay = "proof_of_play";
    }
}
