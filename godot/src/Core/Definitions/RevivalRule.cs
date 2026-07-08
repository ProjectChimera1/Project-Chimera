#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The per-scenario hero-revival rule (Story 3.14) — the authored, level-scaled cost/time/HP-fraction a fallen hero
    /// is revived with, plus a master <see cref="Enabled"/> toggle. A nullable net-new block on
    /// <see cref="ScenarioData.RevivalRule"/> (JSON <c>revival_rule</c>): <c>null</c> ⇒ use <see cref="Default"/> (revival
    /// enabled with sensible defaults — every existing scenario behaves the same), and the block is OMITTED from
    /// serialization when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the
    /// <see cref="PersistenceManifest"/> omit-when-null precedent) so a scenario without one serializes byte-for-byte
    /// identically, moving no golden.
    ///
    /// <para><b>Authoring-only (mirrors <see cref="PersistenceManifest"/>).</b> PURE AUTHORING DATA. The sim never reads
    /// this class directly — it is resolved ONCE (float→<see cref="ProjectChimera.Core.Fixed"/>) at the single load
    /// boundary into the sim-facing <see cref="ProjectChimera.Core.RevivalRuleRuntime"/>, never quantized inside a tick.
    /// So there is deliberately NO checksum/hash fold: an unread nullable POCO moves no golden. Cost/time scale LINEARLY
    /// with the hero level being revived: <c>base + perLevel × Level</c>.</para>
    ///
    /// <para><b>Determinism.</b> Godot-free (<c>src/Core/Definitions</c>), plain <c>int</c>/<c>float</c>/<c>bool</c>
    /// authoring numbers. <see cref="ScenarioValidator"/> range-checks it fail-closed at editor Save AND the pre-tick
    /// gate so a hand-edited/cheat rule is rejected.</para>
    /// </summary>
    public sealed class RevivalRule
    {
        /// <summary>Master toggle. <c>true</c> (default) ⇒ a fallen hero enters the awaiting-revival state and can be
        /// revived; <c>false</c> ⇒ a fallen hero leaves the field like any unit (its row stays Alive so persistence still
        /// finalizes its Level/Xp per FR-7a) and NO revival is offered.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Flat ore cost to revive (added to the per-level term).</summary>
        [JsonPropertyName("cost_ore_base")]
        public int CostOreBase { get; set; } = 100;

        /// <summary>Ore cost added per hero level being revived.</summary>
        [JsonPropertyName("cost_ore_per_level")]
        public int CostOrePerLevel { get; set; } = 25;

        /// <summary>Flat crystal cost to revive (added to the per-level term).</summary>
        [JsonPropertyName("cost_crystal_base")]
        public int CostCrystalBase { get; set; } = 0;

        /// <summary>Crystal cost added per hero level being revived.</summary>
        [JsonPropertyName("cost_crystal_per_level")]
        public int CostCrystalPerLevel { get; set; } = 0;

        /// <summary>Flat revival countdown in seconds (added to the per-level term).</summary>
        [JsonPropertyName("time_base_seconds")]
        public float TimeBaseSeconds { get; set; } = 10f;

        /// <summary>Countdown seconds added per hero level being revived.</summary>
        [JsonPropertyName("time_per_level_seconds")]
        public float TimePerLevelSeconds { get; set; } = 2f;

        /// <summary>Fraction of max HP the revived hero respawns with. Validated finite &amp; in <c>(0, 1]</c>.</summary>
        [JsonPropertyName("revive_hp_fraction")]
        public float ReviveHpFraction { get; set; } = 0.5f;

        /// <summary>A member-wise copy (all fields are value types) — the editor undo/history path, mirroring
        /// <see cref="PersistenceManifest.Clone"/>.</summary>
        public RevivalRule Clone() => new RevivalRule
        {
            Enabled             = Enabled,
            CostOreBase         = CostOreBase,
            CostOrePerLevel     = CostOrePerLevel,
            CostCrystalBase     = CostCrystalBase,
            CostCrystalPerLevel = CostCrystalPerLevel,
            TimeBaseSeconds     = TimeBaseSeconds,
            TimePerLevelSeconds = TimePerLevelSeconds,
            ReviveHpFraction    = ReviveHpFraction,
        };

        /// <summary>The rule applied when a scenario omits <c>revival_rule</c> (null): revival ENABLED with sensible
        /// defaults, so every shipped scenario keeps a working revive path with no authoring.</summary>
        public static RevivalRule Default => new RevivalRule();
    }
}
