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
        public enum PlacementMode { P1Unit, P2Unit, ResourceNode, Building, StartPos, Item }

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

        private static readonly float[] BUILDING_COSTS = { 150f, 100f, 120f, 200f, 200f }; // …, Aviary=200 (Story 2.8 — indexed by (int)BuildingType; a missing entry crashes editor place/delete)

        // Modes displayed left-to-right in the palette (order must match _modeBtns array)
        private static readonly PlacementMode[] MODE_ORDER =
            { PlacementMode.P1Unit, PlacementMode.P2Unit, PlacementMode.ResourceNode, PlacementMode.Building, PlacementMode.StartPos, PlacementMode.Item };
        private static readonly string[] MODE_LABELS = { "P1 Unit", "P2 Unit", "Ore Node", "Building", "Start Pos", "Item" };

        // ── Dependencies ──────────────────────────────────────────────────────
        private RtsCameraController _camCtrl   = null!;
        private EntityWorld         _world     = null!;
        private ResourceNodeStore?  _nodes;
        private ResourceStore?      _resources;
        private BuildingStore?      _buildings;
        private ItemStore?          _items;         // Story 3.15 — ground item instances (Item placement mode)
        private ItemRegistry?       _itemRegistry;  // Story 3.15 — id→index over validated item defs
        private int                 _itemIndex = 0; // Story 3.15 — which registry item to place (cycled by re-clicking the Item mode)
        private FactionDefinition?  _faction;   // Player1
        private FactionDefinition?  _faction2;  // Player2

        /// <summary>
        /// Fired when the user places a start-position marker.
        /// Parameters: (slotIndex 0=P1/1=P2, world position, starting ore).
        /// </summary>
        private System.Action<int, Vector3, float>? _onStartPosMoved;

        /// <summary>
        /// Story 6.1 — ScenarioData sync callbacks (one per persisted entity kind), fired inside the place/delete
        /// undo/redo closures so <c>ScenarioData.Buildings/Units/ResourceNodes</c> stay symmetric with the live
        /// stores across save/reload AND the F5 Edit→Play toggle (which re-applies only <c>_ctx.Scenario</c>). Each
        /// returns an opaque handle to the affected scenario entry. See <see cref="ScenarioSyncOp"/>.
        /// </summary>
        private System.Func<ScenarioSyncOp, object?, BuildingType, Faction, Vector3, bool, object?>? _onBuildingSync;
        private System.Func<ScenarioSyncOp, object?, string, Faction, Vector3, object?>?             _onUnitSync;
        private System.Func<ScenarioSyncOp, object?, Vector3, float, float, int, object?>?           _onResourceNodeSync;

        // ── Placement state ───────────────────────────────────────────────────
        private PlacementMode _mode         = PlacementMode.P1Unit;

        /// <summary>Story 6.1 (UX-DR56) — true while a placement mode is armed (ghost visible, left-click places).
        /// Right-click or Esc disarms it (see <see cref="CancelPlacement"/>); re-selecting any mode re-arms it (see
        /// <see cref="ArmPlacement"/>). Starts armed so placement works immediately on entering Edit mode.</summary>
        private bool _placementActive = true;
        private PlacementMode _lastUnitMode = PlacementMode.P1Unit;
        private BuildingType  _buildingType = BuildingType.CommandCenter;
        private int           _unitIndex    = 0;
        private bool          _gridSnapEnabled = false;

        // Start position sub-state
        private int   _startSlot = 0;    // 0=P1, 1=P2
        private float _startOre  = 200f; // starting ore for the selected slot

        // Resource node sub-state (configurable supply and gather rate)
        private float _nodeSupply = 500f;
        private float _nodeRate   = 5f;

        // Undo/redo history
        private readonly EditorHistory _history = new();

        // Tracks ore set per start-position slot (for undo of MoveStartPos)
        private readonly float[] _slotStartOre = { 200f, 200f };

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
            PlacementMode.Building     => $"Building [{_buildingType}]",
            PlacementMode.StartPos     => $"Start Pos [P{_startSlot + 1}]",
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
                               System.Action<int, Vector3, float>? onStartPosMoved = null,
                               FactionDefinition? faction2 = null,
                               ItemStore? items = null, ItemRegistry? itemRegistry = null,
                               System.Func<ScenarioSyncOp, object?, BuildingType, Faction, Vector3, bool, object?>? onBuildingSync = null,
                               System.Func<ScenarioSyncOp, object?, string, Faction, Vector3, object?>? onUnitSync = null,
                               System.Func<ScenarioSyncOp, object?, Vector3, float, float, int, object?>? onResourceNodeSync = null)
        {
            _camCtrl            = camCtrl;
            _world              = world;
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

            CreateGhostMesh();
            BuildPaletteUi();
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
        }

        // ── Godot lifecycle ───────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            bool edit = GameState.Instance?.Mode == GameMode.Edit;

            if (_paletteCanvas != null)
                _paletteCanvas.Visible = edit;

            UpdateGhostPosition(edit);
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

            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            // UX-DR56 (Story 6.1): Esc cancels an active placement mode. Gated on _placementActive so that when
            // nothing is armed the event is NOT consumed here — it falls through to MainScene._UnhandledInput's
            // global Esc→Settings toggle (this _Input runs before and preempts that unhandled-input handler).
            if (editMode && key.Keycode == Key.Escape)
            {
                if (_placementActive)
                {
                    CancelPlacement();
                    GetViewport().SetInputAsHandled();
                }
                return;
            }

            // Undo / redo — only in Edit mode
            if (editMode && key.CtrlPressed)
            {
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

            // Delete hovered entity
            if (editMode && key.Keycode == Key.Delete)
            {
                TryDeleteAt(_lastCursorWorld);
                GetViewport().SetInputAsHandled();
                return;
            }

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
                    break;
            }
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
            RefreshGhostVisuals();
        }

        /// <summary>
        /// Update ghost mesh shape and colour to reflect the current placement mode.
        /// Called on every mode or building-type change.
        /// </summary>
        private void RefreshGhostVisuals()
        {
            if (_ghost == null) return;

            _ghost.Mesh = _mode switch
            {
                PlacementMode.ResourceNode => (Mesh)new SphereMesh { Radius = 0.8f, Height = 1.6f },
                PlacementMode.Building     => new BoxMesh { Size = new Vector3(4f, 2f, 4f) },
                PlacementMode.StartPos     => new BoxMesh { Size = new Vector3(0.15f, 3f, 0.15f) }, // flag pole
                _                          => new BoxMesh { Size = new Vector3(0.6f, 1.2f, 0.6f) },
            };

            if (_ghost.MaterialOverride is StandardMaterial3D mat)
            {
                mat.AlbedoColor = _mode switch
                {
                    PlacementMode.P2Unit       => new Color(1f,   0.30f, 0.2f, 0.40f),
                    PlacementMode.ResourceNode => new Color(1f,   0.85f, 0.2f, 0.40f),
                    PlacementMode.Building     => new Color(0.2f, 0.80f, 0.3f, 0.35f),
                    PlacementMode.StartPos     => _startSlot == 0
                                                    ? new Color(0.2f, 0.5f, 1f, 0.5f)
                                                    : new Color(1f, 0.3f, 0.2f, 0.5f),
                    _                          => new Color(0.2f, 0.50f, 1f,   0.40f),
                };
            }
        }

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
            float yOff = _mode switch
            {
                PlacementMode.Building     => 1.0f,
                PlacementMode.ResourceNode => 0.8f,
                PlacementMode.StartPos     => 1.5f, // flag pole: half of 3u height
                _                          => 0.6f,
            };

            _lastCursorWorld = new Vector3(x, 0f, z);
            _ghost.Position  = new Vector3(x, yOff, z);
            // UX-DR56 (Story 6.1): keep tracking the cursor (so Delete still targets the hover), but only SHOW the
            // ghost while a placement mode is armed — a right-click/Esc cancel hides it until a mode is re-selected.
            _ghost.Visible   = _placementActive;
        }

        // ── Placement arm / cancel (UX-DR56) ──────────────────────────────────

        /// <summary>Disarm the active placement mode: hides the ghost and makes left-clicks no-op until re-armed.
        /// Fired by right-click or Esc while a placement mode is active.</summary>
        private void CancelPlacement()
        {
            _placementActive = false;
            if (_ghost != null) _ghost.Visible = false;
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
            _history.Push(
                redo: () => _items.Create(defId, charges, pos),
                undo: () => { if (_items.TryResolveRef(packed, out int slot)) _items.Destroy(slot); });
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
            int nodeId = _nodes.Create(pos, supply, rate, NODE_MAX_GATHERERS);
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
            object? syncHandle       = _onResourceNodeSync?.Invoke(ScenarioSyncOp.Add, null, wpos, capturedSupplyF, capturedRateF, NODE_MAX_GATHERERS);
            _history.Push(
                redo: () =>
                {
                    capturedNodes.Active[capturedId]          = true;
                    capturedNodes.SupplyRemaining[capturedId] = capturedSupply;
                    capturedNodes.SupplyTotal[capturedId]     = capturedSupply;
                    capturedNodes.GatherRate[capturedId]      = capturedRate;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, wpos, capturedSupplyF, capturedRateF, NODE_MAX_GATHERERS);
                },
                undo: () =>
                {
                    capturedNodes.Active[capturedId] = false;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, wpos, capturedSupplyF, capturedRateF, NODE_MAX_GATHERERS);
                });
        }

        private void PlaceBuilding(FixedVec3 pos)
        {
            if (_buildings == null) { GD.PrintErr("[EntityPlacer] BuildingStore not set."); return; }

            Faction faction   = Faction.Player1;
            string  buildingId = TechTreeChecker.BuildingTypeId(_buildingType);
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
                    GD.Print($"[EntityPlacer] Cannot place {_buildingType}: requires {missingName}.");
                    return;
                }
            }

            if (_resources != null)
            {
                float costF = BUILDING_COSTS[(int)_buildingType];
                var   cost  = Fixed.FromFloat(costF);
                if (!_resources.SpendOre(faction, cost))
                {
                    GD.Print($"[EntityPlacer] Cannot afford {_buildingType} " +
                             $"(costs {costF} ore, have {_resources.Ore[(int)faction].ToFloat():F0}).");
                    return;
                }
            }

            int id = _buildings.Create(pos, faction, _buildingType);
            if (id < 0) { GD.PrintErr("[EntityPlacer] BuildingStore full."); return; }
            GD.Print($"[EntityPlacer] Placed {_buildingType} id={id} for {faction} at ({pos.X:F1},{pos.Z:F1})");

            // Story 6.1: mirror the placement into ScenarioData so the building survives save/reload AND the F5
            // Edit→Play re-apply. Editor-placed buildings start under construction (BuildingStore.Create seeds the
            // full ConstructionTimer), so persist pre_built:false to re-apply identically.
            BuildingType capturedType = _buildingType;
            Vector3      wpos         = new Vector3(pos.X.ToFloat(), 0f, pos.Z.ToFloat());

            // Capture for undo — building slot id is stable (BuildingStore has no free list)
            int      capturedId       = id;
            Faction  capturedFaction  = faction;
            Fixed    capturedCost     = Fixed.FromFloat(BUILDING_COSTS[(int)_buildingType]);
            Fixed    capturedDuration = _buildings.ConstructionDuration[id];
            var      capturedBuildings = _buildings;

            object?  syncHandle = _onBuildingSync?.Invoke(ScenarioSyncOp.Add, null, capturedType, capturedFaction, wpos, false);
            _history.Push(
                redo: () =>
                {
                    capturedBuildings.Alive[capturedId]              = true;
                    capturedBuildings.ConstructionTimer[capturedId]  = capturedDuration;
                    _resources?.SpendOre(capturedFaction, capturedCost);
                    _onBuildingSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, capturedType, capturedFaction, wpos, false);
                },
                undo: () =>
                {
                    capturedBuildings.Destroy(capturedId);
                    _resources?.AddOre(capturedFaction, capturedCost);
                    _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, capturedType, capturedFaction, wpos, false);
                });
        }

        private void MoveStartPosition(Vector3 worldPos)
        {
            var snapped = new Vector3(SnapValue(worldPos.X), 0f, SnapValue(worldPos.Z));

            // Capture old state before applying
            int   capturedSlot   = _startSlot;
            float capturedNewOre = _startOre;
            float capturedOldOre = _slotStartOre[_startSlot];
            var   capturedNewPos = snapped;
            var   capturedOldBase = _resources?.FactionBase[(int)_startSlot + 1] ?? default;
            var   capturedOldPos = new Vector3(capturedOldBase.X.ToFloat(), 0f, capturedOldBase.Z.ToFloat());

            _onStartPosMoved?.Invoke(_startSlot, snapped, _startOre);
            _slotStartOre[_startSlot] = _startOre;

            GD.Print($"[EntityPlacer] Start pos P{_startSlot + 1} → ({snapped.X:F1}, {snapped.Z:F1})  ore={_startOre:F0}");

            _history.Push(
                redo: () => _onStartPosMoved?.Invoke(capturedSlot, capturedNewPos, capturedNewOre),
                undo: () => _onStartPosMoved?.Invoke(capturedSlot, capturedOldPos, capturedOldOre));
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
            _buildingType = _buildingType switch
            {
                BuildingType.CommandCenter => BuildingType.Barracks,
                BuildingType.Barracks      => BuildingType.ArcheryRange,
                BuildingType.ArcheryRange  => BuildingType.SiegeWorkshop,
                BuildingType.SiegeWorkshop => BuildingType.Aviary,
                BuildingType.Aviary        => BuildingType.CommandCenter,
                _                          => BuildingType.CommandCenter,
            };
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
                var buildGroup = new ButtonGroup();
                foreach (var (label, type) in new (string, BuildingType)[]
                {
                    ("CC",       BuildingType.CommandCenter),
                    ("Barracks", BuildingType.Barracks),
                    ("Archery",  BuildingType.ArcheryRange),
                    ("Siege",    BuildingType.SiegeWorkshop),
                    ("Aviary",   BuildingType.Aviary),
                })
                {
                    var btn = new Button
                    {
                        Text          = label,
                        ToggleMode    = true,
                        ButtonGroup   = buildGroup,
                        ButtonPressed = (type == _buildingType),
                    };
                    var capturedType = type;
                    btn.Pressed += () =>
                    {
                        _buildingType = capturedType;
                        ArmPlacement(); // UX-DR56: re-selecting a building type re-arms placement after a cancel
                        RefreshGhostVisuals();
                        GD.Print($"[EntityPlacer] Building type: {_buildingType}");
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
                // P1 / P2 toggle
                var slotGroup = new ButtonGroup();
                foreach (var (label, slot) in new (string, int)[] { ("P1", 0), ("P2", 1) })
                {
                    int capturedSlot = slot;
                    var btn = new Button
                    {
                        Text          = label,
                        ToggleMode    = true,
                        ButtonGroup   = slotGroup,
                        ButtonPressed = (slot == _startSlot),
                        CustomMinimumSize = new Vector2(36f, 0f),
                    };
                    btn.Pressed += () =>
                    {
                        _startSlot = capturedSlot;
                        ArmPlacement(); // UX-DR56: re-selecting a start-pos slot re-arms placement after a cancel
                        RefreshGhostVisuals(); // update ghost color
                    };
                    _subRow.AddChild(btn);
                }

                // Starting ore spinner
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
                spin.ValueChanged += v => _startOre = (float)v;
                _subRow.AddChild(spin);

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
        }

        private void DeleteBuilding(int id)
        {
            if (_buildings == null) return;
            Fixed capturedCost     = BUILDING_COSTS[(int)_buildings.Type[id]] > 0
                ? Fixed.FromFloat(BUILDING_COSTS[(int)_buildings.Type[id]])
                : Fixed.Zero;
            Faction capturedFaction = _buildings.FactionOf[id];
            Fixed   capturedDuration = _buildings.ConstructionDuration[id];
            Fixed   capturedTimer    = _buildings.ConstructionTimer[id];
            var     capturedBuildings = _buildings;

            // Story 6.1: capture the descriptor from the LIVE slot BEFORE Destroy so the ScenarioData match can run.
            BuildingType capturedType = _buildings.Type[id];
            Vector3      wpos         = new Vector3(_buildings.Position[id].X.ToFloat(), 0f, _buildings.Position[id].Z.ToFloat());

            _buildings.Destroy(id);
            // No ore refund on delete (destructive intent)
            GD.Print($"[EntityPlacer] Deleted building id={id}");

            // Story 6.1: remove the matching ScenarioData.Buildings entry, capturing the REAL object so undo restores
            // it by identity (preserving authored pre_built / slot / type — never reconstructing a lossy value).
            object? syncHandle = _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, capturedType, capturedFaction, wpos, false);

            _history.Push(
                redo: () =>
                {
                    capturedBuildings.Destroy(id);
                    _onBuildingSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, capturedType, capturedFaction, wpos, false);
                },
                undo: () =>
                {
                    capturedBuildings.Alive[id]              = true;
                    capturedBuildings.ConstructionTimer[id]  = capturedTimer;
                    capturedBuildings.ConstructionDuration[id] = capturedDuration;
                    _onBuildingSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, capturedType, capturedFaction, wpos, false);
                });
        }

        private void DeleteUnit(int id)
        {
            // Story 3.17: capture the full authored residue via the Godot-free Core mapper. A def-based unit stores
            // only its def reference; RestoreUnit re-derives every def-derived field through ApplyUnitDefinition, so
            // armor/passives/abilities/feedback/tags/domain/delivery/collision/separation/category/XP no longer revert
            // to Create defaults on undo (the recurring RestoreUnit drop-debt).
            UnitSnapshot snap = _world.SnapshotUnit(id);
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
            object? syncHandle = _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveMatch, null, wpos, 0f, 0f, 0);

            _history.Push(
                redo: () =>
                {
                    capturedNodes.Active[id] = false;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.RemoveHandle, syncHandle, wpos, 0f, 0f, 0);
                },
                undo: () =>
                {
                    capturedNodes.Active[id]          = true;
                    capturedNodes.SupplyRemaining[id] = capturedSupply;
                    capturedNodes.SupplyTotal[id]     = capturedTotal;
                    capturedNodes.GatherRate[id]      = capturedRate;
                    _onResourceNodeSync?.Invoke(ScenarioSyncOp.ReAdd, syncHandle, wpos, 0f, 0f, 0);
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
    }
}
