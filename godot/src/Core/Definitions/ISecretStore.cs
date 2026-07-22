#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 8.1 — the Godot-free secret-store seam. A tiny key/value rail for LLM / mod.io API keys that lives
    /// entirely outside Godot (no <c>using Godot;</c>) so both the services that consume keys and the Tier-1 xUnit
    /// harness can reach it headlessly. The concrete <see cref="FileSecretStore"/> persists each secret to its own
    /// gitignored file under <c>user://secrets/&lt;id&gt;.key</c>; the Godot layer resolves that path once (via
    /// <c>ProjectSettings.GlobalizePath</c>) and injects the OS-absolute directory.
    ///
    /// <para>Contract (see the story I/O matrix): <see cref="Get"/> on an absent secret returns <c>""</c>, never
    /// throws, and writes NOTHING to disk — the directory/file is created lazily only on <see cref="Set"/>. Reads are
    /// fail-soft (a missing/unreadable file → <c>""</c>). Secret ids are validated <c>^[a-z0-9_-]+$</c> to defeat
    /// path traversal; an invalid id throws <see cref="System.ArgumentException"/>.</para>
    ///
    /// <para>This is CONFIG plumbing only (Story 8.1). The <c>ILLMProvider</c> abstraction and provider consumption
    /// are Story 8.2 — this interface only changes where a key comes FROM.</para>
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>Return the stored secret for <paramref name="id"/>, or <c>""</c> if it is absent / unreadable.
        /// Never throws for an absent secret and writes nothing to disk. Throws <see cref="System.ArgumentException"/>
        /// if <paramref name="id"/> is not a valid key id (<c>^[a-z0-9_-]+$</c>).</summary>
        string Get(string id);

        /// <summary>Persist <paramref name="value"/> as the secret for <paramref name="id"/>, creating the backing
        /// directory/file lazily. Throws <see cref="System.ArgumentException"/> for an invalid key id.</summary>
        void Set(string id, string value);

        /// <summary>True iff a non-empty secret is currently stored for <paramref name="id"/>. Throws
        /// <see cref="System.ArgumentException"/> for an invalid key id.</summary>
        bool Has(string id);

        /// <summary>Delete the stored secret for <paramref name="id"/> (no-op if absent). Throws
        /// <see cref="System.ArgumentException"/> for an invalid key id.</summary>
        void Clear(string id);
    }
}
