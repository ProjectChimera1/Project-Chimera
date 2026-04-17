#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Multi-unit selection, control groups, and basic move command.
    ///
    /// Play mode input:
    ///   Left-click         — click-select nearest unit
    ///   Left-drag          — box-select all units inside the drawn rectangle
    ///   Right-click        — move all selected units to the clicked ground point
    ///   Q + Left-click     — attack-move to click destination (engage enemies en route)
    ///   Ctrl+1–9           — assign current selection to control group N
    ///   1–9                — recall control group N (replaces current selection)
    ///   Escape             — deselect all
    ///
    /// Visuals:
    ///   - Yellow glow ring under each selected unit (pooled, up to MAX_RINGS).
    ///   - Selection rectangle drawn as a semi-transparent overlay while dragging.
    ///   - HP bar + stats shown for the focus unit (last clicked/first in box).
    ///   - "N units selected  [group G]" label shown for multi-selection.
    /// </summary>
    public partial class SelectionSystem : Node
    {
        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Legacy single-unit alias used by MainScene HUD.</summary>
        public int SelectedId => _focusId;

        /// <summary>The "focused" unit — HP bar tracks this one.</summary>
        public int FocusId => _focusId;

        /// <summary>All currently selected entity IDs.</summary>
        public IReadOnlyList<int> SelectedIds => _selectedList;

        // ── Constants ─────────────────────────────────────────────────────────────

        private const float PICK_RADIUS    = 2.5f;
        private const float DRAG_THRESHOLD = 5f;    // pixels before drag is recognised
        private const float BAR_W          = 80f;
        private const float BAR_H          = 10f;
        private const float BAR_Y_WORLD    = 2.2f;
        private const int   MAX_RINGS      = 64;
        private const int   NUM_GROUPS     = 9;

        // ── Constants ─────────────────────────────────────────────────────────────

        private const float BUILDING_PICK_RADIUS = 4.0f;  // world units

        // ── Dependencies ──────────────────────────────────────────────────────────

        private RtsCameraController _camCtrl       = null!;
        private EntityWorld         _world         = null!;
        private FlowFieldBridge?    _pathSystem    = null;
        private BuildingStore?      _buildingStore = null;

        // ── Building selection ─────────────────────────────────────────────────────

        /// <summary>
        /// Building ID currently selected in Play mode, or -1 if none.
        /// Read by CommandCardSystem to show/update the command card panel.
        /// </summary>
        public int SelectedBuildingId { get; private set; } = -1;

        // ── Unit selection state ───────────────────────────────────────────────────

        private int _focusId = -1;
        private readonly HashSet<int> _selectedSet  = new();
        private readonly List<int>    _selectedList = new();

        // ── Control groups (1–9) ─────────────────────────────────────────────────

        /// <summary>Which control group the current selection belongs to, or -1 if none.</summary>
        public int ActiveGroupIndex { get; private set; } = -1;

        // Each slot stores a snapshot of entity IDs. Null = unassigned.
        private readonly List<int>?[] _controlGroups = new List<int>?[NUM_GROUPS];

        // ── Drag state ────────────────────────────────────────────────────────────

        private bool    _lmbHeld;
        private bool    _isDragging;
        private Vector2 _dragStart;
        private Vector2 _dragCurrent;

        // ── Command state ─────────────────────────────────────────────────────────

        /// <summary>True when the player has pressed Q and we're waiting for a click destination.</summary>
        private bool _awaitingAttackMoveClick;

        /// <summary>
        /// Optional lockstep coordinator. When set (online mode), all player commands
        /// are queued here instead of applied directly to EntityWorld. When null (offline),
        /// commands apply immediately as before.
        /// </summary>
        private LockstepManager? _lockstep;

        // ── Visuals ───────────────────────────────────────────────────────────────

        private MeshInstance3D[] _rings = null!;
        private Panel            _selBoxPanel = null!;  // selection rect overlay

        // HP bar (focus unit)
        private CanvasLayer  _canvas   = null!;
        private Control      _barRoot  = null!;
        private Panel        _barBg    = null!;
        private Panel        _barFill  = null!;
        private Label        _barLabel = null!;
        private StyleBoxFlat _fillStyle = null!;

        // "N selected" label (multi-select)
        private Label _multiLabel = null!;

        // ── Init ──────────────────────────────────────────────────────────────────

        public void Initialize(RtsCameraController camCtrl, EntityWorld world,
                              FlowFieldBridge? pathSystem = null,
                              BuildingStore? buildingStore = null)
        {
            _camCtrl       = camCtrl;
            _world         = world;
            _pathSystem    = pathSystem;
            _buildingStore = buildingStore;
        }

        /// <summary>
        /// Inject the lockstep manager for online play. Pass null to revert to offline mode.
        /// </summary>
        public void SetLockstep(LockstepManager? lockstep) => _lockstep = lockstep;

        /// <summary>
        /// Route a unit command through the lockstep manager (online) or apply it now (offline).
        /// Returns true if the caller should apply the command immediately to EntityWorld/PathSystem.
        /// Returns false in online mode — LockstepManager.Flush() will apply it later.
        /// </summary>
        private bool EnqueueCommand(int unitId, UnitCommand cmd, Vector3 dest)
        {
            var tx = Fixed.FromFloat(dest.X);
            var tz = Fixed.FromFloat(dest.Z);
            return _lockstep?.EnqueueOrder(unitId, cmd, tx, tz) ?? true;
        }

        /// <summary>
        /// Route a stationary command (Stop/Hold) — no destination needed.
        /// </summary>
        private bool EnqueueStationary(int unitId, UnitCommand cmd)
            => _lockstep?.EnqueueOrder(unitId, cmd, Fixed.Zero, Fixed.Zero) ?? true;

        public override void _Ready()
        {
            SetupRings();
            SetupSelectionBoxOverlay();
            SetupHealthBar();
        }

        // ── Per-frame ─────────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            PruneDeadUnits();
            UpdateRingVisuals();
            UpdateHealthBar();
            UpdateSelectionBox();
            UpdateMultiLabel();
        }

        // ── Input ─────────────────────────────────────────────────────────────────

        public override void _UnhandledInput(InputEvent @event)
        {
            if (GameState.Instance == null || GameState.Instance.Mode != GameMode.Play) return;

            // ── Left mouse ───────────────────────────────────────────────────────
            if (@event is InputEventMouseButton lmb && lmb.ButtonIndex == MouseButton.Left)
            {
                if (lmb.Pressed)
                {
                    // Attack-move pending: consume this click as the command destination
                    if (_awaitingAttackMoveClick)
                    {
                        IssueAttackMoveCommand(lmb.Position);
                        _awaitingAttackMoveClick = false;
                        GetViewport().SetInputAsHandled();
                        return;
                    }

                    _lmbHeld     = true;
                    _isDragging  = false;
                    _dragStart   = lmb.Position;
                    _dragCurrent = lmb.Position;
                }
                else if (_lmbHeld)
                {
                    _lmbHeld = false;
                    if (_isDragging)
                        FinalizeBoxSelect();
                    else
                        TryClickSelect(_dragStart);

                    _isDragging = false;
                    _selBoxPanel.Visible = false;
                }
            }

            // ── Mouse move (drag tracking) ────────────────────────────────────────
            if (@event is InputEventMouseMotion motion && _lmbHeld)
            {
                _dragCurrent = motion.Position;
                if (!_isDragging && _dragStart.DistanceTo(_dragCurrent) > DRAG_THRESHOLD)
                    _isDragging = true;
            }

            // ── Right mouse — move command or rally point ─────────────────────────
            if (@event is InputEventMouseButton rmb
                && rmb.ButtonIndex == MouseButton.Right
                && rmb.Pressed)
            {
                if (_selectedSet.Count > 0)
                    IssueMoveCommand(rmb.Position);
                else if (SelectedBuildingId >= 0 && _buildingStore != null)
                    SetRallyPoint(SelectedBuildingId, rmb.Position);
            }

            // ── Keyboard commands ─────────────────────────────────────────────────
            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                int groupIndex = KeyToGroupIndex(key.Keycode);
                if (groupIndex >= 0)
                {
                    if (key.CtrlPressed)
                        AssignControlGroup(groupIndex);
                    else
                        RecallControlGroup(groupIndex);
                }
                else if (key.Keycode == Key.S && _selectedSet.Count > 0)
                {
                    IssueStopCommand();
                }
                else if (key.Keycode == Key.H && _selectedSet.Count > 0)
                {
                    IssueHoldCommand();
                }
                else if (key.Keycode == Key.Q && _selectedSet.Count > 0)
                {
                    _awaitingAttackMoveClick = true;
                    GD.Print("[Selection] Attack-Move: click a destination.");
                }
                else if (key.Keycode == Key.Escape)
                {
                    _awaitingAttackMoveClick = false;
                    ClearSelection();
                }
            }
        }

        // ── Selection ─────────────────────────────────────────────────────────────

        private void TryClickSelect(Vector2 screenPos)
        {
            Vector3 hit;
            if (!RaycastGround(screenPos, out hit)) return;

            // 1. Try unit first (units take priority over buildings)
            int unitId = FindNearestUnit(hit, PICK_RADIUS);
            ClearSelection(); // clears units and SelectedBuildingId

            if (unitId >= 0)
            {
                AddToSelection(unitId, setFocus: true);
                return;
            }

            // 2. Fall through to building if no unit nearby
            if (_buildingStore != null)
            {
                int bId = FindNearestBuilding(hit, BUILDING_PICK_RADIUS);
                if (bId >= 0)
                    SelectedBuildingId = bId;  // ClearSelection already set it to -1
            }
        }

        private void FinalizeBoxSelect()
        {
            var camera = _camCtrl?.GetCamera();
            if (camera == null) return;

            Rect2 screenRect = MakeRect(_dragStart, _dragCurrent);
            ClearSelection(); // also resets ActiveGroupIndex

            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                if (_world.FactionOf[i] != Faction.Player1) continue; // only select own units

                var sim = _world.Position[i];
                var world3d = new Vector3(sim.X.ToFloat(), 0.8f, sim.Z.ToFloat());

                if (camera.IsPositionBehind(world3d)) continue;

                Vector2 screen = camera.UnprojectPosition(world3d);
                if (screenRect.HasPoint(screen))
                    AddToSelection(i, setFocus: _focusId < 0);
            }
        }

        private void AddToSelection(int id, bool setFocus)
        {
            if (_selectedSet.Add(id))
                _selectedList.Add(id);
            if (setFocus)
                _focusId = id;
        }

        private void ClearSelection()
        {
            _selectedSet.Clear();
            _selectedList.Clear();
            _focusId = -1;
            ActiveGroupIndex   = -1;
            SelectedBuildingId = -1;
            _barRoot.Visible    = false;
            _multiLabel.Visible = false;
        }

        // ── Move command ──────────────────────────────────────────────────────────

        private void IssueMoveCommand(Vector2 screenPos)
        {
            Vector3 target;
            if (!RaycastGround(screenPos, out target)) return;
            target.Y = 0f;

            int n    = _selectedList.Count;
            int cols = (int)System.Math.Ceiling(System.Math.Sqrt(n));

            // Formation spacing: 2 world units between each unit's individual destination.
            const float SPACING = 2.0f;

            for (int si = 0; si < n; si++)
            {
                int id = _selectedList[si];
                if (!_world.IsAlive(id)) continue;

                // Spread units in a square grid centred on the click point.
                int   row = si / cols;
                int   col = si % cols;
                float ox  = (col - (cols - 1) * 0.5f) * SPACING;
                float oz  = (row - ((n - 1) / cols) * 0.5f) * SPACING;

                var dest = new Vector3(target.X + ox, 0f, target.Z + oz);

                if (!EnqueueCommand(id, UnitCommand.Move, dest)) continue; // online: queued, not applied yet

                if (_pathSystem != null)
                {
                    _pathSystem.RequestPath(id, dest);
                }
                else
                {
                    // Fallback: direct steering
                    var goal = new FixedVec3(Fixed.FromFloat(dest.X), Fixed.Zero, Fixed.FromFloat(dest.Z));
                    _world.CommandState[id]  = UnitCommand.Move;
                    _world.CommandGoal[id]   = goal;
                    _world.MoveTarget[id]    = goal;
                    _world.Flags[id]         = (_world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    _world.AttackTarget[id]  = -1;
                }
            }
        }

        // ── Command methods ───────────────────────────────────────────────────────

        /// <summary>
        /// Stop: units halt immediately and only attack enemies that enter their range.
        /// They do not chase.
        /// </summary>
        private void IssueStopCommand()
        {
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id)) continue;
                if (!EnqueueStationary(id, UnitCommand.Stop)) continue; // online: queued

                _world.Flags[id]        = _world.Flags[id] & ~(EntityFlags.Moving | EntityFlags.Attacking);
                _world.AttackTarget[id] = -1;
                _world.CommandState[id] = UnitCommand.Stop;
                _pathSystem?.CancelPath(id);
            }
            GD.Print($"[Selection] Stop issued to {_selectedList.Count} unit(s).");
        }

        /// <summary>
        /// Hold Position: same as Stop in Phase 1. Units defend their position.
        /// </summary>
        private void IssueHoldCommand()
        {
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id)) continue;
                if (!EnqueueStationary(id, UnitCommand.HoldPosition)) continue; // online: queued

                _world.Flags[id]        = _world.Flags[id] & ~(EntityFlags.Moving | EntityFlags.Attacking);
                _world.AttackTarget[id] = -1;
                _world.CommandState[id] = UnitCommand.HoldPosition;
                _pathSystem?.CancelPath(id);
            }
            GD.Print($"[Selection] Hold Position issued to {_selectedList.Count} unit(s).");
        }

        /// <summary>
        /// Attack Move: units navigate to the click destination, engaging enemies they encounter.
        /// After each kill they resume toward the destination.
        /// </summary>
        private void IssueAttackMoveCommand(Vector2 screenPos)
        {
            Vector3 target;
            if (!RaycastGround(screenPos, out target)) return;
            target.Y = 0f;

            int n    = _selectedList.Count;
            int cols = (int)System.Math.Ceiling(System.Math.Sqrt(n));
            const float SPACING = 2.0f;

            for (int si = 0; si < n; si++)
            {
                int id = _selectedList[si];
                if (!_world.IsAlive(id)) continue;

                int   row  = si / cols;
                int   col  = si % cols;
                float ox   = (col - (cols - 1) * 0.5f) * SPACING;
                float oz   = (row - ((n - 1) / cols) * 0.5f) * SPACING;

                var dest = new Vector3(target.X + ox, 0f, target.Z + oz);

                if (!EnqueueCommand(id, UnitCommand.AttackMove, dest)) continue; // online: queued

                if (_pathSystem != null)
                {
                    _pathSystem.RequestAttackMove(id, dest);
                }
                else
                {
                    var goal = new FixedVec3(Fixed.FromFloat(dest.X), Fixed.Zero, Fixed.FromFloat(dest.Z));
                    _world.CommandState[id]  = UnitCommand.AttackMove;
                    _world.CommandGoal[id]   = goal;
                    _world.MoveTarget[id]    = goal;
                    _world.Flags[id]         = (_world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    _world.AttackTarget[id]  = -1;
                }
            }
            GD.Print($"[Selection] Attack-Move issued to {n} unit(s).");
        }

        /// <summary>
        /// Set the rally point for a building to the world position the player right-clicked.
        /// Newly trained units from this building will walk to this point on spawn.
        /// </summary>
        private void SetRallyPoint(int buildingId, Vector2 screenPos)
        {
            if (_buildingStore == null) return;
            if (!RaycastGround(screenPos, out Vector3 hit)) return;

            _buildingStore.RallyPoint[buildingId]    = new FixedVec3(
                Fixed.FromFloat(hit.X), Fixed.Zero, Fixed.FromFloat(hit.Z));
            _buildingStore.HasRallyPoint[buildingId] = true;

            GD.Print($"[Selection] Rally point → building {buildingId} at ({hit.X:F1}, {hit.Z:F1})");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private bool RaycastGround(Vector2 screenPos, out Vector3 hit)
        {
            hit = Vector3.Zero;
            var camera = _camCtrl?.GetCamera();
            if (camera == null) return false;

            Vector3 origin = camera.ProjectRayOrigin(screenPos);
            Vector3 dir    = camera.ProjectRayNormal(screenPos);
            if (Mathf.Abs(dir.Y) < 0.0001f) return false;

            float t = -origin.Y / dir.Y;
            if (t < 0f) return false;

            hit = origin + dir * t;
            return true;
        }

        private int FindNearestUnit(Vector3 worldHit, float radius)
        {
            int   bestId     = -1;
            float bestSqDist = radius * radius;
            int   cap        = _world.HighWaterMark;

            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                if (_world.FactionOf[i] != Faction.Player1) continue; // only select own units
                var pos = _world.Position[i];
                float dx = pos.X.ToFloat() - worldHit.X;
                float dz = pos.Z.ToFloat() - worldHit.Z;
                float sqDist = dx * dx + dz * dz;
                if (sqDist < bestSqDist) { bestSqDist = sqDist; bestId = i; }
            }
            return bestId;
        }

        private int FindNearestBuilding(Vector3 worldHit, float radius)
        {
            if (_buildingStore == null) return -1;

            int   bestId     = -1;
            float bestSqDist = radius * radius;

            for (int i = 0; i < _buildingStore.Count; i++)
            {
                if (!_buildingStore.Alive[i]) continue;
                var pos = _buildingStore.Position[i];
                float dx = pos.X.ToFloat() - worldHit.X;
                float dz = pos.Z.ToFloat() - worldHit.Z;
                float sqDist = dx * dx + dz * dz;
                if (sqDist < bestSqDist) { bestSqDist = sqDist; bestId = i; }
            }
            return bestId;
        }

        private void PruneDeadUnits()
        {
            if (_selectedSet.Count == 0) return;

            _selectedList.RemoveAll(id => !_world.IsAlive(id));
            _selectedSet.RemoveWhere(id => !_world.IsAlive(id));

            if (_focusId >= 0 && !_world.IsAlive(_focusId))
                _focusId = _selectedList.Count > 0 ? _selectedList[0] : -1;
        }

        private static Rect2 MakeRect(Vector2 a, Vector2 b) =>
            new Rect2(
                new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y)),
                new Vector2(Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y)));

        // ── Control group helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Assign the current selection to control group <paramref name="index"/> (0-based).
        /// Overwrites any previous assignment.
        /// </summary>
        private void AssignControlGroup(int index)
        {
            if (_selectedList.Count == 0) return;
            _controlGroups[index] = new List<int>(_selectedList);
            ActiveGroupIndex = index;
            GD.Print($"[Selection] Group {index + 1} assigned — {_selectedList.Count} units.");
        }

        /// <summary>
        /// Recall control group <paramref name="index"/> (0-based), replacing the current selection.
        /// Dead units are pruned from the stored group before applying it.
        /// </summary>
        private void RecallControlGroup(int index)
        {
            var group = _controlGroups[index];
            if (group == null || group.Count == 0) return;

            // Prune dead units from the stored group in-place
            group.RemoveAll(id => !_world.IsAlive(id));
            if (group.Count == 0) { _controlGroups[index] = null; return; }

            ClearSelection();
            foreach (int id in group)
                AddToSelection(id, setFocus: _focusId < 0);

            ActiveGroupIndex = index;
        }

        /// <summary>Map Key.Key1–Key.Key9 to 0-based group index, or -1 for other keys.</summary>
        private static int KeyToGroupIndex(Key keycode) => keycode switch
        {
            Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2,
            Key.Key4 => 3, Key.Key5 => 4, Key.Key6 => 5,
            Key.Key7 => 6, Key.Key8 => 7, Key.Key9 => 8,
            _ => -1
        };

        // ── Visual updates ────────────────────────────────────────────────────────

        private void UpdateRingVisuals()
        {
            // Hide all rings, then show one per selected unit (up to pool size)
            for (int r = 0; r < MAX_RINGS; r++)
                _rings[r].Visible = false;

            int shown = 0;
            foreach (int id in _selectedList)
            {
                if (shown >= MAX_RINGS) break;
                var pos = _world.Position[id];
                _rings[shown].GlobalPosition = new Vector3(pos.X.ToFloat(), 0.04f, pos.Z.ToFloat());
                _rings[shown].Visible = true;
                shown++;
            }
        }

        private void UpdateSelectionBox()
        {
            if (!_isDragging) return;

            Rect2 r = MakeRect(_dragStart, _dragCurrent);
            _selBoxPanel.Position = r.Position;
            _selBoxPanel.Size     = r.Size;
            _selBoxPanel.Visible  = true;
        }

        private void UpdateHealthBar()
        {
            if (_focusId < 0 || !_world.IsAlive(_focusId))
            {
                _barRoot.Visible = false;
                return;
            }

            var camera = _camCtrl?.GetCamera();
            if (camera == null) return;

            var simPos   = _world.Position[_focusId];
            var worldPos = new Vector3(simPos.X.ToFloat(), BAR_Y_WORLD, simPos.Z.ToFloat());

            if (camera.IsPositionBehind(worldPos)) { _barRoot.Visible = false; return; }

            _barRoot.Visible = true;
            Vector2 screen = camera.UnprojectPosition(worldPos);
            _barRoot.Position = screen - new Vector2(BAR_W * 0.5f, BAR_H);

            float maxHp = _world.MaxHealth[_focusId].ToFloat();
            float curHp = _world.Health[_focusId].ToFloat();
            float ratio = maxHp > 0f ? Mathf.Clamp(curHp / maxHp, 0f, 1f) : 0f;

            _barFill.Size   = new Vector2(BAR_W * ratio, BAR_H);
            _fillStyle.BgColor = ratio > 0.5f
                ? new Color(1f - (ratio - 0.5f) * 2f, 1f, 0f)
                : new Color(1f, ratio * 2f, 0f);

            string faction = _world.FactionOf[_focusId] == Faction.Player1 ? "P1" : "P2";
            _barLabel.Text = $"{faction}  {(int)curHp}/{(int)maxHp} HP  [id {_focusId}]";
        }

        private void UpdateMultiLabel()
        {
            if (_selectedList.Count <= 1) { _multiLabel.Visible = false; return; }
            string groupTag = ActiveGroupIndex >= 0 ? $"  [group {ActiveGroupIndex + 1}]" : "";
            _multiLabel.Visible = true;
            _multiLabel.Text = $"{_selectedList.Count} units selected{groupTag}";
        }

        // ── Setup ─────────────────────────────────────────────────────────────────

        private void SetupRings()
        {
            _rings = new MeshInstance3D[MAX_RINGS];
            var sharedMesh = BuildRingMesh();

            for (int i = 0; i < MAX_RINGS; i++)
            {
                var mi = new MeshInstance3D();
                mi.Mesh    = sharedMesh;
                mi.Visible = false;
                GetParent().AddChild(mi);
                _rings[i] = mi;
            }
        }

        private static Mesh BuildRingMesh()
        {
            var cylinder = new CylinderMesh();
            cylinder.TopRadius      = 0.9f;
            cylinder.BottomRadius   = 0.9f;
            cylinder.Height         = 0.08f;
            cylinder.RadialSegments = 32;

            var mat = new StandardMaterial3D();
            mat.AlbedoColor     = new Color(1f, 0.9f, 0.1f);
            mat.EmissionEnabled = true;
            mat.Emission        = new Color(1f, 0.85f, 0f) * 2f;
            mat.ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded;
            cylinder.Material   = mat;
            return cylinder;
        }

        private void SetupSelectionBoxOverlay()
        {
            // CanvasLayer → Control container → selection box Panel
            var overlayCanvas = new CanvasLayer();
            AddChild(overlayCanvas);

            // Transparent container that covers the full viewport
            var root = new Control();
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            overlayCanvas.AddChild(root);

            var boxStyle = new StyleBoxFlat();
            boxStyle.BgColor = new Color(0.3f, 0.7f, 1f, 0.12f);
            boxStyle.BorderColor = new Color(0.5f, 0.85f, 1f, 0.9f);
            boxStyle.BorderWidthTop    = 1;
            boxStyle.BorderWidthBottom = 1;
            boxStyle.BorderWidthLeft   = 1;
            boxStyle.BorderWidthRight  = 1;

            _selBoxPanel = new Panel();
            _selBoxPanel.AddThemeStyleboxOverride("panel", boxStyle);
            _selBoxPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
            _selBoxPanel.Visible = false;
            root.AddChild(_selBoxPanel);
        }

        private void SetupHealthBar()
        {
            _canvas = new CanvasLayer();
            AddChild(_canvas);

            _barRoot = new Control();
            _barRoot.Size    = new Vector2(BAR_W + 80f, BAR_H + 18f);
            _barRoot.Visible = false;
            _canvas.AddChild(_barRoot);

            var bgStyle = new StyleBoxFlat();
            bgStyle.BgColor = new Color(0.05f, 0.05f, 0.05f, 0.85f);
            bgStyle.CornerRadiusTopLeft = bgStyle.CornerRadiusTopRight =
            bgStyle.CornerRadiusBottomLeft = bgStyle.CornerRadiusBottomRight = 2;

            _barBg      = new Panel();
            _barBg.Size = new Vector2(BAR_W, BAR_H);
            _barBg.AddThemeStyleboxOverride("panel", bgStyle);
            _barRoot.AddChild(_barBg);

            _fillStyle = new StyleBoxFlat();
            _fillStyle.BgColor = Colors.Green;
            _fillStyle.CornerRadiusTopLeft = _fillStyle.CornerRadiusTopRight =
            _fillStyle.CornerRadiusBottomLeft = _fillStyle.CornerRadiusBottomRight = 2;

            _barFill          = new Panel();
            _barFill.Position = Vector2.Zero;
            _barFill.Size     = new Vector2(BAR_W, BAR_H);
            _barFill.AddThemeStyleboxOverride("panel", _fillStyle);
            _barRoot.AddChild(_barFill);

            _barLabel          = new Label();
            _barLabel.Position = new Vector2(0f, BAR_H + 2f);
            _barLabel.Size     = new Vector2(BAR_W + 80f, 14f);
            _barLabel.AddThemeColorOverride("font_color", Colors.White);
            _barLabel.AddThemeFontSizeOverride("font_size", 12);
            _barRoot.AddChild(_barLabel);

            // "N units selected" label — shown below HP bar area
            _multiLabel          = new Label();
            _multiLabel.Position = new Vector2(10f, 10f);
            _multiLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.5f));
            _multiLabel.AddThemeFontSizeOverride("font_size", 16);
            _multiLabel.Visible = false;
            _canvas.AddChild(_multiLabel);
        }
    }
}
