#nullable enable

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the four unavailable states plus healthy, the single classification every AI panel and the
    /// Settings Test-connection action render. <see cref="NoProvider"/>/<see cref="NoKey"/> are config-derived and
    /// computed synchronously (cheap enough for panel-open); <see cref="Unreachable"/>/<see cref="FailedValidation"/>
    /// require a network round-trip (Test-connection or a failed generate). In every unavailable state the editor
    /// stays fully usable manually — only the AI affordance is disabled and explained.
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
    }

    /// <summary>
    /// Story 8.2 — the single source of the four-state microcopy, in the UX-DR52 voice (addresses the creator as
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
            _ =>
                "Commander, AI availability is unknown. Run Test connection in Settings › AI Provider.",
        };
    }
}
