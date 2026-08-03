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
        /// <summary>Tracks whether <c>hp</c> was assigned through a <see cref="BuildingDefinition"/>-typed reference
        /// (JSON deserialize, object initializer, or a <c>BuildingDefinition</c>-typed <c>def.Hp = v</c>) — false when
        /// hp was never authored that way. Set by the <see cref="Hp"/> setter shadow below (DW-55). NOTE: the shadow is
        /// non-virtual, so a write through a <see cref="UnitDefinition"/>-typed reference binds the base setter and does
        /// NOT set this flag — every DW-55 authoring path (deserialize/initializer/clone/DoCreate) is
        /// BuildingDefinition-typed, so this is correct in practice.</summary>
        private bool _hpAuthored;

        /// <summary>
        /// DW-55: a buildings-only <c>new</c>-shadow of <see cref="UnitDefinition.Hp"/> whose ONLY job is to record
        /// whether <c>hp</c> was ever authored. The getter returns <c>base.Hp</c> verbatim (so <c>BuildingStore.Create</c>,
        /// cloning, and every existing <c>def.Hp</c> read are byte-unchanged); the setter forwards to <c>base.Hp</c> AND
        /// flags <see cref="_hpAuthored"/>. STJ binds this derived member (verified on net8 in-box System.Text.Json — no
        /// member-collision, single <c>"hp"</c> key on serialize), so an omitted <c>hp</c> leaves <see cref="HpAuthored"/>
        /// false while still defaulting to 100, letting <see cref="BuildingDefinitionValidator"/> distinguish an omitted
        /// <c>hp</c> from an authored <c>100</c>. Deliberately buildings-only — <see cref="UnitDefinition.Hp"/> is left
        /// untouched to avoid the UnitDefinition-wide blast radius that touches unit spawning/validation.
        /// </summary>
        [JsonPropertyName("hp")]
        public new float Hp
        {
            get => base.Hp;
            set { base.Hp = value; _hpAuthored = true; }
        }

        /// <summary>DW-55: true when <see cref="Hp"/> was assigned through a <see cref="BuildingDefinition"/>-typed
        /// reference (see <see cref="_hpAuthored"/> for the non-virtual-shadow caveat); false when never authored that
        /// way. Excluded from JSON — a computed presence flag, never serialized.</summary>
        [JsonIgnore]
        public bool HpAuthored => _hpAuthored;

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
        /// OPTIONAL (unlike <see cref="ProducesCategory"/>, which is required) authored override selecting WHICH single
        /// command-card producer surface this building renders — one of <c>train</c>/<c>research</c>/<c>shop</c>/
        /// <c>revive</c>/<c>none</c> (case-insensitive), validated at import by <see cref="BuildingDefinitionValidator"/>.
        /// Null (the default, and the only value shipped content carries) means "derive the surface" — the
        /// <see cref="ProjectChimera.Economy.BuildingSystem.ResolveCommandCardSurface"/> priority derivation preserves
        /// today's single-capability behaviour. When authored, it maps DIRECTLY to the rendered surface, letting a
        /// dual-capability building (e.g. a producer that also <c>revives_heroes</c>) opt into a single chosen surface
        /// (DW-31); downstream per-grid capability guards still apply.
        /// </summary>
        [JsonPropertyName("command_card_producer")]
        public string? CommandCardProducer { get; set; }

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
        /// DW-169: OPTIONAL authored nav-obstacle footprint override, <c>[width_x, height_y, depth_z]</c> in world
        /// units. When authored (and valid per <see cref="TryGetNavFootprint"/>) it is the building's navmesh-carve
        /// box for <c>NavObstacleManager</c> — built-ins and customs alike. Omitted (the default, and the only value
        /// shipped content carries): a built-in id keeps its legacy fixed footprint table entry, a Custom/authored id
        /// derives its footprint from the mesh AABB (× <see cref="UnitDefinition.MeshScale"/>) with the guarded
        /// 5×3×5 default as last resort — see <c>ProjectChimera.UI.BuildingNavFootprint</c> for the resolution order.
        /// Presentation-only (navmesh carve size): NEVER folded into SimChecksum / CanonicalModelHash / StartStateHash.
        /// Malformed values (wrong length / non-finite / non-positive) are a located import-time error
        /// (<see cref="BuildingDefinitionValidator"/>). Omitted from serialization when null so existing content
        /// re-saves byte-identically.
        /// </summary>
        [JsonPropertyName("nav_footprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? NavFootprint { get; set; }

        /// <summary>
        /// DW-169: the single validity rule for <see cref="NavFootprint"/>, shared by the import-time validator and
        /// the footprint-resolution policy so they can never drift. Returns true — with the authored dimensions —
        /// only when the field is authored as exactly 3 finite, strictly-positive values; false when omitted (null)
        /// or malformed. Pure, Godot-free.
        /// </summary>
        public bool TryGetNavFootprint(out float x, out float y, out float z)
        {
            x = y = z = 0f;
            float[]? fp = NavFootprint;
            if (fp == null || fp.Length != 3) return false;
            for (int i = 0; i < 3; i++)
                if (!float.IsFinite(fp[i]) || fp[i] <= 0f) return false;
            x = fp[0]; y = fp[1]; z = fp[2];
            return true;
        }

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
