#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Win condition evaluated each simulation tick by the (future) WinConditionSystem.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum WinCondition
    {
        /// <summary>First faction to have all buildings destroyed loses.</summary>
        DestroyAllBuildings,
        /// <summary>First faction to have all units killed loses.</summary>
        EliminateAllUnits,
    }

    /// <summary>
    /// Maps a player slot (0-based index) to a faction JSON, starting resources,
    /// and the world position where workers return to deposit ore.
    /// </summary>
    public class ScenarioPlayerSlot
    {
        /// <summary>0-based slot index: 0 = Player1, 1 = Player2.</summary>
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        /// <summary>res:// path to the faction JSON file for this slot.</summary>
        [JsonPropertyName("faction_json")]
        public string FactionJson { get; set; } = "";

        /// <summary>Starting ore balance for this slot's faction.</summary>
        [JsonPropertyName("start_ore")]
        public float StartOre { get; set; } = 200f;

        /// <summary>Starting crystal balance for this slot's faction. Defaults to 0 and is OMITTED from serialization
        /// when 0 (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>), so pre-existing scenarios, procedurally
        /// generated maps, and the in-code golden/hash mirrors that never set it serialize byte-for-byte identically —
        /// no map-identity or golden hash moves. The 0f initializer equals the float type-default, so omit-then-restore
        /// round-trips exactly. NOTE: intentionally NOT folded into <see cref="CanonicalModelHash"/> yet; see the
        /// documented-exclusions note there (deferred alongside the Triggers gap until lockstep MP makes the lobby
        /// start-state handshake load-bearing).</summary>
        [JsonPropertyName("start_crystal")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float StartCrystal { get; set; } = 0f;

        /// <summary>World X of the faction deposit / rally base point.</summary>
        [JsonPropertyName("base_x")]
        public float BaseX { get; set; }

        /// <summary>World Z of the faction deposit / rally base point.</summary>
        [JsonPropertyName("base_z")]
        public float BaseZ { get; set; }
    }

    /// <summary>
    /// A resource node to be created on the map when the scenario loads.
    /// </summary>
    public class ScenarioResourceNode
    {
        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }

        /// <summary>Total ore supply in this node.</summary>
        [JsonPropertyName("supply")]
        public float Supply { get; set; } = 400f;

        /// <summary>Ore per second delivered by each active gatherer.</summary>
        [JsonPropertyName("rate")]
        public float Rate { get; set; } = 5f;

        [JsonPropertyName("max_gatherers")]
        public int MaxGatherers { get; set; } = 4;
    }

    /// <summary>
    /// A pre-placed building entry in a scenario.
    /// </summary>
    public class ScenarioBuilding
    {
        /// <summary>
        /// BuildingType enum name: "CommandCenter", "Barracks", "ArcheryRange", "SiegeWorkshop".
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "CommandCenter";

        /// <summary>0-based player slot that owns this building.</summary>
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }

        /// <summary>
        /// When true the building is immediately fully constructed (ConstructionTimer = 0).
        /// When false it starts under construction — useful for scenario scripting hooks.
        /// </summary>
        [JsonPropertyName("pre_built")]
        public bool PreBuilt { get; set; } = true;
    }

    /// <summary>
    /// A pre-placed unit entry in a scenario.
    /// The unit_id must match an entry in the slot's faction JSON.
    /// </summary>
    public class ScenarioUnit
    {
        /// <summary>Matches <see cref="UnitDefinition.Id"/> in the slot's faction JSON.</summary>
        [JsonPropertyName("unit_id")]
        public string UnitId { get; set; } = "worker";

        /// <summary>0-based player slot that owns this unit.</summary>
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }
    }

    /// <summary>
    /// A pre-placed item entry in a scenario (Story 3.15). The item_id must match an entry in the loaded
    /// <see cref="ItemRegistry"/>; the item is created on the ground at (x, z) with its authored charge count.
    /// Mirrors <see cref="ScenarioUnit"/>.
    /// </summary>
    public class ScenarioItem
    {
        /// <summary>Matches <see cref="ItemDefinition.Id"/> in the loaded item registry.</summary>
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = "";

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }
    }

    /// <summary>
    /// Full scenario definition. Contains everything needed to reconstruct a match:
    /// terrain reference, player faction assignments, resource node layout,
    /// pre-placed buildings and units, and the win condition.
    ///
    /// Loaded from JSON by <see cref="ScenarioSerializer"/>.
    /// Created and saved by the Creation Suite editor tools (Phase 2).
    /// </summary>
    public class ScenarioData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// res:// path to the Terrain3D resource. Empty string = use flat plane fallback (Phase 1).
        /// </summary>
        [JsonPropertyName("terrain_ref")]
        public string TerrainRef { get; set; } = "";

        /// <summary>
        /// Half-extent of the walkable area in world units. Camera and NavMesh are bounded by this.
        /// </summary>
        [JsonPropertyName("map_bounds")]
        public float MapBounds { get; set; } = 120f;

        [JsonPropertyName("win_condition")]
        public WinCondition WinCondition { get; set; } = WinCondition.DestroyAllBuildings;

        [JsonPropertyName("player_slots")]
        public ScenarioPlayerSlot[] PlayerSlots { get; set; } = System.Array.Empty<ScenarioPlayerSlot>();

        [JsonPropertyName("resource_nodes")]
        public ScenarioResourceNode[] ResourceNodes { get; set; } = System.Array.Empty<ScenarioResourceNode>();

        [JsonPropertyName("buildings")]
        public ScenarioBuilding[] Buildings { get; set; } = System.Array.Empty<ScenarioBuilding>();

        [JsonPropertyName("units")]
        public ScenarioUnit[] Units { get; set; } = System.Array.Empty<ScenarioUnit>();

        /// <summary>
        /// Scenario triggers evaluated each tick by ScenarioDirector.
        /// Authored via the Trigger Editor (ECA UI) or natural language → LLM → JSON.
        /// </summary>
        [JsonPropertyName("triggers")]
        public TriggerDefinition[] Triggers { get; set; } = System.Array.Empty<TriggerDefinition>();

        /// <summary>
        /// The per-scenario hero-persistence contract (Story 3.8): which attributes carry forward between matches +
        /// a master enable toggle. NULL ⇒ persistence not configured (the default for every existing scenario), and
        /// the block is OMITTED from serialization when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the
        /// <see cref="ScenarioPlayerSlot.StartCrystal"/> omit-when-default precedent) — so a scenario with no manifest
        /// serializes byte-for-byte identically, moving no golden. Authoring-only: no runtime consumer until Story 3.9,
        /// so it is intentionally NOT folded into <see cref="CanonicalModelHash"/> / <see cref="StartStateHash"/> /
        /// <c>SimChecksum</c> (D-2). Validated (fail-closed) by <see cref="PersistenceManifestValidator"/> at editor
        /// Save AND at the pre-tick <see cref="ScenarioValidator"/> gate.
        /// </summary>
        [JsonPropertyName("persistence_manifest")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PersistenceManifest? PersistenceManifest { get; set; }

        /// <summary>
        /// The per-scenario hero-revival rule (Story 3.14): the level-scaled cost/time/HP-fraction a fallen hero is
        /// revived with + a master <c>enabled</c> toggle. NULL ⇒ use <see cref="RevivalRule.Default"/> (revival enabled
        /// with sensible defaults — every existing scenario behaves the same), and the block is OMITTED from
        /// serialization when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the
        /// <see cref="PersistenceManifest"/> omit-when-null precedent) — so a scenario with no rule serializes
        /// byte-for-byte identically, moving no golden. Authoring-only: resolved once (float→Fixed) into
        /// <see cref="RevivalRuleRuntime"/> at scenario-apply, never folded into any checksum/hash. Validated
        /// (fail-closed) by <see cref="ScenarioValidator"/> when present.
        /// </summary>
        [JsonPropertyName("revival_rule")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RevivalRule? RevivalRule { get; set; }

        /// <summary>
        /// Pre-placed map items (Story 3.15). Created on the ground at scenario-apply via the <see cref="ItemRegistry"/>.
        /// NULL (the default) ⇒ no items, and the block is OMITTED from serialization when null
        /// (<see cref="JsonIgnoreCondition.WhenWritingNull"/>) so every existing scenario serializes byte-for-byte
        /// identically (moving no map-identity / procedural-generator hash). Read as <c>Items ?? Array.Empty</c>. Folded
        /// into <see cref="StartStateHash"/> (v2) so a mismatched placed-item loadout is rejectable at the handshake.
        /// </summary>
        [JsonPropertyName("items")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScenarioItem[]? Items { get; set; }

        /// <summary>
        /// The per-scenario USABLE hero inventory slot count (Story 3.15, D-6) ∈ <c>[1, 6]</c>. NULL ⇒ the full
        /// <see cref="HeroStore.INVENTORY_SLOTS"/> stride (6), and the field is OMITTED from serialization when null
        /// (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the <see cref="RevivalRule"/> omit-when-null precedent) —
        /// so an existing scenario serializes byte-identically. Validated fail-closed by <see cref="ScenarioValidator"/>.
        /// </summary>
        [JsonPropertyName("inventory_slot_count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? InventorySlotCount { get; set; }

        /// <summary>
        /// The scenario-declared, ordered resource registry (Story 4.3) — id/display/starting-amount/collection-
        /// model authoring metadata for future resources. NULL (the default, every existing scenario) ⇒ the
        /// implicit <see cref="ResourceDefinition.DefaultRegistry"/> (today's Ore/Crystal), and the block is
        /// OMITTED from serialization when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the
        /// <see cref="RevivalRule"/>/<see cref="Items"/> omit-when-null precedent) — so every existing scenario
        /// serializes byte-for-byte identically, moving no golden. Validated for internal well-formedness (unique
        /// non-empty ids, non-negative finite starting amounts) by <see cref="ScenarioValidator"/> when non-null.
        /// Authoring-only this story — NOT wired into <see cref="ScenarioApplier"/> (starting balances stay on
        /// <see cref="ScenarioPlayerSlot.StartOre"/>/<see cref="ScenarioPlayerSlot.StartCrystal"/>) and NOT folded
        /// into any checksum/hash (mirrors the <see cref="PersistenceManifest"/> authoring-only precedent).
        /// </summary>
        [JsonPropertyName("resources")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResourceDefinition[]? Resources { get; set; }

        /// <summary>
        /// The per-scenario supply / population-cap config (Story 4.4): starting cap, optional hard ceiling, and a
        /// master enable toggle. NULL (the default, every existing scenario) ⇒ today's hardcoded default exactly
        /// (<see cref="ResourceStore.STARTING_SUPPLY_CAP"/>, no ceiling, gating enabled), and the block is OMITTED
        /// from serialization when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the
        /// <see cref="RevivalRule"/>/<see cref="Resources"/> omit-when-null precedent) — so every existing scenario
        /// serializes byte-for-byte identically, moving no golden. Resolved once (via
        /// <see cref="ResourceStore.ConfigureSupply"/>) at scenario-apply. Validated (fail-closed) by
        /// <see cref="ScenarioValidator"/> when present, and folded (as its resolved values) into
        /// <see cref="CanonicalModelHash"/> — sim-affecting state, unlike the authoring-only blocks above.
        /// </summary>
        [JsonPropertyName("supply")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SupplyConfig? Supply { get; set; }
    }
}
