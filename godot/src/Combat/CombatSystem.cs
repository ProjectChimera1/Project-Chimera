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
    ///   Stop/Hold    — attack enemies within range; never chase or modify MoveTarget
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
                        world.CommandState[i] == UnitCommand.HoldPosition)
                        world.CommandState[i] = UnitCommand.Idle;
                    continue;
                }
                if (world.AttackDamage[i] == Fixed.Zero) continue; // non-combatant

                switch (world.CommandState[i])
                {
                    case UnitCommand.Move:
                        continue; // pure navigation — no combat processing

                    case UnitCommand.Stop:
                    case UnitCommand.HoldPosition:
                        TickStopCombat(world, i, dt);
                        break;

                    case UnitCommand.AttackMove:
                        TickAttackMoveCombat(world, i, dt);
                        break;

                    default: // Idle
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
        // Attack enemies that enter range; never chase; never move.

        private void TickStopCombat(EntityWorld world, int i, Fixed dt)
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
