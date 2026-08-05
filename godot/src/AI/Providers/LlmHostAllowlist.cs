#nullable enable
using System;
using System.Collections.Generic;

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

        /// <summary>
        /// DW-589 — the pinned cloud hosts, in stable order, for the CREATOR-FACING COPY only: the
        /// <see cref="AiAvailability.HostNotAllowlisted"/> message names this set so a rejected base URL tells the
        /// creator exactly which hosts a cloud provider may reach, and so that copy can never drift from the policy.
        ///
        /// <para>PRESENTATION ONLY — this is NOT a membership test and must never become one. The enforced policy is
        /// <see cref="IsAllowed"/>'s PER-PROVIDER exact match (anthropic may reach only <see cref="AnthropicHost"/>,
        /// openrouter only <see cref="OpenRouterHost"/>); widening the check to "is on this list" would let a key
        /// stored for one provider be sent to another's endpoint. Guarded by the cross-host rows in
        /// <c>LlmHostAllowlistTests.IsAllowed_MatchesPinnedPolicy</c>.</para>
        /// </summary>
        public static IReadOnlyList<string> PinnedCloudHosts { get; } = new[] { AnthropicHost, OpenRouterHost };

        /// <summary>True iff a request to <paramref name="endpoint"/> is permitted for
        /// <paramref name="providerId"/>: an exact pinned host for a cloud provider, a loopback host for ollama,
        /// false for anything else (including an unknown provider id).</summary>
        public static bool IsAllowed(string providerId, Uri endpoint)
        {
            if (endpoint == null) return false;

            switch (providerId)
            {
                // Cloud providers: an EXACT, per-provider pinned host. Never a membership test over
                // PinnedCloudHosts — anthropic must not be reachable at openrouter.ai (or vice versa), or a key
                // stored for one provider would be sent to the other's endpoint. DW-589 (recorded scope: the
                // security policy must not widen) changed only how the refusal below is VOICED — the factory
                // classifies it AiAvailability.HostNotAllowlisted so the message names the pinned hosts instead of
                // claiming no provider is configured.
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
