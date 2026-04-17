#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Navigation;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presentation-layer pathfinding bridge using flow fields.
    /// Drop-in replacement for PathRequestSystem.
    ///
    /// Differences from PathRequestSystem:
    ///   - No NavigationServer3D calls — pure C# BFS, fully deterministic across machines.
    ///   - Multiple units moving to the same goal share one cached flow field (one BFS for N units).
    ///   - Removes the NavServer non-determinism that accumulates lockstep desync in online play.
    ///
    /// Each frame (_Process):
    ///   1. Poll BuildingStore for Alive[] changes; rebuild obstacle map if any building changed.
    ///   2. For each unit with an active field: sample direction at current position.
    ///   3. Set MoveTarget = position + direction * LOOK_AHEAD, ensure Moving flag is set.
    ///   4. When direction is Zero (unit is in the goal cell), steer directly toward exact goal.
    ///   5. When within arrival radius of goal, transition to Stop (or leave AttackMove for
    ///      CombatSystem.ResumeAttackMove to handle — same as PathRequestSystem contract).
    ///
    /// Wiring (MainScene):
    ///   - Call Initialize(world, flowFieldSystem, buildings) after scenario load.
    ///   - Wire LockstepManager.OnRequestPath / OnRequestAttackMove / OnCancelPath to this class.
    ///   - Pass this bridge instead of PathRequestSystem to SelectionSystem.Initialize.
    /// </summary>
    public partial class FlowFieldBridge : Node
    {
        // ── Tuning ────────────────────────────────────────────────────────────

        /// <summary>
        /// World units ahead of the unit that MoveTarget is placed.
        /// Large enough for smooth steering but small enough to follow tight turns.
        /// </summary>
        private static readonly Fixed LOOK_AHEAD = Fixed.FromFloat(3.0f);

        // ── Dependencies ──────────────────────────────────────────────────────

        private EntityWorld?     _world;
        private FlowFieldSystem? _flowSys;
        private BuildingStore?   _buildings;

        // ── Per-entity tracking ───────────────────────────────────────────────

        // Active flow field per entity. Null = no path issued.
        private readonly FlowField?[] _fields = new FlowField?[EntityWorld.MAX_ENTITIES];

        // Exact goal world position per entity (for HasArrived and direct-steer fallback).
        private readonly FixedVec3[]  _goals  = new FixedVec3[EntityWorld.MAX_ENTITIES];

        // ── Building-change detection (mirrors NavObstacleManager polling) ─────

        private int    _prevBuildingCount = 0;
        private readonly bool[] _prevAlive = new bool[BuildingStore.MAX_BUILDINGS];

        // ── Init ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Bind this bridge to the entity world, the flow field system, and the building store.
        /// Call after the scenario has been applied and buildings are placed — the obstacle map
        /// will be populated from the current BuildingStore state immediately.
        /// </summary>
        public void Initialize(EntityWorld world, FlowFieldSystem flowSys, BuildingStore buildings)
        {
            _world     = world;
            _flowSys   = flowSys;
            _buildings = buildings;

            // Snapshot initial alive state so first-frame diff doesn't trigger a spurious rebuild.
            _prevBuildingCount = buildings.Count;
            for (int i = 0; i < buildings.Count; i++)
                _prevAlive[i] = buildings.Alive[i];
        }

        // ── Public API (mirrors PathRequestSystem) ────────────────────────────

        /// <summary>
        /// Issue a Move command to <paramref name="entityId"/> toward <paramref name="destination"/>.
        /// The unit ignores enemies en route (same as PathRequestSystem.RequestPath).
        /// </summary>
        public void RequestPath(int entityId, Vector3 destination)
        {
            if (_world == null || _flowSys == null) return;

            var goal = new FixedVec3(
                Fixed.FromFloat(destination.X), Fixed.Zero, Fixed.FromFloat(destination.Z));

            _world.CommandState[entityId] = UnitCommand.Move;
            _world.CommandGoal[entityId]  = goal;
            _fields[entityId]             = _flowSys.GetOrCompute(goal);
            _goals[entityId]              = goal;
        }

        /// <summary>
        /// Issue an AttackMove command to <paramref name="entityId"/> toward <paramref name="destination"/>.
        /// The unit attacks enemies encountered in range and resumes toward the goal after each kill.
        /// </summary>
        public void RequestAttackMove(int entityId, Vector3 destination)
        {
            if (_world == null || _flowSys == null) return;

            var goal = new FixedVec3(
                Fixed.FromFloat(destination.X), Fixed.Zero, Fixed.FromFloat(destination.Z));

            _world.CommandState[entityId] = UnitCommand.AttackMove;
            _world.CommandGoal[entityId]  = goal;
            _fields[entityId]             = _flowSys.GetOrCompute(goal);
            _goals[entityId]              = goal;
        }

        /// <summary>
        /// Cancel the active path for <paramref name="entityId"/>.
        /// Does not change CommandState — caller owns that transition.
        /// </summary>
        public void CancelPath(int entityId)
        {
            _fields[entityId] = null;
        }

        // ── Per-frame ─────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (_world == null) return;

            // Rebuild obstacle map if any building was placed or destroyed since last frame.
            // Mirrors NavObstacleManager's polling pattern — O(64) scan, cheap to run every frame.
            CheckBuildingChanges();

            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (_fields[i] == null) continue;
                if (!_world.IsAlive(i)) { _fields[i] = null; continue; }

                // Only steer units that are actively navigating (Move or AttackMove).
                // If CombatSystem set the unit to AttackMove-Idle to engage an enemy,
                // the unit has a different goal and we shouldn't override MoveTarget.
                UnitCommand cmd = _world.CommandState[i];
                if (cmd != UnitCommand.Move && cmd != UnitCommand.AttackMove)
                {
                    _fields[i] = null;
                    continue;
                }

                FixedVec3 pos    = _world.Position[i];
                FixedVec3 goal   = _goals[i];
                FlowField  field = _fields[i]!;

                // ── Arrival check ─────────────────────────────────────────────
                if (field.HasArrived(pos.X, pos.Z))
                {
                    _fields[i] = null;
                    // Move → Stop (hold position, attack in range only — same as PathRequestSystem).
                    // AttackMove arrival: CombatSystem.ResumeAttackMove owns the →Idle transition.
                    if (cmd == UnitCommand.Move)
                        _world.CommandState[i] = UnitCommand.Stop;
                    continue;
                }

                // ── Sample flow field ──────────────────────────────────────────
                FixedVec3 dir = field.Sample(pos.X, pos.Z);

                FixedVec3 target;
                if (dir == FixedVec3.Zero)
                {
                    // Unit is in the goal cell but not yet within 1.5u of the exact goal.
                    // Steer directly toward the exact goal (direct-steer fallback).
                    FixedVec3 toGoal = goal - pos;
                    Fixed sqrDist = toGoal.SqrMagnitude();
                    // If essentially on the goal, let arrive logic handle it next frame.
                    if (sqrDist <= Fixed.FromFloat(0.01f))
                    {
                        target = goal;
                    }
                    else
                    {
                        target = goal; // steer directly
                    }
                }
                else
                {
                    target = pos + dir * LOOK_AHEAD;
                }

                // ── Update sim-layer MoveTarget ───────────────────────────────
                _world.MoveTarget[i] = new FixedVec3(target.X, Fixed.Zero, target.Z);
                _world.Flags[i]      = (_world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                _world.AttackTarget[i] = -1;
            }
        }

        /// <summary>
        /// Detects any change to BuildingStore.Alive[] since the last frame and rebuilds the
        /// flow field obstacle map when a change is found. This handles building placement,
        /// destruction, and editor undo/redo without requiring explicit callbacks.
        /// </summary>
        private void CheckBuildingChanges()
        {
            if (_buildings == null || _flowSys == null) return;

            int count   = _buildings.Count;
            bool changed = count != _prevBuildingCount;
            if (!changed)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_prevAlive[i] != _buildings.Alive[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            // Snapshot new state before rebuilding so next frame comparison is clean.
            _prevBuildingCount = count;
            for (int i = 0; i < count; i++)
                _prevAlive[i] = _buildings.Alive[i];

            _flowSys.RebuildObstacles(_buildings);
        }
    }
}
