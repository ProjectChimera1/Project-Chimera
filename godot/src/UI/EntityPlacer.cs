#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Click-to-spawn in Edit mode.
    ///
    /// Palette panel (top-right): click buttons to select entity type, building type,
    /// or unit archetype. A semi-transparent ghost mesh follows the cursor to preview
    /// where the entity will land. Grid snap (G key or palette toggle) snaps placement
    /// to the nearest 1-unit grid.
    ///
    /// Keyboard shortcuts still work and stay in sync with the palette:
    ///   Tab         — cycle unit mode (P1 Unit → P2 Unit → Ore Node → back)
    ///   B           — building mode (B again cycles building type)
    ///   U           — cycle unit archetype
    ///   G           — toggle grid snap
    ///   Shift+click — spawn worker instead of combat unit
    /// </summary>
    public partial class EntityPlacer : Node
    {
        public enum PlacementMode { P1Unit, P2Unit, ResourceNode, Building, StartPos, Item, Prop, Select }

        /// <summary>
        /// Operation requested of a ScenarioData-sync callback (Story 6.1). One callback per entity kind
        /// (building/unit/resource-node) multiplexes all four legs of place/delete undo/redo so that
        /// <see cref="ScenarioData"/> never drifts from the live stores:
        ///   • <see cref="Add"/>         — build a new scenario entry from the descriptor, append it, return it as an
        ///                                 opaque handle (initial place).
        ///   • <see cref="RemoveMatch"/> — find the scenario entry matching the descriptor, remove it, return the
        ///                                 REAL removed object as the handle (initial delete). Identity-preserving:
        ///                                 the authored entry is captured, never paraphrased.
        ///   • <see cref="ReAdd"/>       — re-append the exact captured handle (undo-of-delete / redo-of-place).
        ///   • <see cref="RemoveHandle"/>— remove the exact captured handle by identity (undo-of-place / redo-of-delete).
        /// EntityPlacer treats the handle as opaque; MainScene owns the <see cref="ScenarioData"/> mutation, so
        /// EntityPlacer never takes a direct ScenarioData/SceneContext reference.
        /// </summary>
        public enum ScenarioSyncOp { Add, RemoveMatch, ReAdd, RemoveHandle }

        // ── Fallback stats ────────────────────────────────────────────────────
        private const float HEALTH       = 100f;
        private const float SPEED        = 4f;
        private const float ATTACK_RANGE = 5f;
        private const float ATTACK_DMG   = 10f;
        private const float ATTACK_SPEED = 1f;

        private const float WORKER_HEALTH = 60f;
        private const float WORKER_SPEED  = 3.5f;
        private const float WORKER_CARRY  = 20f;

        private const int NODE_MAX_GATHERERS = 4;

        private static readonly float[] BUILDING_COSTS = { 150f, 100f, 120f, 200f, 200f }; // legacy per-enum fallback ore cost; only used when a built-in has no authored `cost` map
        private const float DEFAULT_BUILDING_COST = 150f; // Story 6.8 — guarded ore cost for a Custom/authored building with no authored cost map

        // Modes displayed left-to-right in the palette (order must match _modeBtns array)
        private static readonly PlacementMode[] MODE_ORDER =
            { PlacementMode.P1Unit, PlacementMode.P2Unit, PlacementMode.ResourceNode, PlacementMode.Building, PlacementMode.StartPos, PlacementMode.Item, PlacementMode.Prop, PlacementMode.Select };
        private static readonly string[] MODE_LABELS = { "P1 Unit", "P2 Unit", "Ore Node", "Building", "Start Pos", "Item", "Prop", "Select" };

        // Story 6.6 — the built-in prop library (ids the PropRenderer maps to meshes). No external prop-asset pipeline
        // yet; a small decorative vocabulary the palette cycles.
        private static readonly string[] PROP_LIBRARY = { "tree", "rock", "crystal", "bush" };

        // Story 6.6 — rotation step (radians) applied by the R key to the armed placement's yaw.
        private const float ROT_STEP = Mathf.Pi / 4f; // 45°

        // ── Dependencies ──────────────────────────────────────────────────────
        private RtsCameraController _camCtrl   = null!;
        private EntityWorld         _world     = null!;
        private ResourceNodeStore?  _nodes;
        private ResourceStore?      _resources;
        private BuildingStore?      _buildings;
        // DW-52: the hero store, so the editor delete path can free a hero-linked unit's HeroStore row (and clear its
        // dangling EntityId back-reference) instead of leaking the row. Nullable — a delete no-ops when unset.
        private HeroStore?          _heroes;
        private ItemStore?          _items;         // Story 3.15 — ground item instances (Item placement mode)
        private ItemRegistry?       _itemRegistry;  // Story 3.15 — id→index over validated item defs
        private int                 _itemIndex = 0; // Story 3.15 — which registry item to place (cycled by re-clicking the Item mode)
        private FactionDefinition?  _faction;   // Player1
        private FactionDefinition?  _faction2;  // Player2

        /// <summary>
        /// Fired when the user places a start-position marker.
        /// Story 6.7 parameters: (slotIndex 0..3, world position, starting ore, starting crystal).
        /// Returns the <c>created</c> flag from MainScene.MoveStartPosition — true when the placement APPENDED a new
        /// slot (so undo must remove it rather than repositioning a phantom at origin).
        /// </summary>
        private System.Func<int, Vector3, float, float, bool>? _onStartPosMoved;

        /// <summary>Story 6.7 — fired when the author removes the trailing start slot (2–4 add/remove). Parameter is
        /// the removed 0-based slot index; the owner truncates <c>PlayerSlots</c> and hides the flag marker.</summary>
        private System.Action<int>? _onStartSlotRemoved;

        /// <summary>Story 6.7 (patch 3) — fired when the Ore/Crystal spinner edits an ALREADY-placed slot, so the
        /// economy change persists immediately (StartCrystal is hash-folded). Parameters: (slot, ore, crystal).</summary>
        private System.Action<int, float, float>? _onStartSlotEconomy;

        /// <summary>
        /// Story 6.1 — ScenarioData sync callbacks (one per persisted entity kind), fired inside the place/delete
        /// undo/redo closures so <c>ScenarioData.Buildings/Units/ResourceNodes</c> stay symmetric with the live
        /// stores across save/reload AND the F5 Edit→Play toggle (which re-applies only <c>_ctx.Scenario</c>). Each
        /// returns an opaque handle to the affected scenario entry. See <see cref="ScenarioSyncOp"/>.
        /// </summary>
        // Story 6.8: the building sync closure carries the AUTHORED building id (string), not the BuildingType enum, so
        // a Custom building (whose enum is BuildingType.Custom) round-trips its real id into ScenarioData.Buildings[].Type.
        private System.Func<ScenarioSyncOp, object?, string, Faction, Vector3, bool, object?>? _onBuildingSync;
        private System.Func<ScenarioSyncOp, object?, string, Faction, Vector3, object?>?             _onUnitSync;
        // DW-151: the resource-node sync carries the Story-4.7 economy field set (collection model / resource type /
        // requires-structure id+radius / owner faction / income period) so a group-move/paste re-creates them in the
        // persisted DTO, not just the live store. The single-entity placer + delete/re-add legs pass store defaults.
        private System.Func<ScenarioSyncOp, object?, Vector3, float, float, int,
                            ResourceCollectionModel, ResourceKind, string, Fixed, Faction, int, object?>? _onResourceNodeSync;

        /// <summary>DW-137 — ground-item sync callback (place + undo/redo legs). Signature: (op, handle, item_id, pos)
        /// → opaque handle (the affected <see cref="ScenarioItem"/>). MainScene owns the <c>ScenarioData.Items</c>
        /// mutation (mirroring the building/unit/node/prop syncs), so EntityPlacer never mutates the scenario array
        /// directly. Items are slot-less and rotation-less; matched by position only.</summary>
        private System.Func<ScenarioSyncOp, object?, string, Vector3, object?>? _onItemSync;

        /// <summary>Story 6.6 — prop sync callback (place/delete + undo/redo legs). Signature:
        /// (op, handle, prop_id, pos, rot, scale, blocks_pathing) → opaque handle (the affected <c>ScenarioProp</c>).
        /// MainScene owns the <c>ScenarioData.Props</c> mutation (mirroring the building/unit/node syncs), so
        /// EntityPlacer never mutates the scenario array directly.</summary>
        private System.Func<ScenarioSyncOp, object?, string, Vector3, float, float, bool, object?>? _onPropSync;

        /// <summary>Story 6.6 — narrow READ seam onto the live scenario (props only), for the prop delete scan, the
        /// marquee multi-select, and prop rotation/group moves. Reading is late-bound (the scenario loads after this
        /// placer is constructed). Mutations still route through the sync callbacks / history closures.</summary>
        private System.Func<ScenarioData?>? _scenarioGetter;

        // ── Placement state ───────────────────────────────────────────────────
        private PlacementMode _mode         = PlacementMode.P1Unit;

        /// <summary>Story 6.1 (UX-DR56) — true while a placement mode is armed (ghost visible, left-click places).
        /// Right-click or Esc disarms it (see <see cref="CancelPlacement"/>); re-selecting any mode re-arms it (see
        /// <see cref="ArmPlacement"/>). Starts armed so placement works immediately on entering Edit mode.</summary>
        private bool _placementActive = true;
        private PlacementMode _lastUnitMode = PlacementMode.P1Unit;
        // Story 6.8: the selected building is now an AUTHORED building-def id (string) sourced from the owner faction's
        // Buildings, not a fixed BuildingType enum — so the palette can place any authored (custom) building.
        private string        _buildingId   = "command_center";
        private int           _unitIndex    = 0;

        // ── DW-893: real-mesh placement ghost ────────────────────────────────────────────────────────────────────
        /// <summary>Ingested custom-map meshes (null before FactionVisualsPhase runs — the lookup stays lazy).</summary>
        private AssetRegistry? _assets;
        /// <summary>Resolved ghost meshes keyed by mode + the unit/building/prop id, so the GLB load never runs per
        /// frame. <see cref="MeshLoader.LoadFromGlb"/> does a PackedScene load + Instantiate + Free, which is fine on a
        /// mode change and unacceptable in <c>_Process</c>.</summary>
        private readonly System.Collections.Generic.Dictionary<string, (Mesh Mesh, float Scale, bool Authored)> _ghostMeshCache = new();
        private float _ghostScale      = 1f;      // authored MeshScale for the resolved mesh
        private float _ghostGroundLift = 0.6f;    // Y offset that sits the resolved mesh ON the ground
        /// <summary>Shadow-only twin of the ghost. In Godot 4 an alpha-BLEND material casts no shadow, and the ghost's
        /// translucent look is deliberate — so the silhouette shadow Alec asked for comes from an opaque sibling that
        /// renders nothing but its shadow, sharing the ghost's mesh and transform.</summary>
        private MeshInstance3D? _ghostShadow;

        // ── DW-894: live group-drag preview ─────────────────────────────────────────────────────────────────────
        /// <summary>Snapped drag delta applied to <c>_markerRoot</c> each frame while <c>_groupMoving</c>. Uses the
        /// SAME <see cref="SnapDelta"/> the commit applies, so the preview is byte-equal to where objects land.</summary>
        private Vector3 _groupMovePreviewDelta;
        /// <summary>Translucent mesh copies shown during a drag; children of <c>_markerRoot</c>, so one root offset
        /// moves them all.</summary>
        private readonly System.Collections.Generic.List<MeshInstance3D> _dragPreview = new();
        private bool          _gridSnapEnabled = false;

        // Start position sub-state (Story 6.7: 2–4 slots, capped at the engine ceiling Faction.Player4).
        private const int START_SLOT_CEILING = 4; // (int)Faction.Player4 — 5–8 is Story 9.2
        private const int START_SLOT_MIN     = 2;
        private int   _startSlot      = 0;  // 0..3
        // PATCH 4 (A6): DISPLAY-VESTIGIAL after DW-163 — kept in sync via RefreshStartSlotCount() but no longer READ
        // for any control flow (the palette/marker/"+"/"−" logic reads DeclaredStartSlots() by slot VALUE). Not
        // authoritative; do not reintroduce count-derived slot logic against it. Left in place (not removed) because
        // removal would ripple through the DW-161/DW-163 +/−/undo call sites for no behavioral gain.
        private int   _startSlotCount = 2;  // (2..4) working slot count — display bookkeeping only
        private float _startOre       = 200f; // starting ore for the selected slot (mirrors _slotStartOre[_startSlot])
        private float _startCrystal   = 0f;   // starting crystal for the selected slot

        // Resource node sub-state (configurable supply and gather rate)
        private float _nodeSupply = 500f;
        private float _nodeRate   = 5f;

        // ── Story 6.6: prop sub-state ─────────────────────────────────────────
        private int   _propIndex   = 0;      // index into PROP_LIBRARY
        private float _propScale    = 1f;
        private bool  _propBlocks    = false;

        // ── Story 6.6: shared armed-placement yaw (R key). Applied to the next placed prop/building/unit/node Rot. ─
        private float _placementRot = 0f;

        // ── Story 6.6: multi-select (marquee) state ───────────────────────────
        private readonly List<Selected> _selection = new();
        private bool    _marqueeActive = false;   // left-drag box in progress (Select mode)
        private bool    _groupMoving   = false;   // dragging the selection to move it
        private Vector2 _marqueeStart, _marqueeCurrent;
        private Vector3 _groupMoveStartWorld;
        private readonly List<PropDescriptor> _clipboard = new();
        private Vector3 _clipboardAnchor;         // the cursor world pos when the clipboard was captured
        private Control? _marqueeRect;            // screen-space selection rectangle overlay

        /// <summary>A single member of the multi-select set — one live entity (unit/building/node) or a scenario prop.</summary>
        private readonly struct Selected
        {
            public enum Kind { Unit, Building, Node, Prop }
            public readonly Kind Category;
            public readonly int LiveId;            // EntityWorld / store slot (Unit/Building/Node)
            public readonly object? PropHandle;    // ScenarioProp (Prop)
            public Selected(Kind cat, int liveId, object? prop) { Category = cat; LiveId = liveId; PropHandle = prop; }
        }

        /// <summary>A copy/paste descriptor capturing enough to re-create a placement at a new anchor.</summary>
        private readonly struct PropDescriptor
        {
            public readonly Selected.Kind Category;
            public readonly Vector3 Pos;           // original world position (relative offset preserved on paste)
            public readonly string  Id;            // prop_id / unit_id ("" for node), building type name
            public readonly Faction Faction;
            public readonly float   Rot;
            public readonly float   Scale;
            public readonly bool    Blocks;
            public readonly float   Supply, Rate;
            public readonly int     MaxGatherers;
            // ── DW-151: identity-preserving capture for the multi-select move/copy/paste path ──────────────────────
            /// <summary>Unit residue snapshot (worker SupplyCost/GatherState/CarryCapacity/MeshType + def) — restored
            /// via <see cref="EntityWorld.RestoreUnit"/> so a moved/pasted worker stays a worker, not a combat unit.</summary>
            public readonly UnitSnapshot? UnitSnap;
            /// <summary>The source building's authored <c>pre_built</c> flag, carried into the re-created DTO.</summary>
            public readonly bool    PreBuilt;
            // The Story-4.7 resource-node field set, captured live and restored into BOTH the live store and the DTO.
            public readonly ResourceCollectionModel ResourceCollectionModel;
            public readonly ResourceKind             ResourceKind;
            public readonly string                   RequiresStructureId;
            public readonly Fixed                    RequiresStructureRadius;
            public readonly Faction                  OwnerFaction;
            public readonly int                      IncomePeriodTicks;
            public PropDescriptor(Selected.Kind cat, Vector3 pos, string id, Faction faction, float rot, float scale,
                                  bool blocks, float supply, float rate, int maxGatherers,
                                  UnitSnapshot? unitSnap = null, bool preBuilt = false,
                                  ResourceCollectionModel collectionModel = ResourceCollectionModel.Gather,
                                  ResourceKind resourceKind = ResourceKind.Ore,
                                  string requiresStructureId = "", Fixed requiresStructureRadius = default,
                                  Faction ownerFaction = Faction.Neutral, int incomePeriodTicks = 0)
            { Category = cat; Pos = pos; Id = id; Faction = faction; Rot = rot; Scale = scale; Blocks = blocks; Supply = supply; Rate = rate; MaxGatherers = maxGatherers;
              UnitSnap = unitSnap; PreBuilt = preBuilt; ResourceCollectionModel = collectionModel; ResourceKind = resourceKind;
              RequiresStructureId = requiresStructureId; RequiresStructureRadius = requiresStructureRadius; OwnerFaction = ownerFaction; IncomePeriodTicks = incomePeriodTicks; }
        }

        // Undo/redo history
        private readonly EditorHistory _history = new();

        /// <summary>Story 6.2: the shared editor undo/redo stack. Exposed so <see cref="CreationSuite.TerrainBrush"/>
        /// pushes terrain sculpt/paint stroke commands onto the SAME history the entity place/delete ops use — the
        /// existing Ctrl+Z/Y handler here pops whichever delegate (entity or terrain) is on top, so the two op kinds
        /// interleave in strict LIFO with no second parallel stack and no cross-corruption.</summary>
        public EditorHistory History => _history;

        /// <summary>DW-144: the terrain brush, wired in TerrainBrushPhase after the brush is initialized. The Ctrl+Z/Y
        /// handler consults <see cref="CreationSuite.TerrainBrush.IsPainting"/> to swallow undo/redo while a stroke's
        /// live operate() is in flight, so History.Undo/Redo can't race the stroke's captured after-snapshot. Null
        /// when no Terrain3D is present (PlaneMesh fallback) — the guard then no-ops and undo/redo behaves as before.</summary>
        public CreationSuite.TerrainBrush? TerrainBrush { get; set; }

        // Tracks ore/crystal set per start-position slot (for undo of MoveStartPos). Story 6.7: sized to the engine
        // ceiling (4) rather than a hardcoded 2.
        private readonly float[] _slotStartOre     = { 200f, 200f, 200f, 200f };
        private readonly float[] _slotStartCrystal = { 0f, 0f, 0f, 0f };

        // Last valid 3D cursor position in world space (used by Delete key)
        private Vector3 _lastCursorWorld;

        // ── Ghost preview mesh ────────────────────────────────────────────────
        private MeshInstance3D? _ghost;

        // ── Palette UI ────────────────────────────────────────────────────────
        private CanvasLayer?    _paletteCanvas;
        private Button[]        _modeBtns = System.Array.Empty<Button>();
        private HFlowContainer? _subRow;
        private Button?         _snapBtn;

        // ── Properties ────────────────────────────────────────────────────────

        /// <summary>Current faction for unit spawning.</summary>
        public Faction SelectedFaction =>
            _mode == PlacementMode.P2Unit ? Faction.Player2 : Faction.Player1;

        /// <summary>True when grid snap is active (shown in HUD controls strip).</summary>
        public bool GridSnapEnabled => _gridSnapEnabled;

        /// <summary>Human-readable current mode label for HUD.</summary>
        public string ModeLabel => _mode switch
        {
            PlacementMode.P1Unit       => $"P1 [{GetSelectedUnitName()}]",
            PlacementMode.P2Unit       => $"P2 [{GetSelectedUnitName()}]",
            PlacementMode.ResourceNode => "Ore Node",
            PlacementMode.Building     => $"Building [{BuildingLabel(_buildingId)}]",
            PlacementMode.StartPos     => $"Start Pos [P{_startSlot + 1}]",
            PlacementMode.Prop         => $"Prop [{PROP_LIBRARY[_propIndex % PROP_LIBRARY.Length]}]",
            PlacementMode.Select       => $"Select [{_selection.Count}]",
            _                          => "?"
        };

        private string GetSelectedUnitName()
        {
            var units = GetCombatUnits();
            return units.Count > 0 ? units[_unitIndex % units.Count].DisplayName : "Unit";
        }

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>
        /// Wire dependencies. Called from MainScene after AddChild so GetParent() and
        /// GetViewport() are valid. Creates the ghost mesh and palette UI.
        /// </summary>
        public void Initialize(RtsCameraController camCtrl, EntityWorld world,
                               ResourceNodeStore? nodes = null, ResourceStore? resources = null,
                               BuildingStore? buildings = null, FactionDefinition? faction = null,
                               System.Func<int, Vector3, float, float, bool>? onStartPosMoved = null,
                               FactionDefinition? faction2 = null,
                               ItemStore? items = null, ItemRegistry? itemRegistry = null,
                               System.Func<ScenarioSyncOp, object?, string, Faction, Vector3, bool, object?>? onBuildingSync = null,
                               System.Func<ScenarioSyncOp, object?, string, Faction, Vector3, object?>? onUnitSync = null,
                               System.Func<ScenarioSyncOp, object?, Vector3, float, float, int,
                                           ResourceCollectionModel, ResourceKind, string, Fixed, Faction, int, object?>? onResourceNodeSync = null,
                               System.Func<ScenarioSyncOp, object?, string, Vector3, float, float, bool, object?>? onPropSync = null,
                               System.Func<ScenarioData?>? scenarioGetter = null,
                               System.Action<int>? onStartSlotRemoved = null,
                               System.Action<int, float, float>? onStartSlotEconomy = null,
                               System.Func<ScenarioSyncOp, object?, string, Vector3, object?>? onItemSync = null,
                               HeroStore? heroes = null,
                               AssetRegistry? assets = null)   // DW-893: lets the ghost resolve ingested custom-map meshes
        {
            _camCtrl            = camCtrl;
            // DW-893 — captured by REFERENCE on purpose. CameraPhase (which builds this placer) runs BEFORE
            // FactionVisualsPhase populates the registry, so the ghost's mesh lookup must stay lazy; holding the
            // reference is safe, resolving eagerly would not be.
            _assets             = assets;
            _world              = world;
            _heroes             = heroes;        // DW-52 — free hero rows on editor delete
            _nodes              = nodes;
            _resources          = resources;
            _buildings          = buildings;
            _items              = items;         // Story 3.15
            _itemRegistry       = itemRegistry;  // Story 3.15
            _faction            = faction;
            _faction2           = faction2;
            _onStartPosMoved    = onStartPosMoved;
            _onBuildingSync     = onBuildingSync;      // Story 6.1
            _onUnitSync         = onUnitSync;          // Story 6.1
            _onResourceNodeSync = onResourceNodeSync;  // Story 6.1
            _onPropSync         = onPropSync;          // Story 6.6
            _scenarioGetter     = scenarioGetter;      // Story 6.6
            _onStartSlotRemoved = onStartSlotRemoved;  // Story 6.7
            _onStartSlotEconomy = onStartSlotEconomy;  // Story 6.7 (patch 3)
            _onItemSync         = onItemSync;          // DW-137

            // Story 6.7: seed the working start-slot count from the loaded scenario (2..4) so the picker shows the
            // right number of slots on first open, and mirror each slot's authored ore/crystal into the working arrays.
            var scen0 = scenarioGetter?.Invoke();
            if (scen0?.PlayerSlots != null)
            {
                _startSlotCount = System.Math.Clamp(scen0.PlayerSlots.Length, START_SLOT_MIN, START_SLOT_CEILING);
                foreach (var s in scen0.PlayerSlots)
                    if (s.Slot >= 0 && s.Slot < START_SLOT_CEILING)
                    {
                        _slotStartOre[s.Slot]     = s.StartOre;
                        _slotStartCrystal[s.Slot] = s.StartCrystal;
                    }
                // PATCH 3 (E1): seed the SELECTION to the lowest DECLARED slot value, not the default 0. A scenario
                // whose declared set excludes slot 0 (e.g. {3} or {1,2,3}) would otherwise render a phantom "P1"
                // pending toggle and could create an undeclared slot 0 on the first placement. (The runtime "+"-armed
                // pending logic, which intentionally arms an undeclared slot, is unaffected.)
                // DW-163 patch: filter to slots below the picker ceiling. The mirror loop above already guards
                // s.Slot < START_SLOT_CEILING; this seed must too, or a validator-legal set whose LOWEST slot is
                // >= START_SLOT_CEILING (a 5–8-player {5,6} set) would index the length-CEILING _slotStartOre and
                // crash the EntityPlacer constructor — the whole creation suite fails to init.
                var declaredSeed = StartSlotMath.DeclaredBelowCeiling(scen0.PlayerSlots, START_SLOT_CEILING);
                _startSlot    = declaredSeed.Length > 0 ? declaredSeed[0] : 0;
                _startOre     = _slotStartOre[_startSlot];
                _startCrystal = _slotStartCrystal[_startSlot];
            }

            CreateGhostMesh();
            BuildPaletteUi();
            BuildSelectionOverlay();
        }

        /// <summary>
        /// Re-point the placement faction definitions after a scenario has assigned each
        /// slot's faction. Keeps editor click-to-spawn (mesh + stats) consistent with what
        /// the unit/building bridges render. Initialize() wires the defaults before the
        /// scenario loads; MainScene.SetupFactionVisuals() calls this afterward.
        /// </summary>
        public void SetFactionDefs(FactionDefinition? player1, FactionDefinition? player2)
        {
            if (player1 != null) _faction  = player1;
            if (player2 != null) _faction2 = player2;

            // DW-893 — CRITICAL ordering fix. CameraPhase builds this placer BEFORE FactionVisualsPhase resolves the
            // real slot factions, so any ghost mesh resolved earlier is pinned to the PRE-scenario default factions and
            // would show the wrong faction's art for the rest of the session. Drop the cache and re-resolve here, the
            // moment the real defs arrive.
            _ghostMeshCache.Clear();
            RefreshGhostVisuals();
        }

        // ── Godot lifecycle ───────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            bool edit = GameState.Instance?.Mode == GameMode.Edit;

            if (_paletteCanvas != null)
                _paletteCanvas.Visible = edit;

            UpdateGhostPosition(edit);
            UpdateSelectionOverlay(edit);
        }

        // ── Input ─────────────────────────────────────────────────────────────

        public override void _Input(InputEvent @event)
        {
            bool editMode = GameState.Instance?.Mode == GameMode.Edit;

            // UX-DR56 (Story 6.1): right-click cancels an active placement mode and hides the ghost without
            // placing anything. The RTS camera rotates on MIDDLE mouse, so right-click has no camera conflict.
            // When nothing is armed, do not consume the event (let it fall through as before).
            if (editMode && @event is InputEventMouseButton mb
                && mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                if (_placementActive)
                {
                    CancelPlacement();
                    GetViewport().SetInputAsHandled();
                }
                return;
            }

            // Story 6.6: Select-mode marquee multi-select + group-move (left mouse). Handled in _Input (which fires
            // before _UnhandledInput's placement) so a Select-mode drag never spawns an entity.
            if (editMode && _mode == PlacementMode.Select && HandleSelectMouse(@event)) return;

            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            // Every binding below lives in _Input (it must beat the placer/selection to the event) and ends in
            // SetInputAsHandled(), so without this guard a focused LineEdit never receives the character: typing
            // "r" rotated the ghost instead of writing an r, and "b"/"u"/Tab drove this palette from inside the
            // map-generator prompt and the New Map menu. See TextFocusGuard for the full rule.
            if (TextFocusGuard.IsTyping(this)) return;

            // Story 6.6: R rotates the armed placement's yaw by a step (visual-only; excluded from every checksum) and
            // re-rotates the props in the current selection. The tooltip states rotation is visual-only.
            // Only claim R when there is actually something to rotate. R is DOUBLE-BOUND: MainScene binds it to the
            // Visual Tech Tree Editor (Story 4.6) on the strength of a comment claiming "verified unused — no other
            // Key.R check anywhere in src/", which is false — this handler is the other one. Because _Input runs
            // before MainScene's _UnhandledInput and this branch called SetInputAsHandled unconditionally, R was
            // swallowed here every time and the Tech Tree Editor was UNREACHABLE by its documented hotkey (confirmed
            // in-engine 2026-08-04: pressing R in Edit mode opened nothing). Rotating with no ghost and no prop
            // selection was a no-op anyway, so falling through in that case costs nothing and gives the key back.
            // NB _placementActive, not `_ghost != null`: CancelPlacement only HIDES the ghost (it stays allocated for
            // re-arming), so a null-check reads "armed" forever and would keep swallowing R after a cancel.
            if (editMode && key.Keycode == Key.R && !key.CtrlPressed && (_placementActive || HasPropSelection()))
            {
                _placementRot = Mathf.Wrap(_placementRot + ROT_STEP, 0f, Mathf.Tau);
                if (_ghost != null) _ghost.RotateY(ROT_STEP); // spin the ghost preview
                RotateSelectedProps(ROT_STEP);
                GD.Print($"[EntityPlacer] Placement yaw: {Mathf.RadToDeg(_placementRot):F0}° (visual-only).");
                GetViewport().SetInputAsHandled();
                return;
            }

            // Story 6.6: Ctrl+C / Ctrl+V — copy the selection, paste at the cursor with relative offsets preserved.
            if (editMode && key.CtrlPressed && key.Keycode == Key.C)
            {
                CopySelection();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (editMode && key.CtrlPressed && key.Keycode == Key.V)
            {
                PasteClipboard(_lastCursorWorld);
                GetViewport().SetInputAsHandled();
                return;
            }

            // UX-DR56 (Story 6.1): Esc cancels an active placement mode.
            //
            // DW-898: it must NOT consume the event. The gate below reads as "only swallow Esc when something is
            // armed", which sounds narrow — but _placementActive starts TRUE (see its declaration: "Starts armed so
            // placement works immediately on entering Edit mode"), so on a cold boot into Create the very first Esc
            // was always eaten here and never reached MainScene._UnhandledInput's Esc→Settings toggle. Reported as
            // "Esc isn't working to get to settings in create mode, only after I enter test mode with F5 and press Esc
            // there first". The Play round-trip was correlation, not cause: the swallowed first press had already set
            // _placementActive = false, so the SECOND press fell through and Settings opened. Any palette click
            // re-arms, so it went dead again for exactly one press each time.
            //
            // Letting it fall through costs no affordance: right-click is the documented pure cancel (UX-DR56, the
            // branch above), and the Edit HUD hint strip already promises "Esc=Settings" unconditionally. Esc now
            // hides an armed ghost AND opens Settings, which is what the hint has always claimed.
            if (editMode && key.Keycode == Key.Escape)
            {
                if (_placementActive) CancelPlacement(); // deliberately NOT SetInputAsHandled — see above
                return;
            }

            // Undo / redo — only in Edit mode
            if (editMode && key.CtrlPressed)
            {
                // DW-144: while a terrain stroke's live operate() is in flight, swallow Ctrl+Z/Y so History.Undo/Redo
                // can't run mid-stroke and race the RestoreRegions the finished stroke will build — which would
                // corrupt the captured `after` state. Consume the event without touching the history.
                if ((key.Keycode == Key.Z || key.Keycode == Key.Y) && TerrainBrush?.IsPainting == true)
                {
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (key.Keycode == Key.Z)
                {
                    _history.Undo();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (key.Keycode == Key.Y)
                {
                    _history.Redo();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

            // Delete: in Select mode with a selection, delete the whole group as ONE undo step; otherwise delete the
            // single hovered entity.
            if (editMode && key.Keycode == Key.Delete)
            {
                if (_mode == PlacementMode.Select && _selection.Count > 0) DeleteSelection();
                else TryDeleteAt(_lastCursorWorld);
                GetViewport().SetInputAsHandled();
                return;
            }

            // DW-895 — Ctrl+<letter> belongs to the EDITOR tier (EditorHotkeys), never to this palette. Godot's
            // InputEventKey.Keycode is unaffected by modifiers, so without this bail Ctrl+B *is* Key.B in the switch
            // below: one Ctrl+B press cycled the palette here AND opened the Building editor from MainScene's
            // EditorHotkeys dispatch, because nothing below consumed the event. Same for Ctrl+U (unit card) and — as
            // an unreported third instance — Ctrl+G (DslGraph), which also flipped grid snap on every press.
            // This is the idiom the 2026-08-04 keymap re-tier retrofitted into TerrainBrush/RegionTool/PathabilityTool/
            // WaterTool; EntityPlacer was the one bare-letter consumer it missed. It must sit AFTER the Ctrl+C/V and
            // Ctrl+Z/Y branches above, which legitimately own their chords.
            if (key.CtrlPressed && key.Keycode >= Key.A && key.Keycode <= Key.Z) return;

            // DW-895/897 — the palette is an EDIT-mode tool. Ungated, this switch also ran in Play, where it mutated
            // _mode / _gridSnapEnabled and re-armed placement off the same Tab that SelectionSystem uses for the
            // subgroup cycle.
            if (!editMode) return;

            // DW-897 — Tab: yield to GUI focus traversal. The palette IS the legitimate Edit-mode owner of Tab
            // (MainScene's header documents "Tab cycles mode"), but it was never consuming the event, so one press
            // both cycled the palette and ran Godot's built-in ui_focus_next across every focusable Control on
            // screen — the "Tab cycles the upper-right Palette" report. TextFocusGuard above only bails for
            // LineEdit/TextEdit, so without this check consuming Tab would break focus traversal between the
            // Buttons/OptionButtons inside an open editor panel. With it, the palette politely loses Tab whenever a
            // control has focus, and owns it when the user is driving the map.
            if (key.Keycode == Key.Tab && GetViewport().GuiGetFocusOwner() != null) return;

            bool handled = true;
            switch (key.Keycode)
            {
                case Key.Tab:
                    if (_mode == PlacementMode.Building)
                        _mode = _lastUnitMode;
                    else
                        CycleUnitMode();
                    ArmPlacement(); // UX-DR56: re-selecting a mode re-arms placement after a cancel
                    SyncPaletteToMode();
                    break;

                case Key.B:
                    if (_mode == PlacementMode.Building)
                        CycleBuildingType();
                    else
                    {
                        _lastUnitMode = _mode;
                        _mode = PlacementMode.Building;
                    }
                    ArmPlacement(); // UX-DR56: re-selecting a mode re-arms placement after a cancel
                    SyncPaletteToMode();
                    GD.Print($"[EntityPlacer] Mode: {ModeLabel}");
                    break;

                case Key.U:
                    CycleUnitType();
                    ArmPlacement(); // UX-DR56: re-selecting a unit archetype re-arms placement after a cancel
                    RefreshSubRow();
                    break;

                case Key.G:
                    _gridSnapEnabled = !_gridSnapEnabled;
                    RefreshSnapButton();
                    GD.Print($"[EntityPlacer] Grid snap: {(_gridSnapEnabled ? "ON" : "OFF")}");
                    // G is a deliberate BROADCAST: EntityPlacer, RegionTool and WaterTool each keep their own grid-snap
                    // flag and all three read this one press. Consuming it here would silently desync the other two
                    // (their snap would stop tracking the key) — the exact regression DW-666's proposed fix would have
                    // caused. It stays unconsumed on purpose.
                    handled = false;
                    break;

                default:
                    handled = false; // a key this palette does not bind must fall through untouched
                    break;
            }
            // DW-895: consume ONLY what was actually acted on. This switch was the one branch in this method that
            // never called SetInputAsHandled, which is what let every key it handled fire a second consumer.
            if (handled) GetViewport().SetInputAsHandled();
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (GameState.Instance == null || GameState.Instance.Mode != GameMode.Edit) return;

            if (@event is InputEventMouseButton mb
                && mb.ButtonIndex == MouseButton.Left
                && mb.Pressed)
            {
                // UX-DR56 (Story 6.1): a cancelled (disarmed) placement mode ignores left-clicks until re-armed.
                if (!_placementActive) return;
                // Story 6.6: Select mode never places — its left-drag is the marquee (handled in _Input).
                if (_mode == PlacementMode.Select) return;
                TrySpawnAt(mb.Position, mb.ShiftPressed);
            }
        }

        // ── Ghost preview ─────────────────────────────────────────────────────

        private void CreateGhostMesh()
        {
            _ghost = new MeshInstance3D
            {
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible    = false,
            };
            _ghost.MaterialOverride = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode     = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor  = new Color(0.2f, 0.5f, 1f, 0.4f),
            };
            // Add as a sibling in the scene so it renders in 3D world space
            GetParent()?.AddChild(_ghost);

            // DW-893: the shadow twin. Opaque + ShadowsOnly, so it contributes an accurate silhouette shadow while
            // drawing nothing itself — the ghost keeps its translucent look, which an AlphaScissor conversion would
            // have traded away.
            _ghostShadow = new MeshInstance3D
            {
                CastShadow      = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
                Visible         = false,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0f, 0f, 0f, 1f) },
            };
            GetParent()?.AddChild(_ghostShadow);

            RefreshGhostVisuals();
        }

        /// <summary>
        /// DW-893 — the ghost's mesh for the CURRENT mode: the object's real authored mesh where one exists, else the
        /// primitive stand-in this method used to hard-code for everything.
        ///
        /// <para>Every unit, hero and building already renders from a per-type GLB through
        /// <see cref="MeshLoader.LoadFromGlb"/> (units via MultiMeshBridge, buildings via BuildingBridge), and that
        /// loader already falls back to a box on a miss — so resolving the ghost through the same seam gives the real
        /// silhouette for free and cannot regress to worse than today. Props have no GLBs yet, so they share
        /// <see cref="PropRenderer.MeshForProp"/>, the same primitive table the placed prop renders from; resource
        /// nodes, start positions and items keep their stand-ins because no authored mesh exists for them at all.</para>
        ///
        /// <para>Cached by mode+id: the loader does a PackedScene load + Instantiate + Free, so this may run on a mode
        /// or type change (which is where <c>RefreshGhostVisuals</c> is called from) but never per frame.</para>
        /// </summary>
        private (Mesh Mesh, float Scale, bool Authored) ResolveGhostMesh()
        {
            string key = _mode switch
            {
                PlacementMode.Building => $"b:{_faction?.GetBuilding(_buildingId)?.Id ?? _buildingId.ToString()}",
                PlacementMode.Prop     => $"p:{PROP_LIBRARY[_propIndex % PROP_LIBRARY.Length]}",
                PlacementMode.P1Unit or PlacementMode.P2Unit => $"u:{CurrentUnitDef()?.Id ?? "?"}",
                _                      => $"m:{_mode}",
            };
            if (_ghostMeshCache.TryGetValue(key, out var hit)) return hit;

            (Mesh Mesh, float Scale, bool Authored) resolved;
            switch (_mode)
            {
                case PlacementMode.P1Unit:
                case PlacementMode.P2Unit:
                {
                    UnitDefinition? def = CurrentUnitDef();
                    Color tint = _mode == PlacementMode.P2Unit ? new Color(1f, 0.30f, 0.2f) : new Color(0.2f, 0.50f, 1f);
                    Mesh mesh = MeshLoader.LoadFromGlb(def?.MeshPath ?? "", new Vector3(0.6f, 1.2f, 0.6f), tint, _assets);
                    resolved = (mesh, def?.MeshScale ?? 1f, !string.IsNullOrEmpty(def?.MeshPath));
                    break;
                }
                case PlacementMode.Building:
                {
                    BuildingDefinition? def = _faction?.GetBuilding(_buildingId);
                    Mesh mesh = MeshLoader.LoadFromGlb(def?.MeshPath ?? "", new Vector3(4f, 2f, 4f), new Color(0.2f, 0.80f, 0.3f), _assets);
                    resolved = (mesh, def?.MeshScale ?? 1f, !string.IsNullOrEmpty(def?.MeshPath));
                    break;
                }
                case PlacementMode.Prop:
                    resolved = (PropRenderer.MeshForProp(PROP_LIBRARY[_propIndex % PROP_LIBRARY.Length]), 1f, true);
                    break;
                case PlacementMode.ResourceNode:
                    resolved = (new SphereMesh { Radius = 0.8f, Height = 1.6f }, 1f, false);
                    break;
                case PlacementMode.StartPos:
                    resolved = (new BoxMesh { Size = new Vector3(0.15f, 3f, 0.15f) }, 1f, false); // flag pole
                    break;
                default:
                    resolved = (new BoxMesh { Size = new Vector3(0.6f, 1.2f, 0.6f) }, 1f, false);
                    break;
            }

            _ghostMeshCache[key] = resolved;
            return resolved;
        }

        /// <summary>The unit definition the palette currently has armed (mirrors the pick <c>PlaceUnit</c> makes).</summary>
        private UnitDefinition? CurrentUnitDef()
        {
            var units = GetCombatUnits();
            if (units.Count > 0) return units[_unitIndex % units.Count];
            return ActiveFactionDef()?.GetUnit("infantry");
        }

        /// <summary>
        /// Update ghost mesh shape and colour to reflect the current placement mode.
        /// Called on every mode or building-type change.
        /// </summary>
        private void RefreshGhostVisuals()
        {
            if (_ghost == null) return;

            // DW-893: the object's real mesh where one is authored (see ResolveGhostMesh), so the author can judge
            // footprint, facing and silhouette before committing — instead of the same box for everything.
            var ghostMesh = ResolveGhostMesh();
            _ghost.Mesh   = ghostMesh.Mesh;
            _ghostScale   = ghostMesh.Scale;
            // Sit the mesh ON the ground rather than centring it. An authored GLB is feet-pivoted with its own AABB,
            // so the lift is derived from the mesh (the MultiMeshBridge.GroundOffsetFor formula, which every placed
            // entity already uses); the primitives keep the hand-tuned half-height offsets they were drawn for.
            _ghostGroundLift = ghostMesh.Authored
                ? -ghostMesh.Mesh.GetAabb().Position.Y * ghostMesh.Scale
                : _mode switch
                {
                    PlacementMode.Building     => 1.0f,
                    PlacementMode.ResourceNode => 0.8f,
                    PlacementMode.StartPos     => 1.5f, // flag pole: half of 3u height
                    PlacementMode.Prop         => 0.5f,
                    _                          => 0.6f,
                };

            if (_ghostShadow != null) _ghostShadow.Mesh = ghostMesh.Mesh;

            if (_ghost.MaterialOverride is StandardMaterial3D mat)
            {
                mat.AlbedoColor = _mode switch
                {
                    PlacementMode.P2Unit       => new Color(1f,   0.30f, 0.2f, 0.40f),
                    PlacementMode.ResourceNode => new Color(1f,   0.85f, 0.2f, 0.40f),
                    PlacementMode.Building     => new Color(0.2f, 0.80f, 0.3f, 0.35f),
                    PlacementMode.StartPos     => StartSlotGhostColor(_startSlot),
                    PlacementMode.Prop         => _propBlocks
                                                    ? new Color(1f, 0.45f, 0.2f, 0.45f)   // blocking prop = orange
                                                    : new Color(0.5f, 0.85f, 0.4f, 0.40f), // cosmetic prop = green
                    _                          => new Color(0.2f, 0.50f, 1f,   0.40f),
                };
            }
        }

        /// <summary>Story 6.7 — per-slot ghost/marker tint for a 2–4 start-position slot (matches
        /// <see cref="StartPositionBridge"/>'s P1 blue / P2 red / P3 green / P4 yellow).</summary>
        private static Color StartSlotGhostColor(int slot) => (slot & 3) switch
        {
            0 => new Color(0.20f, 0.50f, 1.00f, 0.5f), // P1 blue
            1 => new Color(1.00f, 0.30f, 0.20f, 0.5f), // P2 red
            2 => new Color(0.30f, 0.85f, 0.35f, 0.5f), // P3 green
            _ => new Color(0.95f, 0.85f, 0.20f, 0.5f), // P4 yellow
        };

        private void UpdateGhostPosition(bool editMode)
        {
            if (_ghost == null) return;

            if (!editMode || _camCtrl == null)
            {
                _ghost.Visible = false;
                return;
            }

            var camera = _camCtrl.GetCamera();
            if (camera == null) { _ghost.Visible = false; return; }

            var mousePos = GetViewport().GetMousePosition();
            var origin   = camera.ProjectRayOrigin(mousePos);
            var dir      = camera.ProjectRayNormal(mousePos);

            if (Mathf.Abs(dir.Y) < 0.0001f) { _ghost.Visible = false; return; }
            float t = -origin.Y / dir.Y;
            if (t < 0f) { _ghost.Visible = false; return; }

            var   hit  = origin + dir * t;
            float x    = SnapValue(hit.X);
            float z    = SnapValue(hit.Z);
            // DW-893: both computed once per mesh resolve in RefreshGhostVisuals, so this per-frame path stays cheap.
            float yOff  = _ghostGroundLift;
            float scale = _mode == PlacementMode.Prop ? _propScale : _ghostScale;

            _lastCursorWorld = new Vector3(x, 0f, z);
            _ghost.Position  = new Vector3(x, yOff, z);
            _ghost.Scale     = new Vector3(scale, scale, scale);
            // UX-DR56 (Story 6.1): keep tracking the cursor (so Delete still targets the hover), but only SHOW the
            // ghost while a placement mode is armed — a right-click/Esc cancel hides it until a mode is re-selected.
            // Story 6.6: Select mode has no ghost (it marquees, not places).
            _ghost.Visible   = _placementActive && _mode != PlacementMode.Select;

            // DW-893: the shadow twin tracks the ghost exactly and is shown with it.
            if (_ghostShadow != null)
            {
                _ghostShadow.Position = _ghost.Position;
                _ghostShadow.Scale    = _ghost.Scale;
                _ghostShadow.Visible  = _ghost.Visible;
            }
        }

        // ── Placement arm / cancel (UX-DR56) ──────────────────────────────────

        /// <summary>Disarm the active placement mode: hides the ghost and makes left-clicks no-op until re-armed.
        /// Fired by right-click or Esc while a placement mode is active.</summary>
        private void CancelPlacement()
        {
            _placementActive = false;
            if (_ghost != null)       _ghost.Visible = false;
            if (_ghostShadow != null) _ghostShadow.Visible = false;
            // DW-894: a right-click mid-drag returns BEFORE the Select-mode dispatch, so without this the drag stayed
            // "live" with a stale start point and the next left-release committed a bogus move. Pre-existing, but the
            // live preview makes it visible (the selection would sit stuck at an offset), so it is cleared here.
            _groupMoving = false;
            _groupMovePreviewDelta = Vector3.Zero;
            HideDragPreview();
            GD.Print("[EntityPlacer] Placement cancelled.");
        }

        /// <summary>Re-arm placement (ghost re-appears, left-click places again). Called whenever the user
        /// re-selects a mode/type via keyboard (Tab/B/U) or the palette.</summary>
        private void ArmPlacement() => _placementActive = true;

        /// <summary>Snap a world coordinate to the nearest 1-unit grid when snap is on.</summary>
        private float SnapValue(float v) => _gridSnapEnabled ? Mathf.Round(v) : v;

        // ── Placement ─────────────────────────────────────────────────────────

        private void TrySpawnAt(Vector2 screenPos, bool shiftHeld)
        {
            var camera = _camCtrl?.GetCamera();
            if (camera == null) return;

            var rayOrigin = camera.ProjectRayOrigin(screenPos);
            var rayDir    = camera.ProjectRayNormal(screenPos);
            if (Mathf.Abs(rayDir.Y) < 0.0001f) return;
            float t = -rayOrigin.Y / rayDir.Y;
            if (t < 0f) return;

            var   hit      = rayOrigin + rayDir * t;
            var   fixedPos = new FixedVec3(
                Fixed.FromFloat(SnapValue(hit.X)),
                Fixed.Zero,
                Fixed.FromFloat(SnapValue(hit.Z)));

            switch (_mode)
            {
                case PlacementMode.ResourceNode:
                    PlaceResourceNode(fixedPos);
                    break;
                case PlacementMode.Building:
                    PlaceBuilding(fixedPos);
                    break;
                case PlacementMode.StartPos:
                    MoveStartPosition(hit);
                    break;
                case PlacementMode.Item:
                    PlaceItem(fixedPos);
                    break;
                case PlacementMode.Prop:
                    PlaceProp(hit);
                    break;
                case PlacementMode.Select:
                    break; // never places (marquee handled in _Input)
                default:
                    PlaceUnit(fixedPos, shiftHeld);
                    break;
            }
        }

        /// <summary>Story 3.15 — place a ground <see cref="ItemStore"/> instance of the currently-selected registry item
        /// (the minimal in-game item placement surface; full item authoring is Story 3.16). No-op when no item registry
        /// is wired or it is empty. Undo destroys the created instance.</summary>
        private void PlaceItem(FixedVec3 pos)
        {
            if (_items == null || _itemRegistry == null || _itemRegistry.Count == 0) return;
            int defId = _itemIndex % _itemRegistry.Count;
            int charges = _itemRegistry.Get(defId).Charges;
            int packed = _items.Create(defId, charges, pos);
            if (packed < 0) return;

            // DW-137: mirror the placement into ScenarioData.Items so the item survives Save/reload AND the F5
            // Edit→Play re-apply (which re-applies only _ctx.Scenario) instead of silently vanishing. itemId is the
            // authored registry id; the sync matches by position only (items are slot-less).
            string  itemId     = _itemRegistry.Get(defId).Id;
            Vector3 wpos       = new Vector3(pos.X.ToFloat(), 0f, pos.Z.ToFloat());
            object? syncHandle = _onItemSync?.Invoke(ScenarioSyncOp.Add, null, itemId, wpos);

            // DW-35: redo re-Creates a NEW packed ref each time — capture it in a box (the PlaceUnit pattern) so undo
            // destroys the LIVE instance. Pushing the ORIGINAL `packed` would leak the redone item: after
            // place→undo→redo the original ref no longer resolves, so the undo could never find (and destroy) the
            // instance the redo just re-created.
            int[] box = { packed };
            _history.Push(
                redo: () =>
                {
                    int r = _items.Create(defId, charges, pos); if (r >= 0) box[0] = r;
                    // Guard the ReAdd on a successful Create (r >= 0), mirroring the PlaceUnit redo legs: a store-full
                    // redo (r < 0) must NOT re-append the handle to ScenarioData.Items, or it strands a phantom item
                    // entry with no live counterpart (which a Save between redo and undo would then persist).
                    if (r >= 0 && syncHandle != null) _onItemSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, itemId, wpos);
                },
                undo: () =>
                {
                    if (_items.TryResolveRef(box[0], out int slot)) _items.Destroy(slot);
                    if (syncHandle != null) _onItemSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, itemId, wpos);
                });
        }

        private void PlaceUnit(FixedVec3 pos, bool asWorker)
        {
            Faction faction = _mode == PlacementMode.P2Unit ? Faction.Player2 : Faction.Player1;
            Vector3 wpos    = new Vector3(pos.X.ToFloat(), 0f, pos.Z.ToFloat());

            if (asWorker)
            {
                int id = DoSpawnWorker(pos, faction);
                if (id < 0) return;

                // Story 6.1: persist to ScenarioData.Units. A def-less spawn (faction has no worker def) is
                // intentionally NOT persisted — sync only when we have a concrete unit id.
                string  unitId     = ActiveFactionDef()?.GetUnitByCategory("Worker")?.Id ?? "";
                object? syncHandle = string.IsNullOrEmpty(unitId) ? null
                    : _onUnitSync?.Invoke(ScenarioSyncOp.Add, null, unitId, faction, wpos);
                ApplyPlacementRot(syncHandle);

                int[] box = { id };
                _history.Push(
                    redo: () =>
                    {
                        int r = DoSpawnWorker(pos, faction); if (r >= 0) box[0] = r;
                        if (r >= 0 && syncHandle != null) _onUnitSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, unitId, faction, wpos);
                    },
                    undo: () =>
                    {
                        _world.Destroy(box[0]);
                        if (syncHandle != null) _onUnitSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, unitId, faction, wpos);
                    });
            }
            else
            {
                // Capture the UnitDefinition at placement time so redo re-creates the same archetype
                var combatUnits = GetCombatUnits();
                UnitDefinition? def = combatUnits.Count > 0
                    ? combatUnits[_unitIndex % combatUnits.Count]
                    : ActiveFactionDef()?.GetUnit("infantry");

                int id = DoSpawnCombatUnit(pos, faction, def);
                if (id < 0) return;

                // Story 6.1: persist to ScenarioData.Units (def-less fallback spawn is not persisted).
                string  unitId     = def?.Id ?? "";
                object? syncHandle = string.IsNullOrEmpty(unitId) ? null
                    : _onUnitSync?.Invoke(ScenarioSyncOp.Add, null, unitId, faction, wpos);
                ApplyPlacementRot(syncHandle);

                int[] box = { id };
                _history.Push(
                    redo: () =>
                    {
                        int r = DoSpawnCombatUnit(pos, faction, def); if (r >= 0) box[0] = r;
                        if (r >= 0 && syncHandle != null) _onUnitSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, unitId, faction, wpos);
                    },
                    undo: () =>
                    {
                        _world.Destroy(box[0]);
                        if (syncHandle != null) _onUnitSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, unitId, faction, wpos);
                    });
            }
        }

        /// <summary>Spawn a worker and return its entity id (-1 on failure).</summary>
        private int DoSpawnWorker(FixedVec3 pos, Faction faction)
        {
            var   fdef  = ActiveFactionDef();
            var   def   = fdef?.GetUnitByCategory("Worker"); // worker id differs per faction (alpha "worker", beta "forgehand")
            float hp    = def?.Hp    ?? WORKER_HEALTH;
            float speed = def?.Speed ?? WORKER_SPEED;

            int id = _world.Create(pos, faction, Fixed.FromFloat(hp), Fixed.FromFloat(speed));
            if (id < 0) { GD.PrintErr("[EntityPlacer] EntityWorld full."); return -1; }

            // Story 2.9b (AC3): route through the single def→SoA mapper (A2 rule) so a placed worker gets its authored
            // abilities/max_energy — and Category/CollisionRadius/SeparationPriorityOf/FeedbackProfile exactly as
            // before — like ScenarioApplier.SpawnUnit and DoSpawnCombatUnit already do. Supersedes the Story 1.13
            // hand-copy exception, whose "workers carry no combat stats" rationale no longer holds now that a worker
            // can cast. With no def, the Create() defaults stand.
            if (def != null)
            {
                _world.ApplyUnitDefinition(id, def);
            }

            // Worker-specific state, applied AFTER the mapper so these intentionally OVERRIDE it: an editor-placed
            // worker is always FREE supply (ApplyUnitDefinition set SupplyCost = def.Supply = 1) — a deliberate,
            // pre-existing divergence from ScenarioApplier.SpawnUnit, out of scope to reconcile here — plus the
            // gather-loop starting state. MUST come after the mapper (AC3.2), else SupplyCost silently becomes 1.
            _world.SupplyCost[id]    = 0;
            _world.GatherState[id]   = GatherState.Idle;
            _world.CarryCapacity[id] = Fixed.FromFloat(WORKER_CARRY);

            int workerMesh = def != null ? (fdef?.IndexOfUnit(def.Id) ?? -1) : -1;
            _world.MeshType[id] = (byte)(workerMesh < 0 ? 0 : workerMesh);

            GD.Print($"[EntityPlacer] Spawned {faction} worker id={id}");
            return id;
        }

        /// <summary>Spawn a combat unit and return its entity id (-1 on failure).</summary>
        private int DoSpawnCombatUnit(FixedVec3 pos, Faction faction, UnitDefinition? def)
        {
            float hp     = def?.Hp    ?? HEALTH;
            float speed  = def?.Speed ?? SPEED;
            byte  supply = (byte)(def?.Supply ?? 1);

            if (_resources != null && !_resources.HasSupply(faction, supply))
            {
                GD.Print($"[EntityPlacer] {faction} supply full " +
                         $"({_resources.SupplyUsed[(int)faction]}/{_resources.SupplyCap[(int)faction]}).");
                return -1;
            }

            int id = _world.Create(pos, faction, Fixed.FromFloat(hp), Fixed.FromFloat(speed));
            if (id < 0) { GD.PrintErr("[EntityPlacer] EntityWorld full."); return -1; }

            // Copy the def's per-entity fields via the SINGLE shared mapper (Story 1.13 review fix) so an editor-placed
            // unit gets its authored collision_radius / separation_priority / Category like a scenario-placed one. With
            // no def, keep the legacy fallback combat stats; the separation/formation fields stay at Create defaults.
            if (def != null)
            {
                _world.ApplyUnitDefinition(id, def);
            }
            else
            {
                _world.SupplyCost[id]   = supply;
                _world.AttackRange[id]  = Fixed.FromFloat(ATTACK_RANGE);
                // Story 3.12: def-less spawn bypasses ApplyUnitDefinition, so mirror the old range→delivery inference
                // (deleted from CombatSystem) — else this ranged (range 5 > 2.5) fallback unit regresses from projectile
                // to the Create-default Hitscan. ProjectileSpeed keeps the Create default (== the old global 18).
                _world.Delivery[id] = _world.AttackRange[id] > UnitDefinition.LegacyDeliveryThreshold
                    ? AttackDelivery.Projectile : AttackDelivery.Hitscan;
                // Story 2.2a (A2): a non-mapper write must set BOTH Base (authored source) and Effective, so a later
                // modifier recomputes Effective = Base + delta correctly instead of from a stale Zero base.
                _world.BaseAttackDamage[id]      = Fixed.FromFloat(ATTACK_DMG);
                _world.EffectiveAttackDamage[id] = _world.BaseAttackDamage[id];
                _world.AttackSpeed[id]  = Fixed.FromFloat(ATTACK_SPEED);
                _world.DamageTypeOf[id] = DamageType.Normal;
                _world.ArmorTypeOf[id]  = ArmorType.Light;
                _world.VisionRange[id]  = Fixed.FromFloat(8f);
                _world.SplashRadius[id] = Fixed.FromFloat(0f);
            }

            int meshType = def != null ? (ActiveFactionDef()?.IndexOfUnit(def.Id) ?? -1) : -1;
            _world.MeshType[id] = (byte)(meshType < 0 ? 0 : meshType);

            GD.Print($"[EntityPlacer] Spawned {faction} {def?.DisplayName ?? "unit"} id={id}");
            return id;
        }

        private void PlaceResourceNode(FixedVec3 pos)
        {
            if (_nodes == null) { GD.PrintErr("[EntityPlacer] ResourceNodeStore not set."); return; }
            var supply = Fixed.FromFloat(_nodeSupply);
            var rate   = Fixed.FromFloat(_nodeRate);
            // DW-151 follow-up: create the LIVE node with the SAME plain-Gather schema defaults (radius 15, income
            // period 30) that its persisted DTO carries below — NOT the store defaults (0/0). Otherwise a later
            // group-move/paste (which re-derives its DTO by READING the live store in Describe) would capture 0/0 and
            // ship income_period_ticks:0 in the moved DTO, re-introducing the exact latent Income footgun A1 fixed for
            // the place path. These two fields are inert on a Gather node (requiresStructureId is "" so the radius is
            // never consulted; IncomePeriodTicks only matters for an Income node) and are NOT folded into SimChecksum,
            // so this is determinism-neutral and makes the freshly-placed live store byte-match the post-reload state.
            int nodeId = _nodes.Create(pos, supply, rate, NODE_MAX_GATHERERS,
                ResourceCollectionModel.Gather, ResourceKind.Ore, "", Fixed.FromFloat(15f), Faction.Neutral, 30);
            if (nodeId < 0) { GD.PrintErr("[EntityPlacer] ResourceNodeStore full."); return; }
            GD.Print($"[EntityPlacer] Placed ore node id={nodeId} supply={_nodeSupply:F0} rate={_nodeRate:F0} at ({pos.X},{pos.Z})");

            // Capture for undo — slot id is stable (no free list in ResourceNodeStore)
            int   capturedId         = nodeId;
            var   capturedSupply     = supply;
            var   capturedRate       = rate;
            var   capturedNodes      = _nodes;

            // Story 6.1: mirror into ScenarioData.ResourceNodes. A freshly placed node is a plain Gather/Ore node
            // (its authored economy fields default), so build it from the editor's supply/rate/max-gatherers.
            Vector3 wpos             = new Vector3(pos.X.ToFloat(), 0f, pos.Z.ToFloat());
            float   capturedSupplyF  = _nodeSupply;
            float   capturedRateF    = _nodeRate;
            // DW-151/A1: a freshly placed node is a plain Gather/Ore node. Pass the ScenarioResourceNode SCHEMA
            // defaults (radius 15, income period 30) — NOT the store defaults (0/0) — so a plain placed node's
            // persisted DTO is byte-identical to the pre-widening object-initializer baseline (and never ships a
            // latent income_period_ticks:0 that a later flip to collection_model:Income would credit every tick).
            object? syncHandle       = _onResourceNodeSync?.Invoke(ScenarioSyncOp.Add, null, wpos, capturedSupplyF, capturedRateF, NODE_MAX_GATHERERS,
                                                                   ResourceCollectionModel.Gather, ResourceKind.Ore, "", Fixed.FromFloat(15f), Faction.Neutral, 30);
            ApplyPlacementRot(syncHandle);
            _history.Push(
                redo: () =>
                {
                    capturedNodes.Active[capturedId]          = true;
                    capturedNodes.SupplyRemaining[capturedId] = capturedSupply;
                    capturedNodes.SupplyTotal[capturedId]     = capturedSupply;
                    capturedNodes.GatherRate[capturedId]      = capturedRate;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, wpos, capturedSupplyF, capturedRateF, NODE_MAX_GATHERERS,
                                                ResourceCollectionModel.Gather, ResourceKind.Ore, "", Fixed.FromFloat(15f), Faction.Neutral, 30);
                },
                undo: () =>
                {
                    capturedNodes.Active[capturedId] = false;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, wpos, capturedSupplyF, capturedRateF, NODE_MAX_GATHERERS,
                                                ResourceCollectionModel.Gather, ResourceKind.Ore, "", Fixed.FromFloat(15f), Faction.Neutral, 30);
                });
        }

        private void PlaceBuilding(FixedVec3 pos)
        {
            if (_buildings == null) { GD.PrintErr("[EntityPlacer] BuildingStore not set."); return; }

            Faction faction   = Faction.Player1;
            string  buildingId = _buildingId; // Story 6.8: the authored building id (built-in OR custom)
            var     buildingDef = _faction?.GetBuilding(buildingId); // buildings always placed for P1 in editor

            if (buildingDef != null && buildingDef.Prerequisites.Length > 0)
            {
                string? missing = TechTreeChecker.FirstMissing(_buildings, faction, buildingDef.Prerequisites);
                if (missing != null)
                {
                    // Story 4.2: TechTreeChecker now returns the raw id — resolve it to a display name here
                    // (the editor's direct-placement path already has _faction in scope), same as BuildingSystem.
                    // An empty (unauthored) DisplayName falls back to the raw id too, not a blank string.
                    string missingName = _faction?.GetBuilding(missing)?.DisplayName is { Length: > 0 } dn ? dn : missing;
                    GD.Print($"[EntityPlacer] Cannot place {buildingId}: requires {missingName}.");
                    return;
                }
            }

            // Story 6.8: cost resolves from the authored def (its `cost` map's ore), not a fixed enum-indexed array —
            // so a Custom building never IndexOutOfRange's BUILDING_COSTS[5].
            Fixed capturedCost = Fixed.FromFloat(BuildingCostOre(buildingId));
            if (_resources != null)
            {
                if (!_resources.SpendOre(faction, capturedCost))
                {
                    GD.Print($"[EntityPlacer] Cannot afford {buildingId} " +
                             $"(costs {capturedCost.ToFloat():F0} ore, have {_resources.Ore[(int)faction].ToFloat():F0}).");
                    return;
                }
            }

            // Story 6.8: place by authored id, threading the resolved def's stats + DefinitionId (so a Custom building
            // previews with real stats and a stable id the nav/render buckets key on).
            int id = CreateEditorBuilding(_buildings, pos, faction, buildingId, _faction);
            if (id < 0) { GD.PrintErr("[EntityPlacer] BuildingStore full."); return; }
            GD.Print($"[EntityPlacer] Placed {buildingId} id={id} for {faction} at ({pos.X:F1},{pos.Z:F1})");

            // Story 6.1: mirror the placement into ScenarioData so the building survives save/reload AND the F5
            // Edit→Play re-apply. Editor-placed buildings start under construction (BuildingStore.Create seeds the
            // full ConstructionTimer), so persist pre_built:false to re-apply identically.
            string       capturedId2  = buildingId;
            Vector3      wpos         = new Vector3(pos.X.ToFloat(), 0f, pos.Z.ToFloat());

            // Capture for undo — building slot id is stable (BuildingStore has no free list)
            int      capturedId       = id;
            Faction  capturedFaction  = faction;
            Fixed    capturedDuration = _buildings.ConstructionDuration[id];
            var      capturedBuildings = _buildings;

            object?  syncHandle = _onBuildingSync?.Invoke(ScenarioSyncOp.Add, null, capturedId2, capturedFaction, wpos, false);
            ApplyPlacementRot(syncHandle);
            _history.Push(
                redo: () =>
                {
                    capturedBuildings.Alive[capturedId]              = true;
                    capturedBuildings.DefinitionId[capturedId]       = capturedId2; // Story 6.8: restore authored id (a recycled slot may carry another)
                    capturedBuildings.ConstructionTimer[capturedId]  = capturedDuration;
                    _resources?.SpendOre(capturedFaction, capturedCost);
                    _onBuildingSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, capturedId2, capturedFaction, wpos, false);
                },
                undo: () =>
                {
                    capturedBuildings.Destroy(capturedId);
                    _resources?.AddOre(capturedFaction, capturedCost);
                    _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, capturedId2, capturedFaction, wpos, false);
                });
        }

        // ── DW-163: declared start-slot set (by VALUE, not contiguous count) ──────────────────────────────────────

        /// <summary>The scenario's declared start-slot VALUES, sorted ascending. Falls back to the 2-slot floor
        /// <c>{0,1}</c> when no scenario is loaded (or it declares none) so the picker still shows P1/P2 — matching the
        /// no-scenario fallback in <c>ScenarioLoadPhase.SetupStartPositionBridge</c>.</summary>
        private int[] DeclaredStartSlots()
        {
            // DW-163 patch: restrict to slots below the picker ceiling so a validator-legal out-of-range slot (a
            // 5–8-player {5,6} set) is never surfaced as a P-toggle nor used to index the length-CEILING economy
            // arrays in the render/remove legs — it would otherwise crash on click/remove. Falls back to {0,1}.
            var declared = StartSlotMath.DeclaredBelowCeiling(_scenarioGetter?.Invoke()?.PlayerSlots, START_SLOT_CEILING);
            return declared.Length > 0 ? declared : new[] { 0, 1 };
        }

        /// <summary>True when <paramref name="slot"/> has a backing <see cref="ScenarioPlayerSlot"/> (is declared) — as
        /// opposed to a "+"-armed PENDING slot that has none yet. Generalizes the old contiguous <c>slot &lt; count</c>
        /// membership test so it stays correct for a non-contiguous set (byte-identical for a contiguous one).</summary>
        private bool IsSlotDeclared(int slot) => System.Array.IndexOf(DeclaredStartSlots(), slot) >= 0;

        /// <summary>Re-derive the working slot count from the LIVE declared set (clamped to [MIN,CEILING]) instead of
        /// the old contiguous <c>max(count,slot+1)</c>/<c>=slot</c> heuristics — those mis-sized a non-contiguous set.</summary>
        private void RefreshStartSlotCount()
            => _startSlotCount = System.Math.Clamp(DeclaredStartSlots().Length, START_SLOT_MIN, START_SLOT_CEILING);

        private void MoveStartPosition(Vector3 worldPos)
        {
            var snapped = new Vector3(SnapValue(worldPos.X), 0f, SnapValue(worldPos.Z));

            // Capture old state before applying (Story 6.7: ore AND crystal per slot).
            int   capturedSlot        = _startSlot;
            float capturedNewOre      = _startOre;
            float capturedNewCrystal  = _startCrystal;
            float capturedOldOre      = _slotStartOre[_startSlot];
            float capturedOldCrystal  = _slotStartCrystal[_startSlot];
            var   capturedNewPos      = snapped;
            var   capturedOldBase = _resources?.FactionBase[(int)FactionRegistry.ToFaction(_startSlot)] ?? default;
            var   capturedOldPos = new Vector3(capturedOldBase.X.ToFloat(), 0f, capturedOldBase.Z.ToFloat());

            bool created = _onStartPosMoved?.Invoke(_startSlot, snapped, _startOre, _startCrystal) ?? false;
            _slotStartOre[_startSlot]     = _startOre;
            _slotStartCrystal[_startSlot] = _startCrystal;
            // DW-163: re-derive the count from the live declared set (UpsertStartSlot appended by VALUE) rather than the
            // old contiguous max(count, slot+1) heuristic. Preserves DW-161 deferred-grow: the declared count grows only
            // when a placement actually created the slot.
            RefreshStartSlotCount();
            // DW-161: when this placement CREATED a slot (the deferred "+" path — the count just grew), refresh the
            // picker so the newly-placed slot surfaces its P-toggle immediately instead of only after the next event.
            if (created) RefreshSubRow();

            GD.Print($"[EntityPlacer] Start pos P{_startSlot + 1} → ({snapped.X:F1}, {snapped.Z:F1})  ore={_startOre:F0} crystal={_startCrystal:F0}");

            _history.Push(
                // Redo re-invokes the move (Upsert re-creates the slot if it had been removed by the undo below).
                redo: () =>
                {
                    _onStartPosMoved?.Invoke(capturedSlot, capturedNewPos, capturedNewOre, capturedNewCrystal);
                    // DW-161 (review): mirror the forward path — a redo that RE-creates the slot must also re-grow the
                    // working count + refresh the picker. Otherwise undo→redo of a slot-creating placement leaves the
                    // slot back in PlayerSlots but the picker under-counted (its P-toggle never re-renders until reload).
                    if (created)
                    {
                        _slotStartOre[capturedSlot]     = capturedNewOre;
                        _slotStartCrystal[capturedSlot] = capturedNewCrystal;
                        RefreshStartSlotCount();
                        RefreshSubRow();
                    }
                },
                undo: () =>
                {
                    if (created)
                    {
                        // The placement CREATED the slot — undo removes it entirely rather than repositioning a phantom
                        // at origin. DW-163: re-derive the count from the now-shrunk declared set and clamp the selection
                        // to a still-declared slot value (not the old contiguous count-1).
                        _onStartSlotRemoved?.Invoke(capturedSlot);
                        RefreshStartSlotCount();
                        int[] decl = DeclaredStartSlots();
                        if (System.Array.IndexOf(decl, _startSlot) < 0)
                            _startSlot = decl.Length > 0 ? StartSlotMath.MaxDeclared(decl) : 0;
                        RefreshSubRow();
                    }
                    else
                    {
                        _onStartPosMoved?.Invoke(capturedSlot, capturedOldPos, capturedOldOre, capturedOldCrystal);
                    }
                });
        }

        /// <summary>DW-161 — remove the trailing start slot at <paramref name="slot"/> (its index == the resulting
        /// count): truncate the owner's PlayerSlots + hide its flag marker, shrink the working count, clamp the
        /// selection, and refresh the picker. Used by BOTH the "−" button and the redo leg of its coalesced undo
        /// entry, so the two stay behaviourally identical.</summary>
        private void RemoveStartSlotAt(int slot)
        {
            _onStartSlotRemoved?.Invoke(slot); // owner truncates PlayerSlots (by Slot VALUE) + hides the marker
            // DW-163: re-derive the count from the live declared set (the slot was removed by value) rather than the old
            // contiguous `_startSlotCount = slot` heuristic, which mis-sized a non-contiguous set. Clamp the selection to
            // the highest still-declared slot value.
            RefreshStartSlotCount();
            int[] decl = DeclaredStartSlots();
            if (System.Array.IndexOf(decl, _startSlot) < 0)
                _startSlot = decl.Length > 0 ? StartSlotMath.MaxDeclared(decl) : 0;
            RefreshSubRow();
            RefreshGhostVisuals();
        }

        /// <summary>DW-167 — push ONE coalesced undo entry for an in-place start-slot economy edit (ore+crystal),
        /// committed on the spinner's focus-exit. Both legs route through <see cref="ApplyStartSlotEconomy"/> so the
        /// mirror arrays, the owner persist callback, the selected-slot fields, and the visible spinner stay coherent.
        /// The push is skipped by the caller when the value did not actually change (same-value edit = no entry).</summary>
        private void PushStartSlotEconomy(int slot, float oldOre, float oldCrystal, float newOre, float newCrystal)
        {
            _history.Push(
                redo: () => ApplyStartSlotEconomy(slot, newOre, newCrystal),
                undo: () => ApplyStartSlotEconomy(slot, oldOre, oldCrystal));
        }

        /// <summary>DW-167 — apply an economy (ore/crystal) value to a start slot: write the mirror arrays, fire the
        /// owner persist callback (StartCrystal is hash-folded), and — when the slot still exists — re-select it and
        /// sync the selected-slot fields so the change is visible, then refresh the palette so the spinner reflects it.
        /// Used by BOTH legs (redo=new, undo=old) of the coalesced entry.</summary>
        private void ApplyStartSlotEconomy(int slot, float ore, float crystal)
        {
            if (slot < 0 || slot >= START_SLOT_CEILING) return;
            _slotStartOre[slot]     = ore;
            _slotStartCrystal[slot] = crystal;
            // DW-163: re-select + surface the slot only when it is still DECLARED (byte-identical to the old
            // `slot < _startSlotCount` for a contiguous set, but correct for a non-contiguous one, e.g. slot 3 in {0,3}).
            if (IsSlotDeclared(slot))
            {
                _startSlot    = slot;
                _startOre     = ore;
                _startCrystal = crystal;
            }
            _onStartSlotEconomy?.Invoke(slot, ore, crystal);
            RefreshSubRow();
        }

        // ── Mode cycling (keyboard) ───────────────────────────────────────────

        private void CycleUnitMode()
        {
            _mode = _mode switch
            {
                PlacementMode.P1Unit       => PlacementMode.P2Unit,
                PlacementMode.P2Unit       => PlacementMode.ResourceNode,
                PlacementMode.ResourceNode => PlacementMode.P1Unit,
                _                          => PlacementMode.P1Unit,
            };
            GD.Print($"[EntityPlacer] Mode: {ModeLabel}");
        }

        private void CycleBuildingType()
        {
            // Story 6.8: cycle through the owner faction's AUTHORED buildings (any id, including custom), not the closed
            // BuildingType enum. Falls back to a no-op when the faction authors none.
            var ids = BuildingPaletteIds();
            if (ids.Count == 0) return;
            int cur = ids.IndexOf(_buildingId);
            _buildingId = ids[(cur + 1) % ids.Count];
        }

        /// <summary>Story 6.8 — the authored building ids the palette offers, in faction-list order. Empty when no
        /// faction def is wired.</summary>
        private System.Collections.Generic.List<string> BuildingPaletteIds()
        {
            var ids = new System.Collections.Generic.List<string>();
            if (_faction != null)
                foreach (var b in _faction.Buildings)
                    if (!string.IsNullOrEmpty(b.Id)) ids.Add(b.Id);
            return ids;
        }

        /// <summary>Story 6.8 — human-readable palette label for a building id (its authored DisplayName, else the raw id).</summary>
        private string BuildingLabel(string buildingId)
        {
            var dn = _faction?.GetBuilding(buildingId)?.DisplayName;
            return string.IsNullOrEmpty(dn) ? buildingId : dn;
        }

        /// <summary>Story 6.8 — resolve the ore cost to place a building by authored id from its def's `cost` map. Falls
        /// back to the legacy per-enum constant for a built-in, or <see cref="DEFAULT_BUILDING_COST"/> for a custom
        /// building with no authored cost — never IndexOutOfRange's BUILDING_COSTS for the Custom sentinel.</summary>
        private float BuildingCostOre(string buildingId)
        {
            var bdef = _faction?.GetBuilding(buildingId);
            if (bdef != null && bdef.ResolvedCost.TryGetValue("ore", out int ore)) return ore;
            var t = TechTreeChecker.BuildingTypeFromId(buildingId);
            return t is BuildingType bt && (int)bt >= 0 && (int)bt < BUILDING_COSTS.Length
                ? BUILDING_COSTS[(int)bt] : DEFAULT_BUILDING_COST;
        }

        /// <summary>Story 6.8 — create an editor building slot by authored id, threading the resolved def's stats +
        /// DefinitionId (mirrors <see cref="ProjectChimera.Economy.BuildingSystem.PlaceBuildingDirectById"/>) so a
        /// Custom building previews with real stats and a stable id the nav/render buckets key on. Returns the slot id
        /// (or -1 if full). Starts under construction (like the legacy Create call it replaces).</summary>
        private static int CreateEditorBuilding(BuildingStore store, FixedVec3 pos, Faction faction,
                                                string buildingId, FactionDefinition? fdef)
        {
            var bdef = fdef?.GetBuilding(buildingId);
            BuildingType type = TechTreeChecker.BuildingTypeFromId(buildingId) ?? BuildingType.Custom;
            return store.Create(pos, faction, type,
                bdef?.RevivesHeroes ?? false,
                bdef?.SellsItems ?? false,
                bdef?.ShopStock,
                bdef != null ? Fixed.FromFloat(bdef.ShopRadius) : default,
                buildingId: bdef?.Id ?? buildingId,
                health: bdef != null ? Fixed.FromFloat(bdef.Hp) : (Fixed?)null,
                supplyBonus: bdef?.SupplyBonus,
                constructionDuration: bdef?.ConstructionTime is float ct ? Fixed.FromFloat(ct) : (Fixed?)null);
        }

        private void CycleUnitType()
        {
            var units = GetCombatUnits();
            if (units.Count == 0) return;
            _unitIndex = (_unitIndex + 1) % units.Count;
            GD.Print($"[EntityPlacer] Unit type: {units[_unitIndex].DisplayName} " +
                     $"({units[_unitIndex].Category}, {units[_unitIndex].Hp}hp, " +
                     $"{units[_unitIndex].AttackRange}rng)");
        }

        // ── Palette UI construction ───────────────────────────────────────────

        private void BuildPaletteUi()
        {
            _paletteCanvas = new CanvasLayer { Visible = false };
            AddChild(_paletteCanvas);

            // ── Outer container: anchored flush to the right viewport edge ────
            // Using AnchorLeft = AnchorRight = 1 pins the right side to the
            // viewport right. OffsetLeft sets the panel width; the panel grows
            // leftward so it never overflows the right edge.
            var panel = new PanelContainer
            {
                AnchorLeft     = 1f,
                AnchorRight    = 1f,
                AnchorTop      = 0f,
                AnchorBottom   = 0f,
                OffsetLeft     = -420f,  // panel width = 420 px
                OffsetRight    = -4f,    // 4 px margin from right edge
                OffsetTop      = 4f,
                GrowHorizontal = Control.GrowDirection.Begin,
                MouseFilter    = Control.MouseFilterEnum.Stop,
            };

            var panelBg = new StyleBoxFlat
            {
                BgColor                 = new Color(0.10f, 0.11f, 0.16f, 0.90f),
                BorderColor             = new Color(0.30f, 0.35f, 0.48f, 0.60f),
                BorderWidthLeft         = 1,
                BorderWidthRight        = 1,
                BorderWidthTop          = 1,
                BorderWidthBottom       = 1,
                CornerRadiusTopLeft     = 6,
                CornerRadiusTopRight    = 6,
                CornerRadiusBottomLeft  = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft       = 10f,
                ContentMarginRight      = 10f,
                ContentMarginTop        = 8f,
                ContentMarginBottom     = 8f,
            };
            panel.AddThemeStyleboxOverride("panel", panelBg);
            _paletteCanvas.AddChild(panel);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 6);
            panel.AddChild(vbox);

            // ── Title row ─────────────────────────────────────────────────────
            var titleRow = new HBoxContainer();
            var title = new Label { Text = "ENTITY PALETTE" };
            title.AddThemeFontSizeOverride("font_size", 12);
            title.AddThemeColorOverride("font_color", new Color(0.55f, 0.60f, 0.75f));
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleRow.AddChild(title);
            vbox.AddChild(titleRow);

            // ── Mode row: 5 toggle buttons in a flow row ──────────────────────
            // Use GridContainer (3 + 2) so buttons never exceed panel width.
            var modeGrid = new GridContainer { Columns = 5 };
            modeGrid.AddThemeConstantOverride("h_separation", 4);
            modeGrid.AddThemeConstantOverride("v_separation", 4);
            vbox.AddChild(modeGrid);

            var modeGroup = new ButtonGroup();
            _modeBtns = new Button[MODE_ORDER.Length];

            for (int i = 0; i < MODE_ORDER.Length; i++)
            {
                var btn = new Button
                {
                    Text              = MODE_LABELS[i],
                    ToggleMode        = true,
                    ButtonGroup       = modeGroup,
                    ButtonPressed     = (MODE_ORDER[i] == _mode),
                    CustomMinimumSize = new Vector2(74f, 28f),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                btn.AddThemeFontSizeOverride("font_size", 12);
                var capturedMode = MODE_ORDER[i];
                btn.Pressed += () => SelectModeFromPalette(capturedMode);
                modeGrid.AddChild(btn);
                _modeBtns[i] = btn;
            }

            vbox.AddChild(new HSeparator());

            // ── Sub-row: wrapping flow container so unit buttons wrap ─────────
            // HFlowContainer is available in Godot 4.1+ (we're on 4.6.2).
            _subRow = new HFlowContainer();
            _subRow.AddThemeConstantOverride("h_separation", 4);
            _subRow.AddThemeConstantOverride("v_separation", 4);
            vbox.AddChild(_subRow);
            RefreshSubRow();

            // ── Grid snap toggle ──────────────────────────────────────────────
            var snapRow = new HBoxContainer();
            snapRow.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(snapRow);

            var snapLabel = new Label { Text = "Grid Snap" };
            snapLabel.AddThemeFontSizeOverride("font_size", 12);
            snapLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            snapRow.AddChild(snapLabel);

            var snapHint = new Label { Text = "[G]" };
            snapHint.AddThemeFontSizeOverride("font_size", 11);
            snapHint.AddThemeColorOverride("font_color", new Color(0.45f, 0.45f, 0.5f));
            snapRow.AddChild(snapHint);

            _snapBtn = new Button
            {
                Text              = "OFF",
                ToggleMode        = true,
                ButtonPressed     = _gridSnapEnabled,
                CustomMinimumSize = new Vector2(46f, 26f),
            };
            _snapBtn.AddThemeFontSizeOverride("font_size", 12);
            _snapBtn.Toggled += on =>
            {
                _gridSnapEnabled = on;
                _snapBtn.Text    = on ? "ON" : "OFF";
            };
            snapRow.AddChild(_snapBtn);
        }

        /// <summary>
        /// Called when a palette mode button is clicked.
        /// Updates state, ghost, and sub-row without triggering keyboard cycle logic.
        /// </summary>
        private void SelectModeFromPalette(PlacementMode mode)
        {
            if (mode == PlacementMode.Building && _mode != PlacementMode.Building)
                _lastUnitMode = _mode;

            _mode = mode;
            ArmPlacement(); // UX-DR56: selecting a palette mode re-arms placement after a cancel
            RefreshGhostVisuals();
            RefreshSubRow();
            GD.Print($"[EntityPlacer] Mode: {ModeLabel}");
        }

        /// <summary>
        /// Synchronise palette button states after a keyboard-driven mode change.
        /// Rebuilds the sub-row to match the new mode.
        /// </summary>
        private void SyncPaletteToMode()
        {
            // Setting ButtonPressed on one button in a ButtonGroup deactivates the rest
            for (int i = 0; i < _modeBtns.Length; i++)
                _modeBtns[i].ButtonPressed = (MODE_ORDER[i] == _mode);

            RefreshGhostVisuals();
            RefreshSubRow();
        }

        /// <summary>
        /// Rebuild the sub-row of archetype / building-type buttons for the current mode.
        /// Clears old buttons first (RemoveChild + QueueFree to take effect immediately).
        /// </summary>
        private void RefreshSubRow()
        {
            if (_subRow == null) return;

            // Remove and free existing children synchronously so new ones don't pile up
            foreach (var child in _subRow.GetChildren())
            {
                _subRow.RemoveChild(child);
                child.QueueFree();
            }

            if (_mode == PlacementMode.Building)
            {
                // Story 6.8: enumerate the owner faction's AUTHORED buildings (id + DisplayName), so a custom building
                // appears in the palette alongside the built-ins. If the selected id is no longer offered (faction
                // swap), default to the first available so placement stays valid.
                var buildGroup = new ButtonGroup();
                var ids = BuildingPaletteIds();
                if (ids.Count > 0 && !ids.Contains(_buildingId)) _buildingId = ids[0];
                foreach (var bid in ids)
                {
                    var btn = new Button
                    {
                        Text          = BuildingLabel(bid),
                        ToggleMode    = true,
                        ButtonGroup   = buildGroup,
                        ButtonPressed = (bid == _buildingId),
                    };
                    var capturedId = bid;
                    btn.Pressed += () =>
                    {
                        _buildingId = capturedId;
                        ArmPlacement(); // UX-DR56: re-selecting a building type re-arms placement after a cancel
                        RefreshGhostVisuals();
                        GD.Print($"[EntityPlacer] Building: {_buildingId}");
                    };
                    _subRow.AddChild(btn);
                }
            }
            else if (_mode is PlacementMode.P1Unit or PlacementMode.P2Unit)
            {
                var units = GetCombatUnits();
                if (units.Count == 0)
                {
                    var hint = new Label { Text = "(no units loaded)" };
                    hint.AddThemeFontSizeOverride("font_size", 12);
                    _subRow.AddChild(hint);
                    return;
                }

                var unitGroup  = new ButtonGroup();
                int clampedIdx = _unitIndex % units.Count;
                for (int i = 0; i < units.Count; i++)
                {
                    int capturedIdx = i;
                    var btn = new Button
                    {
                        Text          = units[i].DisplayName,
                        ToggleMode    = true,
                        ButtonGroup   = unitGroup,
                        ButtonPressed = (i == clampedIdx),
                    };
                    btn.Pressed += () =>
                    {
                        _unitIndex = capturedIdx;
                        ArmPlacement(); // UX-DR56: re-selecting a unit archetype re-arms placement after a cancel
                        RefreshGhostVisuals();
                        GD.Print($"[EntityPlacer] Unit archetype: {units[capturedIdx].DisplayName}");
                    };
                    _subRow.AddChild(btn);
                }
            }
            else if (_mode == PlacementMode.StartPos)
            {
                // DW-163: render toggles by DECLARED slot VALUE (not a contiguous 0..count-1 loop) so a validator-legal
                // non-contiguous set ({0,3}) shows P1+P4, not P1+P2. The scenario WRITE path (UpsertStartSlot/
                // RemoveStartSlot) is already keyed by slot value and is unchanged.
                int[] declared = DeclaredStartSlots();
                RefreshStartSlotCount(); // working count == declared count, clamped [MIN,CEILING]

                // Clamp the selection: keep it when declared OR a valid in-range "+"-armed pending slot; otherwise fall
                // back to the highest declared slot value.
                if (_startSlot < 0 || _startSlot >= START_SLOT_CEILING)
                    _startSlot = declared.Length > 0 ? StartSlotMath.MaxDeclared(declared) : 0;

                // The slots to render: every declared value, plus the "+"-armed pending slot (selected but not yet
                // declared) so its toggle surfaces immediately (DW-161 deferred-grow).
                var render = new System.Collections.Generic.List<int>(declared);
                if (_startSlot >= 0 && _startSlot < START_SLOT_CEILING && System.Array.IndexOf(declared, _startSlot) < 0)
                    render.Add(_startSlot);
                render.Sort();

                var slotGroup = new ButtonGroup();
                foreach (int slot in render)
                {
                    int capturedSlot = slot;
                    var btn = new Button
                    {
                        // Label P{value+1} — equivalent to the old FactionRegistry.ToFaction(slot)=Player{slot+1}.
                        Text          = $"P{capturedSlot + 1}",
                        ToggleMode    = true,
                        ButtonGroup   = slotGroup,
                        ButtonPressed = (capturedSlot == _startSlot),
                        CustomMinimumSize = new Vector2(36f, 0f),
                    };
                    btn.Pressed += () =>
                    {
                        _startSlot    = capturedSlot;
                        _startOre     = _slotStartOre[_startSlot];
                        _startCrystal = _slotStartCrystal[_startSlot];
                        ArmPlacement(); // UX-DR56: re-selecting a start-pos slot re-arms placement after a cancel
                        RefreshSubRow();       // reflect this slot's ore/crystal in the spinners
                        RefreshGhostVisuals(); // update ghost color
                    };
                    _subRow.AddChild(btn);
                }

                // Add-slot ("+") — arms the LOWEST undeclared slot (fills a gap first, e.g. {0,3}→1). Disabled once
                // every slot below the ceiling is declared.
                int lowestUndeclared = StartSlotMath.LowestUndeclared(declared, START_SLOT_CEILING);
                var addBtn = new Button { Text = "+", CustomMinimumSize = new Vector2(28f, 0f),
                                          Disabled = lowestUndeclared < 0 };
                addBtn.Pressed += () =>
                {
                    int target = StartSlotMath.LowestUndeclared(DeclaredStartSlots(), START_SLOT_CEILING);
                    if (target < 0) return;
                    // DW-161: arm the pending slot but do NOT grow the declared set yet — the backing PlayerSlot (and
                    // the count) grows only when the slot is actually placed on terrain (MoveStartPosition), so a "+"
                    // with no placement leaves no phantom slot and needs no undo entry.
                    _startSlot      = target;
                    _startOre       = _slotStartOre[_startSlot];
                    _startCrystal   = _slotStartCrystal[_startSlot];
                    ArmPlacement();
                    RefreshSubRow();
                    RefreshGhostVisuals();
                };
                _subRow.AddChild(addBtn);

                // Remove-slot ("−") — removes the HIGHEST declared slot VALUE, down to a floor of 2.
                var remBtn = new Button { Text = "−", CustomMinimumSize = new Vector2(28f, 0f),
                                          Disabled = declared.Length <= START_SLOT_MIN };
                remBtn.Pressed += () =>
                {
                    int[] decl = DeclaredStartSlots();
                    if (decl.Length <= START_SLOT_MIN) return;
                    int removed = StartSlotMath.MaxDeclared(decl);
                    // DW-161: capture the slot's pre-remove position + economy BEFORE removing, so the undo re-adds
                    // it exactly (position from the live sim base, ore/crystal from the working mirror arrays).
                    var basePt = _resources?.FactionBase[(int)FactionRegistry.ToFaction(removed)] ?? default;
                    Vector3 capturedPos     = new Vector3(basePt.X.ToFloat(), 0f, basePt.Z.ToFloat());
                    float   capturedOre     = _slotStartOre[removed];
                    float   capturedCrystal = _slotStartCrystal[removed];
                    RemoveStartSlotAt(removed);
                    _history.Push(
                        redo: () => RemoveStartSlotAt(removed),
                        undo: () =>
                        {
                            // Re-add the slot via the move bridge (Upsert re-appends the PlayerSlot by value), then
                            // restore the mirror economy and re-derive the count/selection from the live declared set.
                            _onStartPosMoved?.Invoke(removed, capturedPos, capturedOre, capturedCrystal);
                            _slotStartOre[removed]     = capturedOre;
                            _slotStartCrystal[removed] = capturedCrystal;
                            RefreshStartSlotCount();
                            _startSlot      = removed;
                            _startOre       = _slotStartOre[_startSlot];
                            _startCrystal   = _slotStartCrystal[_startSlot];
                            RefreshSubRow();
                            RefreshGhostVisuals();
                        });
                };
                _subRow.AddChild(remBtn);

                // Starting ore spinner (per selected slot)
                var oreLabel = new Label { Text = " Ore:" };
                oreLabel.AddThemeFontSizeOverride("font_size", 12);
                _subRow.AddChild(oreLabel);

                var spin = new SpinBox
                {
                    MinValue          = 0,
                    MaxValue          = 9999,
                    Step              = 50,
                    Value             = _startOre,
                    CustomMinimumSize = new Vector2(80f, 0f),
                };
                // DW-167: coalesce spinner edits into ONE undo entry (mirror of BuildingCardPanel.Edit's
                // FocusEntered-snap / ValueChanged-live-persist / FocusExited-commit pattern). `oreEditSlot` binds this
                // spinner to the slot it was built for (RefreshSubRow rebuilds it whenever the selection changes, so it
                // always equals the shown slot). The live ValueChanged still fires `_onStartSlotEconomy` so the
                // hash-folded economy persists immediately; only the coalesced history push is added on commit.
                int      oreEditSlot = _startSlot;
                LineEdit oreLe       = spin.GetLineEdit();
                float    oreSnap     = _slotStartOre[oreEditSlot];
                oreLe.FocusEntered += () => oreSnap = _slotStartOre[oreEditSlot];
                spin.ValueChanged  += v => { _startOre = (float)v; _slotStartOre[oreEditSlot] = _startOre;
                                             // DW-161 (review): only persist/coalesce for a slot with a backing PlayerSlot.
                                             // A "+"-armed PENDING slot has none — the owner persist would no-op and a
                                             // coalesced undo entry would dangle; its edited economy instead rides into
                                             // MoveStartPosition when the slot is placed. DW-163: the pending test is now
                                             // DECLARED-membership, not `slot < count` (which mis-classified a non-
                                             // contiguous declared slot, e.g. slot 3 in {0,3}, as pending).
                                             if (IsSlotDeclared(oreEditSlot))
                                                 _onStartSlotEconomy?.Invoke(oreEditSlot, _slotStartOre[oreEditSlot], _slotStartCrystal[oreEditSlot]); };
                oreLe.FocusExited  += () =>
                {
                    float now = _slotStartOre[oreEditSlot];
                    if (now != oreSnap && IsSlotDeclared(oreEditSlot))
                        PushStartSlotEconomy(oreEditSlot, oreSnap, _slotStartCrystal[oreEditSlot], now, _slotStartCrystal[oreEditSlot]);
                    oreSnap = now;
                };
                _subRow.AddChild(spin);

                // Starting crystal spinner (per selected slot)
                var crysLabel = new Label { Text = " Crystal:" };
                crysLabel.AddThemeFontSizeOverride("font_size", 12);
                _subRow.AddChild(crysLabel);

                var crysSpin = new SpinBox
                {
                    MinValue          = 0,
                    MaxValue          = 9999,
                    Step              = 50,
                    Value             = _startCrystal,
                    CustomMinimumSize = new Vector2(80f, 0f),
                };
                // DW-167: same coalescing shape as the ore spinner, for the crystal axis.
                int      crysEditSlot = _startSlot;
                LineEdit crysLe       = crysSpin.GetLineEdit();
                float    crysSnap     = _slotStartCrystal[crysEditSlot];
                crysLe.FocusEntered += () => crysSnap = _slotStartCrystal[crysEditSlot];
                crysSpin.ValueChanged += v => { _startCrystal = (float)v; _slotStartCrystal[crysEditSlot] = _startCrystal;
                                                // DW-161/DW-163: declared-membership pending guard, mirror of the ore spinner above.
                                                if (IsSlotDeclared(crysEditSlot))
                                                    _onStartSlotEconomy?.Invoke(crysEditSlot, _slotStartOre[crysEditSlot], _slotStartCrystal[crysEditSlot]); };
                crysLe.FocusExited  += () =>
                {
                    float now = _slotStartCrystal[crysEditSlot];
                    if (now != crysSnap && IsSlotDeclared(crysEditSlot))
                        PushStartSlotEconomy(crysEditSlot, _slotStartOre[crysEditSlot], crysSnap, _slotStartOre[crysEditSlot], now);
                    crysSnap = now;
                };
                _subRow.AddChild(crysSpin);

                var hint = new Label { Text = " Click terrain" };
                hint.AddThemeFontSizeOverride("font_size", 11);
                _subRow.AddChild(hint);
            }
            else if (_mode == PlacementMode.Item)
            {
                // Story 3.16: a per-item selector so the palette can place ANY registry item (closes the 3.15 defer where
                // _itemIndex was never advanced — the palette could only ever place registry item 0). Also fixes the
                // latent bug where Item mode fell into the ResourceNode branch below and showed ore-node spinners.
                if (_itemRegistry == null || _itemRegistry.Count == 0)
                {
                    var hint = new Label { Text = "(no items loaded)" };
                    hint.AddThemeFontSizeOverride("font_size", 12);
                    _subRow.AddChild(hint);
                    return;
                }
                var itemGroup = new ButtonGroup();
                int clampedItem = _itemIndex % _itemRegistry.Count;
                _itemIndex = clampedItem;
                for (int i = 0; i < _itemRegistry.Count; i++)
                {
                    int capturedIdx = i;
                    var idef = _itemRegistry.Get(i);
                    var btn = new Button
                    {
                        Text          = string.IsNullOrEmpty(idef.DisplayName) ? idef.Id : idef.DisplayName,
                        ToggleMode    = true,
                        ButtonGroup   = itemGroup,
                        ButtonPressed = (i == clampedItem),
                    };
                    btn.AddThemeFontSizeOverride("font_size", 12);
                    btn.Pressed += () =>
                    {
                        _itemIndex = capturedIdx;
                        ArmPlacement(); // UX-DR56: re-selecting an item re-arms placement after a cancel
                        GD.Print($"[EntityPlacer] Item: {_itemRegistry.Get(capturedIdx).Id}");
                    };
                    _subRow.AddChild(btn);
                }
                var hint2 = new Label { Text = " Click terrain" };
                hint2.AddThemeFontSizeOverride("font_size", 11);
                _subRow.AddChild(hint2);
            }
            else if (_mode == PlacementMode.Prop)
            {
                // Prop library picker + scale + blocks_pathing + a rotation hint (R rotates; visual-only).
                var propGroup = new ButtonGroup();
                int clampedProp = _propIndex % PROP_LIBRARY.Length;
                _propIndex = clampedProp;
                for (int i = 0; i < PROP_LIBRARY.Length; i++)
                {
                    int capturedIdx = i;
                    var btn = new Button
                    {
                        Text          = PROP_LIBRARY[i],
                        ToggleMode    = true,
                        ButtonGroup   = propGroup,
                        ButtonPressed = (i == clampedProp),
                    };
                    btn.AddThemeFontSizeOverride("font_size", 12);
                    btn.Pressed += () => { _propIndex = capturedIdx; ArmPlacement(); RefreshGhostVisuals(); GD.Print($"[EntityPlacer] Prop: {PROP_LIBRARY[capturedIdx]}"); };
                    _subRow.AddChild(btn);
                }

                var scaleLabel = new Label { Text = " Scale:" };
                scaleLabel.AddThemeFontSizeOverride("font_size", 12);
                _subRow.AddChild(scaleLabel);
                var scaleSpin = new SpinBox { MinValue = 0.1, MaxValue = 10, Step = 0.25, Value = _propScale, CustomMinimumSize = new Vector2(70f, 0f) };
                scaleSpin.ValueChanged += v => _propScale = (float)v;
                _subRow.AddChild(scaleSpin);

                var blocksBtn = new Button { Text = "Blocks path", ToggleMode = true, ButtonPressed = _propBlocks };
                blocksBtn.AddThemeFontSizeOverride("font_size", 12);
                blocksBtn.Toggled += on => { _propBlocks = on; RefreshGhostVisuals(); };
                _subRow.AddChild(blocksBtn);

                var rotHint = new Label { Text = " [R] rotate (visual only)" };
                rotHint.AddThemeFontSizeOverride("font_size", 11);
                _subRow.AddChild(rotHint);
            }
            else if (_mode == PlacementMode.Select)
            {
                var hint = new Label { Text = "Drag a box to select. Shift adds. Del removes. Ctrl+C/V copy/paste. Drag selection to move." };
                hint.AddThemeFontSizeOverride("font_size", 11);
                _subRow.AddChild(hint);
                var dupBtn = new Button { Text = "Duplicate" };
                dupBtn.AddThemeFontSizeOverride("font_size", 12);
                dupBtn.Pressed += DuplicateSelection;
                _subRow.AddChild(dupBtn);
            }
            else // ResourceNode
            {
                var supplyLabel = new Label { Text = "Supply:" };
                supplyLabel.AddThemeFontSizeOverride("font_size", 12);
                _subRow.AddChild(supplyLabel);

                var supplySpin = new SpinBox
                {
                    MinValue          = 100,
                    MaxValue          = 9999,
                    Step              = 100,
                    Value             = _nodeSupply,
                    CustomMinimumSize = new Vector2(80f, 0f),
                };
                supplySpin.ValueChanged += v => _nodeSupply = (float)v;
                _subRow.AddChild(supplySpin);

                var rateLabel = new Label { Text = " Rate:" };
                rateLabel.AddThemeFontSizeOverride("font_size", 12);
                _subRow.AddChild(rateLabel);

                var rateSpin = new SpinBox
                {
                    MinValue          = 1,
                    MaxValue          = 20,
                    Step              = 1,
                    Value             = _nodeRate,
                    CustomMinimumSize = new Vector2(60f, 0f),
                };
                rateSpin.ValueChanged += v => _nodeRate = (float)v;
                _subRow.AddChild(rateSpin);

                var hint = new Label { Text = " Click terrain" };
                hint.AddThemeFontSizeOverride("font_size", 11);
                _subRow.AddChild(hint);
            }
        }

        /// <summary>
        /// Sync the snap button text and toggle state from <see cref="_gridSnapEnabled"/>.
        /// Called by the G key handler; the button's Toggled event keeps itself in sync on click.
        /// </summary>
        private void RefreshSnapButton()
        {
            if (_snapBtn == null) return;
            _snapBtn.Text          = _gridSnapEnabled ? "ON" : "OFF";
            _snapBtn.ButtonPressed = _gridSnapEnabled; // fires Toggled, which re-sets _gridSnapEnabled (no-op)
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Story 6.6 — persist the armed placement yaw onto a just-added scenario entry (the sync-Add handle
        /// IS the ScenarioBuilding/Unit/ResourceNode entry). Rotation is presentation-only and excluded from every
        /// checksum/hash, so this only affects round-trip + (for props) the visual yaw.</summary>
        private void ApplyPlacementRot(object? handle)
        {
            switch (handle)
            {
                case ScenarioBuilding b:     b.Rot = _placementRot; break;
                case ScenarioUnit u:         u.Rot = _placementRot; break;
                case ScenarioResourceNode n: n.Rot = _placementRot; break;
            }
        }

        /// <summary>Returns the faction definition for the currently active placement mode.</summary>
        private FactionDefinition? ActiveFactionDef()
            => _mode == PlacementMode.P2Unit ? _faction2 : _faction;

        /// <summary>Returns all non-Worker, non-Structure units — the placeable combat archetypes.</summary>
        private List<UnitDefinition> GetCombatUnits()
        {
            var def = ActiveFactionDef();
            if (def == null) return new List<UnitDefinition>();
            var result = new List<UnitDefinition>();
            foreach (var u in def.Units)
            {
                if (string.Equals(u.Category, "Worker",    System.StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(u.Category, "Structure", System.StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(u);
            }
            return result;
        }

        // ── Delete (Delete key) ───────────────────────────────────────────────

        /// <summary>
        /// Delete whatever entity is closest to <paramref name="worldPos"/> in Edit mode.
        /// Priority order: buildings → units → resource nodes.
        /// Pushes an undo command so the deletion can be reversed.
        /// </summary>
        private void TryDeleteAt(Vector3 worldPos)
        {
            if (_buildings != null)
            {
                int bid = FindNearestBuilding(worldPos, 3f);
                if (bid >= 0) { DeleteBuilding(bid); return; }
            }
            {
                int uid = FindNearestUnit(worldPos, 2.5f);
                if (uid >= 0) { DeleteUnit(uid); return; }
            }
            if (_nodes != null)
            {
                int nid = FindNearestNode(worldPos, 2f);
                if (nid >= 0) { DeleteResourceNode(nid); return; }
            }
            // Story 6.6: props (scenario-owned) delete last in the priority order.
            {
                object? prop = FindNearestProp(worldPos, 2f);
                if (prop != null) { DeleteProp(prop); return; }
            }
        }

        private void DeleteBuilding(int id)
        {
            if (_buildings == null) return;
            Faction capturedFaction = _buildings.FactionOf[id];
            Fixed   capturedDuration = _buildings.ConstructionDuration[id];
            Fixed   capturedTimer    = _buildings.ConstructionTimer[id];
            var     capturedBuildings = _buildings;

            // Story 6.1/6.8: capture the descriptor (authored id) from the LIVE slot BEFORE Destroy so the ScenarioData
            // match + undo restore can run. DefinitionId keys the sync (Custom-safe — no BUILDING_COSTS[5] indexing).
            string       capturedDefId = _buildings.DefinitionId[id];
            Vector3      wpos          = new Vector3(_buildings.Position[id].X.ToFloat(), 0f, _buildings.Position[id].Z.ToFloat());

            _buildings.Destroy(id);
            // No ore refund on delete (destructive intent)
            GD.Print($"[EntityPlacer] Deleted building id={id}");

            // Story 6.1: remove the matching ScenarioData.Buildings entry, capturing the REAL object so undo restores
            // it by identity (preserving authored pre_built / slot / type — never reconstructing a lossy value).
            object? syncHandle = _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, capturedDefId, capturedFaction, wpos, false);

            _history.Push(
                redo: () =>
                {
                    capturedBuildings.Destroy(id);
                    _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, capturedDefId, capturedFaction, wpos, false);
                },
                undo: () =>
                {
                    capturedBuildings.Alive[id]              = true;
                    capturedBuildings.DefinitionId[id]       = capturedDefId; // Story 6.8: restore authored id
                    capturedBuildings.ConstructionTimer[id]  = capturedTimer;
                    capturedBuildings.ConstructionDuration[id] = capturedDuration;
                    _onBuildingSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, capturedDefId, capturedFaction, wpos, false);
                });
        }

        private void DeleteUnit(int id)
        {
            // Story 3.17: capture the full authored residue via the Godot-free Core mapper. A def-based unit stores
            // only its def reference; RestoreUnit re-derives every def-derived field through ApplyUnitDefinition, so
            // armor/passives/abilities/feedback/tags/domain/delivery/collision/separation/category/XP no longer revert
            // to Create defaults on undo (the recurring RestoreUnit drop-debt).
            UnitSnapshot snap = _world.SnapshotUnit(id);
            // DW-52: free the linked HeroStore row (if this is a hero) BEFORE tearing down the entity, so the row does
            // not leak. HeroIndex[id] is the packed handle; a non-hero's HERO_NONE (-1) resolves false → clean no-op.
            _heroes?.DestroyByRef(_world.HeroIndex[id]);
            _world.Destroy(id);
            GD.Print($"[EntityPlacer] Deleted unit id={id}");

            // Story 6.1: remove the matching ScenarioData.Units entry (identity-preserving), matched by slot+position.
            // A def-less spawn was never persisted, so only sync def-based units.
            string  unitId     = snap.Def?.Id ?? "";
            Vector3 wpos       = new Vector3(snap.Position.X.ToFloat(), 0f, snap.Position.Z.ToFloat());
            object? syncHandle = snap.Def != null
                ? _onUnitSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, unitId, snap.Faction, wpos)
                : null;

            // Undo re-creates the unit; the new id is boxed so redo can destroy it again
            int[] box = { -1 };
            _history.Push(
                redo: () =>
                {
                    if (box[0] >= 0)
                    {
                        _world.Destroy(box[0]);
                        if (syncHandle != null) _onUnitSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, unitId, snap.Faction, wpos);
                    }
                },
                undo: () =>
                {
                    box[0] = _world.RestoreUnit(snap);
                    // RestoreUnit returns -1 only when EntityWorld is at capacity (graceful, no partial state). Surface
                    // it — the Core method is Godot-free and cannot log, and the old EntityPlacer.RestoreUnit did.
                    if (box[0] < 0) { GD.PrintErr("[EntityPlacer] EntityWorld full — cannot restore deleted unit."); return; }
                    // Re-add to ScenarioData only AFTER the live restore is confirmed (>= 0) — a full world must not
                    // leave a phantom ScenarioData.Units entry.
                    if (syncHandle != null) _onUnitSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, unitId, snap.Faction, wpos);
                });
        }

        private void DeleteResourceNode(int id)
        {
            if (_nodes == null) return;
            var capturedNodes   = _nodes;
            var capturedSupply  = _nodes.SupplyRemaining[id];
            var capturedTotal   = _nodes.SupplyTotal[id];
            var capturedRate    = _nodes.GatherRate[id];

            // Story 6.1: capture position BEFORE clearing so the ScenarioData match can run.
            Vector3 wpos = new Vector3(_nodes.Position[id].X.ToFloat(), 0f, _nodes.Position[id].Z.ToFloat());

            _nodes.Active[id] = false;
            GD.Print($"[EntityPlacer] Deleted resource node id={id}");

            // Story 6.1: remove the matching ScenarioData.ResourceNodes entry (identity-preserving) so an authored
            // Income/Crystal/owner-slotted node is restored intact on undo (never degraded to a plain Gather/Ore node).
            // DW-151: RemoveMatch captures the REAL authored entry (4.7 fields intact) and ReAdd restores it by
            // identity, so the delete/undo path preserves the node's economy fields without passing them here.
            object? syncHandle = _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, wpos, 0f, 0f, 0,
                                                             ResourceCollectionModel.Gather, ResourceKind.Ore, "", default, Faction.Neutral, 0);

            _history.Push(
                redo: () =>
                {
                    capturedNodes.Active[id] = false;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, wpos, 0f, 0f, 0,
                                                ResourceCollectionModel.Gather, ResourceKind.Ore, "", default, Faction.Neutral, 0);
                },
                undo: () =>
                {
                    capturedNodes.Active[id]          = true;
                    capturedNodes.SupplyRemaining[id] = capturedSupply;
                    capturedNodes.SupplyTotal[id]     = capturedTotal;
                    capturedNodes.GatherRate[id]      = capturedRate;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, wpos, 0f, 0f, 0,
                                                ResourceCollectionModel.Gather, ResourceKind.Ore, "", default, Faction.Neutral, 0);
                });
        }

        // ── Nearest-entity scans (used by delete) ─────────────────────────────

        private int FindNearestBuilding(Vector3 worldPos, float radius)
        {
            if (_buildings == null) return -1;
            float best = radius * radius;
            int   hit  = -1;
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                float bx = _buildings.Position[i].X.ToFloat();
                float bz = _buildings.Position[i].Z.ToFloat();
                float dx = worldPos.X - bx, dz = worldPos.Z - bz;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; hit = i; }
            }
            return hit;
        }

        private int FindNearestUnit(Vector3 worldPos, float radius)
        {
            float best = radius * radius;
            int   hit  = -1;
            int   hwm  = _world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if ((_world.Flags[i] & EntityFlags.Alive) == 0) continue;
                float ux = _world.Position[i].X.ToFloat();
                float uz = _world.Position[i].Z.ToFloat();
                float dx = worldPos.X - ux, dz = worldPos.Z - uz;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; hit = i; }
            }
            return hit;
        }

        private int FindNearestNode(Vector3 worldPos, float radius)
        {
            if (_nodes == null) return -1;
            float best = radius * radius;
            int   hit  = -1;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes.Active[i]) continue;
                float nx = _nodes.Position[i].X.ToFloat();
                float nz = _nodes.Position[i].Z.ToFloat();
                float dx = worldPos.X - nx, dz = worldPos.Z - nz;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; hit = i; }
            }
            return hit;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Story 6.6 — props + multi-select / copy-paste / group-move / rotation
        // ═══════════════════════════════════════════════════════════════════════

        private ScenarioProp[] CurrentProps() => _scenarioGetter?.Invoke()?.Props ?? System.Array.Empty<ScenarioProp>();

        // ── Prop place / delete (single, one history pair) ────────────────────

        private void PlaceProp(Vector3 hit)
        {
            string  propId = PROP_LIBRARY[_propIndex % PROP_LIBRARY.Length];
            Vector3 wpos   = new Vector3(SnapValue(hit.X), 0f, SnapValue(hit.Z));
            float   rot    = _placementRot, scale = _propScale;
            bool    blocks = _propBlocks;

            // DW-159: reject an off-map placement before the sync so it never persists to fail whole-scenario
            // validation later (mirrors WaterTool.CommitDrag's bounds guard).
            if (!WithinMapBounds(wpos))
            {
                GD.Print($"[EntityPlacer] Prop at ({wpos.X:F1},{wpos.Z:F1}) is outside map bounds (±{_scenarioGetter?.Invoke()?.MapBounds:F0}) — not placed.");
                return;
            }

            object? handle = _onPropSync?.Invoke(ScenarioSyncOp.Add, null, propId, wpos, rot, scale, blocks);
            if (handle == null) { GD.Print("[EntityPlacer] Prop not persisted (no scenario loaded)."); return; }

            _history.Push(
                redo: () => _onPropSync?.Invoke(ScenarioSyncOp.ReAdd, handle, propId, wpos, rot, scale, blocks),
                undo: () => _onPropSync?.Invoke(ScenarioSyncOp.RemoveHandle, handle, propId, wpos, rot, scale, blocks));
            GD.Print($"[EntityPlacer] Placed prop '{propId}' at ({wpos.X:F1},{wpos.Z:F1}) blocks={blocks} rot={Mathf.RadToDeg(rot):F0}°.");
        }

        private object? FindNearestProp(Vector3 worldPos, float radius)
        {
            float best = radius * radius; object? hit = null;
            foreach (var p in CurrentProps())
            {
                float dx = worldPos.X - p.X, dz = worldPos.Z - p.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) { best = d2; hit = p; }
            }
            return hit;
        }

        private void DeleteProp(object prop)
        {
            var cmd = BuildDeleteProp(prop);
            if (cmd == null) return;
            _history.Push(cmd.Value.redo, cmd.Value.undo);
        }

        // ── R-key rotation of selected props (one history pair; visual-only) ───

        /// <summary>True when the current selection holds at least one prop — i.e. R has something to rotate. Mirrors
        /// <see cref="RotateSelectedProps"/>'s own filter so the two can never disagree about whether R is meaningful.</summary>
        private bool HasPropSelection()
        {
            PruneSelection();
            foreach (var s in _selection)
                if (s.Category == Selected.Kind.Prop && s.PropHandle is ScenarioProp) return true;
            return false;
        }

        private void RotateSelectedProps(float step)
        {
            PruneSelection();
            var props = new List<ScenarioProp>();
            foreach (var s in _selection) if (s.Category == Selected.Kind.Prop && s.PropHandle is ScenarioProp p) props.Add(p);
            if (props.Count == 0) return;
            void Apply(float d) { foreach (var p in props) p.Rot = Mathf.Wrap(p.Rot + d, 0f, Mathf.Tau); }
            Apply(step);
            _history.Push(redo: () => Apply(step), undo: () => Apply(-step));
        }

        // ── Marquee multi-select (Select mode) ────────────────────────────────

        /// <summary>Handle Select-mode left-mouse: a drag starting over the current selection MOVES it; otherwise it
        /// marquees (Shift adds to the existing selection). Returns true when the event was consumed.</summary>
        private bool HandleSelectMouse(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    if (IsOverUi(mb.Position)) return false; // DW-901: never claim a click the GUI is about to deliver to a control
                    PruneSelection(); // review fix (B3/E1): don't hit-test the cursor against dead/removed selections
                    Vector3? gp = GroundPointOf(mb.Position);
                    if (gp != null && _selection.Count > 0 && OverSelection(gp.Value))
                    {
                        _groupMoving = true;
                        _groupMoveStartWorld = gp.Value;
                        _groupMovePreviewDelta = Vector3.Zero;
                        BuildDragPreview();   // DW-894: real meshes that follow the cursor for the whole drag
                    }
                    else
                    {
                        _marqueeActive = true;
                        _marqueeStart = mb.Position;
                        _marqueeCurrent = mb.Position;
                    }
                    GetViewport().SetInputAsHandled();
                    return true;
                }
                else // released
                {
                    if (_groupMoving)
                    {
                        _groupMoving = false;
                        _groupMovePreviewDelta = Vector3.Zero;
                        HideDragPreview();
                        Vector3? gp = GroundPointOf(mb.Position);
                        // Still EXACTLY ONE MoveSelection call — it is a delete+recreate that pushes an undo/redo
                        // pair, so it must never run per motion event. The RAW delta is passed as before; MoveSelection
                        // re-derives the snap itself.
                        if (gp != null) MoveSelection(gp.Value - _groupMoveStartWorld);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                    if (_marqueeActive)
                    {
                        _marqueeActive = false;
                        FinishMarquee(mb.ShiftPressed);
                        GetViewport().SetInputAsHandled();
                        return true;
                    }
                }
            }
            else if (@event is InputEventMouseMotion motion && (_marqueeActive || _groupMoving))
            {
                if (_marqueeActive) _marqueeCurrent = motion.Position;
                if (_groupMoving)
                {
                    // DW-894: the drag now FOLLOWS the cursor. Previously this branch did nothing for _groupMoving, so
                    // nothing moved until mouse-up and the selection appeared to teleport. The preview uses the SAME
                    // SnapDelta the commit applies, so what the author sees during the drag is byte-equal to where the
                    // objects actually land.
                    Vector3? gp = GroundPointOf(motion.Position);
                    if (gp != null)
                        _groupMovePreviewDelta = new Vector3(
                            SnapDelta(gp.Value.X - _groupMoveStartWorld.X), 0f,
                            SnapDelta(gp.Value.Z - _groupMoveStartWorld.Z));
                }
                GetViewport().SetInputAsHandled();
                return true;
            }
            return false;
        }

        // ── DW-894: live drag preview ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Populate the drag preview: one translucent copy of each selected object's REAL mesh, parented to
        /// <c>_markerRoot</c>.
        ///
        /// <para>Parenting is the whole trick. <c>_markerRoot</c> sits at the origin with an identity transform and its
        /// children are written in absolute world coordinates, so offsetting the ROOT by the drag delta translates
        /// every preview (and the existing selection markers) in one assignment — no per-child bookkeeping, and
        /// resetting the root to zero on release restores truth with no cleanup pass.</para>
        /// </summary>
        private void BuildDragPreview()
        {
            HideDragPreview();
            if (_markerRoot == null) return;

            foreach (Selected s in _selection)
            {
                Mesh? mesh = PreviewMeshFor(s);
                if (mesh == null) continue;

                var node = new MeshInstance3D
                {
                    Mesh       = mesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position   = SelectedWorldPos(s),
                    MaterialOverride = new StandardMaterial3D
                    {
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        CullMode     = BaseMaterial3D.CullModeEnum.Disabled,
                        AlbedoColor  = new Color(0.95f, 0.85f, 0.30f, 0.45f),   // the selection amber, translucent
                    },
                };
                _markerRoot.AddChild(node);
                _dragPreview.Add(node);
            }
        }

        /// <summary>
        /// DW-894/893 — the real mesh for a selected object, so the drag preview shows the BUILDING moving rather than
        /// a floating pad. Resolved through the same <see cref="MeshLoader"/> seam the placed entity renders from, with
        /// the same box fallback, so it can never be worse than a stand-in.
        /// </summary>
        private Mesh? PreviewMeshFor(Selected s)
        {
            switch (s.Category)
            {
                case Selected.Kind.Unit:
                {
                    string? id = _world.SourceDefinition[s.LiveId]?.Id;
                    UnitDefinition? def = id == null ? null : (ActiveFactionDef()?.GetUnit(id) ?? _faction2?.GetUnit(id));
                    return MeshLoader.LoadFromGlb(def?.MeshPath ?? "", new Vector3(0.6f, 1.2f, 0.6f), new Color(0.95f, 0.85f, 0.3f), _assets);
                }
                case Selected.Kind.Building:
                {
                    BuildingDefinition? def = _buildings == null ? null : BuildingDefFor(_buildings.Type[s.LiveId]);
                    return MeshLoader.LoadFromGlb(def?.MeshPath ?? "", new Vector3(4f, 2f, 4f), new Color(0.95f, 0.85f, 0.3f), _assets);
                }
                case Selected.Kind.Node:
                    return new SphereMesh { Radius = 0.8f, Height = 1.6f };
                case Selected.Kind.Prop:
                    return s.PropHandle is ScenarioProp p ? PropRenderer.MeshForProp(p.PropId) : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The definition behind a PLACED building. <c>BuildingStore</c> keeps the <see cref="BuildingType"/> enum, not
        /// the authored id, and only the id→enum direction has a helper — so walk the loaded rosters and take the
        /// definition whose id maps back to this type. A handful of definitions, and it runs once per drag start
        /// (never per frame), so the scan is cheaper than caching it would be to keep correct.
        /// </summary>
        private BuildingDefinition? BuildingDefFor(BuildingType type)
        {
            foreach (FactionDefinition? f in new[] { _faction, _faction2 })
            {
                if (f?.Buildings == null) continue;
                foreach (BuildingDefinition b in f.Buildings)
                    if (TechTreeChecker.BuildingTypeFromId(b.Id) is BuildingType bt && bt == type) return b;
            }
            return null;
        }

        /// <summary>Drop the preview copies and put the marker root back at the origin.</summary>
        private void HideDragPreview()
        {
            foreach (MeshInstance3D n in _dragPreview) n.QueueFree();
            _dragPreview.Clear();
            if (_markerRoot != null) _markerRoot.Position = Vector3.Zero;
        }

        private void FinishMarquee(bool shiftAdd)
        {
            if (!shiftAdd) _selection.Clear();
            var cam = _camCtrl?.GetCamera();
            if (cam == null) return;

            float minX = Mathf.Min(_marqueeStart.X, _marqueeCurrent.X), maxX = Mathf.Max(_marqueeStart.X, _marqueeCurrent.X);
            float minY = Mathf.Min(_marqueeStart.Y, _marqueeCurrent.Y), maxY = Mathf.Max(_marqueeStart.Y, _marqueeCurrent.Y);
            // A near-zero box (a click, not a drag) selects nothing new — leave the set unchanged.
            if (maxX - minX < 3f && maxY - minY < 3f) { RefreshSubRow(); return; }

            bool InBox(Vector3 world)
            {
                if (cam.IsPositionBehind(world)) return false;
                Vector2 s = cam.UnprojectPosition(world);
                return s.X >= minX && s.X <= maxX && s.Y >= minY && s.Y <= maxY;
            }

            // Units.
            int hwm = _world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if ((_world.Flags[i] & EntityFlags.Alive) == 0) continue;
                // DW-153: unproject at the entity's sampled terrain height so the hit-test tracks Story-6.3 raised
                // ground (flat maps sample 0 ⇒ unchanged).
                if (InBox(new Vector3(_world.Position[i].X.ToFloat(), _world.SampleElevation(_world.Position[i].X, _world.Position[i].Z).ToFloat() + 0.5f, _world.Position[i].Z.ToFloat())))
                    AddToSelection(new Selected(Selected.Kind.Unit, i, null));
            }
            // Buildings.
            if (_buildings != null)
                for (int i = 0; i < _buildings.Count; i++)
                {
                    if (!_buildings.Alive[i]) continue;
                    if (InBox(new Vector3(_buildings.Position[i].X.ToFloat(), _world.SampleElevation(_buildings.Position[i].X, _buildings.Position[i].Z).ToFloat() + 1f, _buildings.Position[i].Z.ToFloat())))
                        AddToSelection(new Selected(Selected.Kind.Building, i, null));
                }
            // Nodes.
            if (_nodes != null)
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (!_nodes.Active[i]) continue;
                    if (InBox(new Vector3(_nodes.Position[i].X.ToFloat(), _world.SampleElevation(_nodes.Position[i].X, _nodes.Position[i].Z).ToFloat() + 0.5f, _nodes.Position[i].Z.ToFloat())))
                        AddToSelection(new Selected(Selected.Kind.Node, i, null));
                }
            // Props.
            foreach (var p in CurrentProps())
                if (InBox(new Vector3(p.X, _world.SampleElevation(Fixed.FromFloat(p.X), Fixed.FromFloat(p.Z)).ToFloat() + 0.5f, p.Z)))
                    AddToSelection(new Selected(Selected.Kind.Prop, -1, p));

            GD.Print($"[EntityPlacer] Selection: {_selection.Count} object(s).");
            RefreshSubRow();
        }

        private void AddToSelection(Selected s)
        {
            foreach (var e in _selection)
                if (e.Category == s.Category && e.LiveId == s.LiveId && ReferenceEquals(e.PropHandle, s.PropHandle)) return;
            _selection.Add(s);
        }

        private Vector3 SelectedWorldPos(Selected s) => s.Category switch
        {
            Selected.Kind.Unit     => new Vector3(_world.Position[s.LiveId].X.ToFloat(), 0f, _world.Position[s.LiveId].Z.ToFloat()),
            Selected.Kind.Building => _buildings != null ? new Vector3(_buildings.Position[s.LiveId].X.ToFloat(), 0f, _buildings.Position[s.LiveId].Z.ToFloat()) : Vector3.Zero,
            Selected.Kind.Node     => _nodes != null ? new Vector3(_nodes.Position[s.LiveId].X.ToFloat(), 0f, _nodes.Position[s.LiveId].Z.ToFloat()) : Vector3.Zero,
            Selected.Kind.Prop     => s.PropHandle is ScenarioProp p ? new Vector3(p.X, 0f, p.Z) : Vector3.Zero,
            _                      => Vector3.Zero,
        };

        private bool OverSelection(Vector3 world)
        {
            foreach (var s in _selection)
            {
                Vector3 p = SelectedWorldPos(s);
                float dx = world.X - p.X, dz = world.Z - p.Z;
                if (dx * dx + dz * dz <= 9f) return true; // within 3 units of any selected object
            }
            return false;
        }

        // ── Group operations (each ONE history pair) ──────────────────────────

        /// <summary>Review fix (B3/E1): drop selected entries whose backing slot/handle is no longer live before any
        /// group op reads it. Between a marquee and a group action, an intervening hover-Delete, single-op undo, or F5
        /// re-apply can free a selected live slot (or remove a selected prop); operating on it would mutate a dead/reused
        /// slot or read garbage. Freed-then-reused slots (same index, different entity) are a narrower residual not
        /// covered here.</summary>
        private void PruneSelection()
        {
            _selection.RemoveAll(s => s.Category switch
            {
                Selected.Kind.Unit     => s.LiveId < 0 || s.LiveId >= _world.Flags.Length || (_world.Flags[s.LiveId] & EntityFlags.Alive) == 0,
                Selected.Kind.Building => _buildings == null || s.LiveId < 0 || s.LiveId >= _buildings.Count || !_buildings.Alive[s.LiveId],
                Selected.Kind.Node     => _nodes == null || s.LiveId < 0 || s.LiveId >= _nodes.Count || !_nodes.Active[s.LiveId],
                Selected.Kind.Prop     => s.PropHandle is not ScenarioProp p || System.Array.IndexOf(CurrentProps(), p) < 0,
                _                      => true,
            });
        }

        private void DeleteSelection()
        {
            PruneSelection();
            var cmds = new List<(System.Action redo, System.Action undo)>();
            foreach (var s in _selection)
            {
                var c = BuildDelete(s);
                if (c != null) cmds.Add(c.Value);
            }
            _selection.Clear();
            RefreshSubRow();
            if (cmds.Count == 0) return;

            _history.Push(
                redo: () => { foreach (var c in cmds) c.redo(); },
                undo: () => { for (int i = cmds.Count - 1; i >= 0; i--) cmds[i].undo(); });
            GD.Print($"[EntityPlacer] Deleted {cmds.Count} selected object(s) (one undo step).");
        }

        /// <summary>Group move = delete the originals and re-create them at (pos + delta) — reusing the delete + create
        /// builders so the live stores AND ScenarioData both stay consistent, as ONE undo step.</summary>
        private void MoveSelection(Vector3 delta)
        {
            if (delta.LengthSquared() < 0.0001f) return;
            PruneSelection();
            var descriptors = new List<PropDescriptor>();
            foreach (var s in _selection) descriptors.Add(Describe(s));

            // DW-159: reject the WHOLE move atomically if ANY moved target lands off-map. Deletes precede creates, so a
            // late per-item bounds reject inside BuildCreate would strand an already-deleted original — check every
            // target BEFORE the first BuildDelete runs. Describe above is read-only, so nothing was mutated yet.
            foreach (var d in descriptors)
            {
                var moved = d.Pos + new Vector3(SnapDelta(delta.X), 0f, SnapDelta(delta.Z));
                if (!WithinMapBounds(moved))
                {
                    GD.Print($"[EntityPlacer] Move aborted — a target at ({moved.X:F1},{moved.Z:F1}) is outside map bounds (±{_scenarioGetter?.Invoke()?.MapBounds:F0}).");
                    return;
                }
            }

            var deletes = new List<(System.Action redo, System.Action undo)>();
            foreach (var s in _selection) { var c = BuildDelete(s); if (c != null) deletes.Add(c.Value); }

            var creates = new List<(System.Action redo, System.Action undo, Selected selected)>();
            _selection.Clear();
            foreach (var d in descriptors)
            {
                var moved = d.Pos + new Vector3(SnapDelta(delta.X), 0f, SnapDelta(delta.Z));
                var c = BuildCreate(d, moved);
                if (c != null) { creates.Add(c.Value); _selection.Add(c.Value.selected); }
            }
            RefreshSubRow();

            // Review fix (E5): if the selection pruned to empty (every selected slot freed mid-drag), nothing was
            // deleted or recreated — skip the Push so we don't strand a phantom no-op undo step on the shared stack
            // (mirrors the DeleteSelection / PasteClipboard empty guards).
            if (deletes.Count == 0 && creates.Count == 0) return;

            _history.Push(
                redo: () => { foreach (var c in deletes) c.redo(); foreach (var c in creates) c.redo(); },
                undo: () => { for (int i = creates.Count - 1; i >= 0; i--) creates[i].undo(); for (int i = deletes.Count - 1; i >= 0; i--) deletes[i].undo(); });
            GD.Print($"[EntityPlacer] Moved {descriptors.Count} object(s) by ({delta.X:F1},{delta.Z:F1}) (one undo step).");
        }

        private float SnapDelta(float v) => _gridSnapEnabled ? Mathf.Round(v) : v;

        private void CopySelection()
        {
            PruneSelection();
            _clipboard.Clear();
            foreach (var s in _selection) _clipboard.Add(Describe(s));
            _clipboardAnchor = _selection.Count > 0 ? SelectedWorldPos(_selection[0]) : Vector3.Zero;
            GD.Print($"[EntityPlacer] Copied {_clipboard.Count} object(s).");
        }

        private void PasteClipboard(Vector3 cursorWorld)
        {
            if (_clipboard.Count == 0) return;
            var creates = new List<(System.Action redo, System.Action undo, Selected selected)>();
            _selection.Clear();
            foreach (var d in _clipboard)
            {
                // Preserve each descriptor's offset relative to the clipboard anchor, snapping the paste to the grid.
                Vector3 rel = d.Pos - _clipboardAnchor;
                Vector3 at  = new Vector3(SnapValue(cursorWorld.X + rel.X), 0f, SnapValue(cursorWorld.Z + rel.Z));
                var c = BuildCreate(d, at);
                if (c != null) { creates.Add(c.Value); _selection.Add(c.Value.selected); }
            }
            RefreshSubRow();
            if (creates.Count == 0) return;
            _history.Push(
                redo: () => { foreach (var c in creates) c.redo(); },
                undo: () => { for (int i = creates.Count - 1; i >= 0; i--) creates[i].undo(); });
            GD.Print($"[EntityPlacer] Pasted {creates.Count} object(s) at cursor (one undo step).");
        }

        private void DuplicateSelection()
        {
            CopySelection();
            // Paste with a small default offset so the copies don't overlap the originals.
            Vector3 anchor = _clipboardAnchor + new Vector3(4f, 0f, 4f);
            PasteClipboard(anchor);
        }

        // ── Descriptor capture + create/delete builders (one op, no push) ─────

        /// <summary>World-unit tolerance matching a live building row to its scenario entry (mirrors
        /// <c>MainScene.SCENARIO_SYNC_EPS</c>).</summary>
        private const float BUILDING_MATCH_EPS = 0.1f;

        /// <summary>DW-151 — look up the authored <c>pre_built</c> flag of the <see cref="ScenarioBuilding"/> backing a
        /// live building slot (matched by slot + position, the same key <c>MainScene.SyncBuilding</c> uses). Returns
        /// false when no scenario entry matches (a cosmetic-only / undeclared-slot placement was never persisted).</summary>
        private bool LookupBuildingPreBuilt(Faction faction, Vector3 pos)
        {
            var scen = _scenarioGetter?.Invoke();
            if (scen?.Buildings == null) return false;
            int slot = (int)faction - 1;
            foreach (var b in scen.Buildings)
                if (b.Slot == slot
                    && System.Math.Abs(b.X - pos.X) <= BUILDING_MATCH_EPS
                    && System.Math.Abs(b.Z - pos.Z) <= BUILDING_MATCH_EPS)
                    return b.PreBuilt;
            return false;
        }

        /// <summary>DW-159 — true when world point <paramref name="p"/> is inside the loaded scenario's ±MapBounds
        /// square (mirroring <c>WaterTool.CommitDrag</c>'s guard). No scenario ⇒ bounds unknown ⇒ allow.</summary>
        private bool WithinMapBounds(Vector3 p)
        {
            // DW-159: delegate to the Godot-free MapBoundsMath so the predicate is unit-testable. A null scenario
            // yields a null bounds ⇒ allow (bounds unknown), byte-identical to the previous inline guard.
            var scen = _scenarioGetter?.Invoke();
            return MapBoundsMath.Within(p.X, p.Z, scen?.MapBounds);
        }

        private PropDescriptor Describe(Selected s)
        {
            switch (s.Category)
            {
                case Selected.Kind.Prop:
                {
                    var p = (ScenarioProp)s.PropHandle!;
                    return new PropDescriptor(Selected.Kind.Prop, new Vector3(p.X, 0f, p.Z), p.PropId, Faction.Neutral, p.Rot, p.Scale ?? 1f, p.BlocksPathing, 0f, 0f, 0);
                }
                case Selected.Kind.Unit:
                {
                    // DW-151: capture the full identity-preserving snapshot so BuildCreate re-homes it via RestoreUnit —
                    // a moved/pasted worker keeps its worker residue (SupplyCost=0/GatherState/CarryCapacity/MeshType)
                    // instead of respawning as a combat unit via DoSpawnCombatUnit.
                    UnitSnapshot snap = _world.SnapshotUnit(s.LiveId);
                    Vector3 pos = new Vector3(_world.Position[s.LiveId].X.ToFloat(), 0f, _world.Position[s.LiveId].Z.ToFloat());
                    return new PropDescriptor(Selected.Kind.Unit, pos, snap.Def?.Id ?? "", snap.Faction, 0f, 1f, false, 0f, 0f, 0,
                        unitSnap: snap);
                }
                case Selected.Kind.Building:
                {
                    Vector3 pos = new Vector3(_buildings!.Position[s.LiveId].X.ToFloat(), 0f, _buildings.Position[s.LiveId].Z.ToFloat());
                    // Story 6.8: descriptor carries the authored DefinitionId (not the enum name), so a copy/paste of a
                    // Custom building re-creates it as the right authored building.
                    // DW-151: carry the source ScenarioBuilding's authored pre_built flag so a moved building persists
                    // pre_built:true instead of resetting to false.
                    return new PropDescriptor(Selected.Kind.Building, pos, _buildings.DefinitionId[s.LiveId], _buildings.FactionOf[s.LiveId], 0f, 1f, false, 0f, 0f, 0,
                        preBuilt: LookupBuildingPreBuilt(_buildings.FactionOf[s.LiveId], pos));
                }
                default: // Node
                {
                    Vector3 pos = new Vector3(_nodes!.Position[s.LiveId].X.ToFloat(), 0f, _nodes.Position[s.LiveId].Z.ToFloat());
                    // DW-151: capture the full Story-4.7 field set from the live store so a moved/pasted node keeps its
                    // collection model / resource type / requires-structure gate / owner / income period.
                    return new PropDescriptor(Selected.Kind.Node, pos, "", Faction.Neutral, 0f, 1f, false,
                        _nodes.SupplyTotal[s.LiveId].ToFloat(), _nodes.GatherRate[s.LiveId].ToFloat(), _nodes.MaxGatherers[s.LiveId],
                        collectionModel:         _nodes.CollectionModel[s.LiveId],
                        resourceKind:            _nodes.ResourceType[s.LiveId],
                        requiresStructureId:     _nodes.RequiresStructureId[s.LiveId],
                        requiresStructureRadius: _nodes.RequiresStructureRadius[s.LiveId],
                        ownerFaction:            _nodes.OwnerFaction[s.LiveId],
                        incomePeriodTicks:       _nodes.IncomePeriodTicks[s.LiveId]);
                }
            }
        }

        private (System.Action redo, System.Action undo)? BuildDelete(Selected s) => s.Category switch
        {
            Selected.Kind.Prop     => BuildDeleteProp(s.PropHandle!),
            Selected.Kind.Unit     => BuildDeleteUnit(s.LiveId),
            Selected.Kind.Building => BuildDeleteBuilding(s.LiveId),
            Selected.Kind.Node     => BuildDeleteNode(s.LiveId),
            _                      => null,
        };

        private (System.Action redo, System.Action undo)? BuildDeleteProp(object prop)
        {
            if (prop is not ScenarioProp p) return null;
            string id = p.PropId; Vector3 wp = new Vector3(p.X, 0f, p.Z);
            float rot = p.Rot, scale = p.Scale ?? 1f; bool blocks = p.BlocksPathing;
            // Review fix (E2): remove the EXACT selected prop by identity, not the first entry matching its (x,z).
            // Two props stacked on one cell (paste/duplicate at zero offset) would otherwise unlink the wrong one, and
            // undo would restore the wrong one. `p` IS the ScenarioData.Props entry, so RemoveHandle targets it directly.
            _onPropSync?.Invoke(ScenarioSyncOp.RemoveHandle, p, id, wp, rot, scale, blocks);
            return (
                redo: () => _onPropSync?.Invoke(ScenarioSyncOp.RemoveHandle, p, id, wp, rot, scale, blocks),
                undo: () => _onPropSync?.Invoke(ScenarioSyncOp.ReAdd, p, id, wp, rot, scale, blocks));
        }

        private (System.Action redo, System.Action undo)? BuildDeleteUnit(int id)
        {
            UnitSnapshot snap = _world.SnapshotUnit(id);
            // DW-52: free the linked HeroStore row before entity teardown (non-hero HERO_NONE handle no-ops for free).
            _heroes?.DestroyByRef(_world.HeroIndex[id]);
            _world.Destroy(id);
            string  unitId = snap.Def?.Id ?? "";
            Vector3 wp     = new Vector3(snap.Position.X.ToFloat(), 0f, snap.Position.Z.ToFloat());
            object? h      = snap.Def != null ? _onUnitSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, unitId, snap.Faction, wp) : null;
            int[] box = { -1 };
            return (
                redo: () => { if (box[0] >= 0) { _world.Destroy(box[0]); if (h != null) _onUnitSync?.Invoke(ScenarioSyncOp.RemoveHandle, h, unitId, snap.Faction, wp); } },
                undo: () => { box[0] = _world.RestoreUnit(snap); if (box[0] < 0) return; if (h != null) _onUnitSync?.Invoke(ScenarioSyncOp.ReAdd, h, unitId, snap.Faction, wp); });
        }

        private (System.Action redo, System.Action undo)? BuildDeleteBuilding(int id)
        {
            if (_buildings == null) return null;
            Faction faction = _buildings.FactionOf[id];
            Fixed   dur = _buildings.ConstructionDuration[id], tim = _buildings.ConstructionTimer[id];
            BuildingType type = _buildings.Type[id];
            string  defId = _buildings.DefinitionId[id]; // Story 6.8: authored id for the sync + slot restore
            // Review fix (F2): capture the EXACT position and re-write Position/Type/Faction on undo. A group-move
            // deletes-then-recreates through the LIFO slot, so BuildingStore.Create reuses this very slot and
            // overwrites Position with the moved value; resurrecting via `Alive[id]=true` alone would strand the
            // building at the moved position (live store ≠ ScenarioData until the next reload). Writing full
            // identifying state makes the undo self-sufficient rather than relying on slot residue.
            FixedVec3 origPos = _buildings.Position[id];
            Vector3 wp = new Vector3(origPos.X.ToFloat(), 0f, origPos.Z.ToFloat());
            // DW-173: capture the full def-derived stat set at DELETE time and restore it verbatim on undo. A
            // group-move deletes-then-recreates through the same LIFO slot, so BuildingStore.Create may recycle THIS
            // slot for a different building and overwrite these fields; restoring identity/timers alone (as F2 did)
            // would leave the undone building carrying the recycled occupant's Health/MaxHealth/SupplyBonus/shop/
            // revive. Capturing actual post-Create stats here is the self-sufficient equivalent of the (non-existent)
            // DW-172 CreateFromDefinition helper — it preserves exact runtime state without relying on slot residue.
            Fixed     capHealth    = _buildings.Health[id];
            Fixed     capMaxHealth = _buildings.MaxHealth[id];
            int       capSupplyBon = _buildings.SupplyBonus[id];
            bool      capRevives   = _buildings.RevivesHeroes[id];
            bool      capSells     = _buildings.SellsItems[id];
            string[]  capShopStock = _buildings.ShopStock[id];
            Fixed     capShopRad   = _buildings.ShopRadius[id];
            var b = _buildings;
            b.Destroy(id);
            object? h = _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, defId, faction, wp, false);
            return (
                redo: () => { b.Destroy(id); _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveHandle, h, defId, faction, wp, false); },
                undo: () => { b.Alive[id] = true; b.Position[id] = origPos; b.FactionOf[id] = faction; b.Type[id] = type; b.DefinitionId[id] = defId; b.ConstructionTimer[id] = tim; b.ConstructionDuration[id] = dur; b.Health[id] = capHealth; b.MaxHealth[id] = capMaxHealth; b.SupplyBonus[id] = capSupplyBon; b.RevivesHeroes[id] = capRevives; b.SellsItems[id] = capSells; b.ShopStock[id] = capShopStock; b.ShopRadius[id] = capShopRad; _onBuildingSync?.Invoke(ScenarioSyncOp.ReAdd, h, defId, faction, wp, false); });
        }

        private (System.Action redo, System.Action undo)? BuildDeleteNode(int id)
        {
            if (_nodes == null) return null;
            var n = _nodes;
            var sup = n.SupplyRemaining[id]; var tot = n.SupplyTotal[id]; var rate = n.GatherRate[id];
            Vector3 wp = new Vector3(n.Position[id].X.ToFloat(), 0f, n.Position[id].Z.ToFloat());
            n.Active[id] = false;
            // DW-151: RemoveMatch/ReAdd preserve the node's authored 4.7 fields by identity, so the delete legs pass
            // store defaults for the extended tail.
            object? h = _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, wp, 0f, 0f, 0, ResourceCollectionModel.Gather, ResourceKind.Ore, "", default, Faction.Neutral, 0);
            return (
                redo: () => { n.Active[id] = false; _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveHandle, h, wp, 0f, 0f, 0, ResourceCollectionModel.Gather, ResourceKind.Ore, "", default, Faction.Neutral, 0); },
                undo: () => { n.Active[id] = true; n.SupplyRemaining[id] = sup; n.SupplyTotal[id] = tot; n.GatherRate[id] = rate; _onResourceNodeSync?.Invoke(ScenarioSyncOp.ReAdd, h, wp, 0f, 0f, 0, ResourceCollectionModel.Gather, ResourceKind.Ore, "", default, Faction.Neutral, 0); });
        }

        /// <summary>Create a placement from a descriptor at <paramref name="at"/> (performs it now, returns the
        /// composable redo/undo + the resulting <see cref="Selected"/> so paste/move can re-select the copies).</summary>
        private (System.Action redo, System.Action undo, Selected selected)? BuildCreate(PropDescriptor d, Vector3 at)
        {
            // DW-159: reject an off-map create (paste/duplicate) up front — returning null makes the caller skip this
            // item so nothing persists outside ±MapBounds to fail whole-scenario validation later. (MoveSelection
            // pre-checks ALL targets before any BuildDelete, so a move never reaches here off-map.)
            if (!WithinMapBounds(at))
            {
                GD.Print($"[EntityPlacer] Create at ({at.X:F1},{at.Z:F1}) is outside map bounds — skipped.");
                return null;
            }

            var pos = new FixedVec3(Fixed.FromFloat(at.X), Fixed.Zero, Fixed.FromFloat(at.Z));
            switch (d.Category)
            {
                case Selected.Kind.Prop:
                {
                    object? h = _onPropSync?.Invoke(ScenarioSyncOp.Add, null, d.Id, at, d.Rot, d.Scale, d.Blocks);
                    if (h == null) return null;
                    return (
                        redo: () => _onPropSync?.Invoke(ScenarioSyncOp.ReAdd, h, d.Id, at, d.Rot, d.Scale, d.Blocks),
                        undo: () => _onPropSync?.Invoke(ScenarioSyncOp.RemoveHandle, h, d.Id, at, d.Rot, d.Scale, d.Blocks),
                        selected: new Selected(Selected.Kind.Prop, -1, h));
                }
                case Selected.Kind.Unit:
                {
                    // DW-151: when a residue snapshot was captured (every marquee-selected unit), re-home it via the
                    // identity-preserving RestoreUnit — this replays the worker overrides (SupplyCost=0/GatherState/
                    // CarryCapacity/MeshType) exactly like the single-entity delete→undo path, so a moved/pasted worker
                    // stays a worker rather than respawning as a combat unit through DoSpawnCombatUnit.
                    if (d.UnitSnap is UnitSnapshot snap)
                    {
                        snap.Position = pos; // re-home the snapshot to the moved/pasted world position
                        int rid = _world.RestoreUnit(snap);
                        if (rid < 0) return null;
                        object? rh = string.IsNullOrEmpty(d.Id) ? null : _onUnitSync?.Invoke(ScenarioSyncOp.Add, null, d.Id, d.Faction, at);
                        var replay = snap; // captured copy (already re-homed) for the redo leg
                        int[] rbox = { rid };
                        return (
                            redo: () => { int r = _world.RestoreUnit(replay); if (r >= 0) { rbox[0] = r; if (rh != null) _onUnitSync?.Invoke(ScenarioSyncOp.ReAdd, rh, d.Id, d.Faction, at); } },
                            undo: () => { _world.Destroy(rbox[0]); if (rh != null) _onUnitSync?.Invoke(ScenarioSyncOp.RemoveHandle, rh, d.Id, d.Faction, at); },
                            selected: new Selected(Selected.Kind.Unit, rid, null));
                    }
                    // Fallback (def-less capture, no snapshot): the pre-DW-151 combat-spawn path.
                    if (string.IsNullOrEmpty(d.Id)) return null;
                    UnitDefinition? def = (d.Faction == Faction.Player2 ? _faction2 : _faction)?.GetUnit(d.Id);
                    int id = DoSpawnCombatUnit(pos, d.Faction, def);
                    if (id < 0) return null;
                    object? h = _onUnitSync?.Invoke(ScenarioSyncOp.Add, null, d.Id, d.Faction, at);
                    int[] box = { id };
                    return (
                        redo: () => { int r = DoSpawnCombatUnit(pos, d.Faction, def); if (r >= 0) { box[0] = r; if (h != null) _onUnitSync?.Invoke(ScenarioSyncOp.ReAdd, h, d.Id, d.Faction, at); } },
                        undo: () => { _world.Destroy(box[0]); if (h != null) _onUnitSync?.Invoke(ScenarioSyncOp.RemoveHandle, h, d.Id, d.Faction, at); },
                        selected: new Selected(Selected.Kind.Unit, id, null));
                }
                case Selected.Kind.Building:
                {
                    if (_buildings == null) return null;
                    // Story 6.8: d.Id is the authored DefinitionId. Resolve its def (from the owner faction) + enum, and
                    // create through the shared editor helper so a Custom building re-creates with real stats + id.
                    string defId = d.Id;
                    var fdef = d.Faction == Faction.Player2 ? _faction2 : _faction;
                    BuildingType type = TechTreeChecker.BuildingTypeFromId(defId) ?? BuildingType.Custom;
                    int id = CreateEditorBuilding(_buildings, pos, d.Faction, defId, fdef);
                    if (id < 0) return null;
                    Fixed dur = _buildings.ConstructionDuration[id];
                    var b = _buildings;
                    // DW-151: carry the source building's authored pre_built into the persisted DTO (only Add reads it;
                    // ReAdd/RemoveHandle pass it for signature consistency) so a moved pre_built:true building stays true.
                    object? h = _onBuildingSync?.Invoke(ScenarioSyncOp.Add, null, defId, d.Faction, at, d.PreBuilt);
                    return (
                        // Review fix (F2, symmetric): redo re-writes Position/Type/Faction, not just `Alive[id]=true`.
                        // After an undo restored the slot to its pre-move state, a redo must re-apply the moved
                        // position rather than relying on residue the undo just overwrote.
                        redo: () => { b.Alive[id] = true; b.Position[id] = pos; b.FactionOf[id] = d.Faction; b.Type[id] = type; b.DefinitionId[id] = defId; b.ConstructionTimer[id] = dur; b.ConstructionDuration[id] = dur; _onBuildingSync?.Invoke(ScenarioSyncOp.ReAdd, h, defId, d.Faction, at, d.PreBuilt); },
                        undo: () => { b.Destroy(id); _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveHandle, h, defId, d.Faction, at, d.PreBuilt); },
                        selected: new Selected(Selected.Kind.Building, id, null));
                }
                default: // Node
                {
                    if (_nodes == null) return null;
                    // DW-151: re-create through the full 10-arg store Create so the live node keeps its Story-4.7 field
                    // set, and pass the same fields into the sync so the persisted DTO does too.
                    int id = _nodes.Create(pos, Fixed.FromFloat(d.Supply), Fixed.FromFloat(d.Rate), d.MaxGatherers,
                        d.ResourceCollectionModel, d.ResourceKind, d.RequiresStructureId, d.RequiresStructureRadius, d.OwnerFaction, d.IncomePeriodTicks);
                    if (id < 0) return null;
                    var n = _nodes;
                    object? h = _onResourceNodeSync?.Invoke(ScenarioSyncOp.Add, null, at, d.Supply, d.Rate, d.MaxGatherers,
                        d.ResourceCollectionModel, d.ResourceKind, d.RequiresStructureId, d.RequiresStructureRadius, d.OwnerFaction, d.IncomePeriodTicks);
                    return (
                        // The node store is append-only (no free list), so this slot is never recycled — undo only flips
                        // Active off, and redo re-activates it with its (still-intact) 4.7 fields.
                        redo: () => { n.Active[id] = true; n.SupplyRemaining[id] = Fixed.FromFloat(d.Supply); n.SupplyTotal[id] = Fixed.FromFloat(d.Supply); n.GatherRate[id] = Fixed.FromFloat(d.Rate); _onResourceNodeSync?.Invoke(ScenarioSyncOp.ReAdd, h, at, d.Supply, d.Rate, d.MaxGatherers, d.ResourceCollectionModel, d.ResourceKind, d.RequiresStructureId, d.RequiresStructureRadius, d.OwnerFaction, d.IncomePeriodTicks); },
                        undo: () => { n.Active[id] = false; _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveHandle, h, at, d.Supply, d.Rate, d.MaxGatherers, d.ResourceCollectionModel, d.ResourceKind, d.RequiresStructureId, d.RequiresStructureRadius, d.OwnerFaction, d.IncomePeriodTicks); },
                        selected: new Selected(Selected.Kind.Node, id, null));
                }
            }
        }

        // ── Raycast / overlay helpers ─────────────────────────────────────────

        private Vector3? GroundPointOf(Vector2 screenPos)
        {
            var cam = _camCtrl?.GetCamera();
            if (cam == null) return null;
            var origin = cam.ProjectRayOrigin(screenPos);
            var dir    = cam.ProjectRayNormal(screenPos);
            if (Mathf.Abs(dir.Y) < 0.0001f) return null;
            float t = -origin.Y / dir.Y;
            if (t < 0f) return null;
            return origin + dir * t;
        }

        private bool IsOverPalette(Vector2 screenPos)
        {
            if (_paletteCanvas == null) return false;
            foreach (Node c in _paletteCanvas.GetChildren())
                if (c is PanelContainer pc && pc.GetGlobalRect().HasPoint(screenPos)) return true;
            return false;
        }

        /// <summary>
        /// DW-901 — is the cursor over ANY interactive UI, not just this placer's own palette?
        ///
        /// <para>This exists because <see cref="IsOverPalette"/> was the only guard on a handler that runs in
        /// <c>_Input</c> and ends in <c>SetInputAsHandled()</c>. <c>_Input</c> fires BEFORE Godot's GUI phase, so a
        /// left-click the placer claims is a click the Control under the cursor never receives. The palette was
        /// excluded; every OTHER editor panel was not — so with Select mode armed, clicking a
        /// control in any tool panel started a marquee instead of pressing the control. Reported from live use as
        /// "can't select erase mode in paint paths, can't uncheck slope auto-block": both of those are CheckButtons
        /// sitting in exactly such a panel, and both were confirmed correctly wired, enabled, laid out inside their
        /// panel rect, and MouseFilter.Stop — the clicks simply never arrived.</para>
        ///
        /// <para><c>GuiGetHoveredControl()</c> is the general form of the question: it returns the Control the GUI
        /// would deliver this click to (null when the cursor is over bare 3D world), and it already accounts for
        /// MouseFilter.Ignore anchors, visibility and z-order — none of which a hand-rolled rect check can see. The
        /// palette rect check is kept as a cheap first pass and as a backstop for the frame right after the palette
        /// appears, before hover has been recomputed.</para>
        /// </summary>
        private bool IsOverUi(Vector2 screenPos)
            => IsOverPalette(screenPos) || GetViewport().GuiGetHoveredControl() != null;

        // ── Selection overlay (screen-space marquee rect + 3D markers) ────────

        private CanvasLayer?       _selectCanvas;
        private ColorRect?         _marqueeVisual;
        private Node3D?            _markerRoot;
        private readonly List<MeshInstance3D> _markerPool = new();

        private void BuildSelectionOverlay()
        {
            _selectCanvas = new CanvasLayer { Layer = 6 };
            AddChild(_selectCanvas);
            _marqueeVisual = new ColorRect { Color = new Color(0.3f, 0.6f, 1f, 0.2f), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _selectCanvas.AddChild(_marqueeVisual);

            _markerRoot = new Node3D { Name = "SelectionMarkers" };
            GetParent()?.AddChild(_markerRoot);
        }

        private void UpdateSelectionOverlay(bool edit)
        {
            // DW-894: ONE assignment moves the whole drag preview. _markerRoot sits at the origin and its children are
            // written in absolute world coordinates, so offsetting the root translates every marker and every preview
            // mesh together — and zeroing it on release restores truth with no cleanup pass.
            if (_markerRoot != null)
                _markerRoot.Position = _groupMoving ? _groupMovePreviewDelta : Vector3.Zero;

            // Marquee rectangle.
            if (_marqueeVisual != null)
            {
                if (edit && _marqueeActive)
                {
                    Vector2 tl = new Vector2(Mathf.Min(_marqueeStart.X, _marqueeCurrent.X), Mathf.Min(_marqueeStart.Y, _marqueeCurrent.Y));
                    Vector2 br = new Vector2(Mathf.Max(_marqueeStart.X, _marqueeCurrent.X), Mathf.Max(_marqueeStart.Y, _marqueeCurrent.Y));
                    _marqueeVisual.Position = tl;
                    _marqueeVisual.Size     = br - tl;
                    _marqueeVisual.Visible  = true;
                }
                else _marqueeVisual.Visible = false;
            }

            // 3D markers over each selected object (Edit-only).
            if (_markerRoot == null) return;
            bool show = edit && _mode == PlacementMode.Select;
            _markerRoot.Visible = show;
            if (!show) return;

            // Grow the pool as needed.
            while (_markerPool.Count < _selection.Count)
            {
                var mi = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(1.5f, 0.1f, 1.5f) },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        AlbedoColor = new Color(1f, 1f, 0.3f, 0.5f),
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    },
                };
                _markerRoot.AddChild(mi);
                _markerPool.Add(mi);
            }
            for (int i = 0; i < _markerPool.Count; i++)
            {
                bool used = i < _selection.Count;
                _markerPool[i].Visible = used;
                if (used)
                {
                    Vector3 p = SelectedWorldPos(_selection[i]);
                    // DW-153: sit the marker on the sampled terrain height so it no longer sinks below raised ground
                    // (flat maps sample 0 ⇒ unchanged).
                    float y = _world.SampleElevation(Fixed.FromFloat(p.X), Fixed.FromFloat(p.Z)).ToFloat() + 0.15f;
                    _markerPool[i].Position = new Vector3(p.X, y, p.Z);
                }
            }
        }
    }
}
