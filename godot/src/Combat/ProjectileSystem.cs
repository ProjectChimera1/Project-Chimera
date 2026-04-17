#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Moves in-flight projectiles each simulation tick and resolves hits.
    ///
    /// Each tick:
    ///   1. Track target: update last-known goal position while target is alive.
    ///   2. Move projectile toward goal at PROJECTILE_SPEED units/sec.
    ///   3. On arrival (within HIT_RADIUS): deal damage if target still alive, then destroy.
    /// </summary>
    public class ProjectileSystem : ISimSystem
    {
        /// <summary>World-units per second for all projectiles in Phase 1.</summary>
        public static readonly Fixed PROJECTILE_SPEED = Fixed.FromFloat(18f);

        /// <summary>Squared hit-detection radius (0.5 world units → 0.25 sqr).</summary>
        private static readonly Fixed HIT_SQR = Fixed.FromFloat(0.5f) * Fixed.FromFloat(0.5f);

        private readonly ProjectileStore   _store;
        private readonly CombatEventQueue? _events;
        private readonly MatchStats?        _stats;

        public ProjectileSystem(ProjectileStore store, CombatEventQueue? events = null, MatchStats? stats = null)
        {
            _store  = store;
            _events = events;
            _stats  = stats;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            int count = _store.HighWaterMark;
            for (int i = 0; i < count; i++)
            {
                if (!_store.Alive[i]) continue;

                int  targetId    = _store.TargetId[i];
                bool targetAlive = world.IsAlive(targetId);

                // Track target: refresh goal while target is alive
                FixedVec3 goalPos;
                if (targetAlive)
                {
                    _store.LastKnownPos[i] = world.Position[targetId];
                    goalPos = world.Position[targetId];
                }
                else
                {
                    goalPos = _store.LastKnownPos[i];
                }

                FixedVec3 delta   = goalPos - _store.Position[i];
                Fixed     distSqr = delta.SqrMagnitude();

                if (distSqr <= HIT_SQR)
                {
                    if (targetAlive)
                        ApplyHit(world, i, targetId);
                    _store.Destroy(i);
                    continue;
                }

                // Advance toward goal
                Fixed     dist = delta.Magnitude();
                FixedVec3 dir  = delta / dist;
                _store.Position[i] = _store.Position[i] + dir * PROJECTILE_SPEED * dt;
            }
        }

        /// <summary>Resolve projectile damage on a live target. Destroys target if HP reaches zero.</summary>
        private void ApplyHit(EntityWorld world, int projId, int targetId)
        {
            Fixed multiplier = DamageMatrix.Get(_store.DmgType[projId], _store.TargetArmor[projId]);
            Fixed damage     = _store.Damage[projId] * multiplier;

            Fixed splashRadius = _store.SplashRadius[projId];
            bool  isSplash     = splashRadius > Fixed.Zero;

            // Emit hit event at the impact position
            _events?.Push(isSplash ? CombatEventType.SplashHit : CombatEventType.RangedHit,
                          _store.Position[projId]);

            world.Health[targetId] = world.Health[targetId] - damage;
            if (world.Health[targetId] <= Fixed.Zero)
            {
                _events?.Push(CombatEventType.UnitKilled, world.Position[targetId]);
                _stats?.RecordKill(world.FactionOf[targetId], _store.Owner[projId]);
                world.Destroy(targetId);
            }

            // AoE splash: deal same damage to all other enemies within splash radius
            if (isSplash)
                ApplySplash(world, projId, targetId, splashRadius);
        }

        /// <summary>
        /// Deals splash damage to all enemies of the projectile owner within <paramref name="radius"/>
        /// of the hit position, excluding the primary target (already hit by <see cref="ApplyHit"/>).
        /// </summary>
        private void ApplySplash(EntityWorld world, int projId, int primaryTarget, Fixed radius)
        {
            FixedVec3 hitPos    = _store.Position[projId];
            Faction   owner     = _store.Owner[projId];
            DamageType dmgType  = _store.DmgType[projId];
            Fixed      damage   = _store.Damage[projId];
            Fixed      radiusSqr = radius * radius;

            int count = world.HighWaterMark;
            for (int i = 0; i < count; i++)
            {
                if (i == primaryTarget) continue;
                if ((world.Flags[i] & EntityFlags.Alive) == 0) continue;
                if (world.FactionOf[i] == owner) continue; // don't splash friendlies

                Fixed distSqr = FixedVec3.SqrDistance(hitPos, world.Position[i]);
                if (distSqr > radiusSqr) continue;

                Fixed multiplier = DamageMatrix.Get(dmgType, world.ArmorTypeOf[i]);
                Fixed splashDamage = damage * multiplier;
                world.Health[i] -= splashDamage;
                if (world.Health[i] <= Fixed.Zero)
                {
                    _events?.Push(CombatEventType.UnitKilled, world.Position[i]);
                    _stats?.RecordKill(world.FactionOf[i], owner);
                    world.Destroy(i);
                }
            }
        }
    }
}
