#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Data-driven building definition (Story 4.1) — extends <see cref="UnitDefinition"/> (the Epic 3 unit-authoring
    /// shape) with the building-only fields the old <c>BuildingStore.Create</c> per-<c>BuildingType</c> switch used to
    /// bake as hardcoded constants: <see cref="ConstructionTime"/>, <see cref="SupplyBonus"/>, and
    /// <see cref="ProducesCategory"/>. All three are REQUIRED — nullable with NO silent fallback (unlike most
    /// <see cref="UnitDefinition"/> fields, which default) — so a building entry that omits one is a located import-time
    /// error (<see cref="BuildingDefinitionValidator"/>), not a silent zero/empty. <see cref="UnitDefinition.Hp"/> is no
    /// longer vestigial once a def is threaded through <c>BuildingStore.Create</c>'s new resolved-stats params.
    /// </summary>
    public class BuildingDefinition : UnitDefinition
    {
        /// <summary>Seconds to build this building (was baked per-<see cref="BuildingType"/> in the old switch).
        /// Required — null means "not authored", rejected at import by <see cref="BuildingDefinitionValidator"/>.</summary>
        [JsonPropertyName("construction_time")]
        public float? ConstructionTime { get; set; }

        /// <summary>Amount this building adds to its faction's supply cap while alive (was baked per-type; only
        /// CommandCenter was non-zero). Required — null means "not authored".</summary>
        [JsonPropertyName("supply_bonus")]
        public int? SupplyBonus { get; set; }

        /// <summary>The unit category this building produces (e.g. "Worker"/"Melee"/"Ranged"/"Siege"/"Air"), mirroring
        /// <see cref="ProjectChimera.Economy.BuildingSystem"/>'s <c>CategoryForBuilding</c> switch values. Required —
        /// null means "not authored". Authored/validated/available this story; NOT yet wired to replace
        /// <c>CategoryForBuilding</c> (left for a later story per the epic's retirement list).</summary>
        [JsonPropertyName("produces_category")]
        public string? ProducesCategory { get; set; }

        /// <summary>
        /// Research ids (Story 4.8) this building makes available for the owning faction to start — the
        /// building-side authoring half of the research content model, mirroring
        /// <see cref="UnitDefinition.Prerequisites"/>'s declaration exactly (a snake_case JSON string array,
        /// default empty, no legacy fallback). Each entry must resolve against <see cref="FactionDefinition.Research"/>
        /// — a dangling id is a located <see cref="ResearchValidator"/> import-time error. Content-only: no
        /// command-card affordance reads this yet (Story 4.11 owns that).
        /// </summary>
        [JsonPropertyName("available_research")]
        public string[] AvailableResearch { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// The minimum game version this definition requires, stamped via property initializer (deserialization only
        /// touches JSON-mapped members, so a freshly-constructed or JSON-loaded <see cref="BuildingDefinition"/> both
        /// carry this default) — matches <see cref="ContentPackageManifest.MinGameVersion"/>'s default. Excluded from
        /// JSON (authoring content does not carry a per-building version override today).
        /// </summary>
        [JsonIgnore]
        public string MinGameVersion { get; set; } = "0.1";

        /// <summary>
        /// The construction cost as a sparse resource map. Story 4.3: now a thin alias for the inherited
        /// <see cref="UnitDefinition.ResolvedCost"/> (the authored sparse <c>cost</c> map when present, else the
        /// legacy <c>{ "ore": CostOre, "crystal": CostCrystal }</c> derivation) — the name is kept so no consumer
        /// breaks, but the real authored sparse N-resource schema (<see cref="UnitDefinition.Cost"/>) now lives on
        /// the base type, generalized from Building-only to every <see cref="UnitDefinition"/> (units train with
        /// the same sparse map buildings construct with).
        /// </summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, int> ConstructionCost => ResolvedCost;
    }
}
