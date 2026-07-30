#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // UnitDefinition (Story 11.5: per-definition subgroup key)
using ProjectChimera.Combat;     // CombatEventQueue (Story 2.12: OrderDenied feedback on a full-ring reject)
using ProjectChimera.Economy;    // BuildingSystem (Story 2.12: rally rides the wire via BuildingSystem.SetRallyCommand)
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

        // ── Subgroups (Story 11.5, FR-74) — presentation-only, WC3 type-grouped view of the selection ──────────────

        /// <summary>The current selection grouped into WC3 subgroups by unit type (first-appearance key order; member
        /// input order). Recomputed on every selection change; pruned/clamped on death. Presentation-only — the sim
        /// never sees a subgroup.</summary>
        public IReadOnlyList<SelectionSubgroups.Subgroup> Subgroups => _subgroups;

        /// <summary>Index of the active subgroup (the one Tab/HP-bar/command-card follow), or -1 when none. Distinct
        /// from <see cref="ActiveGroupIndex"/> (control groups — a different concept).</summary>
        public int ActiveSubgroupIndex => _activeSubgroupIndex;

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
        // Story 2.12: the production system the shared OrderApplier uses to EXECUTE a SetRally command (the offline
        // apply site — online routes through LockstepManager which holds its own BuildSys). Null in tests/menus.
        private BuildingSystem?     _buildSys      = null;
        // Story 2.12: the presentation event bus for the OrderDenied feedback on a full-ring reject (AC4). Optional —
        // a null sink still rejects deterministically (the reject reads the folded OrderQueueCount); only the visual is skipped.
        private CombatEventQueue?   _combatEvents  = null;

        // Story 11.4 (FR-74): issue-time order-acknowledgment deps. The ack sound + order-confirmed ground marker fire
        // LOCALLY on the input frame (masking lockstep delay); the deterministic sim effect still lands at exec-tick.
        // Both nullable — wired by MatchAlertPhase; a null dep just skips its half of the feedback.
        private AudioManager?      _audioMgr     = null;
        private OrderMarkerBridge? _orderMarkers = null;
        // Own-view faction tint for the order-confirmed marker (matches the minimap own-colour).
        private static readonly Color OrderMarkerTint = new Color(0.20f, 0.55f, 1.00f);

        /// <summary>Story 11.4: inject the issue-time acknowledgment deps (audio cue + ground-marker pool).</summary>
        public void SetFeedbackDeps(AudioManager audio, OrderMarkerBridge markers)
        {
            _audioMgr     = audio;
            _orderMarkers = markers;
        }

        // Story 9.5: the local player's faction, late-bound. Defaults to Player1 so every offline/single-player path and
        // any un-wired instance stay byte-identical to today; CameraPhase wires it to _ctx.Lockstep?.EffectiveLocalFaction
        // (offline/spectator clamps to Player1) so a client assigned Player2..Player8 selects/commands/casts on its OWN
        // units. Presentation-only — no sim fold.
        private System.Func<Faction> _localFaction = () => Faction.Player1;

        /// <summary>Story 9.5: inject the live local-faction getter (see <see cref="CameraPhase"/>).</summary>
        public void SetLocalFaction(System.Func<Faction> getter) => _localFaction = getter;

        // ── Building selection ─────────────────────────────────────────────────────

        /// <summary>
        /// Building ID currently selected in Play mode, or -1 if none — or if the selection is now STALE (its slot was
        /// destroyed, or recycled into a DIFFERENT building). Read by CommandCardSystem to show/update the command card.
        /// Story 2.13 review (patch): the raw slot is generation-stamped at selection and re-validated on every read, so
        /// a razed-then-recycled slot (a new building at the same index — now possible under 2.13's free-list recycling)
        /// never inherits the old selection; a rally/train issued on a stale selection would otherwise silently mis-target
        /// the recycled occupant. Mirrors the D-3 packed-ref armor for the presentation-side holder the story didn't cover.
        /// </summary>
        public int SelectedBuildingId
        {
            get
            {
                if (_selectedBuildingSlot < 0 || _buildingStore == null) return -1;
                if (!_buildingStore.Alive[_selectedBuildingSlot] ||
                    _buildingStore.Generation[_selectedBuildingSlot] != _selectedBuildingGen)
                    return -1;
                return _selectedBuildingSlot;
            }
        }
        private int _selectedBuildingSlot = -1;
        private int _selectedBuildingGen  = -1;

        // ── Unit selection state ───────────────────────────────────────────────────

        private int _focusId = -1;
        private readonly HashSet<int> _selectedSet  = new();
        private readonly List<int>    _selectedList = new();

        // ── Subgroup state (Story 11.5) — presentation-only; never mutates selection membership ──
        private readonly List<SelectionSubgroups.Subgroup> _subgroups = new();
        private int _activeSubgroupIndex = -1;
        // Stable per-UnitDefinition grouping key (reference-keyed; assigned lazily as new types appear). Positive keys
        // (≥1) never collide with the CategoryOf fallback keys, which live in the negative space.
        private readonly Dictionary<UnitDefinition, long> _defSubgroupKeys = new();
        private long _nextDefSubgroupKey = 1;

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
        private Label        _upgradeLabel = null!;  // Story 4.11: summed per-stat completed-research line
        private StyleBoxFlat _fillStyle = null!;

        // Story 4.11: the mid-match-mutable per-faction research substrate — read-only here (never written).
        // Null until wired (SetResearchStore) → the upgrade line stays omitted (matches "no completed research"
        // I/O-matrix row: omit the line, not a zero).
        private ResearchStore? _researchStore;

        // "N selected" label (multi-select)
        private Label _multiLabel = null!;

        // ── Init ──────────────────────────────────────────────────────────────────

        // Story 3.15: item stores for the minimal pickup/use affordance (right-click a ground item → PickupItem;
        // a hotkey → UseItem on a slot). Optional — null in headless / online-without-items paths.
        private ItemStore?  _items;
        private ItemSystem? _itemSys;

        // Story 3.16: the inventory grid slot the HUD has selected for a per-slot Use/Drop (retires the 3.15 hard-coded
        // slot 0); -1 = none selected → the T hotkey falls back to slot 0. Set by CommandCardSystem's inventory grid.
        private int _selectedInventorySlot = -1;
        // Story 3.16: a hero mid-issuing a pickup that must be EXCLUDED from the rest-of-selection move/attack fall-through
        // (so a mixed selection right-clicking a ground item no longer strands the non-pickup units — the 3.15 defer). -1 = none.
        private int _pickupHeroExclusion = -1;

        public void Initialize(RtsCameraController camCtrl, EntityWorld world,
                              FlowFieldBridge? pathSystem = null,
                              BuildingStore? buildingStore = null,
                              BuildingSystem? buildSys = null,
                              CombatEventQueue? combatEvents = null,
                              ItemStore? items = null, ItemSystem? itemSys = null)
        {
            _camCtrl       = camCtrl;
            _world         = world;
            _pathSystem    = pathSystem;
            _buildingStore = buildingStore;
            _buildSys      = buildSys;      // Story 2.12: offline SetRally apply site
            _combatEvents  = combatEvents;  // Story 2.12: OrderDenied feedback bus (optional)
            _items         = items;         // Story 3.15
            _itemSys       = itemSys;       // Story 3.15
        }

        /// <summary>Story 3.15: the packed <see cref="ItemStore"/> ref of the nearest ON-GROUND item within
        /// <paramref name="radius"/> of world point <paramref name="hit"/>, or -1 (ascending-slot tie-break). Godot-side
        /// pick helper for the pickup affordance.</summary>
        private int FindNearestGroundItem(Vector3 hit, float radius)
        {
            if (_items == null) return -1;
            int best = -1;
            float bestSq = radius * radius;
            for (int s = 0; s < _items.Count; s++)
            {
                if (!_items.Alive[s] || _items.Held[s]) continue;
                float dx = _items.PosX[s].ToFloat() - hit.X;
                float dz = _items.PosZ[s].ToFloat() - hit.Z;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; best = _items.PackRef(s); }
            }
            return best;
        }

        /// <summary>Story 3.15: the first selected entity that is a hero (has a live HeroStore link), or -1.</summary>
        private int FirstSelectedHero()
        {
            for (int i = 0; i < _selectedList.Count; i++)
            {
                int id = _selectedList[i];
                if (_world.IsAlive(id) && _world.HeroIndex[id] != EntityWorld.HERO_NONE) return id;
            }
            return -1;
        }

        /// <summary>Story 3.15: issue a PickupItem order (routed through the shared applier — online via lockstep,
        /// offline applied now). The target item's packed ref rides TargetX as a RAW int.</summary>
        private void IssuePickupCommand(int heroEntity, int itemRef)
        {
            if (_lockstep?.EnqueueOrder(heroEntity, UnitCommand.PickupItem, Fixed.FromRaw(itemRef), Fixed.Zero) ?? true)
            {
                var order = new UnitOrder(heroEntity, UnitCommand.PickupItem, Fixed.FromRaw(itemRef), Fixed.Zero);
                OrderApplier.Apply(_world, in order, _world.FactionOf[heroEntity], events: _combatEvents, items: _itemSys);
            }
        }

        /// <summary>Story 3.15/3.16: issue a UseItem order for an inventory slot (shared applier; offline passes the item
        /// system so the consumable fires). The slot rides TargetX as a RAW int. Public so the HUD inventory grid can drive
        /// a per-slot Use on the exact selected slot.</summary>
        public void IssueUseItemCommand(int heroEntity, int slot)
        {
            if (_lockstep?.EnqueueOrder(heroEntity, UnitCommand.UseItem, Fixed.FromRaw(slot), Fixed.Zero) ?? true)
            {
                var order = new UnitOrder(heroEntity, UnitCommand.UseItem, Fixed.FromRaw(slot), Fixed.Zero);
                OrderApplier.Apply(_world, in order, _world.FactionOf[heroEntity], events: _combatEvents, items: _itemSys);
            }
        }

        /// <summary>Story 3.16: issue a DropItem order for an inventory slot (mirrors <see cref="IssueUseItemCommand"/>).
        /// The slot rides TargetX as a RAW int. Public so the HUD inventory grid can drive a per-slot Drop.</summary>
        public void IssueDropItemCommand(int heroEntity, int slot)
        {
            if (_lockstep?.EnqueueOrder(heroEntity, UnitCommand.DropItem, Fixed.FromRaw(slot), Fixed.Zero) ?? true)
            {
                var order = new UnitOrder(heroEntity, UnitCommand.DropItem, Fixed.FromRaw(slot), Fixed.Zero);
                OrderApplier.Apply(_world, in order, _world.FactionOf[heroEntity], events: _combatEvents, items: _itemSys);
            }
        }

        /// <summary>Story 3.16: the HUD inventory grid sets which slot a subsequent per-slot Use (T / grid button) targets.</summary>
        public void SetSelectedInventorySlot(int slot) => _selectedInventorySlot = slot;

        /// <summary>
        /// Inject the lockstep manager for online play. Pass null to revert to offline mode.
        /// </summary>
        public void SetLockstep(LockstepManager? lockstep) => _lockstep = lockstep;

        /// <summary>Story 4.11: inject the mid-match-mutable research substrate so the focus unit's floating panel
        /// can show its faction's aggregate completed-research upgrade contribution. A setter (like
        /// <see cref="SetLockstep"/>); wired by CameraPhase off the host. Null until wired → the upgrade line
        /// stays omitted (never shown as a zero).</summary>
        public void SetResearchStore(ResearchStore? research) => _researchStore = research;

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

        /// <summary>
        /// Issue a Shift-queued (append) order (Story 2.12, AC1.2). Sets the wire's <see cref="UnitOrderFlags.Queued"/>
        /// high bit on the command byte so <c>OrderApplier</c> APPENDS it to the entity's ring instead of touching
        /// CommandState — routed through the SAME shared applier online (deferred via lockstep) and offline (applied
        /// now), so live == replay == offline. <paramref name="tx"/>/<paramref name="tz"/> are the wire targets already
        /// in <see cref="Fixed"/> (a ground point via FromFloat at the issue seam, or a packed id via FromRaw). The
        /// queue itself lives in the sim (EntityWorld ring) — SelectionSystem only ships an individual flagged order.
        /// </summary>
        private void IssueQueuedOrder(int unitId, UnitCommand cmd, Fixed tx, Fixed tz)
        {
            var wireCmd = (UnitCommand)((byte)cmd | UnitOrderFlags.Queued);
            // Online: EnqueueOrder returns false (deferred to Flush). Offline (_lockstep == null): the ?? true applies now.
            if (_lockstep?.EnqueueOrder(unitId, wireCmd, tx, tz) ?? true)
            {
                var order = new UnitOrder(unitId, wireCmd, tx, tz);
                OrderApplier.Apply(_world, in order, _world.FactionOf[unitId], events: _combatEvents);
            }
        }

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
                    // Attack-move pending: consume this click as the command destination. Story 2.12: a Shift-held
                    // click QUEUES the attack-move (append) instead of replacing; Shift also keeps the arm live so
                    // Q, click, shift-click, shift-click… chains attack-move waypoints (mirrors the patrol-arm idiom).
                    if (_awaitingAttackMoveClick)
                    {
                        IssueAttackMoveCommand(lmb.Position, queued: lmb.ShiftPressed);
                        if (!lmb.ShiftPressed) _awaitingAttackMoveClick = false;
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
                            int friendlyId = FindNearestUnit(fHit, PICK_RADIUS); // local-faction-only = friendly
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
                                IssueCastAbilityCommand(_pendingCastCasterId, _pendingCastSlot, targetId, queued: lmb.ShiftPressed);
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
                    // Story 2.12: Shift+RMB QUEUES the order (append to the ring) instead of replacing — the WC3
                    // waypoint-chain gesture. Distinct from Shift+LMB-Patrol-append (the P-armed path below).
                    bool queued = rmb.ShiftPressed;
                    if (RaycastGround(rmb.Position, out Vector3 hit))
                    {
                        // Story 3.15: right-click a ground item with a hero selected → PickupItem for the nearest hero.
                        // Story 3.16: the REST of a mixed selection is NOT stranded — it falls through to the normal
                        // move/attack path with the pickup hero EXCLUDED (closes the 3.15 "army stops near item" defer).
                        int heroForPickup = ResolveActingHero(); // review #4: prefer the focus hero (card-consistent)
                        int groundItem = heroForPickup >= 0 ? FindNearestGroundItem(hit, PICK_RADIUS) : -1;
                        if (groundItem >= 0) IssuePickupCommand(heroForPickup, groundItem);

                        // Exclude the pickup hero from the remainder's move/attack (its PickupItem order must survive).
                        _pickupHeroExclusion = groundItem >= 0 ? heroForPickup : -1;
                        int enemyId = FindNearestEnemyUnit(hit, PICK_RADIUS);
                        int enemyBuildingId = enemyId < 0 ? FindNearestEnemyBuilding(hit, BUILDING_PICK_RADIUS) : -1;
                        if (enemyId >= 0)              IssueAttackTargetCommand(enemyId, queued);
                        else if (enemyBuildingId >= 0) IssueAttackBuildingCommand(enemyBuildingId, queued);
                        else                           IssueMoveCommand(rmb.Position, queued);
                        _pickupHeroExclusion = -1;
                    }
                    else IssueMoveCommand(rmb.Position, queued);
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
                else if (key.Keycode == Key.T && _selectedSet.Count > 0 && _itemSys != null)
                {
                    // Story 3.16: use-consumable affordance — use the item in the HUD-selected inventory slot (retires the
                    // 3.15 hard-coded slot 0). When no grid slot is selected, fall back to slot 0. Empty/non-consumable ⇒ no-op.
                    ResetPendingCommandClicks();
                    int hero = ResolveActingHero(); // review #4: act on the FOCUS hero (matches the focus-following card)
                    if (hero >= 0) IssueUseItemCommand(hero, _selectedInventorySlot >= 0 ? _selectedInventorySlot : 0);
                }
                else if (key.Keycode == Key.Escape)
                {
                    ResetPendingCommandClicks();
                    ClearSelection();
                }
                else if (key.Keycode == Key.Tab && _subgroups.Count >= 2)
                {
                    // Story 11.5 (FR-74): Tab cycles the active WC3 subgroup (repoints _focusId so the command card /
                    // HP bar follow), non-destructively — the multi-selection is unchanged and no order is issued.
                    // Consume ONLY when there are 2+ subgroups; a single/zero-subgroup Tab passes through.
                    CycleSubgroup(+1);
                    GetViewport().SetInputAsHandled();
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
                RebuildSubgroups(resetActive: true); // Story 11.5: single-unit selection → one subgroup
                return;
            }

            // 2. Fall through to building if no unit nearby
            if (_buildingStore != null)
            {
                int bId = FindNearestBuilding(hit, BUILDING_PICK_RADIUS);
                if (bId >= 0)
                {
                    // Story 2.13 review (patch): stamp the current generation so a later recycle of this slot
                    // invalidates the selection (see SelectedBuildingId). ClearSelection already reset both fields.
                    _selectedBuildingSlot = bId;
                    _selectedBuildingGen  = _buildingStore.Generation[bId];
                }
            }
        }

        private void FinalizeBoxSelect()
        {
            var camera = _camCtrl?.GetCamera();
            if (camera == null) return;

            Rect2 screenRect = MakeRect(_dragStart, _dragCurrent);
            ClearSelection(); // also resets ActiveGroupIndex

            Faction me = _localFaction(); // hoist the local-faction read out of the per-entity loop (invariant here)
            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                if (_world.FactionOf[i] != me) continue; // only select own units

                var sim = _world.Position[i];
                var world3d = new Vector3(sim.X.ToFloat(), 0.8f, sim.Z.ToFloat());

                if (camera.IsPositionBehind(world3d)) continue;

                Vector2 screen = camera.UnprojectPosition(world3d);
                if (screenRect.HasPoint(screen))
                    AddToSelection(i, setFocus: _focusId < 0);
            }
            RebuildSubgroups(resetActive: true); // Story 11.5: a fresh box-select resets the active subgroup
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
            _selectedBuildingSlot = -1;  // clears SelectedBuildingId (generation-validated computed getter)
            _selectedBuildingGen  = -1;
            // Story 3.16 review: reset the HUD-selected inventory slot on every selection change, so slot 3 chosen for
            // hero A never leaks into hero B (whose T-hotkey would otherwise Use its own slot 3). -1 = none → falls back to 0.
            _selectedInventorySlot = -1;
            // Story 11.5: subgroups are a view of the selection — clear them and the active-subgroup pointer.
            _subgroups.Clear();
            _activeSubgroupIndex = -1;
            _barRoot.Visible    = false;
            _multiLabel.Visible = false;
        }

        // ── Move command ──────────────────────────────────────────────────────────

        /// <summary>Spacing (world units) between adjacent units' destinations in a formation (Story 1.13).</summary>
        private static readonly Fixed FORMATION_SPACING = Fixed.FromInt(2);

        // ── Story 11.4 (FR-74): issue-time order acknowledgment ─────────────────────────────

        /// <summary>Fire the issue-time acknowledgment: an ack sound (the primary selected unit's authored
        /// <c>AckSoundId</c>, else the default) + a short-lived order-confirmed ground marker at
        /// <paramref name="worldTarget"/>. No-op when nothing is selected (no selection → no ack). Presentation-only —
        /// fired on the input frame while the deterministic order still executes ticks later.
        ///
        /// <para>Review fix: when <paramref name="queued"/>, skip the ack unless at least one selected unit can still
        /// take an order. A Shift-queued order onto units whose rings are all full is rejected by the shared guard,
        /// which now raises a <c>QueueFull</c> denial toast + sound — acking as well produced the confirmation AND the
        /// refusal for one click. The precheck reads the same FOLDED <c>OrderQueueCount</c> the guard rejects on, so
        /// the two cannot disagree. Issue-time optimism is retained everywhere else (that IS the GDD §6 design); this
        /// only covers the one guard that rejects synchronously at issue time.</para></summary>
        private void ConfirmOrder(Vector3 worldTarget, bool queued = false)
        {
            string? ack = null;
            bool any = false;
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id)) continue;
                if (queued && _world.OrderQueueCount[id] >= EntityWorld.MAX_ORDER_QUEUE) continue; // ring full → this unit refuses
                any = true;
                ack = _world.FeedbackProfile[id]?.AckSoundId; // primary accepting unit's per-unit ack sound
                break;
            }
            if (!any) return;
            _audioMgr?.PlayOrderAck(ack);
            _orderMarkers?.Spawn(worldTarget, OrderMarkerTint);
        }

        /// <summary>World position of the first alive selected unit (marker anchor for a stationary order), or the
        /// origin if the selection is empty.</summary>
        private Vector3 PrimarySelectionPos()
        {
            foreach (int id in _selectedList)
                if (_world.IsAlive(id)) return _world.Position[id].ToGodotVector3();
            return Vector3.Zero;
        }

        private void IssueMoveCommand(Vector2 screenPos, bool queued = false)
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

                // Story 2.12: a Shift-queued Move APPENDS to the ring through the shared applier (no flow-field path
                // request — a popped queued move direct-steers via MoveTarget, since OrderQueueSystem is sim-side and
                // cannot call a presentation path hook). A plain Move replaces: clear the ring, then apply as before.
                if (queued)
                {
                    IssueQueuedOrder(id, UnitCommand.Move, Fixed.FromFloat(dest.X), Fixed.FromFloat(dest.Z));
                    continue;
                }

                if (!EnqueueCommand(id, UnitCommand.Move, dest)) continue; // online plain: deferred to Flush (OrderApplier clears the ring there)
                _world.OrderQueueCount[id] = 0; // offline plain = replace: this direct-write path bypasses OrderApplier, so clear the ring here

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
            ConfirmOrder(target, queued); // Story 11.4: issue-time ack + ground marker at the move target
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
                if (_world.IsAlive(id) && id != _pickupHeroExclusion) idList.Add(id); // Story 3.16: a pickup hero is excluded
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
                _world.OrderQueueCount[id] = 0; // offline plain = replace: this direct-write path bypasses OrderApplier, so clear the ring here (AC1.2, review R2)

                _world.Flags[id]        = _world.Flags[id] & ~(EntityFlags.Moving | EntityFlags.Attacking);
                _world.AttackTarget[id] = -1;
                _world.CommandState[id] = UnitCommand.Stop;
                _pathSystem?.CancelPath(id);
            }
            ConfirmOrder(PrimarySelectionPos()); // Story 11.4: issue-time ack (marker at the selection)
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
                _world.OrderQueueCount[id] = 0; // offline plain = replace: this direct-write path bypasses OrderApplier, so clear the ring here (AC1.2, review R2)

                _world.Flags[id]        = _world.Flags[id] & ~(EntityFlags.Moving | EntityFlags.Attacking);
                _world.AttackTarget[id] = -1;
                _world.CommandState[id] = UnitCommand.HoldPosition;
                _pathSystem?.CancelPath(id);
            }
            ConfirmOrder(PrimarySelectionPos()); // Story 11.4: issue-time ack (marker at the selection)
            GD.Print($"[Selection] Hold Position issued to {_selectedList.Count} unit(s).");
        }

        /// <summary>
        /// Attack Move: units navigate to the click destination, engaging enemies they encounter.
        /// After each kill they resume toward the destination.
        /// </summary>
        private void IssueAttackMoveCommand(Vector2 screenPos, bool queued = false)
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

                // Story 2.12: Shift-queued attack-move appends (shared applier, no path request); plain replaces (clear ring).
                if (queued)
                {
                    IssueQueuedOrder(id, UnitCommand.AttackMove, Fixed.FromFloat(dest.X), Fixed.FromFloat(dest.Z));
                    continue;
                }

                if (!EnqueueCommand(id, UnitCommand.AttackMove, dest)) continue; // online plain: deferred to Flush
                _world.OrderQueueCount[id] = 0; // offline plain = replace: this direct-write path bypasses OrderApplier

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
            ConfirmOrder(target, queued); // Story 11.4: issue-time ack + ground marker at the attack-move target
            GD.Print($"[Selection] Attack-Move issued to {ids.Length} unit(s).");
        }

        /// <summary>
        /// Single-target Attack (Story 1.12): force every selected unit to attack ONE specific enemy, chasing
        /// only it and ignoring nearer enemies. Issued by right-clicking an enemy unit.
        /// </summary>
        private void IssueAttackTargetCommand(int enemyId, bool queued = false)
        {
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id) || id == _pickupHeroExclusion) continue; // Story 3.16: skip a pickup hero
                // Story 2.12: a Shift-queued attack appends to the ring (the enemy id packs into TargetX as a raw int).
                if (queued)
                {
                    IssueQueuedOrder(id, UnitCommand.AttackTarget, Fixed.FromRaw(enemyId), Fixed.Zero);
                    continue;
                }
                if (!EnqueueTargetedCommand(id, UnitCommand.AttackTarget, enemyId)) continue; // online plain: queued
                // Offline plain: apply through the SAME shared OrderApplier the lockstep/replay paths use (Review,
                // Story 1.12) — never a hand-rolled copy that could silently drift. The applier clears the ring (replace).
                var atkOrder = new UnitOrder(id, UnitCommand.AttackTarget, Fixed.FromRaw(enemyId), Fixed.Zero);
                OrderApplier.Apply(_world, in atkOrder, _world.FactionOf[id]);
            }
            if (_world.IsAlive(enemyId)) ConfirmOrder(_world.Position[enemyId].ToGodotVector3(), queued); // Story 11.4: ack + marker at the target
            GD.Print($"[Selection] Attack issued on enemy {enemyId} to {_selectedList.Count} unit(s).");
        }

        /// <summary>
        /// Force every selected COMBAT unit to attack ONE specific enemy building (Story 2.9a): chase its centre point
        /// and raze it. Mirrors <see cref="IssueAttackTargetCommand"/> but (a) uses <see cref="UnitCommand.AttackBuilding"/>
        /// with the BUILDING id, and (b) issues ONLY to combat units (<c>EffectiveAttackDamage &gt; 0</c>) so workers /
        /// non-combatants — which CombatSystem skips and thus never self-revert — aren't left in a dangling AttackBuilding
        /// state. Issued by right-clicking an enemy building. Presentation issues an INTENT only; the tick validates it.
        /// </summary>
        private void IssueAttackBuildingCommand(int buildingId, bool queued = false)
        {
            // Story 2.13 (AC3.4): carry a PACKED, generation-stamped building ref so a since-recycled slot fails
            // TryResolveRef in the tick (clean revert) instead of ABA-retargeting the new building — AND so the player
            // CAN still target a legitimately-recycled (generation ≥ 1) base. Golden-neutral at generation 0 (packed
            // == id). _buildingStore is non-null whenever FindNearestEnemyBuilding returned a valid id.
            int packedRef = _buildingStore != null ? _buildingStore.PackRef(buildingId) : buildingId;
            foreach (int id in _selectedList)
            {
                if (!_world.IsAlive(id) || id == _pickupHeroExclusion) continue; // Story 3.16: skip a pickup hero
                if (_world.EffectiveAttackDamage[id] <= Fixed.Zero) continue; // combat units only
                // Story 2.12: a Shift-queued anti-building attack appends (the packed ref rides TargetX as a raw int).
                if (queued)
                {
                    IssueQueuedOrder(id, UnitCommand.AttackBuilding, Fixed.FromRaw(packedRef), Fixed.Zero);
                    continue;
                }
                if (!EnqueueTargetedCommand(id, UnitCommand.AttackBuilding, packedRef)) continue; // online plain: queued
                // Offline plain: apply through the SAME shared OrderApplier — identical to AttackTarget (clears the ring).
                var atkOrder = new UnitOrder(id, UnitCommand.AttackBuilding, Fixed.FromRaw(packedRef), Fixed.Zero);
                OrderApplier.Apply(_world, in atkOrder, _world.FactionOf[id]);
            }
            if (_buildingStore != null && buildingId >= 0 && buildingId < _buildingStore.Count) // Story 11.4: ack + marker at the target building
                ConfirmOrder(_buildingStore.Position[buildingId].ToGodotVector3(), queued);
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
            ConfirmOrder(target); // Story 11.4: issue-time ack + ground marker at the patrol waypoint
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
            if (_world.IsAlive(friendlyId)) ConfirmOrder(_world.Position[friendlyId].ToGodotVector3()); // Story 11.4: ack + marker at the escorted unit
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
        public void IssueCastAbilityCommand(int casterId, int slot, int targetEntityId, bool queued = false)
        {
            // Story 2.4b review: re-validate the caster at the ISSUE seam, not just at arm time. The pending caster id
            // (ArmCastTargeting) persists across frames and is NOT pruned like _selectedList, so the armed caster can
            // die and its slot recycle to a different unit before the target-click. Refuse unless the caster is still
            // alive AND locally owned (the local faction — the selection convention at :387/:732), so a recycled enemy slot can
            // never be made to cast (offline this also re-seats expectedFaction to the local player, closing the
            // self-comparison hole in OrderApplier's anti-cheat guard).
            if (!_world.IsAlive(casterId) || _world.FactionOf[casterId] != _localFaction()) return;
            // Story 2.12 (review R3): a Shift-held cast APPENDS to the order ring (queued flag on the wire byte) instead
            // of replacing — OrderApplier masks 0x80 off before the CastAbility case, and a popped cast dispatches through
            // the shared ApplyActiveOrder core. A plain (non-Shift) cast is unflagged → clears the ring + casts now.
            var wireCmd = queued ? (UnitCommand)((byte)UnitCommand.CastAbility | UnitOrderFlags.Queued)
                                 : UnitCommand.CastAbility;
            // Online: EnqueueOrder returns false (queued). Offline (_lockstep == null): the ?? true yields apply-now.
            bool applyNow = _lockstep?.EnqueueOrder(casterId, wireCmd,
                                                    Fixed.FromRaw(slot), Fixed.FromRaw(targetEntityId)) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush will apply it later
            var order = new UnitOrder(casterId, wireCmd, Fixed.FromRaw(slot), Fixed.FromRaw(targetEntityId));
            OrderApplier.Apply(_world, in order, _world.FactionOf[casterId]);
        }

        /// <summary>
        /// Set the rally point for a building to the world position the player right-clicked (Story 2.12, AC3).
        /// Newly trained units from this building will walk to this point on spawn. Now rides the UnitOrder wire
        /// (<see cref="UnitCommand.SetRally"/>) through the SAME shared <see cref="OrderApplier"/> the lockstep/replay
        /// paths use — so the rally change is lockstep-replicated, replayable, and folded (v9), NOT a direct store
        /// write (the pre-2.12 desync path). One issue-side <see cref="Fixed.FromFloat"/> quantization of the raycast
        /// hit (like <c>IssueMoveCommand</c>): the local peer resolves its screen-ray, ships the Fixed raw, and every
        /// peer applies the identical value. UnitId = buildingId (handled before the entity guard, like Train).
        /// </summary>
        private void SetRallyPoint(int buildingId, Vector2 screenPos)
        {
            if (!RaycastGround(screenPos, out Vector3 hit)) return;

            var tx = Fixed.FromFloat(hit.X);
            var tz = Fixed.FromFloat(hit.Z);
            // Online: EnqueueOrder defers to Flush (LockstepManager applies with its own BuildSys). Offline
            // (_lockstep == null): apply now through the shared OrderApplier, wiring THIS scene's BuildSys so the
            // deterministic store write (BuildingSystem.SetRallyCommand) runs. A null BuildSys → deterministic no-op.
            if (_lockstep?.EnqueueOrder(buildingId, UnitCommand.SetRally, tx, tz) ?? true)
            {
                var order = new UnitOrder(buildingId, UnitCommand.SetRally, tx, tz);
                OrderApplier.Apply(_world, in order, _localFaction(), buildings: _buildSys);
            }

            _audioMgr?.PlayOrderAck(null);              // Story 11.4: rally is a building order (no unit selection) → default ack
            _orderMarkers?.Spawn(hit, OrderMarkerTint); // + order-confirmed marker at the rally point
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
            Faction me       = _localFaction(); // hoist out of the per-entity loop (invariant here)

            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                if (_world.FactionOf[i] != me) continue; // only select own units
                var pos = _world.Position[i];
                float dx = pos.X.ToFloat() - worldHit.X;
                float dz = pos.Z.ToFloat() - worldHit.Z;
                float sqDist = dx * dx + dz * dz;
                if (sqDist < bestSqDist) { bestSqDist = sqDist; bestId = i; }
            }
            return bestId;
        }

        /// <summary>
        /// Nearest ENEMY unit to the world hit within radius (Story 1.12). Enemy = alive and neither the local
        /// faction nor Neutral. Mirror of <see cref="FindNearestUnit"/>, which finds local-faction-only.
        /// </summary>
        private int FindNearestEnemyUnit(Vector3 worldHit, float radius)
        {
            int   bestId     = -1;
            float bestSqDist = radius * radius;
            int   cap        = _world.HighWaterMark;
            Faction me       = _localFaction(); // hoist out of the per-entity loop (invariant here)

            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                Faction f = _world.FactionOf[i];
                if (f == me || f == Faction.Neutral) continue; // enemy = not me, not neutral
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
        /// player's faction. Clone of <see cref="FindNearestBuilding"/> (faction-agnostic) with a
        /// local-faction exclusion only — Neutral buildings stay targetable when explicitly ordered (AC2.5), so we
        /// do NOT copy <see cref="FindNearestEnemyUnit"/>'s Neutral skip.
        /// </summary>
        private int FindNearestEnemyBuilding(Vector3 worldHit, float radius)
        {
            if (_buildingStore == null) return -1;

            int   bestId     = -1;
            float bestSqDist = radius * radius;
            Faction me       = _localFaction(); // hoist out of the per-entity loop (invariant here)

            for (int i = 0; i < _buildingStore.Count; i++)
            {
                if (!_buildingStore.Alive[i]) continue;
                if (_buildingStore.FactionOf[i] == me) continue; // exclude ONLY the local player's buildings
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

            int before = _selectedList.Count;
            _selectedList.RemoveAll(id => !_world.IsAlive(id));
            _selectedSet.RemoveWhere(id => !_world.IsAlive(id));

            // Story 11.5: a death is NOT a fresh selection — rebuild the subgroup structure but PRESERVE/clamp the
            // active-subgroup index rather than resetting it to 0. Only when membership actually shrank.
            if (_selectedList.Count != before)
                RebuildSubgroups(resetActive: false);

            if (_focusId >= 0 && !_world.IsAlive(_focusId))
            {
                // Reassign focus to the (clamped) active subgroup's first live member, else the first selected unit.
                int reassigned = FirstActiveSubgroupMember();
                _focusId = reassigned >= 0 ? reassigned
                         : _selectedList.Count > 0 ? _selectedList[0] : -1;
            }
        }

        // ── Subgroup helpers (Story 11.5) ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Recompute the WC3 subgroups from the current (already-pruned) selection via the Godot-free
        /// <see cref="SelectionSubgroups.Group"/>. <paramref name="resetActive"/> = true for a genuine selection change
        /// (active subgroup resets to the first); false for a death (preserve the index, clamped into range).
        /// Presentation-only — never mutates <c>_selectedList</c> / <c>_selectedSet</c> or enqueues an order.
        /// </summary>
        private void RebuildSubgroups(bool resetActive)
        {
            // Capture the OLD active subgroup's key so a non-reset rebuild can preserve it by identity even when an
            // earlier subgroup was wiped and the indices shifted (Story 11.5 review #3).
            bool hadActive = _activeSubgroupIndex >= 0 && _activeSubgroupIndex < _subgroups.Count;
            long prevKey   = hadActive ? _subgroups[_activeSubgroupIndex].Key : 0L;
            int  prevIndex = _activeSubgroupIndex;

            _subgroups.Clear();
            if (_selectedList.Count > 0)
            {
                var keys = new List<long>(_selectedList.Count);
                for (int i = 0; i < _selectedList.Count; i++)
                    keys.Add(SubgroupKeyOf(_selectedList[i]));
                foreach (var g in SelectionSubgroups.Group(_selectedList, keys))
                    _subgroups.Add(g);
            }

            if (_subgroups.Count == 0) { _activeSubgroupIndex = -1; return; }
            if (resetActive || !hadActive)
                _activeSubgroupIndex = 0;
            else
                // Preserve the active subgroup by KEY (falls back to a clamp when that subgroup itself emptied).
                _activeSubgroupIndex = SelectionSubgroups.ReconcileActiveIndex(_subgroups, prevKey, prevIndex);
        }

        /// <summary>The subgroup grouping key for an entity: a stable per-<see cref="UnitDefinition"/> key (positive),
        /// falling back to a per-<c>CategoryOf</c> key (negative, so it never collides with a def key) when the unit has
        /// no source definition. A dead id gets a sentinel (it is pruned before this runs).</summary>
        private long SubgroupKeyOf(int id)
        {
            if (!_world.IsAlive(id)) return long.MinValue;
            UnitDefinition def = _world.SourceDefinition[id];
            if (def != null)
            {
                if (!_defSubgroupKeys.TryGetValue(def, out long k))
                {
                    k = _nextDefSubgroupKey++;
                    _defSubgroupKeys[def] = k;
                }
                return k;
            }
            return -1L - (long)_world.CategoryOf[id]; // fallback key space (negative)
        }

        /// <summary>The active subgroup's first live member id, or -1 (no/empty active subgroup).</summary>
        private int FirstActiveSubgroupMember()
        {
            if (_activeSubgroupIndex < 0 || _activeSubgroupIndex >= _subgroups.Count) return -1;
            var members = _subgroups[_activeSubgroupIndex].Members;
            for (int i = 0; i < members.Count; i++)
                if (_world.IsAlive(members[i])) return members[i];
            return -1;
        }

        /// <summary>
        /// Story 11.5: advance the active subgroup by <paramref name="dir"/> (wraps), repointing <c>_focusId</c> to the
        /// new subgroup's first member. No-op with fewer than 2 subgroups. Does NOT narrow/mutate the selection or issue
        /// an order — WC3 "make this subgroup active" semantics.
        /// </summary>
        public void CycleSubgroup(int dir)
        {
            if (_subgroups.Count < 2) return;
            int count = _subgroups.Count;
            int next = ((_activeSubgroupIndex + dir) % count + count) % count;
            SetActiveSubgroup(next);
        }

        /// <summary>
        /// Story 11.5: make subgroup <paramref name="index"/> active and repoint <c>_focusId</c> to its first live
        /// member (the command card, ability/worker/inventory cards, and HP bar all follow focus). Bounds-guarded;
        /// selection membership is untouched. Wired to <see cref="Components.ChimeraTabs"/>' <c>TabChanged</c> by the panel.
        /// </summary>
        public void SetActiveSubgroup(int index)
        {
            if (index < 0 || index >= _subgroups.Count) return;
            _activeSubgroupIndex = index;
            int member = FirstActiveSubgroupMember();
            if (member >= 0) _focusId = member;
            // Review #4: the inventory-slot selection is per-hero; moving focus to a different hero must not let a slot
            // chosen for the old hero leak into the new one (mirror ClearSelection's reset). -1 = none → falls back to 0.
            _selectedInventorySlot = -1;
        }

        /// <summary>Story 11.5 review #4: the hero the focus-following inventory card acts on — the FOCUS unit when it
        /// is a hero (so the acting hero matches the card the player sees), else the first selected hero. Keeps the T /
        /// pickup action from diverging from the card after a subgroup Tab.</summary>
        private int ResolveActingHero()
        {
            if (_focusId >= 0 && _world.IsAlive(_focusId) && _world.HeroIndex[_focusId] != EntityWorld.HERO_NONE)
                return _focusId;
            return FirstSelectedHero();
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
            RebuildSubgroups(resetActive: true); // Story 11.5: recalling a control group is a fresh selection
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

            string upgradeText = BuildResearchUpgradeSummary(_focusId);
            _upgradeLabel.Visible = upgradeText.Length > 0;
            if (_upgradeLabel.Visible) _upgradeLabel.Text = upgradeText;
        }

        /// <summary>
        /// Story 4.11: the focus unit's faction's aggregate completed-research upgrade contribution, e.g. "+2 Atk"
        /// (summed across every research index where <c>CompletedLevels[faction][i] &gt; 0</c> — the SAME cumulative
        /// per-stat totals <see cref="ProjectChimera.Economy.ResearchSystem"/> already maintains in
        /// <see cref="ResearchStore"/>, never re-derived from <c>ModifierStore</c>). Empty string when the faction
        /// has zero completed research OR every non-zero sum happens to net to zero — the caller omits the line
        /// entirely rather than showing "+0" (I/O-matrix: "omit the line, not a zero").
        /// </summary>
        private string BuildResearchUpgradeSummary(int id)
        {
            if (_researchStore == null) return "";
            int f = (int)_world.FactionOf[id];
            if (f < 0 || f >= _researchStore.CompletedLevels.Length) return "";

            int[] completed = _researchStore.CompletedLevels[f];
            Fixed maxHealthDelta    = Fixed.Zero;
            Fixed attackDamageDelta = Fixed.Zero;
            Fixed armorDelta        = Fixed.Zero;
            Fixed moveSpeedDelta    = Fixed.Zero;
            for (int i = 0; i < completed.Length; i++)
            {
                if (completed[i] <= 0) continue; // never completed a level of this research — no contribution
                maxHealthDelta    += _researchStore.CumulativeMaxHealthDelta[f][i];
                attackDamageDelta += _researchStore.CumulativeAttackDamageDelta[f][i];
                armorDelta        += _researchStore.CumulativeArmorDelta[f][i];
                moveSpeedDelta    += _researchStore.CumulativeMoveSpeedDelta[f][i];
            }

            var parts = new List<string>(4);
            AppendUpgradePart(parts, attackDamageDelta, "Atk");
            AppendUpgradePart(parts, maxHealthDelta, "HP");
            AppendUpgradePart(parts, armorDelta, "Armor");
            AppendUpgradePart(parts, moveSpeedDelta, "Spd");
            return parts.Count == 0 ? "" : string.Join("  ", parts);
        }

        /// <summary>Append "{+/-}{value} {suffix}" for a non-zero delta — display-side ToFloat() math (the no-float
        /// rule applies to src/Core/src/Effects only, exactly like <see cref="UpdateHealthBar"/>'s own ratio math).</summary>
        private static void AppendUpgradePart(List<string> parts, Fixed delta, string suffix)
        {
            if (delta == Fixed.Zero) return;
            float v = delta.ToFloat();
            // Review-pass fix: a nonzero Fixed delta can still round to "0" at the ":0.#" display precision (e.g.
            // 0.03) — checking the ROUNDED value, not the exact Fixed, keeps the "omit the line, not a zero" rule
            // from being violated per-stat, not just for the whole line.
            float rounded = Mathf.Round(v * 10f) / 10f;
            if (rounded == 0f) return;
            parts.Add($"{(rounded > 0f ? "+" : "")}{rounded:0.#} {suffix}");
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
            // Story 4.11: +14px so the optional upgrade line (below _barLabel) has room without resizing at runtime.
            _barRoot.Size    = new Vector2(BAR_W + 80f, BAR_H + 18f + 14f);
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

            // Story 4.11: the aggregate completed-research upgrade line (e.g. "+2 Atk"), directly below the HP
            // readout. Omitted (Visible = false) whenever the focus unit's faction has zero completed research —
            // never shown as a zero (I/O-matrix: "No upgrade line shown").
            _upgradeLabel          = new Label();
            _upgradeLabel.Position = new Vector2(0f, BAR_H + 16f);
            _upgradeLabel.Size     = new Vector2(BAR_W + 80f, 14f);
            _upgradeLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.90f, 1.00f));
            _upgradeLabel.AddThemeFontSizeOverride("font_size", 11);
            _upgradeLabel.Visible  = false;
            _barRoot.AddChild(_upgradeLabel);

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
