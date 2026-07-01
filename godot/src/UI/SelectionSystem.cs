#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using ProjectChimera.Navigation; // FormationPlanner (Story 1.13)

namespace ProjectChimera.UI
{
    /// <summary>
    /// Multi-unit selection, control groups, and basic move command.
    ///
    /// Play mode input:
    ///   Left-click         — click-select nearest unit
    ///   Left-drag          — box-select all units inside the drawn rectangle
    ///   Right-click        — on an ENEMY: single-target Attack (force-fire); on ground/friendly: move (Story 1.12)
    ///   Q + Left-click     — attack-move to click destination (engage enemies en route)
    ///   P + Left-click     — patrol to the clicked waypoint; hold Shift and click to add more waypoints (Story 1.12)
    ///   F + Left-click     — follow (escort) the clicked friendly unit (Story 1.12)
    ///   S / H              — stop / hold position (H is a TRUE hold — defends its tile, never displaced; Story 1.12)
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

        /// <summary>True after P: the next left-click places a patrol waypoint; Shift+click keeps it armed for more (Story 1.12).</summary>
        private bool _awaitingPatrolClick;
        /// <summary>True after F: the next left-click picks a friendly unit to follow (Story 1.12).</summary>
        private bool _awaitingFollowClick;
        /// <summary>True after a TargetUnit ability button arms a cast: the next left-click picks the target (Story 2.4b).</summary>
        private bool _awaitingCastClick;
        /// <summary>The caster + ability slot a pending cast-target click will fire (set by <see cref="ArmCastTargeting"/>; -1 = none).</summary>
        private int _pendingCastCasterId = -1;
        private int _pendingCastSlot     = -1;

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

        /// <summary>
        /// Route a targeted command (AttackTarget/Follow): packs the TARGET ENTITY id into TargetX as a RAW int
        /// via Fixed.FromRaw — NEVER Fixed.FromFloat, which would round the id through float and corrupt it
        /// (and break determinism). Read back at apply time as o.TargetX. Story 1.12.
        /// </summary>
        private bool EnqueueTargetedCommand(int unitId, UnitCommand cmd, int targetEntityId)
            => _lockstep?.EnqueueOrder(unitId, cmd, Fixed.FromRaw(targetEntityId), Fixed.Zero) ?? true;

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

                    // Patrol pending (Story 1.12): place a waypoint. Shift keeps the placement armed, so
                    // P, click, shift-click, shift-click… builds a multi-waypoint route; a plain click disarms.
                    if (_awaitingPatrolClick)
                    {
                        IssuePatrolCommand(lmb.Position, append: lmb.ShiftPressed);
                        if (!lmb.ShiftPressed) _awaitingPatrolClick = false;
                        GetViewport().SetInputAsHandled();
                        return;
                    }

                    // Follow pending (Story 1.12): pick a friendly unit under the cursor to escort.
                    if (_awaitingFollowClick)
                    {
                        if (RaycastGround(lmb.Position, out Vector3 fHit))
                        {
                            int friendlyId = FindNearestUnit(fHit, PICK_RADIUS); // Player1-only = friendly
                            if (friendlyId >= 0) IssueFollowCommand(friendlyId);
                        }
                        _awaitingFollowClick = false;
                        GetViewport().SetInputAsHandled();
                        return;
                    }

