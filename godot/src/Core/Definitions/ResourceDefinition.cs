#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Authored scenario-declared resource registry entry (Story 4.3) — the ordered, creator-facing metadata a
    /// scenario declares for each resource id it wants to expose (id/display/starting-amount/collection-model).
    /// Mirrors Story 4.1's "authored, validated, not yet wired" <see cref="BuildingDefinition.ProducesCategory"/>
    /// precedent: <see cref="ScenarioValidator"/> checks this block for internal well-formedness (unique non-empty
    /// ids, non-negative finite starting amounts), but nothing threads <see cref="StartingAmount"/>/
    /// <see cref="CollectionModel"/> into <see cref="ScenarioApplier"/> yet — starting balances still come from
    /// <see cref="ScenarioPlayerSlot.StartOre"/>/<see cref="ScenarioPlayerSlot.StartCrystal"/>, untouched by this
    /// story. A creator MAY declare a resource beyond "ore"/"crystal" (e.g. "gems") as forward-looking metadata;
    /// no unit/building cost can reference it until a future story backs it with real <see cref="ResourceStore"/>
    /// balance storage AND extends <see cref="ResourceCostValidator"/>'s known-id set accordingly (see the spec's
    /// Design Notes).
    /// </summary>
    public class ResourceDefinition
    {
        /// <summary>The resource id (e.g. "ore", "crystal", "gems") — the sparse cost-map key namespace this
        /// registry documents. Must be non-empty and unique within a scenario's <see cref="ScenarioData.Resources"/>.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Human-readable display name for this resource.</summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>Starting balance for this resource. Authoring metadata only this story — NOT wired to any
        /// starting balance write (that stays on <see cref="ScenarioPlayerSlot"/>). Default 0.</summary>
        [JsonPropertyName("starting_amount")]
        public float StartingAmount { get; set; } = 0f;

        /// <summary>The collection model this resource uses — one of <see cref="KnownCollectionModels"/> ("Gather",
        /// "Income", "Streaming"). Authored/unused this story (Story 4.7 wires collection models), but validated
        /// against the closed set now so a typo is rejected at import instead of silently doing nothing once 4.7
        /// tries to consume it. Default "Gather" (today's only model).</summary>
        [JsonPropertyName("collection_model")]
        public string CollectionModel { get; set; } = "Gather";

        /// <summary>The closed set of collection-model names <see cref="ScenarioValidator"/> accepts for
        /// <see cref="CollectionModel"/> (Story 4.7 wires the behavior; this story only gates the vocabulary).</summary>
        public static readonly string[] KnownCollectionModels = { "Gather", "Income", "Streaming" };
    }
}
