#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the Godot-free availability evaluator. Splits the availability classification along its natural
    /// seam:
    /// <list type="bullet">
    ///   <item><see cref="EvaluateConfig"/> — synchronous, config-derived: <see cref="AiAvailability.NoProvider"/> /
    ///         <see cref="AiAvailability.NoKey"/> / <see cref="AiAvailability.HostRestricted"/> /
    ///         <see cref="AiAvailability.HostNotAllowlisted"/> or a
    ///         Healthy-candidate. Cheap enough to run on panel-open.</item>
    ///   <item><see cref="TestConnectionAsync"/> — builds the provider via <see cref="LlmProviderFactory"/>, runs a
    ///         minimal round-trip, and maps the result to <see cref="AiAvailability.Healthy"/> /
    ///         <see cref="AiAvailability.Unreachable"/> / <see cref="AiAvailability.FailedValidation"/>.</item>
    /// </list>
    /// The selected provider is authoritative: there is NO fallback — a failing provider is reported as-is.
    /// </summary>
    public sealed class AiAvailabilityEvaluator
    {
        private readonly HttpClient _http;

        /// <summary>A minimal round-trip prompt. Cheap and provider-agnostic — only the fact that a parseable
        /// completion comes back matters for the Healthy classification.</summary>
        private static readonly NormalizedRequest TestPrompt = new(
            systemPrompt: "You are a connection probe. Reply with a single word.",
            userMessage:  "ping",
            maxTokens:    16);

        public AiAvailabilityEvaluator(HttpClient http) => _http = http;

        /// <summary>Synchronous, config-only classification. Returns <see cref="AiAvailability.NoProvider"/> when the
        /// settings name a provider outside the catalog OR an un-parseable base URL; a well-formed base URL the host
        /// allowlist rejects is voiced as the state that NAMES the policy rather than the generic one —
        /// <see cref="AiAvailability.HostRestricted"/> for ollama's loopback-only rule (DW-370) and
        /// <see cref="AiAvailability.HostNotAllowlisted"/> for a cloud provider's pinned-host rule (DW-589);
        /// <see cref="AiAvailability.NoKey"/> when a cloud provider has no stored key, else
        /// <see cref="AiAvailability.Healthy"/> (a candidate — reachability is only confirmed by
        /// <see cref="TestConnectionAsync"/>). No network call.
        ///
        /// <para>Delegates the classification to <see cref="LlmProviderFactory.TryCreate"/> (which builds no network
        /// connection) so the synchronous state the panels gate their Generate button on can NEVER disagree with what
        /// the actual generate / Test-connection path will accept: catalog membership, key presence, the base-URL
        /// parse, AND the host allowlist are all enforced by the one predicate. Building the adapter object is a cheap,
        /// side-effect-free allocation — no request is sent.</para></summary>
        public AiAvailability EvaluateConfig(SettingsData settings, ISecretStore secretStore)
            => LlmProviderFactory.TryCreate(settings, secretStore!, _http, out _, out AiAvailability failure)
                ? AiAvailability.Healthy
                : failure;

        /// <summary>Build the configured provider and run a minimal round-trip. Returns the config-derived state when
        /// the factory refuses (NoProvider/NoKey/HostRestricted/HostNotAllowlisted); otherwise maps the round-trip
        /// result: success →
        /// <see cref="AiAvailability.Healthy"/>, <see cref="NormalizedFailure.Unreachable"/> →
        /// <see cref="AiAvailability.Unreachable"/>, and a reached-but-unhealthy answer
        /// (<see cref="NormalizedFailure.HttpError"/>/<see cref="NormalizedFailure.MalformedResponse"/>) →
        /// <see cref="AiAvailability.FailedValidation"/>. No fallback.</summary>
        public async Task<AiAvailability> TestConnectionAsync(
            SettingsData settings, ISecretStore secretStore, CancellationToken ct)
        {
            if (!LlmProviderFactory.TryCreate(settings, secretStore, _http, out ILLMProvider? provider, out AiAvailability failure))
                return failure; // NoProvider / NoKey / HostRestricted / HostNotAllowlisted — sync, no request attempted

            NormalizedResult result = await provider!.GenerateAsync(TestPrompt, ct);
            if (result.Ok)
                return AiAvailability.Healthy;

            // Story 8.3: the failure→state mapping is now the shared AiAvailabilityMap so the generate path and
            // Test-connection classify identically (a runtime failure is voiced with the same availability microcopy).
            return AiAvailabilityMap.FromFailure(result.Failure);
        }
    }
}