                    // Cast-target pending (Story 2.4b): the player armed a TargetUnit ability on the command card;
                    // this left-click picks the nearest enemy as the target and issues the cast (Decision B = enemy-only,
                    // 2.4b's lone TargetUnit sample is offensive). A click hitting no enemy just disarms; an
                    // unfulfillable target is refused atomically by AbilityCastSystem (no spend, no cooldown).
                    if (_awaitingCastClick)
                    {
                        if (RaycastGround(lmb.Position, out Vector3 cHit))
                        {
                            int targetId = FindNearestEnemyUnit(cHit, PICK_RADIUS);
                            if (targetId >= 0)
                                IssueCastAbilityCommand(_pendingCastCasterId, _pendingCastSlot, targetId);
                        }
                        ResetPendingCommandClicks();
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
                // Story 2.4b: right-click cancels a pending cast-target click (no cast, no move command).
                if (_awaitingCastClick)
                {
                    ResetPendingCommandClicks();
                }
                else if (_selectedSet.Count > 0)
                {
                    // Right-click dispatch (Story 1.12 + 2.9a): enemy UNIT → single-target Attack (force-fire);
                    // else enemy BUILDING → AttackBuilding; else ground/friendly → Move. A friendly-building pick
                    // must FALL THROUGH to Move (not swallow the click into a dead no-op).
                    if (RaycastGround(rmb.Position, out Vector3 hit))
                    {
                        int enemyId = FindNearestEnemyUnit(hit, PICK_RADIUS);
                        int enemyBuildingId = enemyId < 0 ? FindNearestEnemyBuilding(hit, BUILDING_PICK_RADIUS) : -1;
                        if (enemyId >= 0)              IssueAttackTargetCommand(enemyId);
                        else if (enemyBuildingId >= 0) IssueAttackBuildingCommand(enemyBuildingId);
                        else                           IssueMoveCommand(rmb.Position);
                    }
                    else IssueMoveCommand(rmb.Position);
                }
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
                    ResetPendingCommandClicks();
                    IssueStopCommand();
                }
                else if (key.Keycode == Key.H && _selectedSet.Count > 0)
                {
                    ResetPendingCommandClicks();
                    IssueHoldCommand();
                }
                else if (key.Keycode == Key.Q && _selectedSet.Count > 0)
                {
                    ResetPendingCommandClicks();
                    _awaitingAttackMoveClick = true;
                    GD.Print("[Selection] Attack-Move: click a destination.");
                }
                else if (key.Keycode == Key.P && _selectedSet.Count > 0)
                {
                    ResetPendingCommandClicks();
                    _awaitingPatrolClick = true;
                    GD.Print("[Selection] Patrol: click a waypoint (hold Shift and click to add more).");
                }
                else if (key.Keycode == Key.F && _selectedSet.Count > 0)
                {
                    ResetPendingCommandClicks();
                    _awaitingFollowClick = true;
                    GD.Print("[Selection] Follow: click a friendly unit to escort.");
                }
                else if (key.Keycode == Key.Escape)
                {
                    ResetPendingCommandClicks();
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

        /// <summary>Spacing (world units) between adjacent units' destinations in a formation (Story 1.13).</summary>
        private static readonly Fixed FORMATION_SPACING = Fixed.FromInt(2);

        private void IssueMoveCommand(Vector2 screenPos)
        {
            Vector3 target;
            if (!RaycastGround(screenPos, out target)) return;
            target.Y = 0f;

            // Story 1.13: role-based formation via the Godot-free FormationPlanner (replaces the flat ceil(sqrt N)
            // grid). The planner is shared with IssueAttackMoveCommand so the two paths can never diverge (AC4d).
            FixedVec3[] dests = BuildFormation(target, out int[] ids);
            for (int k = 0; k < ids.Length; k++)
            {
                int id = ids[k];
                FixedVec3 fd = dests[k];
                var dest = new Vector3(fd.X.ToFloat(), 0f, fd.Z.ToFloat());

                if (!EnqueueCommand(id, UnitCommand.Move, dest)) continue; // online: queued, not applied yet

                if (_pathSystem != null)
                {
                    _pathSystem.RequestPath(id, dest);
                }
                else
                {
                    // Fallback: direct steering (goal rebuilt from the Vector3 so online + offline use the IDENTICAL
                    // Fixed.FromFloat boundary value — exactly the pre-1.13 offline-apply pattern).
                    var goal = new FixedVec3(Fixed.FromFloat(dest.X), Fixed.Zero, Fixed.FromFloat(dest.Z));
                    _world.CommandState[id]  = UnitCommand.Move;
                    _world.CommandGoal[id]   = goal;
                    _world.MoveTarget[id]    = goal;
                    _world.Flags[id]         = (_world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    _world.AttackTarget[id]  = -1;
                }
            }
        }

        /// <summary>
        /// Build the role-based formation for the current selection toward <paramref name="target"/> (Story 1.13).
        /// Gathers alive selected ids in ASCENDING order (the planner's deterministic slot contract — <c>_selectedList</c>
        /// may be control-group/click ordered), reads each unit's archetype from the sim SoA, derives the facing from
        /// the selection centroid → target, and calls the Godot-free <see cref="FormationPlanner"/>. Returns one
        /// destination per id (parallel to <paramref name="ids"/>). Shared by both issue paths (AC4d).
        /// </summary>
        private FixedVec3[] BuildFormation(Vector3 target, out int[] ids)
        {
            var idList = new List<int>(_selectedList.Count);
            foreach (int id in _selectedList)
                if (_world.IsAlive(id)) idList.Add(id);
            idList.Sort(); // ascending entity-id
            ids = idList.ToArray();

            int m = ids.Length;
            var cats = new UnitCategory[m];
            // Accumulate the centroid in 64-bit raw sums: summing ~200+ unit Positions (Fixed is 16.16 over int32)
            // can overflow int32 near a map edge BEFORE the divide, wrapping the centroid → a wrong group facing on
            // large selections. long holds the worst-case 4096-unit sum with room to spare; each per-axis mean lands
            // back inside Fixed range. Presentation/issuer-only (the planner's output is transmitted as Fixed), so
            // this never touches lockstep determinism — but it removes a real big-army failure mode.
            long sumX = 0, sumY = 0, sumZ = 0;
            for (int k = 0; k < m; k++)
            {
                cats[k] = _world.CategoryOf[ids[k]];
                FixedVec3 p = _world.Position[ids[k]];
                sumX += p.X.Raw; sumY += p.Y.Raw; sumZ += p.Z.Raw;
            }

            var ftarget = new FixedVec3(Fixed.FromFloat(target.X), Fixed.Zero, Fixed.FromFloat(target.Z));
            FixedVec3 facing = ftarget; // m == 0 → Plan returns an empty array; facing is unused
            if (m > 0)
            {
                var centroid = new FixedVec3(
                    Fixed.FromRaw((int)(sumX / m)), Fixed.FromRaw((int)(sumY / m)), Fixed.FromRaw((int)(sumZ / m)));
                facing = ftarget - centroid;
            }

            return FormationPlanner.Plan(ids, cats, ftarget, facing, FORMATION_SPACING);
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

            // Story 1.13: same role-based formation as IssueMoveCommand (AC4d — both paths share FormationPlanner).
            FixedVec3[] dests = BuildFormation(target, out int[] ids);
            for (int k = 0; k < ids.Length; k++)
            {
                int id = ids[k];
                FixedVec3 fd = dests[k];
                var dest = new Vector3(fd.X.ToFloat(), 0f, fd.Z.ToFloat());

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
            GD.Print($"[Selection] Attack-Move issued to {ids.Length} unit(s).");
        }

        /// <summary>
        /// Single-target Attack (Story 1.12): force every selected unit to attack ONE specific enemy, chasing
        /// only it and ignoring nearer enemies. Issued by right-clicking an enemy unit.
        /// </summary>
        private void IssueAttackTargetCommand(int enemyId)
        {
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id)) continue;
                if (!EnqueueTargetedCommand(id, UnitCommand.AttackTarget, enemyId)) continue; // online: queued
                // Offline: apply through the SAME shared OrderApplier the lockstep/replay paths use (Review,
                // Story 1.12) — never a hand-rolled copy that could silently drift from production apply logic.
                var atkOrder = new UnitOrder(id, UnitCommand.AttackTarget, Fixed.FromRaw(enemyId), Fixed.Zero);
                OrderApplier.Apply(_world, in atkOrder, _world.FactionOf[id]);
            }
            GD.Print($"[Selection] Attack issued on enemy {enemyId} to {_selectedList.Count} unit(s).");
        }

