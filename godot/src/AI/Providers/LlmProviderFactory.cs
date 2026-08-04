#nullable enable
using System;
using System.Net.Http;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the single construction site for an <see cref="ILLMProvider"/>. Resolves the provider from the
    /// catalog, the base URL (settings override else the catalog default), the model, and the key
    /// (<see cref="ISecretStore.Get"/> with the PER-PROVIDER id <see cref="SecretIds.ForLlmProvider"/> — never an
    /// <c>[Export]</c> field or <see cref="SettingsData"/>, and never the legacy shared <see cref="SecretIds.Llm"/>
    /// id, so a key stored for one provider is never sent to another's endpoint — DW-368), and enforces the pinned
    /// host allowlist BEFORE any adapter is built. Emits the
    /// synchronous unavailable states (<see cref="AiAvailability.NoProvider"/>/<see cref="AiAvailability.NoKey"/>/
    /// <see cref="AiAvailability.HostRestricted"/>) rather than throwing. Godot-free / unit-testable.
    /// </summary>
    public static class LlmProviderFactory
    {
        /// <summary>Build the adapter for the configured provider. On success returns <c>true</c> with
        /// <paramref name="provider"/> set and <paramref name="failure"/> = <see cref="AiAvailability.Healthy"/>. On a
        /// synchronous-unavailable case returns <c>false</c> with <paramref name="provider"/> null and
        /// <paramref name="failure"/> set to the reason:
        /// <list type="bullet">
        ///   <item><see cref="AiAvailability.NoProvider"/> — unknown provider id, an un-parseable base URL, or a
        ///         cloud provider on a non-pinned host</item>
        ///   <item><see cref="AiAvailability.HostRestricted"/> — ollama with a well-formed but non-loopback base URL
        ///         (DW-370: the loopback-only restriction is kept and named in the message)</item>
        ///   <item><see cref="AiAvailability.NoKey"/> — a cloud provider with no stored key</item>
        /// </list>
        /// NEVER falls back to another provider.</summary>
        public static bool TryCreate(
            SettingsData settings,
            ISecretStore secretStore,
            HttpClient http,
            out ILLMProvider? provider,
            out AiAvailability failure)
        {
            provider = null;

            // 1. Provider must be in the curated catalog.
            if (settings == null || !LlmProviderCatalog.TryGet(settings.LlmProvider, out LlmProviderCatalog.ProviderInfo? info))
            {
                failure = AiAvailability.NoProvider;
                return false;
            }

            string providerId = info!.Id;

            // 2. Key: cloud providers require one from the secret store; ollama (local) needs none.
            //    DW-368: the key is read under the PER-PROVIDER id (llm_anthropic / llm_openrouter / …), never the
            //    legacy shared "llm" id — a key stored for provider A must never reach provider B's endpoint. A
            //    provider whose per-provider key is absent is NoKey even if another provider has a stored key.
            string key = "";
            if (RequiresKey(providerId))
            {
                key = secretStore?.Get(SecretIds.ForLlmProvider(providerId)) ?? "";
                if (string.IsNullOrEmpty(key))
                {
                    failure = AiAvailability.NoKey;
                    return false;
                }
            }

            // 3. Base URL: settings override (if any) else the catalog default. Must be an absolute http(s) URI.
            string baseUrl = string.IsNullOrWhiteSpace(settings.LlmBaseUrl) ? info.DefaultBaseUrl : settings.LlmBaseUrl.Trim();
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
                || (baseUri!.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                failure = AiAvailability.NoProvider;
                return false;
            }

            // 4. Allowlist: cloud hosts must be pinned; ollama must be loopback. Rejected pre-flight, no network call.
            if (!LlmHostAllowlist.IsAllowed(providerId, baseUri))
            {
                // DW-370 (recorded decision): the loopback-only ollama policy is KEPT, but a well-formed ollama config
                // rejected solely for a non-loopback host (e.g. a LAN-hosted Ollama at http://192.168.1.5:11434) is
                // classified HostRestricted so the unavailable message names the restriction instead of the misleading
                // "no provider configured". Cloud providers on a non-pinned host remain NoProvider.
                failure = string.Equals(providerId, "ollama", StringComparison.Ordinal)
                    ? AiAvailability.HostRestricted
                    : AiAvailability.NoProvider;
                return false;
            }

            // 5. Build the adapter for the selected provider — authoritative, no fallback.
            provider = providerId switch
            {
                "anthropic"  => new AnthropicProvider(http, baseUrl, settings.LlmModel, key),
                "ollama"     => new OllamaProvider(http, baseUrl, settings.LlmModel),
                "openrouter" => new OpenRouterProvider(http, baseUrl, settings.LlmModel, key),
                _            => null,
            };

            if (provider == null)
            {
                // A catalog id with no adapter (should not happen while the catalog and this switch agree).
                failure = AiAvailability.NoProvider;
                return false;
            }

            failure = AiAvailability.Healthy;
            return true;
        }

        /// <summary>The key-requirement rule lives here (Story 8.2), NOT in Story 8.1's frozen catalog data: a
        /// provider requires a key iff it is not the local (loopback) ollama.</summary>
        public static bool RequiresKey(string providerId)
            => !string.Equals(providerId, "ollama", StringComparison.Ordinal);

        /// <summary>
        /// DW-368 — one-time MOVE of the legacy shared <see cref="SecretIds.Llm"/> secret onto the per-provider id
        /// it belongs to. The shared key was stored for the provider the creator had selected when they saved it
        /// (Test-connection verified it against that provider), so it migrates to
        /// <paramref name="selectedProviderId"/>'s id when that provider requires a key; when the selected provider
        /// needs no key (ollama) or is unknown, it falls back to <see cref="LlmProviderCatalog.DefaultProviderId"/>
        /// (Anthropic — the id's documented historical owner). The legacy id is ALWAYS cleared on migration — leaving
        /// it would let a later boot under a different selected provider re-migrate the same key there, re-creating
        /// the exact cross-provider key reuse this closes. An already-set per-provider key is never overwritten (the
        /// stale shared duplicate is discarded).
        ///
        /// <para>Returns <c>true</c> iff a legacy shared key existed and was consumed. Fail-soft (mirrors
        /// <see cref="SecretMigration.MigrateLegacyKey"/>): this runs during bootstrap, so a store failure must never
        /// throw out of here and abort scene load. Idempotent — the first call clears the legacy id, so a second call
        /// returns <c>false</c>.</para>
        /// </summary>
        public static bool MigrateLegacySharedKey(ISecretStore? store, string? selectedProviderId)
        {
            if (store == null) return false;
            try
            {
                string legacy = store.Get(SecretIds.Llm);
                if (string.IsNullOrEmpty(legacy)) return false;   // nothing to migrate

                string owner = selectedProviderId != null
                               && LlmProviderCatalog.TryGet(selectedProviderId, out _)
                               && RequiresKey(selectedProviderId)
                    ? selectedProviderId
                    : LlmProviderCatalog.DefaultProviderId;

                string target = SecretIds.ForLlmProvider(owner);
                if (!store.Has(target)) store.Set(target, legacy);
                store.Clear(SecretIds.Llm);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
