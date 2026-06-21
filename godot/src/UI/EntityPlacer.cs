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
        public enum PlacementMode { P1Unit, P2Unit, ResourceNode, Building, StartPos }

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

        private static readonly float[] BUILDING_COSTS = { 150f, 100f, 120f, 200f };

        // Modes displayed left-to-right in the palette (order must match _modeBtns array)
        private static readonly PlacementMode[] MODE_ORDER =
            { PlacementMode.P1Unit, PlacementMode.P2Unit, PlacementMode.ResourceNode, PlacementMode.Building, PlacementMode.StartPos };
        private static readonly string[] MODE_LABELS = { "P1 Unit", "P2 Unit", "Ore Node", "Building", "Start Pos" };

        // ── Dependencies ──────────────────────────────────────────────────────
        private RtsCameraController _camCtrl   = null!;
        private EntityWorld         _world     = null!;
        private ResourceNodeStore?  _nodes;
        private ResourceStore?      _resources;
        private BuildingStore?      _buildings;
        private FactionDefinition?  _faction;   // Player1
        private FactionDefinition?  _faction2;  // Player2

        /// <summary>
        /// Fired when the user places a start-position marker.
        /// Parameters: (slotIndex 0=P1/1=P2, world position, starting ore).
        /// </summary>
        private System.Action<int, Vector3, float>? _onStartPosMoved;

        // ── Placement state ───────────────────────────────────────────────────
        private PlacementMode _mode         = PlacementMode.P1Unit;
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

        // ── Snapshot for unit delete undo ─────────────────────────────────────
        private struct UnitSnapshot
        {
            public FixedVec3   Position;
            public Faction     Faction;
            public Fixed       MaxHealth;
            public Fixed       Speed;
            public Fixed       AttackRange;
            public Fixed       AttackDamage;
            public Fixed       AttackSpeed;
            public DamageType  DamageType;
            public ArmorType   ArmorType;
            public Fixed       VisionRange;
            public Fixed       SplashRadius;
            public byte        SupplyCost;
            public GatherState GatherState;
            public Fixed       CarryCapacity;
            public byte        MeshType;
        }

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
                               FactionDefinition? faction2 = null)
        {
            _camCtrl          = camCtrl;
            _world            = world;
            _nodes            = nodes;
            _resources        = resources;
            _buildings        = buildings;
            _faction          = faction;
            _faction2         = faction2;
            _onStartPosMoved  = onStartPosMoved;

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
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            bool editMode = GameState.Instance?.Mode == GameMode.Edit;

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
                    SyncPaletteToMode();
                    GD.Print($"[EntityPlacer] Mode: {ModeLabel}");
                    break;

                case Key.U:
                    CycleUnitType();
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
            _ghost.Visible   = true;
        }

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
                default:
                    PlaceUnit(fixedPos, shiftHeld);
                    break;
            }
        }

        private void PlaceUnit(FixedVec3 pos, bool asWorker)
        {
            Faction faction = _mode == PlacementMode.P2Unit ? Faction.Player2 : Faction.Player1;

            if (asWorker)
            {
                int id = DoSpawnWorker(pos, faction);
                if (id < 0) return;
                int[] box = { id };
                _history.Push(
                    redo: () => { int r = DoSpawnWorker(pos, faction); if (r >= 0) box[0] = r; },
                    undo: () => _world.Destroy(box[0]));
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
                int[] box = { id };
                _history.Push(
                    redo: () => { int r = DoSpawnCombatUnit(pos, faction, def); if (r >= 0) box[0] = r; },
                    undo: () => _world.Destroy(box[0]));
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
            float hp           = def?.Hp           ?? HEALTH;
            float speed        = def?.Speed        ?? SPEED;
            float atkRng       = def?.AttackRange  ?? ATTACK_RANGE;
            float atkDmg       = def?.AttackDamage ?? ATTACK_DMG;
            float atkSpd       = def?.AttackSpeed  ?? ATTACK_SPEED;
            float vision       = def?.VisionRange  ?? 8f;
            float splashRadius = def?.SplashRadius ?? 0f;
            byte  supply       = (byte)(def?.Supply ?? 1);
            var   dmgType      = def?.ParsedDamageType ?? DamageType.Normal;
            var   armType      = def?.ParsedArmorType  ?? ArmorType.Light;

            if (_resources != null && !_resources.HasSupply(faction, supply))
            {
                GD.Print($"[EntityPlacer] {faction} supply full " +
                         $"({_resources.SupplyUsed[(int)faction]}/{_resources.SupplyCap[(int)faction]}).");
                return -1;
            }

            int id = _world.Create(pos, faction, Fixed.FromFloat(hp), Fixed.FromFloat(speed));
            if (id < 0) { GD.PrintErr("[EntityPlacer] EntityWorld full."); return -1; }

            _world.SupplyCost[id]    = supply;
            _world.AttackRange[id]   = Fixed.FromFloat(atkRng);
            _world.AttackDamage[id]  = Fixed.FromFloat(atkDmg);
            _world.AttackSpeed[id]   = Fixed.FromFloat(atkSpd);
            _world.DamageTypeOf[id]  = dmgType;
            _world.ArmorTypeOf[id]   = armType;
            _world.VisionRange[id]   = Fixed.FromFloat(vision);
            _world.SplashRadius[id]  = Fixed.FromFloat(splashRadius);

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
            int capturedId         = nodeId;
            var capturedSupply     = supply;
            var capturedRate       = rate;
            var capturedNodes      = _nodes;
            _history.Push(
                redo: () =>
                {
                    capturedNodes.Active[capturedId]          = true;
                    capturedNodes.SupplyRemaining[capturedId] = capturedSupply;
                    capturedNodes.SupplyTotal[capturedId]     = capturedSupply;
                    capturedNodes.GatherRate[capturedId]      = capturedRate;
                },
                undo: () => capturedNodes.Active[capturedId] = false);
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
                    GD.Print($"[EntityPlacer] Cannot place {_buildingType}: requires {missing}.");
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

            // Capture for undo — building slot id is stable (BuildingStore has no free list)
            int      capturedId       = id;
            Faction  capturedFaction  = faction;
            Fixed    capturedCost     = Fixed.FromFloat(BUILDING_COSTS[(int)_buildingType]);
            Fixed    capturedDuration = _buildings.ConstructionDuration[id];
            var      capturedBuildings = _buildings;
            _history.Push(
                redo: () =>
                {
                    capturedBuildings.Alive[capturedId]              = true;
                    capturedBuildings.ConstructionTimer[capturedId]  = capturedDuration;
                    _resources?.SpendOre(capturedFaction, capturedCost);
                },
                undo: () =>
                {
                    capturedBuildings.Destroy(capturedId);
                    _resources?.AddOre(capturedFaction, capturedCost);
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
                BuildingType.SiegeWorkshop => BuildingType.CommandCenter,
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

            _buildings.Destroy(id);
            // No ore refund on delete (destructive intent)
            GD.Print($"[EntityPlacer] Deleted building id={id}");

            _history.Push(
                redo: () => capturedBuildings.Destroy(id),
                undo: () =>
                {
                    capturedBuildings.Alive[id]              = true;
                    capturedBuildings.ConstructionTimer[id]  = capturedTimer;
                    capturedBuildings.ConstructionDuration[id] = capturedDuration;
                });
        }

        private void DeleteUnit(int id)
        {
            // Snapshot all relevant fields before destroying
            var snap = new UnitSnapshot
            {
                Position     = _world.Position[id],
                Faction      = _world.FactionOf[id],
                MaxHealth    = _world.MaxHealth[id],
                Speed        = _world.Speed[id],
                AttackRange  = _world.AttackRange[id],
                AttackDamage = _world.AttackDamage[id],
                AttackSpeed  = _world.AttackSpeed[id],
                DamageType   = _world.DamageTypeOf[id],
                ArmorType    = _world.ArmorTypeOf[id],
                VisionRange  = _world.VisionRange[id],
                SplashRadius = _world.SplashRadius[id],
                SupplyCost   = _world.SupplyCost[id],
                GatherState  = _world.GatherState[id],
                CarryCapacity = _world.CarryCapacity[id],
                MeshType     = _world.MeshType[id],
            };
            _world.Destroy(id);
            GD.Print($"[EntityPlacer] Deleted unit id={id}");

            // Undo re-creates the unit; the new id is boxed so redo can destroy it again
            int[] box = { -1 };
            _history.Push(
                redo: () => { if (box[0] >= 0) _world.Destroy(box[0]); },
                undo: () => { box[0] = RestoreUnit(snap); });
        }

        private void DeleteResourceNode(int id)
        {
            if (_nodes == null) return;
            var capturedNodes   = _nodes;
            var capturedSupply  = _nodes.SupplyRemaining[id];
            var capturedTotal   = _nodes.SupplyTotal[id];
            var capturedRate    = _nodes.GatherRate[id];

            _nodes.Active[id] = false;
            GD.Print($"[EntityPlacer] Deleted resource node id={id}");

            _history.Push(
                redo: () => capturedNodes.Active[id] = false,
                undo: () =>
                {
                    capturedNodes.Active[id]          = true;
                    capturedNodes.SupplyRemaining[id] = capturedSupply;
                    capturedNodes.SupplyTotal[id]     = capturedTotal;
                    capturedNodes.GatherRate[id]      = capturedRate;
                });
        }

        /// <summary>Re-create a unit from a snapshot. Returns the new entity id.</summary>
        private int RestoreUnit(UnitSnapshot snap)
        {
            int id = _world.Create(snap.Position, snap.Faction, snap.MaxHealth, snap.Speed);
            if (id < 0) { GD.PrintErr("[EntityPlacer] EntityWorld full — cannot restore deleted unit."); return -1; }

            _world.MaxHealth[id]    = snap.MaxHealth;
            _world.SupplyCost[id]   = snap.SupplyCost;
            _world.AttackRange[id]  = snap.AttackRange;
            _world.AttackDamage[id] = snap.AttackDamage;
            _world.AttackSpeed[id]  = snap.AttackSpeed;
            _world.DamageTypeOf[id] = snap.DamageType;
            _world.ArmorTypeOf[id]  = snap.ArmorType;
            _world.VisionRange[id]  = snap.VisionRange;
            _world.SplashRadius[id] = snap.SplashRadius;
            _world.GatherState[id]  = snap.GatherState;
            _world.CarryCapacity[id] = snap.CarryCapacity;
            _world.MeshType[id]     = snap.MeshType;
            return id;
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