        /// <summary>
        /// Force every selected COMBAT unit to attack ONE specific enemy building (Story 2.9a): chase its centre point
        /// and raze it. Mirrors <see cref="IssueAttackTargetCommand"/> but (a) uses <see cref="UnitCommand.AttackBuilding"/>
        /// with the BUILDING id, and (b) issues ONLY to combat units (<c>EffectiveAttackDamage &gt; 0</c>) so workers /
        /// non-combatants — which CombatSystem skips and thus never self-revert — aren't left in a dangling AttackBuilding
        /// state. Issued by right-clicking an enemy building. Presentation issues an INTENT only; the tick validates it.
        /// </summary>
        private void IssueAttackBuildingCommand(int buildingId)
        {
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id)) continue;
                if (_world.EffectiveAttackDamage[id] <= Fixed.Zero) continue; // combat units only
                if (!EnqueueTargetedCommand(id, UnitCommand.AttackBuilding, buildingId)) continue; // online: queued
                // Offline: apply through the SAME shared OrderApplier — identical to the AttackTarget call, NO buildings arg.
                var atkOrder = new UnitOrder(id, UnitCommand.AttackBuilding, Fixed.FromRaw(buildingId), Fixed.Zero);
                OrderApplier.Apply(_world, in atkOrder, _world.FactionOf[id]);
            }
            GD.Print($"[Selection] Attack-building issued on building {buildingId} to {_selectedList.Count} unit(s).");
        }

        /// <summary>
        /// Patrol (Story 1.12): a plain click starts a fresh route [current position, clicked point]; each
        /// subsequent Shift+click appends a waypoint (PatrolAppend) up to MAX_PATROL_WAYPOINTS. Single
        /// destination — NO formation grid here (that is Story 1.13). The offline path applies through the SAME
        /// OrderApplier the lockstep/replay paths use, so patrol route setup is never duplicated in presentation.
        /// </summary>
        private void IssuePatrolCommand(Vector2 screenPos, bool append)
        {
            if (!RaycastGround(screenPos, out Vector3 target)) return;
            target.Y = 0f;

            UnitCommand cmd = append ? UnitCommand.PatrolAppend : UnitCommand.Patrol;
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id)) continue;
                var dest = new Vector3(target.X, 0f, target.Z);
                if (!EnqueueCommand(id, cmd, dest)) continue; // online: queued (applied later by Flush)
                var order = new UnitOrder(id, cmd, Fixed.FromFloat(dest.X), Fixed.FromFloat(dest.Z));
                OrderApplier.Apply(_world, in order, _world.FactionOf[id]);
            }
            GD.Print($"[Selection] Patrol ({(append ? "append" : "new")}) issued to {_selectedList.Count} unit(s).");
        }

        /// <summary>
        /// Follow (Story 1.12): every selected unit escorts the clicked friendly unit, tracking it within a leash.
        /// </summary>
        private void IssueFollowCommand(int friendlyId)
        {
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id)) continue;
                if (id == friendlyId) continue; // a unit cannot follow itself
                if (!EnqueueTargetedCommand(id, UnitCommand.Follow, friendlyId)) continue; // online: queued
                // Offline: apply through the shared OrderApplier (Review, Story 1.12) — same path as Patrol.
                var followOrder = new UnitOrder(id, UnitCommand.Follow, Fixed.FromRaw(friendlyId), Fixed.Zero);
                OrderApplier.Apply(_world, in followOrder, _world.FactionOf[id]);
            }
            GD.Print($"[Selection] Follow issued on friendly {friendlyId} to {_selectedList.Count} unit(s).");
        }

        // ── Ability cast (Story 2.4b) ───────────────────────────────────────────────

        /// <summary>
        /// Clear ALL pending click-arm states (attack-move / patrol / follow / cast-target) and the cast's pending
        /// caster+slot. Called before arming any one of them (mutual exclusion — only one click-arm is ever live at a
        /// time) and on Stop/Hold/Escape. Centralising the reset means a NEW arm flag can never be forgotten at a
        /// clear site (the missed-spot defect class). Story 2.4b folded the cast-target arm into this set.
        /// </summary>
        private void ResetPendingCommandClicks()
        {
            _awaitingAttackMoveClick = false;
            _awaitingPatrolClick     = false;
            _awaitingFollowClick     = false;
            _awaitingCastClick       = false;
            _pendingCastCasterId     = -1;
            _pendingCastSlot         = -1;
        }

        /// <summary>
        /// Arm a TargetUnit cast (Story 2.4b): the command card calls this when the player presses a TargetUnit
        /// ability button. The NEXT left-click picks the nearest enemy as the target and issues the cast; right-click
        /// or Escape cancels. Stores the caster + ability slot (a cast needs BOTH, unlike the other click-arms which
        /// act on the whole selection). SelectionSystem stays ability-data-free — the card supplies caster+slot.
        /// </summary>
        public void ArmCastTargeting(int casterId, int slot)
        {
            ResetPendingCommandClicks();
            _pendingCastCasterId = casterId;
            _pendingCastSlot     = slot;
            _awaitingCastClick   = true;
            GD.Print("[Selection] Cast: click an enemy target.");
        }

        /// <summary>
        /// Issue a single-caster ability cast (Story 2.4b). Mirrors <see cref="IssueAttackTargetCommand"/> but on ONE
        /// caster and packs BOTH values into the shipped 11-byte wire: the ability slot in TargetX and the target
        /// entity id in TargetZ, each a RAW int via <see cref="Fixed.FromRaw"/> — NEVER <c>Fixed.FromFloat</c> (it
        /// scales by 65536 and corrupts the packed ints — the 1.12 lesson). Self/None casts pass targetEntityId = -1
        /// (issued directly from the card, no arming). Online → queued via <c>EnqueueOrder</c> (Flush applies later);
        /// offline (<c>_lockstep == null</c>) → applied now through the SAME shared <see cref="OrderApplier"/> the
        /// lockstep/replay paths use, so live/replay/offline cast application can never diverge.
        /// </summary>
        public void IssueCastAbilityCommand(int casterId, int slot, int targetEntityId)
        {
            // Story 2.4b review: re-validate the caster at the ISSUE seam, not just at arm time. The pending caster id
            // (ArmCastTargeting) persists across frames and is NOT pruned like _selectedList, so the armed caster can
            // die and its slot recycle to a different unit before the target-click. Refuse unless the caster is still
            // alive AND locally owned (Player1 — the selection convention at :387/:732), so a recycled enemy slot can
            // never be made to cast (offline this also re-seats expectedFaction to the local player, closing the
            // self-comparison hole in OrderApplier's anti-cheat guard).
            if (!_world.IsAlive(casterId) || _world.FactionOf[casterId] != Faction.Player1) return;
            // Online: EnqueueOrder returns false (queued). Offline (_lockstep == null): the ?? true yields apply-now.
            bool applyNow = _lockstep?.EnqueueOrder(casterId, UnitCommand.CastAbility,
                                                    Fixed.FromRaw(slot), Fixed.FromRaw(targetEntityId)) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush will apply it later
            var order = new UnitOrder(casterId, UnitCommand.CastAbility, Fixed.FromRaw(slot), Fixed.FromRaw(targetEntityId));
            OrderApplier.Apply(_world, in order, _world.FactionOf[casterId]);
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

        /// <summary>
        /// Nearest ENEMY unit to the world hit within radius (Story 1.12). Enemy = alive and neither Player1
        /// (the local player) nor Neutral. Mirror of <see cref="FindNearestUnit"/>, which finds Player1-only.
        /// </summary>
        private int FindNearestEnemyUnit(Vector3 worldHit, float radius)
        {
            int   bestId     = -1;
            float bestSqDist = radius * radius;
            int   cap        = _world.HighWaterMark;

            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                Faction f = _world.FactionOf[i];
                if (f == Faction.Player1 || f == Faction.Neutral) continue; // enemy = not me, not neutral
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

        /// <summary>
        /// Nearest ENEMY building to the world hit within radius (Story 2.9a). Enemy = alive and NOT the local
        /// player's faction (Player1). Clone of <see cref="FindNearestBuilding"/> (faction-agnostic) with a
        /// local-faction exclusion only — Neutral buildings stay targetable when explicitly ordered (AC2.5), so we
        /// do NOT copy <see cref="FindNearestEnemyUnit"/>'s Neutral skip.
        /// </summary>
        private int FindNearestEnemyBuilding(Vector3 worldHit, float radius)
        {
            if (_buildingStore == null) return -1;

            int   bestId     = -1;
            float bestSqDist = radius * radius;

            for (int i = 0; i < _buildingStore.Count; i++)
            {
                if (!_buildingStore.Alive[i]) continue;
                if (_buildingStore.FactionOf[i] == Faction.Player1) continue; // exclude ONLY the local player's buildings
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

            float maxHp = _world.EffectiveMaxHealth[_focusId].ToFloat();
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
