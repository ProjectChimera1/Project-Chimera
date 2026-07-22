#nullable enable
using System;
using System.Net.Http;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the single construction site for an <see cref="ILLMProvider"/>. Resolves the provider from the
    /// catalog, the base URL (settings override else the catalog default), the model, and the key
    /// (<see cref="ISecretStore.Get"/> with <see cref="SecretIds.Llm"/> — never an <c>[Export]</c> field or
    /// <see cref="SettingsData"/>), and enforces the pinned host allowlist BEFORE any adapter is built. Emits the two
    /// synchronous unavailable states (<see cref="AiAvailability.NoProvider"/>/<see cref="AiAvailability.NoKey"/>)
    /// rather than throwing. Godot-free / unit-testable.
    /// </summary>
    public static class LlmProviderFactory
    {
        /// <summary>Build the adapter for the configured provider. On success returns <c>true</c> with
        /// <paramref name="provider"/> set and <paramref name="failure"/> = <see cref="AiAvailability.Healthy"/>. On a
        /// synchronous-unavailable case returns <c>false</c> with <paramref name="provider"/> null and
        /// <paramref name="failure"/> set to the reason:
        /// <list type="bullet">
        ///   <item><see cref="AiAvailability.NoProvider"/> — unknown provider id, or an un-parseable / non-allowlisted host</item>
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
            string key = "";
            if (RequiresKey(providerId))
            {
                key = secretStore?.Get(SecretIds.Llm) ?? "";
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
                failure = AiAvailability.NoProvider;
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
    }
}
