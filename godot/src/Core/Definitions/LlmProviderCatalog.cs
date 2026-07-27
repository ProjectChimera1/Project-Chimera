#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 8.1 — the curated, code-backed provider/model catalog. The single source of truth for the three
    /// supported LLM providers (Anthropic, local Ollama, OpenRouter): each carries a display name, a default base
    /// URL, and a curated model list. Godot-free (no <c>using Godot;</c>) so it lands under <c>src/Core</c> and is
    /// Tier-1 testable / AOT-clean.
    ///
    /// <para>This is CATALOG DATA only. Story 8.1 persists the creator's provider/model/baseUrl choice into
    /// <see cref="SettingsData"/>; Story 8.2's provider-abstraction + four-state UI is what CONSUMES this list (the
    /// per-provider model dropdown, the free-text override, the base-URL default). The persisted model
    /// (<see cref="SettingsData.LlmModel"/>) can be a curated pick OR a free-text override — this list never
    /// constrains what can be saved, it only offers curated defaults.</para>
    /// </summary>
    public static class LlmProviderCatalog
    {
        /// <summary>The default provider id used when a settings file names an unknown provider (see
        /// <see cref="SettingsData.MigrateForward"/>).</summary>
        public const string DefaultProviderId = "anthropic";

        /// <summary>The forward-migrating default model — a fresh or unknown-model settings file lands here.</summary>
        public const string DefaultModel = "claude-sonnet-5";

        /// <summary>One curated provider entry: stable id + display name + default base URL + curated model list.</summary>
        public sealed class ProviderInfo
        {
            public string Id { get; }
            public string DisplayName { get; }
            public string DefaultBaseUrl { get; }
            public IReadOnlyList<string> Models { get; }

            public ProviderInfo(string id, string displayName, string defaultBaseUrl, IReadOnlyList<string> models)
            {
                Id             = id;
                DisplayName    = displayName;
                DefaultBaseUrl = defaultBaseUrl;
                Models         = models;
            }
        }

        // The curated catalog, in stable authored order. Model ids are curated defaults offered in the picker; the
        // creator can still free-text any model (persisted verbatim in SettingsData.LlmModel).
        private static readonly ProviderInfo[] _providers =
        {
            new("anthropic", "Anthropic (Claude)", "https://api.anthropic.com", new[]
            {
                "claude-opus-4-8",
                "claude-sonnet-5",
                "claude-haiku-4-5",
            }),
            new("ollama", "Ollama (local)", "http://localhost:11434", new[]
            {
                "llama3.1",
                "mistral",
                "qwen2.5",
            }),
            new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", new[]
            {
                "anthropic/claude-sonnet-5",
                "openai/gpt-4o",
                "meta-llama/llama-3.1-70b-instruct",
            }),
        };

        /// <summary>All curated providers, in stable authored order.</summary>
        public static IReadOnlyList<ProviderInfo> Providers => _providers;

        /// <summary>Look up a provider by id. Returns <c>true</c> and sets <paramref name="info"/> on a hit; returns
        /// <c>false</c> (with <paramref name="info"/> = null) for an unknown id. Case-sensitive on the stable id.</summary>
        public static bool TryGet(string id, out ProviderInfo? info)
        {
            foreach (ProviderInfo p in _providers)
            {
                if (string.Equals(p.Id, id, StringComparison.Ordinal))
                {
                    info = p;
                    return true;
                }
            }
            info = null;
            return false;
        }
    }
}
