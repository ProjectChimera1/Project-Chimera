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
        /// <summary>The LLM (Anthropic) API key id — backs <c>user://secrets/llm.key</c>.</summary>
        public const string Llm = "llm";

        /// <summary>The mod.io read-only API key id — backs <c>user://secrets/modio.key</c>.</summary>
        public const string ModIo = "modio";
    }
}
