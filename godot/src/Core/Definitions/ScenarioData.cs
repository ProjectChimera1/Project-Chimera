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
    ///
    /// Story 4.7 adds a per-node collection model (<see cref="CollectionModel"/>: GATHER — today's worker
    /// round-trip, the default — vs. Income — a periodic flat credit with no workers — vs. Streaming — workers
    /// credit in place, no base trip), a per-node <see cref="ResourceType"/> (Ore/Crystal, closing the Crystal-
    /// production dead path), an optional <see cref="RequiresStructure"/> proximity gate, an <see cref="OwnerSlot"/>
    /// (Income's credit destination — no workers to infer a faction from), and <see cref="IncomePeriodTicks"/>.
    /// Every new field defaults to reproduce today's GATHER node exactly when omitted.
    /// </summary>
    public class ScenarioResourceNode
    {
        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }

        /// <summary>Total supply in this node (Ore or Crystal, per <see cref="ResourceType"/>).</summary>
        [JsonPropertyName("supply")]
        public float Supply { get; set; } = 400f;

        /// <summary>
        /// Dual meaning by <see cref="CollectionModel"/> (Story 4.7): under GATHER/Streaming, the amount
        /// delivered per second by each active gatherer (today's sole meaning). Under Income, the amount
        /// granted per <see cref="IncomePeriodTicks"/> — reused rather than adding a new field (see the Story 4.7
        /// spec's Design Notes): "amount granted per unit of production time" maps directly onto "amount per
        /// period", and <see cref="MaxGatherers"/> is already inert-by-context for Income the same way.
        /// </summary>
        [JsonPropertyName("rate")]
        public float Rate { get; set; } = 5f;

        [JsonPropertyName("max_gatherers")]
        public int MaxGatherers { get; set; } = 4;

        /// <summary>The collection model this node uses — one of <see cref="ResourceDefinition.KnownCollectionModels"/>
        /// ("Gather", "Income", "Streaming"). Default "Gather" — every existing scenario JSON omits this and loads/
        /// simulates byte-identically. "Income" credits <see cref="Rate"/> to <see cref="OwnerSlot"/>'s faction every
        /// <see cref="IncomePeriodTicks"/> ticks with zero assigned workers (<c>GatheringSystem.FindBestNode</c> always
        /// skips it). "Streaming" behaves like GATHER for worker assignment/extraction but credits the gathering
        /// worker's faction directly at the node each tick — no carry, no base trip.</summary>
        [JsonPropertyName("collection_model")]
        public string CollectionModel { get; set; } = "Gather";

        /// <summary>The resource this node produces — "Ore" or "Crystal". Default "Ore" (today's only produced
        /// resource). All node credit dispatches through <see cref="ResourceStore.AddOre"/>/
        /// <see cref="ResourceStore.AddCrystal"/> by this field, closing the Crystal-production dead path (no node
        /// ever called AddCrystal before Story 4.7).</summary>
        [JsonPropertyName("resource_type")]
        public string ResourceType { get; set; } = "Ore";

        /// <summary>
        /// Optional building-definition id (<see cref="BuildingStore.DefinitionId"/> — the Story 4.1 data-driven
        /// id, NOT the closed <see cref="BuildingType"/> enum, so a creator-authored custom building can satisfy
        /// the gate) a faction must OWN within <see cref="RequiresStructureRadius"/> of this node before it becomes
        /// eligible: <c>GatheringSystem.FindBestNode</c> excludes an ungated-faction candidate, and the Income pass
        /// withholds credit. NULL (the default) ⇒ no gate — every existing scenario is unaffected. Owned-only:
        /// never satisfied by a shared/ally structure.
        /// </summary>
        [JsonPropertyName("requires_structure")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RequiresStructure { get; set; }

        /// <summary>World-unit radius of the <see cref="RequiresStructure"/> proximity check. Default 15 (only
        /// consulted when <see cref="RequiresStructure"/> is set).</summary>
        [JsonPropertyName("requires_structure_radius")]
        public float RequiresStructureRadius { get; set; } = 15f;

        /// <summary>0-based player slot this node's Income credits belong to (resolved to a <see cref="Faction"/>
        /// exactly like <see cref="ScenarioBuilding.Slot"/>/<see cref="ScenarioUnit.Slot"/>). Required (and must
        /// reference a declared <see cref="ScenarioPlayerSlot"/>) when <see cref="CollectionModel"/>="Income" — an
        /// Income node has no assigned worker to infer a faction from. Default -1 (unset; inert for GATHER/Streaming,
        /// which credit the gathering worker's own faction).</summary>
        [JsonPropertyName("owner_slot")]
        public int OwnerSlot { get; set; } = -1;

        /// <summary>Whole simulation ticks between Income credits (only consulted when
        /// <see cref="CollectionModel"/>="Income"; counted via a mutable tick counter, never dt-accumulated, never
        /// wall-clock). Default 30 (1 second at 30 ticks/sec).</summary>
        [JsonPropertyName("income_period_ticks")]
        public int IncomePeriodTicks { get; set; } = 30;

        /// <summary>Presentation-only yaw (radians, Story 6.6) applied to the resource-node mesh at spawn — cosmetic,
        /// EXCLUDED from every checksum/hash (see <see cref="ScenarioBuilding.Rot"/>). Omit-when-default.</summary>
        [JsonPropertyName("rot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Rot { get; set; } = 0f;
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

        /// <summary>Presentation-only yaw (radians, Story 6.6) applied to the building mesh at spawn. Default 0f,
        /// OMITTED from serialization when default (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>) so existing
        /// scenarios serialize byte-identically. Rotation is COSMETIC for 1.0 (sim footprints stay axis-aligned) and is
        /// deliberately EXCLUDED from <see cref="CanonicalModelHash"/> and per-tick <c>SimChecksum</c> — like
        /// <c>DisplayName</c>.</summary>
        [JsonPropertyName("rot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Rot { get; set; } = 0f;
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

        /// <summary>Presentation-only yaw (radians, Story 6.6) applied to the unit mesh at spawn — cosmetic, EXCLUDED
        /// from every checksum/hash (see <see cref="ScenarioBuilding.Rot"/>). Omit-when-default.</summary>
        [JsonPropertyName("rot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Rot { get; set; } = 0f;
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
    /// A named, rectangular map area (Story 6.4) — the first-class map/trigger primitive Epic 7's win-condition
    /// presets bind to. Rect-only for 1.0 (circles/polygons deferred post-1.0). Authored as <c>float
    /// MinX/MinZ/MaxX/MaxZ</c> (mirroring <see cref="ScenarioResourceNode"/>'s float X/Z convention), resolved
    /// float→<see cref="Fixed"/> exactly once at <c>ScenarioApplier</c> into a <see cref="FixedRect"/> held by a
    /// Godot-free <c>RegionStore</c>. Referenced by string <see cref="Id"/> from a <c>unit_in_region</c> trigger
    /// condition. Validated fail-closed (unique non-empty id; <c>MinX &lt; MaxX &amp;&amp; MinZ &lt; MaxZ</c>; all
    /// four corners within <c>MapBounds</c>) by <see cref="ScenarioValidator"/>.
    /// </summary>
    public class ScenarioRegion
    {
        /// <summary>Unique, non-empty region id (the string key a <c>unit_in_region</c> condition references).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Human-readable display name shown on the editor overlay label. Not a key.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("min_x")]
        public float MinX { get; set; }

        [JsonPropertyName("min_z")]
        public float MinZ { get; set; }

        [JsonPropertyName("max_x")]
        public float MaxX { get; set; }

        [JsonPropertyName("max_z")]
        public float MaxZ { get; set; }
    }

    /// <summary>
    /// A placed doodad/prop (Story 6.6) — decorative map geometry rendered by a single-<c>MultiMesh</c>-per-mesh
    /// <c>PropRenderer</c> (never a node per prop). Authored via the prop mode of <c>EntityPlacer</c>. When
    /// <see cref="BlocksPathing"/> is true, the prop's single-cell footprint at (<see cref="X"/>, <see cref="Z"/>)
    /// unions into 6.5's <c>PathabilityGrid</c> at load and folds into <see cref="CanonicalModelHash"/> (lockstep-
    /// critical); when false it is purely cosmetic and never touches sim state or either checksum. <see cref="Rot"/>
    /// (visual yaw) and <see cref="Scale"/> (uniform) are presentation-only and EXCLUDED from every checksum/hash.
    /// </summary>
    public class ScenarioProp
    {
        /// <summary>Prop library id (selects the mesh the <c>PropRenderer</c> instances).</summary>
        [JsonPropertyName("prop_id")]
        public string PropId { get; set; } = "";

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }

        /// <summary>Presentation-only yaw (radians). Omit-when-default (0f).</summary>
        [JsonPropertyName("rot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Rot { get; set; } = 0f;

        /// <summary>Uniform visual scale. NULL ⇒ the default 1.0 (read as <c>Scale ?? 1f</c>); OMITTED from
        /// serialization when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>) so an unscaled prop is
        /// byte-identical. Presentation-only — never folded into any checksum/hash.</summary>
        [JsonPropertyName("scale")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Scale { get; set; }

        /// <summary>When true, the prop's footprint cell (at (<see cref="X"/>,<see cref="Z"/>) via
        /// <c>FlowField.WorldToCell</c>) unions into the runtime pathability grid and folds into
        /// <see cref="CanonicalModelHash"/>. Default false, omit-when-default so a non-blocking prop is byte-identical
        /// and leaves both checksums untouched.</summary>
        [JsonPropertyName("blocks_pathing")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool BlocksPathing { get; set; } = false;
    }

    /// <summary>
    /// A named camera viewpoint (Story 6.6) — position + look-at target + field of view. Pure PRESENTATION: authored
    /// by the camera tool for the in-editor "view through camera" preview and consumed by Epic 7's <c>MoveCamera</c>
    /// trigger action (Story 7.13). Cameras never touch sim state and are EXCLUDED from both hashes. Validated
    /// fail-closed (unique non-empty name) by <see cref="ScenarioValidator"/>.
    /// </summary>
    public class ScenarioCamera
    {
        /// <summary>Unique, non-empty camera name (the key <c>MoveCamera</c> references).</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("z")]
        public float Z { get; set; }

        [JsonPropertyName("target_x")]
        public float TargetX { get; set; }

        [JsonPropertyName("target_y")]
        public float TargetY { get; set; }

        [JsonPropertyName("target_z")]
        public float TargetZ { get; set; }

        /// <summary>Vertical field of view in degrees. Default 70 (Godot's Camera3D default).</summary>
        [JsonPropertyName("fov")]
        public float Fov { get; set; } = 70f;
    }

    /// <summary>
    /// A cheap water volume (Story 6.6) — a visual plane at level <see cref="Y"/> over the axis-aligned rect
    /// (<see cref="X"/>,<see cref="Z"/>) sized <see cref="W"/>×<see cref="H"/>, with an auto-impassable footprint. NO
    /// fluid simulation: the rect's cells union into 6.5's <c>PathabilityGrid</c> at load (units route around) and
    /// fold into <see cref="CanonicalModelHash"/> (lockstep-critical, like a blocking prop). Removing the volume
    /// un-stamps deterministically because the grid is rebuilt from source each load. Validated fail-closed
    /// (well-formed rect: finite, in-range, positive extents) by <see cref="ScenarioValidator"/>.
    /// </summary>
    public class ScenarioWater
    {
        /// <summary>World X of the rect's min corner.</summary>
        [JsonPropertyName("x")]
        public float X { get; set; }

        /// <summary>World Z of the rect's min corner.</summary>
        [JsonPropertyName("z")]
        public float Z { get; set; }

        /// <summary>Rect width along +X (world units, > 0).</summary>
        [JsonPropertyName("w")]
        public float W { get; set; }

        /// <summary>Rect depth along +Z (world units, > 0).</summary>
        [JsonPropertyName("h")]
        public float H { get; set; }

        /// <summary>Water-surface world Y (the visual plane height). Presentation-only — the impassable footprint is
        /// XZ-based, so Y is NOT folded into any checksum/hash.</summary>
        [JsonPropertyName("y")]
        public float Y { get; set; } = 0f;
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

        /// <summary>
        /// The per-scenario height-advantage vision toggle (Story 6.3). When true, <c>FogOfWarSystem</c> widens an
        /// elevated unit's stamped vision radius by an elevation-derived per-step bonus
        /// (<see cref="HeightVisionBonusPerStep"/>); when false (the default, every existing scenario) the stamped fog
        /// Grid is byte-for-byte identical to pre-feature. OMITTED from serialization when default
        /// (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>, the <see cref="ScenarioPlayerSlot.StartCrystal"/>
        /// omit-when-default precedent) so every existing scenario serializes byte-identically, moving no golden.
        /// Threaded into <see cref="EntityWorld.HeightAdvantageVision"/> at scenario-apply. Deliberately NOT folded into
        /// <c>CanonicalModelHash</c>/<c>StartStateHash</c>: the fog Grid it affects is not in <c>SimChecksum</c> and no
        /// sim system consumes it, so a toggle mismatch cannot cause a lockstep desync (it is not lockstep-critical).
        /// </summary>
        [JsonPropertyName("height_advantage_vision")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HeightAdvantageVision { get; set; } = false;

        /// <summary>
        /// The per-scenario vision-radius bonus per whole elevation step (Story 6.3), in world units — consulted only
        /// when <see cref="HeightAdvantageVision"/> is enabled. Default 0f (no bonus even if the toggle is on), OMITTED
        /// from serialization when default (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>) so existing scenarios
        /// serialize byte-identically. Resolved once (the single float→Fixed boundary) into
        /// <see cref="EntityWorld.HeightVisionBonusPerStep"/> at scenario-apply. NOT folded into any checksum/hash (the
        /// <see cref="HeightAdvantageVision"/> rationale — it only affects the non-folded fog Grid).
        /// </summary>
        [JsonPropertyName("height_vision_bonus_per_step")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float HeightVisionBonusPerStep { get; set; } = 0f;

        /// <summary>
        /// Named rectangular map regions (Story 6.4) — the first-class map/trigger primitive a <c>unit_in_region</c>
        /// condition references by string id and Epic 7's win-condition presets bind to. NULL (the default, every
        /// existing scenario) ⇒ no regions, and the block is OMITTED from serialization when null
        /// (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the <see cref="Items"/>/<see cref="Resources"/>
        /// omit-when-null precedent) — so an absent/empty regions collection serializes byte-for-byte identically to
        /// pre-feature (no map-package format change, no new zip file). Read as <c>Regions ?? Array.Empty</c>.
        /// Resolved once (float→Fixed) into a Godot-free <c>RegionStore</c> at scenario-apply. Validated (fail-closed)
        /// by <see cref="ScenarioValidator"/> when present. Deliberately EXCLUDED from
        /// <see cref="CanonicalModelHash"/> / <see cref="StartStateHash"/> / <c>SimChecksum</c> on the SAME basis as
        /// <see cref="Triggers"/>: regions are a *trigger input* (the <c>unit_in_region</c> condition CAN gate trigger
        /// actions — spawn_unit/add_resources/set_variable — that DO mutate SimChecksum-folded state), and Triggers
        /// are an already-accepted, bounded handshake gap (deferred to Epic 7). When Triggers are folded into the
        /// handshake, Regions fold with them. The Block-If tripwire is a NON-trigger sim consumer of region
        /// containment. No <c>AlgoVersion</c> bump, no golden re-record.
        /// </summary>
        [JsonPropertyName("regions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScenarioRegion[]? Regions { get; set; }

        /// <summary>
        /// The authored impassable-terrain paint (Story 6.5): base64 of the packed 128²/8 = 2048-byte blocked bitset
        /// (bit i = cell i = row*128 + col, mapping through <c>FlowField.WorldToCell</c>). NULL (the default, every
        /// existing scenario) ⇒ nothing painted, and the field is OMITTED from serialization when null
        /// (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the <see cref="Regions"/> omit-when-null precedent) —
        /// so a flat/legacy map serializes byte-for-byte identically to pre-feature. An all-clear painted layer is
        /// normalized back to null at the serialize chokepoint (<c>ScenarioSerializer.Serialize</c>). Decoded once at
        /// load into a Godot-free <c>PathabilityGrid</c> that the deterministic sim honors (a unit cannot cross into
        /// a blocked cell) and the flow field routes around. FOLDED into <see cref="CanonicalModelHash"/> (via
        /// <c>PathabilityGrid.DigestOfBase64</c>) because pathing is lockstep-critical: two peers with mismatched
        /// painted layers produce divergent unit paths, so a mismatch must be rejected at the handshake rather than
        /// desyncing in-sim. Validated (fail-closed) by <see cref="ScenarioValidator"/> — a start/spawn on a painted
        /// blocked cell is rejected before any tick.
        /// </summary>
        [JsonPropertyName("pathability_blocked")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PathabilityBlocked { get; set; }

        /// <summary>
        /// The per-map slope-derived auto-block toggle (Story 6.5). When true, steep cells (neighbor rise/run ≥
        /// <see cref="SlopeBlockThreshold"/>) are derived deterministically from the <c>ElevationGrid</c> at load and
        /// UNIONED into the runtime pathability grid; when false (the default, every existing scenario) no cells are
        /// derived and a flat map behaves byte-identically to pre-feature. OMITTED from serialization when default
        /// (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>, the <see cref="HeightAdvantageVision"/>
        /// omit-when-default precedent). The slope CONFIG (this toggle + threshold) is FOLDED into
        /// <see cref="CanonicalModelHash"/>; the derived cells themselves ride the terrain heightmap (not the
        /// handshake — <c>TerrainRef</c> is neutralized) and are recomputed deterministically at load.
        /// </summary>
        [JsonPropertyName("slope_auto_block")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool SlopeAutoBlock { get; set; } = false;

        /// <summary>
        /// The slope-auto-block steepness threshold (Story 6.5) — the minimum neighbor rise/run (world Y per world
        /// unit) at which a flow cell auto-blocks — consulted only when <see cref="SlopeAutoBlock"/> is enabled.
        /// Default 0f, OMITTED from serialization when default (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>)
        /// so existing scenarios serialize byte-identically. Resolved once (float→Fixed) at load-time slope
        /// derivation. Folded (as its quantized <c>Fixed.Raw</c>) into <see cref="CanonicalModelHash"/> with the
        /// toggle so a mismatched slope config is handshake-rejectable.
        /// </summary>
        [JsonPropertyName("slope_block_threshold")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float SlopeBlockThreshold { get; set; } = 0f;

        /// <summary>
        /// Placed doodads/props (Story 6.6). NULL (the default, every existing scenario) ⇒ no props, and the block is
        /// OMITTED from serialization when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>, the
        /// <see cref="Regions"/> precedent) — an absent/empty collection is byte-identical to pre-feature (no
        /// map-package format change, no new zip entry; <c>.chimera.zip</c> round-trips inline). An empty array
        /// normalizes back to null at the <c>ScenarioSerializer.Serialize</c> chokepoint. Read as
        /// <c>Props ?? Array.Empty</c>. A <c>blocks_pathing</c> prop's single-cell footprint unions into 6.5's
        /// <c>PathabilityGrid</c> at load and FOLDS into <see cref="CanonicalModelHash"/> (lockstep-critical, AlgoVersion
        /// 7); a non-blocking prop, and every prop's rotation/scale, are cosmetic and EXCLUDED from both hashes.
        /// </summary>
        [JsonPropertyName("props")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScenarioProp[]? Props { get; set; }

        /// <summary>
        /// Named camera viewpoints (Story 6.6) for the in-editor "view through camera" preview and Epic 7's
        /// <c>MoveCamera</c> action. NULL ⇒ none, OMITTED when null / normalized empty→null at the serialize
        /// chokepoint (the <see cref="Regions"/> precedent). Read as <c>Cameras ?? Array.Empty</c>. Pure PRESENTATION
        /// — EXCLUDED from <see cref="CanonicalModelHash"/> / <c>StartStateHash</c> / <c>SimChecksum</c>.
        /// </summary>
        [JsonPropertyName("cameras")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScenarioCamera[]? Cameras { get; set; }

        /// <summary>
        /// Cheap water volumes (Story 6.6) — a visual plane + an auto-impassable footprint (no fluid sim). NULL ⇒
        /// none, OMITTED when null / normalized empty→null at the serialize chokepoint (the <see cref="Regions"/>
        /// precedent). Read as <c>Water ?? Array.Empty</c>. Each volume's rect cells union into 6.5's
        /// <c>PathabilityGrid</c> at load and FOLD into <see cref="CanonicalModelHash"/> (lockstep-critical, AlgoVersion
        /// 7) — removing a volume un-stamps for free because the grid is rebuilt from source each load.
        /// </summary>
        [JsonPropertyName("water")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScenarioWater[]? Water { get; set; }
    }
}
