#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Handles targeting, attack cooldowns, damage application, and unit death.
    /// Uses a SpatialHash for O(k) nearest-enemy queries instead of O(n²) brute force.
    ///
    /// Behaviour is gated by each unit's CommandState:
    ///   Idle         — auto-attack in range; chase globally if no target nearby
    ///   Move         — skip all combat (pure navigation)
    ///   AttackMove   — attack enemies within range; resume toward CommandGoal after kill
    ///   Stop         — attack enemies within range; never chase or modify MoveTarget
    ///   HoldPosition — like Stop in combat (attack in range, never chase); its DISTINCTION from Stop is the
    ///                  MovementSystem separation-anchor (a Hold unit is never pushed off its tile) — Story 1.12
    ///   AttackTarget — force-attack ONE specific enemy (CommandTarget); chase only it, ignoring nearer enemies;
    ///                  falls back to Idle if that target dies/becomes invalid (Story 1.12)
    ///   Patrol       — walk an ordered waypoint route, engaging enemies en route like AttackMove, reversing at
    ///                  both ends (Story 1.12)
    ///   Follow       — track a friendly (CommandTarget) within a leash; re-path beyond it, idle within; drop to
    ///                  Idle if the followed unit dies (tracking only — no auto-engage in 1.12) (Story 1.12)
    ///
    /// Gatherers (GatherState != Inactive) are skipped entirely regardless of their
    /// attack damage — auto-combat would hijack the gather loop's MoveTarget.
    /// </summary>
    public class CombatSystem : ISimSystem
    {
        private readonly SpatialHash      _spatialHash = new SpatialHash();
        private readonly ProjectileStore  _projectiles;
        private readonly CombatEventQueue? _events;
        private readonly MatchStats?       _stats;
        private readonly DamageTable       _table;

        /// <summary>
        /// Units with AttackRange above this value use projectiles; at or below it use instant melee damage.
        /// Matches the highest melee range in alpha_faction.json (griffin = 2.0u).
        /// </summary>
        private static readonly Fixed MELEE_THRESHOLD = Fixed.FromFloat(2.5f);

        public CombatSystem(ProjectileStore projectiles, CombatEventQueue? events = null, MatchStats? stats = null,
            DamageTable? table = null)
        {
            _projectiles = projectiles;
            _events      = events;
            _stats       = stats;
            _table       = table ?? DamageTable.Default;
        }

        // Squared arrive threshold for AttackMove goal detection (0.5 world units)
        private static readonly Fixed AMOVE_ARRIVE_SQR = Fixed.FromFloat(0.5f) * Fixed.FromFloat(0.5f);

        public void Tick(EntityWorld world, Fixed dt)
        {
            _spatialHash.Rebuild(world);

            // ── AR-40 fork #1 — cross-faction same-tick tie-break (Story 1.9a, D11) ───────────────────────
            // CANONICAL RULE: when two state-mutating events from DIFFERENT faction slots resolve on the SAME
            // tick, they are ordered by ASCENDING FACTION SLOT. Today that rule is SUBSUMED by this
            // ascending-entity-ID iteration: the only cross-faction same-tick *hashed* mutation at present is
            // the combat death sequence (world.Destroy inside DamageResolver.Apply), and units are created in
            // faction-slot order, so ascending entity id == ascending faction slot here. Pinned (no behavior
            // change) by Golden/SameTickTieBreakGoldenTests. Forward owner for ordering cross-faction *DSL
            // events*: Epic 7 (SD-2). Do NOT replace this in-order scan with an unstable enumeration.
            int count = world.HighWaterMark;
            for (int i = 0; i < count; i++)
            {
                if ((world.Flags[i] & EntityFlags.Alive) == 0) continue;
                // Gatherers are exempt from auto-combat even when their unit data
                // carries attack damage — idle-chase would overwrite their MoveTarget
                // every tick and halt all gathering (see GatheringSystem). Combat
                // command states issued to a gatherer are normalized back to Idle so
                // it can never sit in AttackMove with no system able to complete it;
                // explicit worker fight-back is a future feature.
                if (world.GatherState[i] != GatherState.Inactive)
                {
                    if (world.CommandState[i] == UnitCommand.AttackMove ||
                        world.CommandState[i] == UnitCommand.Stop ||
                        world.CommandState[i] == UnitCommand.HoldPosition ||
                        world.CommandState[i] == UnitCommand.AttackTarget ||
                        world.CommandState[i] == UnitCommand.Patrol ||
                        world.CommandState[i] == UnitCommand.Follow ||
                        world.CommandState[i] == UnitCommand.PatrolAppend)
                        world.CommandState[i] = UnitCommand.Idle;
                    continue;
                }
                // EDGE (Story 1.12): this zero-damage guard sits BEFORE the command switch, so a zero-damage
                // non-gatherer is skipped entirely — it therefore cannot Patrol/Follow (both are partly
                // movement). Acceptable for 1.12: every order is issued to damage-bearing combat units (the
                // golden + tests use such units). Supporting zero-damage support units patrolling/following is
                // an Epic-2 concern; do NOT hoist the movement branches above this guard here. Flagged, not built.
                if (world.AttackDamage[i] == Fixed.Zero) continue; // non-combatant

                switch (world.CommandState[i])
                {
                    case UnitCommand.Move:
                        continue; // pure navigation — no combat processing

                    case UnitCommand.Stop:
                        TickStopCombat(world, i, dt);
                        break;

                    case UnitCommand.HoldPosition:
                        TickHoldCombat(world, i, dt);
                        break;

                    case UnitCommand.AttackMove:
                        TickAttackMoveCombat(world, i, dt);
                        break;

                    case UnitCommand.AttackTarget:
                        TickAttackTargetCombat(world, i, dt);
                        break;

                    case UnitCommand.Patrol:
                        TickPatrolCombat(world, i, dt);
                        break;

                    case UnitCommand.Follow:
                        TickFollowCombat(world, i, dt);
                        break;

                    default: // Idle (and Build — workers are handled by the gatherer guard above)
                        TickIdleCombat(world, i, dt);
                        break;
                }
            }
        }

        // ── Idle ──────────────────────────────────────────────────────────────────
        // Auto-attack nearest in-range enemy; chase globally if none in range.

        private void TickIdleCombat(EntityWorld world, int i, Fixed dt)
        {
            TickCooldown(world, i, dt);

            int target = ValidateOrClearTarget(world, i);
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i);
            world.AttackTarget[i] = target;

            if (target < 0)
            {
                // No enemy in attack range — advance toward nearest enemy anywhere
                int anyEnemy = _spatialHash.FindNearestEnemyGlobal(world, i);
                if (anyEnemy >= 0)
                {
                    world.MoveTarget[i] = world.Position[anyEnemy];
                    world.Flags[i] = (world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                }
                return;
            }

            Fixed sqrDist  = FixedVec3.SqrDistance(world.Position[i], world.Position[target]);
            Fixed sqrRange = world.AttackRange[i] * world.AttackRange[i];

            if (sqrDist > sqrRange)
            {
                // Target moved out of range — chase
                world.MoveTarget[i]   = world.Position[target];
                world.Flags[i]        = (world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                world.AttackTarget[i] = -1;
                return;
            }

            world.Flags[i] = (world.Flags[i] | EntityFlags.Attacking) & ~EntityFlags.Moving;
            TryDealDamage(world, i, target);
        }

        // ── Stop / Hold Position ──────────────────────────────────────────────────
        // Attack enemies that enter range; never chase; never move. Stop and Hold share this combat body —
        // their ONLY difference is the MovementSystem separation anchor (Story 1.12 AC5b): a Hold unit is
        // never displaced from its tile, a Stop unit can still be pushed. Two case labels + one shared body.

        private void TickStopCombat(EntityWorld world, int i, Fixed dt) => TickStationaryCombat(world, i, dt);

        /// <summary>
        /// Hold Position combat (Story 1.12). Identical to Stop in combat — attack in range, never chase, never
        /// set a MoveTarget. The distinction from Stop is enforced by the dedicated case label here plus the
        /// MovementSystem Hold-anchor exemption; do NOT add chase logic.
        /// </summary>
        private void TickHoldCombat(EntityWorld world, int i, Fixed dt) => TickStationaryCombat(world, i, dt);

        private void TickStationaryCombat(EntityWorld world, int i, Fixed dt)
        {
            TickCooldown(world, i, dt);

            int target = ValidateOrClearTarget(world, i);
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i);
            world.AttackTarget[i] = target;

            if (target < 0)
            {
                world.Flags[i] &= ~EntityFlags.Attacking;
                return;
            }

            Fixed sqrDist  = FixedVec3.SqrDistance(world.Position[i], world.Position[target]);
            Fixed sqrRange = world.AttackRange[i] * world.AttackRange[i];

            if (sqrDist > sqrRange)
            {
                // Enemy wandered out of range — drop target, stay put
                world.AttackTarget[i] = -1;
                world.Flags[i]       &= ~EntityFlags.Attacking;
                return;
            }

            world.Flags[i] = (world.Flags[i] | EntityFlags.Attacking) & ~EntityFlags.Moving;
            TryDealDamage(world, i, target);
        }

        // ── AttackTarget (Story 1.12) ──────────────────────────────────────────────
        // Force-attack ONE specific enemy (CommandTarget): path to and chase ONLY it, ignoring nearer enemies.
        // If that target dies/becomes invalid, clear the slot and fall back to Idle (acquire-nearest) — no
        // freeze, no per-tick stutter, no dangling id (AC3).

        private void TickAttackTargetCombat(EntityWorld world, int i, Fixed dt)
        {
            int forced = world.CommandTarget[i];
            // Invalid if: no target (-1), dead/out-of-range (IsAlive short-circuits before the FactionOf index so a
            // bad id never reads out of bounds), itself, or a SAME-faction unit. The faction/self guard (Review
            // Option A, Story 1.12) blocks force-firing your own units — reachable when a killed enemy's slot is
            // recycled into a friendly before this tick, or via a crafted order. Same-faction ONLY: a Neutral is
            // still a valid force-fire target (the golden relies on it).
            if (forced < 0 || !world.IsAlive(forced) || forced == i || world.FactionOf[forced] == world.FactionOf[i])
            {
                // AC3 — forced target gone/invalid: clear and resume normal Idle acquire-nearest THIS tick (no
                // freeze). Delegate cooldown + acquisition to TickIdleCombat (do NOT also tick cooldown here, or it
                // would decrement twice this tick).
                world.CommandTarget[i] = -1;
                world.CommandState[i]  = UnitCommand.Idle;
                world.AttackTarget[i]  = -1;
                TickIdleCombat(world, i, dt);
                return;
            }

            TickCooldown(world, i, dt);

            // Force-fire: target is exactly the player-issued enemy, regardless of any nearer enemy (the AC2
            // distinction from Idle's nearest-enemy acquisition).
            world.AttackTarget[i] = forced;

            Fixed sqrDist  = FixedVec3.SqrDistance(world.Position[i], world.Position[forced]);
            Fixed sqrRange = world.AttackRange[i] * world.AttackRange[i];

            if (sqrDist > sqrRange)
            {
                // Out of range — chase ONLY the forced target (its position moves, so re-aim each tick like Idle-chase).
                world.MoveTarget[i] = world.Position[forced];
                world.Flags[i]      = (world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                return;
            }

            world.Flags[i] = (world.Flags[i] | EntityFlags.Attacking) & ~EntityFlags.Moving;
            TryDealDamage(world, i, forced);
        }

        // ── Patrol (Story 1.12) ────────────────────────────────────────────────────
        // Walk an ordered waypoint route, engaging enemies in range exactly like AttackMove, then resuming
        // toward the current waypoint; advance the route on arrival, reversing at both ends.

        private void TickPatrolCombat(EntityWorld world, int i, Fixed dt)
        {
            TickCooldown(world, i, dt);

            int target = ValidateOrClearTarget(world, i);
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i);
            world.AttackTarget[i] = target;

            if (target < 0)
            {
                // No enemy in range — resume walking the route.
                world.Flags[i] &= ~EntityFlags.Attacking;
                ResumePatrol(world, i);
                return;
            }

            Fixed sqrDist  = FixedVec3.SqrDistance(world.Position[i], world.Position[target]);
            Fixed sqrRange = world.AttackRange[i] * world.AttackRange[i];

            if (sqrDist > sqrRange)
            {
                world.AttackTarget[i] = -1;
                world.Flags[i]       &= ~EntityFlags.Attacking;
                ResumePatrol(world, i);
                return;
            }

            world.Flags[i] = (world.Flags[i] | EntityFlags.Attacking) & ~EntityFlags.Moving;
            TryDealDamage(world, i, target);
        }

        /// <summary>
        /// Steer a Patrol unit toward its current waypoint; on arrival, advance the route index along
        /// <see cref="EntityWorld.PatrolDir"/>, reversing at either end (the AC's "reverses at the final leg").
        /// Index/direction arithmetic is pure integer; the arrival test reuses the shared Fixed threshold —
        /// fully deterministic. A degenerate route (count &lt;= 1) just holds in place.
        /// </summary>
        private static void ResumePatrol(EntityWorld world, int id)
        {
            int n = world.PatrolCount[id];
            if (n <= 1)
            {
                world.Flags[id] &= ~EntityFlags.Moving; // no real route — hold position
                return;
            }

            int baseIdx = id * EntityWorld.MAX_PATROL_WAYPOINTS;
            int leg     = world.PatrolIndex[id];

            Fixed sqrToWp = FixedVec3.SqrDistance(world.Position[id], world.PatrolWaypoints[baseIdx + leg]);
            if (sqrToWp <= AMOVE_ARRIVE_SQR)
            {
                // Arrived — advance, reversing at either end (top: dir→-1; bottom: dir→+1).
                int dir  = world.PatrolDir[id];
                int next = leg + dir;
                if      (next > n - 1) { dir = -1; next = leg + dir; }
                else if (next < 0)     { dir =  1; next = leg + dir; }
                world.PatrolDir[id]   = (sbyte)dir;
                world.PatrolIndex[id] = (byte)next;
                leg = next;
            }

            world.MoveTarget[id] = world.PatrolWaypoints[baseIdx + leg];
            world.Flags[id]      = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
        }

        // ── Follow (Story 1.12) ────────────────────────────────────────────────────
        // Track a friendly unit (CommandTarget) within a leash: re-path toward it when beyond the leash, idle
        // in place within it, drop to Idle if it dies. Tracking only — no auto-engage in 1.12 (per AC).

        /// <summary>Re-path threshold for Follow: stay put within this distance of the followed unit, re-path beyond it.</summary>
        private static readonly Fixed FOLLOW_LEASH     = Fixed.FromInt(3); // 3 world units (1.12 default tuning)
        private static readonly Fixed FOLLOW_LEASH_SQR = FOLLOW_LEASH * FOLLOW_LEASH;

        private void TickFollowCombat(EntityWorld world, int i, Fixed dt)
        {
            int friendly = world.CommandTarget[i];
            // Drop to Idle if the followed unit is gone (-1 / dead / out-of-range — IsAlive short-circuits before
            // the FactionOf index), is itself, or is no longer SAME-faction (a recycled slot now holding an
            // enemy/neutral). Follow tracks a friendly only (Review, Story 1.12).
            if (friendly < 0 || !world.IsAlive(friendly) || friendly == i || world.FactionOf[friendly] != world.FactionOf[i])
            {
                // Followed unit gone/invalid — drop to Idle (AC4b).
                world.CommandState[i]  = UnitCommand.Idle;
                world.CommandTarget[i] = -1;
                world.Flags[i]        &= ~(EntityFlags.Moving | EntityFlags.Attacking);
                return;
            }

            Fixed sqrDist = FixedVec3.SqrDistance(world.Position[i], world.Position[friendly]);
            if (sqrDist > FOLLOW_LEASH_SQR)
            {
                // Beyond leash — re-path toward the (moving) friendly each tick.
                world.MoveTarget[i] = world.Position[friendly];
                world.Flags[i]      = (world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
            }
            else
            {
                // Within leash — idle in place.
                world.Flags[i] &= ~EntityFlags.Moving;
            }
        }

        // ── AttackMove ────────────────────────────────────────────────────────────
        // Navigate toward CommandGoal; engage enemies in attack range; resume after kill.

        private void TickAttackMoveCombat(EntityWorld world, int i, Fixed dt)
        {
            TickCooldown(world, i, dt);

            int target = ValidateOrClearTarget(world, i);
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i);
            world.AttackTarget[i] = target;

            if (target < 0)
            {
                // No enemy in range — resume toward goal
                world.Flags[i] &= ~EntityFlags.Attacking;
                ResumeAttackMove(world, i);
                return;
            }

            Fixed sqrDist  = FixedVec3.SqrDistance(world.Position[i], world.Position[target]);
            Fixed sqrRange = world.AttackRange[i] * world.AttackRange[i];

            if (sqrDist > sqrRange)
            {
                // Hash returned a candidate but it's now out of range — resume
                world.AttackTarget[i] = -1;
                world.Flags[i]       &= ~EntityFlags.Attacking;
                ResumeAttackMove(world, i);
                return;
            }

            world.Flags[i] = (world.Flags[i] | EntityFlags.Attacking) & ~EntityFlags.Moving;
            TryDealDamage(world, i, target);
        }

        /// <summary>
        /// Steer an AttackMove unit back toward its CommandGoal.
        /// Transitions to Idle when the goal is reached.
        /// </summary>
        private static void ResumeAttackMove(EntityWorld world, int id)
        {
            Fixed sqrToGoal = FixedVec3.SqrDistance(world.Position[id], world.CommandGoal[id]);
            if (sqrToGoal <= AMOVE_ARRIVE_SQR)
            {
                world.CommandState[id] = UnitCommand.Idle;
                world.Flags[id]       &= ~EntityFlags.Moving;
            }
            else
            {
                world.MoveTarget[id] = world.CommandGoal[id];
                world.Flags[id]      = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
            }
        }

        // ── Shared helpers ────────────────────────────────────────────────────────

        private static void TickCooldown(EntityWorld world, int i, Fixed dt)
        {
            if (world.AttackCooldown[i] > Fixed.Zero)
            {
                world.AttackCooldown[i] = world.AttackCooldown[i] - dt;
                if (world.AttackCooldown[i] < Fixed.Zero)
                    world.AttackCooldown[i] = Fixed.Zero;
            }
        }

        /// <summary>
        /// Returns the current AttackTarget if still alive, or clears it and returns -1.
        /// </summary>
        private static int ValidateOrClearTarget(EntityWorld world, int id)
        {
            int target = world.AttackTarget[id];
            if (target >= 0 && !world.IsAlive(target))
            {
                world.AttackTarget[id] = -1;
                world.Flags[id]       &= ~EntityFlags.Attacking;
                return -1;
            }
            return target;
        }

        /// <summary>
        /// Fires an attack from <paramref name="attacker"/> toward <paramref name="target"/> if
        /// the cooldown has expired.
        ///
        /// Ranged units (AttackRange > MELEE_THRESHOLD) spawn a projectile in ProjectileStore;
        /// the projectile flies at 18 u/s and deals damage on arrival.
        /// Melee units deal instant damage and destroy the target if HP reaches zero.
        /// </summary>
        private void TryDealDamage(EntityWorld world, int attacker, int target)
        {
            if (world.AttackCooldown[attacker] > Fixed.Zero) return;

            world.AttackCooldown[attacker] = world.AttackSpeed[attacker];

            if (world.AttackRange[attacker] > MELEE_THRESHOLD)
            {
                // Ranged — spawn a tracking projectile; damage resolved by ProjectileSystem on hit
                _projectiles.Spawn(
                    world.Position[attacker],
                    target,
                    world.Position[target],
                    world.AttackDamage[attacker],
                    world.DamageTypeOf[attacker],
                    world.ArmorTypeOf[target],
                    world.FactionOf[attacker],
                    world.SplashRadius[attacker]);
            }
            else
            {
                // Melee — instant damage. Event BEFORE Apply; attacker-cleanup AFTER, gated on death —
                // operation order preserved exactly so the golden checksums stay byte-identical (Story 1.6 AC2).
                _events?.Push(CombatEventType.MeleeHit, world.Position[target]);

                var ctx = new DamageContext(world, target, world.ArmorTypeOf[target],
                                            world.FactionOf[attacker], _table, _events, _stats);
                if (DamageResolver.Apply(in ctx, world.AttackDamage[attacker], world.DamageTypeOf[attacker]))
                {
                    world.AttackTarget[attacker] = -1;
                    world.Flags[attacker]       &= ~EntityFlags.Attacking;
                }
            }
        }
    }
}
