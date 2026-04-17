#nullable enable
using System.Text.Json.Serialization;
using ProjectChimera.Combat;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Data-driven unit definition loaded from JSON.
    /// One entry per unit type in a faction.
    /// </summary>
    public class UnitDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>One of: Worker, Melee, Ranged, Siege, Air, Structure</summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = "Melee";

        /// <summary>
        /// Res path to the GLB file (e.g. "res://assets/models/factions/alpha/warrior.glb").
        /// If null or file missing, MeshLoader falls back to a box placeholder.
        /// </summary>
        [JsonPropertyName("mesh_path")]
        public string? MeshPath { get; set; }

        [JsonPropertyName("hp")]
        public float Hp { get; set; } = 100f;

        [JsonPropertyName("speed")]
        public float Speed { get; set; } = 4f;

        [JsonPropertyName("attack_damage")]
        public float AttackDamage { get; set; } = 10f;

        [JsonPropertyName("attack_range")]
        public float AttackRange { get; set; } = 5f;

        /// <summary>Seconds between attacks.</summary>
        [JsonPropertyName("attack_speed")]
        public float AttackSpeed { get; set; } = 1f;

        /// <summary>Normal | Pierce | Siege | Magic</summary>
        [JsonPropertyName("damage_type")]
        public string DamageType { get; set; } = "Normal";

        /// <summary>Unarmored | Light | Medium | Heavy | Fortified</summary>
        [JsonPropertyName("armor_type")]
        public string ArmorType { get; set; } = "Unarmored";

        /// <summary>Ore cost.</summary>
        [JsonPropertyName("cost_ore")]
        public int CostOre { get; set; } = 50;

        /// <summary>Crystal cost (advanced units only).</summary>
        [JsonPropertyName("cost_crystal")]
        public int CostCrystal { get; set; } = 0;

        /// <summary>Supply consumed by one of these units.</summary>
        [JsonPropertyName("supply")]
        public int Supply { get; set; } = 1;

        /// <summary>Visual scale applied to the unit mesh at import time.</summary>
        [JsonPropertyName("mesh_scale")]
        public float MeshScale { get; set; } = 1f;

        /// <summary>Seconds to train this unit at a producing building.</summary>
        [JsonPropertyName("train_time")]
        public float TrainTime { get; set; } = 8f;

        /// <summary>Vision radius in world units. Stamped each tick by FogOfWarSystem.</summary>
        [JsonPropertyName("vision_range")]
        public float VisionRange { get; set; } = 8f;

        /// <summary>
        /// AoE splash radius on projectile hit (world units). 0 = no splash (default).
        /// Applies to Siege archetype; dealt at full damage to all enemies in radius.
        /// </summary>
        [JsonPropertyName("splash_radius")]
        public float SplashRadius { get; set; } = 0f;

        /// <summary>
        /// Building-type IDs that must be alive and fully constructed (for the same faction)
        /// before this unit can be trained or this building can be placed.
        /// Example: ["barracks"] means a completed Barracks is required.
        /// Empty array = no prerequisites.
        /// </summary>
        [JsonPropertyName("prerequisites")]
        public string[] Prerequisites { get; set; } = System.Array.Empty<string>();

        // ── Enum conversions ────────────────────────────────────────────────────

        /// <summary>DamageType string from JSON resolved to enum.</summary>
        public DamageType ParsedDamageType => DamageType switch
        {
            "Pierce" => Combat.DamageType.Pierce,
            "Siege"  => Combat.DamageType.Siege,
            "Magic"  => Combat.DamageType.Magic,
            _        => Combat.DamageType.Normal,
        };

        /// <summary>ArmorType string from JSON resolved to enum.</summary>
        public ArmorType ParsedArmorType => ArmorType switch
        {
            "Light"     => Combat.ArmorType.Light,
            "Medium"    => Combat.ArmorType.Medium,
            "Heavy"     => Combat.ArmorType.Heavy,
            "Fortified" => Combat.ArmorType.Fortified,
            _           => Combat.ArmorType.Unarmored,
        };
    }
}
