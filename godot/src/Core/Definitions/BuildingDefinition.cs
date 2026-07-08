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
        /// The minimum game version this definition requires, stamped via property initializer (deserialization only
        /// touches JSON-mapped members, so a freshly-constructed or JSON-loaded <see cref="BuildingDefinition"/> both
        /// carry this default) — matches <see cref="ContentPackageManifest.MinGameVersion"/>'s default. Excluded from
        /// JSON (authoring content does not carry a per-building version override today).
        /// </summary>
        [JsonIgnore]
        public string MinGameVersion { get; set; } = "0.1";

        /// <summary>
        /// The construction cost as a sparse resource map, COMPUTED (not a raw JSON field) from the inherited
        /// <see cref="UnitDefinition.CostOre"/>/<see cref="UnitDefinition.CostCrystal"/> — keys <c>"ore"</c>/<c>"crystal"</c>,
        /// omitted when 0 (a free resource is simply absent from the map, not a zero entry). Story 4.3 owns the real
        /// authored sparse N-resource schema; this derives today's two-resource map from the existing fields rather than
        /// pre-empting that schema with a second, soon-to-be-replaced JSON surface.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, int> ConstructionCost
        {
            get
            {
                var map = new Dictionary<string, int>();
                if (CostOre != 0) map["ore"] = CostOre;
                if (CostCrystal != 0) map["crystal"] = CostCrystal;
                return map;
            }
        }
    }
}
