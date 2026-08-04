#nullable enable

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the unavailable states plus healthy, the single classification every AI panel and the
    /// Settings Test-connection action render. <see cref="NoProvider"/>/<see cref="NoKey"/>/<see cref="HostRestricted"/>
    /// are config-derived and computed synchronously (cheap enough for panel-open);
    /// <see cref="Unreachable"/>/<see cref="FailedValidation"/> require a network round-trip (Test-connection or a
    /// failed generate). In every unavailable state the editor stays fully usable manually — only the AI affordance
    /// is disabled and explained.
    /// </summary>
    public enum AiAvailability
    {
        /// <summary>Provider configured, key present (or not required), round-trip validated — AI is usable.</summary>
        Healthy,

        /// <summary>The settings name a provider that is not in the curated catalog — nothing to talk to.</summary>
        NoProvider,

        /// <summary>A cloud provider is selected but no API key is stored (config-derived, synchronous).</summary>
        NoKey,

        /// <summary>The provider's host could not be reached (DNS/connection failure or timeout).</summary>
        Unreachable,

        /// <summary>The host answered, but not with a healthy/parseable response (bad status or malformed body).</summary>
        FailedValidation,

        /// <summary>Ollama (the local-only provider) is configured with a well-formed base URL whose host is not
        /// loopback — e.g. a LAN-hosted Ollama at <c>http://192.168.1.5:11434</c>. DW-370 (recorded decision): the
        /// loopback-only policy is KEPT, but the rejection is voiced as this state so the unavailable message can NAME
        /// the restriction instead of the misleading "no provider configured". Config-derived, synchronous.</summary>
        HostRestricted,
    }

    /// <summary>
    /// Story 8.2 — the single source of the availability microcopy, in the UX-DR52 voice (addresses the creator as
    /// "Commander"; terse, mechanical). Godot-free so the exact strings are Tier-1 assertable (each state maps to a
    /// distinct, non-empty message).
    /// </summary>
    public static class AiAvailabilityMessages
    {
        /// <summary>The creator-facing message for <paramref name="state"/> — distinct and non-empty per state.</summary>
        public static string Describe(AiAvailability state) => state switch
        {
            AiAvailability.Healthy =>
                "Commander, the AI collaborator is online. Generation authorized.",
            AiAvailability.NoProvider =>
                "Commander, no AI provider is configured. Select one in Settings › AI Provider to enable generation.",
            AiAvailability.NoKey =>
                "Commander, the selected provider needs an API key. Enter one in Settings › AI Provider to enable generation.",
            AiAvailability.Unreachable =>
                "Commander, the AI provider host is unreachable. Check the connection or base URL and run Test connection.",
            AiAvailability.FailedValidation =>
                "Commander, the AI provider answered but the response failed validation. Verify the key, model, and base URL.",
            AiAvailability.HostRestricted =>
                "Commander, Ollama is loopback-only — a LAN or remote host is not supported. Point the base URL at localhost / 127.0.0.1 in Settings › AI Provider.",
            _ =>
                "Commander, AI availability is unknown. Run Test connection in Settings › AI Provider.",
        };
    }

    /// <summary>
    /// Story 8.3 — the single failure→availability mapping shared by <c>AiAvailabilityEvaluator.TestConnectionAsync</c>
    /// and the <c>LLMService</c> generate path, so a runtime generation failure is voiced with the SAME four-state
    /// microcopy Test-connection uses instead of a raw adapter string. A network/timeout failure is
    /// <see cref="AiAvailability.Unreachable"/>; a reached-but-unhealthy answer (bad status / malformed body) is
    /// <see cref="AiAvailability.FailedValidation"/>. Only ever called on a non-Ok result.
    /// </summary>
    public static class AiAvailabilityMap
    {
        /// <summary>Map a provider <see cref="NormalizedFailure"/> to the creator-facing <see cref="AiAvailability"/> state.</summary>
        public static AiAvailability FromFailure(NormalizedFailure failure) => failure switch
        {
            NormalizedFailure.Unreachable => AiAvailability.Unreachable,
            _                             => AiAvailability.FailedValidation, // HttpError / MalformedResponse
        };
    }
}
