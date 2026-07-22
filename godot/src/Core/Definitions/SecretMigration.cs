#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 8.1 — one-time migration of a legacy plaintext key into the <see cref="ISecretStore"/>. When the
    /// plaintext <c>[Export] AnthropicApiKey</c> / <c>ModIoApiKey</c> fields are ripped off <c>MainScene</c>, any
    /// value a user had configured on a prior build must survive: on first run the bootstrap passes the legacy value
    /// here, and it is copied into the store IFF the store doesn't already hold that id and the legacy value is
    /// non-empty. Godot-free static helper (no <c>using Godot;</c>) so it is Tier-1 testable.
    ///
    /// <para>In THIS repo the legacy value is <c>""</c> (the fields defaulted empty and no scene set them), so the
    /// call is a no-op today — this is the forward-compatible seam, not a live migration.</para>
    /// </summary>
    public static class SecretMigration
    {
        /// <summary>
        /// Copy <paramref name="legacyPlaintext"/> into <paramref name="store"/> under <paramref name="id"/> IFF the
        /// store lacks <paramref name="id"/> AND the legacy value is non-empty. Returns <c>true</c> when a value was
        /// copied; <c>false</c> on the no-op branches (store already set, or legacy null/empty/whitespace).
        /// Idempotent: a second call after a successful migrate returns <c>false</c>.
        /// </summary>
        public static bool MigrateLegacyKey(ISecretStore store, string id, string? legacyPlaintext)
        {
            if (store == null) return false;
            if (string.IsNullOrWhiteSpace(legacyPlaintext)) return false;     // nothing to migrate

            // Fail-soft: this runs during bootstrap (SettingsPhase). An invalid id, or a store write that fails on a
            // full/read-only disk, must NEVER throw out of here and abort scene load — a best-effort migration that
            // can't persist just doesn't migrate (returns false). An explicit user "save key" still surfaces failures
            // (it calls store.Set directly, not through this seam).
            try
            {
                if (store.Has(id)) return false;                             // store already owns this secret
                store.Set(id, legacyPlaintext);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
