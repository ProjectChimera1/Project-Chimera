#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Queries NavigationServer3D for unit paths, rate-limited to MAX_QUERIES_PER_FRAME
    /// per frame. Steers each unit through waypoints by updating world.MoveTarget
    /// one waypoint at a time.
    ///
    /// Architecture: Presentation layer (uses Godot API). Sits between SelectionSystem
    /// (issues move commands) and MovementSystem (consumes MoveTarget).
    /// The simulation layer stays clean — MovementSystem still just steers toward
    /// MoveTarget; this system advances MoveTarget through the path transparently.
    /// </summary>
    public partial class PathRequestSystem : Node
    {
        // ── Constants ─────────────────────────────────────────────────────────

        /// <summary>Maximum NavigationServer3D queries per frame to avoid stalls.</summary>
        private const int MAX_QUERIES_PER_FRAME = 30;

        /// <summary>
        /// Squared distance at which a unit is considered to have reached a waypoint.
        /// 1.5 world units is intentionally generous — tight radii cause jitter.
        /// </summary>
        private const float WAYPOINT_REACH_SQR = 1.5f * 1.5f;

        // ── Dependencies ──────────────────────────────────────────────────────

        private EntityWorld _world = null!;
        private Rid         _navMap;

        // ── Per-entity path data (indexed by entity ID) ───────────────────────

        // _paths[id] is null when no active path exists (direct steering or arrived).
        private readonly Vector3[]?[] _paths  = new Vector3[]?[EntityWorld.MAX_ENTITIES];

        // _wpIdx[id] is the index of the NEXT waypoint to head toward.
        private readonly int[]        _wpIdx  = new int[EntityWorld.MAX_ENTITIES];

        // ── Request queue ──────────────────────────────────────────────────────

        private readonly Queue<(int id, Vector3 dest)> _queue = new();

        // ── Init ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Bind the system to the entity world and the active navigation map RID.
        /// Call after NavigationRegion3D has been added to the scene tree.
        /// </summary>
        public void Initialize(EntityWorld world, Rid navMap)
        {
            _world  = world;
            _navMap = navMap;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Request a Move-command path for <paramref name="entityId"/> to <paramref name="destination"/>.
        /// The unit will ignore enemies en route. Cancels any in-progress path.
        /// </summary>
        public void RequestPath(int entityId, Vector3 destination)
        {
            _paths[entityId] = null;
            _wpIdx[entityId] = 0;
            _queue.Enqueue((entityId, destination));

            _world.CommandState[entityId] = UnitCommand.Move;
            _world.CommandGoal[entityId]  = new FixedVec3(
                Fixed.FromFloat(destination.X), Fixed.Zero, Fixed.FromFloat(destination.Z));
        }

        /// <summary>
        /// Request an Attack-Move path for <paramref name="entityId"/> to <paramref name="destination"/>.
        /// The unit attacks enemies encountered within range and resumes toward the goal after each kill.
        /// Cancels any in-progress path.
        /// </summary>
        public void RequestAttackMove(int entityId, Vector3 destination)
        {
            _paths[entityId] = null;
            _wpIdx[entityId] = 0;
            _queue.Enqueue((entityId, destination));

            _world.CommandState[entityId] = UnitCommand.AttackMove;
            _world.CommandGoal[entityId]  = new FixedVec3(
                Fixed.FromFloat(destination.X), Fixed.Zero, Fixed.FromFloat(destination.Z));
        }

        /// <summary>
        /// Cancel any queued or active path for <paramref name="entityId"/>.
        /// Does not change CommandState — caller is responsible for that.
        /// </summary>
        public void CancelPath(int entityId)
        {
            _paths[entityId] = null;
            _wpIdx[entityId] = 0;
        }

        // ── Per-frame ─────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            DrainRequestQueue();
            AdvanceWaypoints();
            CheckMoveGoalArrival();
        }

        // ── Private ───────────────────────────────────────────────────────────

        /// <summary>
        /// Execute up to MAX_QUERIES_PER_FRAME pending path requests this frame.
        /// Queries that find no valid path fall back to direct steering.
        /// </summary>
        private void DrainRequestQueue()
        {
            int processed = 0;
            while (_queue.Count > 0 && processed < MAX_QUERIES_PER_FRAME)
            {
                var (id, dest) = _queue.Dequeue();
                processed++;

                if (!_world.IsAlive(id)) continue;

                var simPos = _world.Position[id];
                var start  = new Vector3(simPos.X.ToFloat(), 0f, simPos.Z.ToFloat());

                Vector3[] path = NavigationServer3D.MapGetPath(_navMap, start, dest, optimize: true);

                if (path.Length >= 2)
                {
                    _paths[id] = path;
                    _wpIdx[id] = 1;
                    SetMoveTarget(id, path[1]);
                }
                else
                {
                    // No navigable path found — steer directly; MovementSystem handles arrive
                    _paths[id] = null;
                    SetMoveTargetFixed(id, dest);
                }
            }
        }

        /// <summary>
        /// For each entity with an active path, advance to the next waypoint when close enough.
        ///
        /// Proximity check runs FIRST — before the moving-flag guard — so MovementSystem
        /// briefly clearing the Moving flag at an intermediate waypoint does not destroy
        /// the remaining path before we can step to the next point.
        ///
        /// Paths for Move commands are kept alive while CommandState == Move even if the
        /// unit momentarily stops (e.g. arrive at a waypoint before this frame's advance).
        /// All other commands (Stop, Hold, Idle) have their paths cleared when not moving.
        /// </summary>
        private void AdvanceWaypoints()
        {
            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                Vector3[]? path = _paths[i];
                if (path == null) continue;
                if (!_world.IsAlive(i)) { _paths[i] = null; continue; }

                int wpIdx = _wpIdx[i];
                if (wpIdx >= path.Length) { _paths[i] = null; continue; }

                // ── Proximity check (runs before moving-flag guard) ────────────────
                var simPos = _world.Position[i];
                float dx = simPos.X.ToFloat() - path[wpIdx].X;
                float dz = simPos.Z.ToFloat() - path[wpIdx].Z;

                if (dx * dx + dz * dz <= WAYPOINT_REACH_SQR)
                {
                    wpIdx++;
                    _wpIdx[i] = wpIdx;

                    if (wpIdx >= path.Length)
                    {
                        // Final waypoint reached — path complete
                        _paths[i] = null;
                        // Move command: hold position at destination (Stop), not Idle.
                        // Idle immediately triggers global enemy chase via TickIdleCombat,
                        // making the unit sprint away from where the player sent it.
                        // Stop means: defend in place, attack enemies that enter range.
                        if (_world.CommandState[i] == UnitCommand.Move)
                            _world.CommandState[i] = UnitCommand.Stop;
                        // AttackMove: CombatSystem.ResumeAttackMove handles Idle transition
                    }
                    else
                    {
                        SetMoveTarget(i, path[wpIdx]);
                    }
                    continue;
                }

                // ── Not near the next waypoint ────────────────────────────────────
                // Keep Move-command paths alive even when briefly not moving (e.g. the
                // unit just stopped at the previous waypoint and hasn't been steered yet).
                // Clear the path for all other states (Stop/Hold issued, AttackMove engaged).
                if ((_world.Flags[i] & EntityFlags.Moving) == 0 &&
                    _world.CommandState[i] != UnitCommand.Move)
                {
                    _paths[i] = null;
                }
            }
        }

        /// <summary>
        /// Checks whether Move-command units without an active nav path have reached their
        /// CommandGoal by proximity (same 1.5u threshold used for waypoint advancement).
        /// Transitions to Stop when arrived (not Idle — Idle triggers global enemy chase).
        ///
        /// Using goal proximity instead of the Moving flag avoids false resets: the Moving
        /// flag is briefly false at intermediate waypoints and in the first frame after a
        /// RequestPath call (before DrainRequestQueue has a chance to set it).
        /// </summary>
        private void CheckMoveGoalArrival()
        {
            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                if (_paths[i] != null) continue;              // nav path active — AdvanceWaypoints owns transition
                if (_world.CommandState[i] != UnitCommand.Move) continue;

                var pos = _world.Position[i];
                float dx = pos.X.ToFloat() - _world.CommandGoal[i].X.ToFloat();
                float dz = pos.Z.ToFloat() - _world.CommandGoal[i].Z.ToFloat();
                if (dx * dx + dz * dz <= WAYPOINT_REACH_SQR)
                    _world.CommandState[i] = UnitCommand.Stop;
            }
        }

        /// <summary>
        /// Update the entity's MoveTarget to a Godot Vector3 waypoint and ensure
        /// the Moving flag is set (Attacking cleared).
        /// </summary>
        private void SetMoveTarget(int id, Vector3 wp)
        {
            _world.MoveTarget[id] = new FixedVec3(
                Fixed.FromFloat(wp.X),
                Fixed.Zero,
                Fixed.FromFloat(wp.Z));

            _world.Flags[id] = (_world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
            _world.AttackTarget[id] = -1;
        }

        /// <summary>Sets MoveTarget from a Godot Vector3 (fallback: no waypoint path).</summary>
        private void SetMoveTargetFixed(int id, Vector3 dest)
        {
            _world.MoveTarget[id] = new FixedVec3(
                Fixed.FromFloat(dest.X),
                Fixed.Zero,
                Fixed.FromFloat(dest.Z));

            _world.Flags[id] = (_world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
            _world.AttackTarget[id] = -1;
        }
    }
}
