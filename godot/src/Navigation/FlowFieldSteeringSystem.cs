#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Flow-field path following: samples each pathing unit's field once per SIMULATION TICK and writes the
    /// sim-layer <see cref="EntityWorld.MoveTarget"/> that <see cref="MovementSystem"/> then seeks toward.
    ///
    /// <para><b>Why this is a sim system and not a presentation bridge (DW-916).</b> This logic used to live in
    /// <c>FlowFieldBridge._Process</c> — a Godot <c>Node</c>, so it ran once per RENDERED FRAME while the sim ticks
    /// at a fixed 30 Hz. Online, <c>MainScene</c> drains the <c>LockstepPacer</c> with
    /// <c>while (HasTickBudget) StepOnce()</c>, up to <c>LockstepPacer.MAX_CATCHUP_TICKS</c> = 4 ticks in ONE frame.
    /// So any frame longer than 33.3 ms advanced the sim 2-4 ticks against a SINGLE steering refresh: every tick past
    /// the first sought a <see cref="EntityWorld.MoveTarget"/> computed from a stale position. A peer that was not
    /// stalling refreshed every tick, its units took a different step, and <see cref="EntityWorld.Position"/> — which
    /// IS folded into <see cref="SimChecksum"/> — diverged. That is a hard desync, and it is exactly what ended the
    /// 2026-08-09 two-machine LAN run at tick 2640, the moment combat load pushed one machine under 30 FPS.</para>
    ///
    /// <para>The rule this encodes is the DW-912 rule applied to the WRITE path rather than the tick pump: nothing
    /// that writes simulation state may be paced by the frame rate. Steering is per-tick, in the tick, on every peer.
    /// Registered immediately BEFORE <see cref="MovementSystem"/> so a unit seeks a target sampled from its position
    /// at the end of the previous tick — one refresh per tick, never zero, never four.</para>
    ///
    /// <para><b>Ordering.</b> It sits after <c>GatheringSystem</c> (which also writes <c>MoveTarget</c> for a gathering
    /// worker) and before <see cref="MovementSystem"/>, preserving the pre-fix precedence exactly: an active flow
    /// field wins over the gather sweep's target, and movement consumes the winner the same tick.</para>
    ///
    /// <para><b>Determinism.</b> Pure <see cref="Fixed"/> / integer state, ascending entity id, no Godot, no float,
    /// no wall clock. The per-entity field and goal are sim-owned (they used to be presentation-owned arrays), so a
    /// peer cannot hold a different field for the same unit. The obstacle-change poll runs here too — on the tick
    /// boundary — so a building placement invalidates the field cache at the SAME tick on every peer.</para>
    /// </summary>
    public sealed class FlowFieldSteeringSystem : ISimSystem
    {
        /// <summary>
        /// World units ahead of the unit that MoveTarget is placed. Large enough for smooth steering but small
        /// enough to follow tight turns. Unchanged from the pre-DW-916 bridge.
        /// </summary>
        private static readonly Fixed LOOK_AHEAD = Fixed.FromFloat(3.0f);

        private readonly FlowFieldSystem _flowSys;
        private readonly BuildingStore   _buildings;

        /// <summary>Active flow field per entity (null = no path issued). Sim-owned since DW-916.</summary>
        private readonly FlowField?[] _fields = new FlowField?[EntityWorld.MAX_ENTITIES];

        /// <summary>Exact goal world position per entity, for the direct-steer fallback inside the goal cell.</summary>
        private readonly FixedVec3[] _goals = new FixedVec3[EntityWorld.MAX_ENTITIES];

        // ── Building-change detection (moved off the frame poll onto the tick) ─────────────────────
        private int _prevBuildingCount;
        private readonly bool[] _prevAlive = new bool[BuildingStore.MAX_BUILDINGS];

        public FlowFieldSteeringSystem(FlowFieldSystem flowSys, BuildingStore buildings)
        {
            _flowSys   = flowSys;
            _buildings = buildings;
        }

        /// <summary>
        /// Snapshot the current building alive-state so the first tick's diff does not fire a spurious rebuild.
        /// Called once the scenario's buildings are placed (the FlowFieldInit phase), and again on reset.
        /// </summary>
        public void SyncBuildingBaseline()
        {
            _prevBuildingCount = _buildings.Count;
            for (int i = 0; i < _buildings.Count; i++)
                _prevAlive[i] = _buildings.Alive[i];
        }

        /// <summary>Drop every active path — match reset / return to Edit, so a new match inherits no stale field.</summary>
        public void ClearAll()
        {
            for (int i = 0; i < _fields.Length; i++)
            {
                _fields[i] = null;
                _goals[i]  = FixedVec3.Zero;
            }
            _prevBuildingCount = 0;
            System.Array.Clear(_prevAlive, 0, _prevAlive.Length);
        }

        // ── Order entry points (one per wire order; called from the deterministic dispatch) ─────────

        /// <summary>
        /// Issue a Move command to <paramref name="entityId"/> toward <paramref name="goal"/>. The unit ignores
        /// enemies en route.
        /// </summary>
        public void RequestPath(EntityWorld world, int entityId, FixedVec3 goal)
        {
            if ((uint)entityId >= (uint)_fields.Length) return;

            world.CommandState[entityId] = UnitCommand.Move;
            world.CommandGoal[entityId]  = goal;
            _fields[entityId]            = _flowSys.GetOrCompute(goal);
            _goals[entityId]             = goal;
        }

        /// <summary>
        /// Issue an AttackMove command to <paramref name="entityId"/> toward <paramref name="goal"/>. The unit
        /// attacks enemies encountered in range and resumes toward the goal after each kill.
        /// </summary>
        public void RequestAttackMove(EntityWorld world, int entityId, FixedVec3 goal)
        {
            if ((uint)entityId >= (uint)_fields.Length) return;

            world.CommandState[entityId] = UnitCommand.AttackMove;
            world.CommandGoal[entityId]  = goal;
            _fields[entityId]            = _flowSys.GetOrCompute(goal);
            _goals[entityId]             = goal;
        }

        /// <summary>Cancel the active path for <paramref name="entityId"/>. Does not change CommandState —
        /// the caller owns that transition (unchanged contract).</summary>
        public void CancelPath(int entityId)
        {
            if ((uint)entityId >= (uint)_fields.Length) return;
            _fields[entityId] = null;
        }

        /// <summary>True when <paramref name="entityId"/> is currently following a flow field (test/diagnostic seam).</summary>
        public bool HasPath(int entityId) =>
            (uint)entityId < (uint)_fields.Length && _fields[entityId] != null;

        // ── Tick ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One steering pass, in ascending entity id. Identical in body to the pre-DW-916
        /// <c>FlowFieldBridge._Process</c>; only the CADENCE changed (per tick, not per rendered frame).
        /// </summary>
        public void Tick(EntityWorld world, Fixed dt)
        {
            // Rebuild the obstacle map if any building was placed or destroyed since the last TICK. Moved off the
            // per-frame poll (DW-916): on the frame path a building change cleared the field cache at a
            // machine-dependent moment, so two peers could compute the same goal against different obstacle maps.
            CheckBuildingChanges();

            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (_fields[i] == null) continue;
                if (!world.IsAlive(i)) { _fields[i] = null; continue; }

                // Only steer units that are actively navigating (Move or AttackMove). If CombatSystem set the unit
                // to AttackMove-Idle to engage an enemy, the unit has a different goal and we must not override
                // MoveTarget.
                UnitCommand cmd = world.CommandState[i];
                if (cmd != UnitCommand.Move && cmd != UnitCommand.AttackMove)
                {
                    _fields[i] = null;
                    continue;
                }

                // DW-936 follow-up (2026-08-12 field report — "units run PAST the enemy, then turn back"): while an
                // attack-moving unit holds an engagement target, COMBAT owns MoveTarget (the chase leg aims at the
                // enemy). This pass used to overwrite it every tick with the field's goal-ward look-ahead point, so
                // the unit kept marching down the path THROUGH the fight and only doubled back once something else
                // broke the tug-of-war. Yield — but KEEP the field cached, so when the engagement ends
                // (kill/leash → ResumeAttackMove) the unit resumes the pathed route instead of a blind direct seek.
                if (cmd == UnitCommand.AttackMove && world.AttackTarget[i] >= 0)
                    continue;

                FixedVec3 pos   = world.Position[i];
                FixedVec3 goal  = _goals[i];
                FlowField field = _fields[i]!;

                // ── Arrival check ───────────────────────────────────────────────────────────────
                if (field.HasArrived(pos.X, pos.Z))
                {
                    _fields[i] = null;
                    if (cmd == UnitCommand.Move)
                    {
                        // Story 2.12 (R1): the flow field arrives at its 1.5u radius — WIDER than OrderQueueSystem's
                        // 0.5u completion. With orders queued behind this Move, do NOT flip to Stop here (that would
                        // strand the queue); aim MoveTarget at the true CommandGoal and keep Moving so MovementSystem
                        // drives the unit into the 0.5u pop radius and the queue advances (WC3 waypoint chaining).
                        // With no queue, flip to Stop exactly as before.
                        if (world.OrderQueueCount[i] > 0)
                        {
                            world.MoveTarget[i] = world.CommandGoal[i];
                            world.Flags[i]      = (world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                        }
                        else
                        {
                            world.CommandState[i] = UnitCommand.Stop;
                        }
                    }
                    // AttackMove arrival: CombatSystem.ResumeAttackMove owns the →Idle transition.
                    continue;
                }

                // ── Sample the field ────────────────────────────────────────────────────────────
                FixedVec3 dir = field.Sample(pos.X, pos.Z);

                // Zero direction = the unit is inside the goal cell but not yet within the field's arrival radius;
                // steer straight at the exact goal (the direct-steer fallback).
                FixedVec3 target = dir == FixedVec3.Zero ? goal : pos + dir * LOOK_AHEAD;

                // ── Write the sim-layer steering target ─────────────────────────────────────────
                world.MoveTarget[i]   = new FixedVec3(target.X, Fixed.Zero, target.Z);
                world.Flags[i]        = (world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                world.AttackTarget[i] = -1;
            }
        }

        /// <summary>
        /// Detect any change to <see cref="BuildingStore.Alive"/> since the last tick and rebuild the flow-field
        /// obstacle map when one is found. Handles placement, destruction, and editor undo/redo without callbacks.
        /// </summary>
        private void CheckBuildingChanges()
        {
            int count    = _buildings.Count;
            bool changed = count != _prevBuildingCount;
            if (!changed)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_prevAlive[i] != _buildings.Alive[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            // Snapshot the new state before rebuilding so next tick's comparison is clean.
            _prevBuildingCount = count;
            for (int i = 0; i < count; i++)
                _prevAlive[i] = _buildings.Alive[i];

            _flowSys.RebuildObstacles(_buildings);
        }
    }
}
