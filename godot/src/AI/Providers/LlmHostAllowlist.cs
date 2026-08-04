#nullable enable
using System;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the pinned cloud-host guard. A cloud provider request (anthropic, openrouter) may only proceed
    /// when the resolved endpoint host is on this small pinned allowlist; a base-URL override pointing at any other
    /// host is rejected before any network call is made. Ollama is a LOCAL provider — only a loopback host is
    /// permitted (and no key is required). Godot-free so the rule is Tier-1 assertable.
    /// </summary>
    public static class LlmHostAllowlist
    {
        /// <summary>The Anthropic cloud host.</summary>
        public const string AnthropicHost = "api.anthropic.com";

        /// <summary>The OpenRouter cloud host.</summary>
        public const string OpenRouterHost = "openrouter.ai";

        /// <summary>True iff a request to <paramref name="endpoint"/> is permitted for
        /// <paramref name="providerId"/>: an exact pinned host for a cloud provider, a loopback host for ollama,
        /// false for anything else (including an unknown provider id).</summary>
        public static bool IsAllowed(string providerId, Uri endpoint)
        {
            if (endpoint == null) return false;

            switch (providerId)
            {
                case "anthropic":
                    return HostEquals(endpoint.Host, AnthropicHost);
                case "openrouter":
                    return HostEquals(endpoint.Host, OpenRouterHost);
                case "ollama":
                    // Local provider — only loopback (localhost / 127.0.0.0/8 / ::1). Never a remote host.
                    // DW-370 (recorded decision 2026-07-30): loopback-only is KEPT (not widened to RFC-1918);
                    // the factory voices the rejection as AiAvailability.HostRestricted so the creator-facing
                    // message names this restriction.
                    return endpoint.IsLoopback;
                default:
                    return false;
            }
        }

        private static bool HostEquals(string host, string expected)
            => string.Equals(host, expected, StringComparison.OrdinalIgnoreCase);
    }
}
