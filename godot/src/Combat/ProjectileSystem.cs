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
        private readonly DamageTable        _table;
        private readonly BuildingStore?     _buildings; // Story 2.9a (D-4) — building-target projectiles; null ⇒ no building hits

        public ProjectileSystem(ProjectileStore store, CombatEventQueue? events = null, MatchStats? stats = null,
            DamageTable? table = null, BuildingStore? buildings = null)
        {
            _store     = store;
            _events    = events;
            _stats     = stats;
            _table     = table ?? DamageTable.Default;
            _buildings = buildings;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            int count = _store.HighWaterMark;
            for (int i = 0; i < count; i++)
            {
                if (!_store.Alive[i]) continue;

                int  targetId    = _store.TargetId[i]; // entity id, OR (Story 2.13) a PACKED building ref when isBuilding
                bool isBuilding  = _store.TargetIsBuilding[i]; // Story 2.9a: TargetId indexes BuildingStore, not EntityWorld
                bool targetAlive;
                FixedVec3 goalPos;
                int  buildingSlot = -1; // Story 2.13: the resolved live slot for a building-target projectile

                // Track target: refresh goal while target is alive; on death fly toward its last known position and
                // drop harmlessly (targetAlive == false → no hit resolved on arrival).
                if (isBuilding)
                {
                    // Story 2.13 (AC3.4): TargetId holds a PACKED building ref — TryResolveRef validates bounds + Alive
                    // + GENERATION, so a shell in flight when its building is razed AND its slot recycled into a new
                    // building drops harmlessly (stale generation ⇒ unresolved) instead of striking the new occupant.
                    targetAlive = _buildings != null && _buildings.TryResolveRef(targetId, out buildingSlot);
                    if (targetAlive)
                    {
                        _store.LastKnownPos[i] = _buildings!.Position[buildingSlot];
                        goalPos = _buildings.Position[buildingSlot];
                    }
                    else
                    {
                        goalPos = _store.LastKnownPos[i];
                    }
                }
                else
                {
                    targetAlive = world.IsAlive(targetId);
                    if (targetAlive)
                    {
                        _store.LastKnownPos[i] = world.Position[targetId];
                        goalPos = world.Position[targetId];
                    }
                    else
                    {
                        goalPos = _store.LastKnownPos[i];
                    }
                }

                FixedVec3 delta   = goalPos - _store.Position[i];
                Fixed     distSqr = delta.SqrMagnitude();

                if (distSqr <= HIT_SQR)
                {
                    if (targetAlive)
                    {
                        if (isBuilding) ApplyBuildingHit(i, buildingSlot); // resolved live slot, not the packed ref
                        else            ApplyHit(world, i, targetId);
                    }
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
            Fixed splashRadius = _store.SplashRadius[projId];
            bool  isSplash     = splashRadius > Fixed.Zero;

            // Emit hit event at the impact position — BEFORE Apply, to preserve event order (Story 1.6 AC2).
            _events?.Push(isSplash ? CombatEventType.SplashHit : CombatEventType.RangedHit,
                          _store.Position[projId], _store.Feedback[projId]); // Story 2.7 SD-4: the firing unit's override, snapshotted at Spawn

            // Primary hit uses the armor SNAPSHOT captured at spawn (_store.TargetArmor), not live armor.
            var ctx = new DamageContext(world, targetId, _store.TargetArmor[projId],
                                        _store.Owner[projId], _table, _events, _stats);
            DamageResolver.Apply(in ctx, _store.Damage[projId], _store.DmgType[projId]);

            // AoE splash: deal same damage to all other enemies within splash radius
            if (isSplash)
                ApplySplash(world, projId, targetId, splashRadius);
        }

        /// <summary>
        /// Story 2.9a (AC2.6 / D-4): resolve a ranged hit on a BUILDING. Fortified matrix damage via the SAME shared
        /// <see cref="DamageResolver.ApplyToBuilding"/> helper the melee path uses (no drift), and NO splash against a
        /// building target. The caller has already confirmed the building is alive and in range this tick.
        /// </summary>
        private void ApplyBuildingHit(int projId, int buildingId)
        {
            // Impact event at the shell's position — BEFORE damage, preserving event order (Story 1.6 AC2).
            _events?.Push(CombatEventType.RangedHit, _store.Position[projId], _store.Feedback[projId]);
            DamageResolver.ApplyToBuilding(_buildings!, buildingId, _store.Damage[projId], _store.DmgType[projId],
                                           _table, _events);
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

                // Secondary splash targets use LIVE armor (caller-supplied), and emit no pre-hit event.
                var ctx = new DamageContext(world, i, world.ArmorTypeOf[i], owner, _table, _events, _stats);
                DamageResolver.Apply(in ctx, damage, dmgType);
            }
        }
    }
}
