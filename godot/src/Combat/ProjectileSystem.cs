#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Moves in-flight projectiles each simulation tick and resolves hits.
    ///
    /// Each tick:
    ///   1. Track target: update last-known goal position while target is alive.
    ///   2. Move projectile toward goal at its per-unit speed (ProjectileStore.Speed; PROJECTILE_SPEED is the fallback).
    ///   3. On arrival (within HIT_RADIUS): deal damage if target still alive, then destroy.
    /// </summary>
    public class ProjectileSystem : ISimSystem
    {
        /// <summary>
        /// The DEFAULT/fallback projectile travel speed (world units/second). Story 3.12: projectile speed is now
        /// per-unit (<c>EntityWorld.ProjectileSpeed</c> → <c>ProjectileStore.Speed</c>, honoured at the advance step
        /// below); this constant is the fallback a unit gets when it omits <c>projectile_speed</c> (== the value
        /// <c>UnitDefinition.ProjectileSpeed</c>/<c>EntityWorld.Create</c> default to), so existing data is unchanged.
        /// </summary>
        public static readonly Fixed PROJECTILE_SPEED = Fixed.FromFloat(18f);

        /// <summary>Squared hit-detection radius (0.5 world units → 0.25 sqr).</summary>
        private static readonly Fixed HIT_SQR = Fixed.FromFloat(0.5f) * Fixed.FromFloat(0.5f);

        /// <summary>
        /// DW-25: maximum shell lifetime in seconds. A shell that never enters the hit radius — a target fleeing faster
        /// than <c>Speed*dt</c>, which the snap-to-goal clamp cannot help because the goal keeps receding — is dropped
        /// harmlessly after this many seconds instead of orbiting forever and permanently leaking its pool slot. A
        /// deliberately generous upper bound: real shots land within a couple seconds (small attack ranges), so a
        /// legitimate shot is never dropped. NOT folded / NOT persisted (see <c>ProjectileStore.Age</c>).
        /// </summary>
        public static readonly Fixed MAX_LIFETIME = Fixed.FromInt(10);

        private readonly ProjectileStore   _store;
        private readonly CombatEventQueue? _events;
        private readonly MatchStats?        _stats;
        private readonly DamageTable        _table;
        private readonly BuildingStore?     _buildings; // Story 2.9a (D-4) — building-target projectiles; null ⇒ no building hits
        private readonly DeathFeed?         _deaths;    // Story 3.13 — records a lethal projectile hit's victim for the XP runtime
        private readonly AllianceStore?     _alliances; // Story 9.14 — AoE splash skips ALLIED factions; null/FFA ⇒ byte-identical

        // Story 7.13 — the trigger-DSL sim-event feed (unit_damaged raised at the damage site via DamageContext).
        // Wired by SimulationHost after construction; null ⇒ no raise.
        private DslSimEventFeed? _dslSimEvents;
        /// <summary>Story 7.13 — wire the trigger-DSL sim-event feed so projectile/splash damage raises unit_damaged.</summary>
        public void SetDslSimEvents(DslSimEventFeed? feed) => _dslSimEvents = feed;

        public ProjectileSystem(ProjectileStore store, CombatEventQueue? events = null, MatchStats? stats = null,
            DamageTable? table = null, BuildingStore? buildings = null, DeathFeed? deaths = null,
            AllianceStore? alliances = null)
        {
            _store     = store;
            _events    = events;
            _stats     = stats;
            _table     = table ?? DamageTable.Default;
            _buildings = buildings;
            _deaths    = deaths;   // Story 3.13 (optional — XP credited only when wired)
            _alliances = alliances; // Story 9.14 (optional — FFA/null → no allied splash exclusion, byte-identical)
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
                    // DW-444 → DW-775 (Story 15-23): a live SLOT is not by itself a valid primary target. Entity ids
                    // are RECYCLED (EntityWorld keeps a LIFO free list), so between this shell's spawn and its impact
                    // the slot can be re-allocated to a brand-new unit. TargetId now holds a PACKED entity ref
                    // (mirroring the building half below): TryResolveRef validates bounds + Alive + GENERATION, so a
                    // slot recycled into ANY new occupant — friendly, allied, or a DIFFERENT HOSTILE faction (the
                    // half DW-444 left open) — fails to resolve and the shell is treated exactly like "the target
                    // died in flight": stop tracking, coast to the last known position and drop harmlessly (no
                    // damage, no impact event, no splash). The friend test stays on top (shared with ApplySplash, so
                    // the primary and splash halves of one impact can never disagree about who is friendly) for the
                    // no-recycle case where the SAME occupant's allegiance is what matters. Golden-neutral at
                    // generation 0 (PackRef(id) == id).
                    targetAlive = world.TryResolveRef(targetId, out int targetEnt)
                                  && !IsFriendlyToOwner(world, _store.Owner[i], targetEnt);
                    if (targetAlive)
                    {
                        targetId = targetEnt; // the RESOLVED live id — ApplyHit below indexes with it
                        _store.LastKnownPos[i] = world.Position[targetEnt];
                        goalPos = world.Position[targetEnt];
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

                // DW-25: TTL backstop — age the shell and drop it harmlessly (no hit resolved, identical to the
                // "target died in flight" drop) once it exceeds MAX_LIFETIME. Covers an UNREACHABLE goal (target
                // fleeing faster than the shell), which the snap clamp below cannot fix because the goal keeps receding.
                _store.Age[i] += dt;
                if (_store.Age[i] >= MAX_LIFETIME)
                {
                    _store.Destroy(i);
                    continue;
                }

                // Advance toward goal at THIS projectile's per-unit speed (Story 3.12 — was the global PROJECTILE_SPEED).
                // DW-25 snap-to-goal clamp: when this tick's step would reach or pass the goal, land EXACTLY on the goal
                // instead of overshooting it — a high-speed shell on final approach to a reachable target converges next
                // tick (distSqr == 0 ≤ HIT_SQR) instead of orbiting forever. The non-overshoot else branch is kept
                // byte-identical to the pre-DW-25 expression (Fixed multiply is not associative — re-ordering the
                // operands would silently move the DeliveryScenario golden).
                Fixed     dist = delta.Magnitude();
                FixedVec3 dir  = delta / dist;
                Fixed     step = _store.Speed[i] * dt;
                if (step >= dist)
                    _store.Position[i] = goalPos;
                else
                    _store.Position[i] = _store.Position[i] + dir * _store.Speed[i] * dt;
            }
        }

        /// <summary>Resolve projectile damage on a live target. Destroys target if HP reaches zero.</summary>
        private void ApplyHit(EntityWorld world, int projId, int targetId)
        {
            Fixed splashRadius = _store.SplashRadius[projId];
            bool  isSplash     = splashRadius > Fixed.Zero;

            // Emit hit event at the impact position — BEFORE Apply, to preserve event order (Story 1.6 AC2).
            _events?.Push(isSplash ? CombatEventType.SplashHit : CombatEventType.RangedHit,
                          _store.Position[projId], world.FactionOf[targetId], _store.Feedback[projId]); // Story 2.7 SD-4: firing unit's override, snapshotted at Spawn; Story 11.4: stamp the victim faction

            // Primary hit uses the armor SNAPSHOT captured at spawn (_store.TargetArmor), not live armor.
            // Story 7.5: the attacker ref snapshotted at Spawn rides along for kill attribution (event.killer).
            // Story 15-23 (DW-775): SourceId is a PACKED ref resolved with the ATTRIBUTION rule — a dead-but-not-
            // recycled attacker keeps its kill credit (a unit that dies after firing the killing shot still owns the
            // kill), while a RECYCLED slot degrades to -1 (unknown) instead of crediting the new occupant.
            int attributedSource = world.TryResolveRefIncludingDead(_store.SourceId[projId], out int srcEnt) ? srcEnt : -1;
            var ctx = new DamageContext(world, targetId, _store.TargetArmor[projId],
                                        _store.Owner[projId], _table, _events, _stats, _deaths,
                                        attackerId: attributedSource, dslSimEvents: _dslSimEvents,
                                        isWeaponHit: true); // Story 15-24b: the PRIMARY tracked impact is a weapon hit — the victim's dodge rolls at arrival (the splash ring below stays non-dodgeable area damage)
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
            _events?.Push(CombatEventType.RangedHit, _store.Position[projId], _buildings!.FactionOf[buildingId], _store.Feedback[projId]); // Story 11.4: stamp the victim building's faction
            DamageResolver.ApplyToBuilding(_buildings!, buildingId, _store.Damage[projId], _store.DmgType[projId],
                                           _table, _events, _store.Owner[projId], _stats); // Story 11.2 — credit the razing faction
        }

        /// <summary>
        /// DW-444 — the SINGLE "is this entity friendly to the shell's owner?" test: true for the owner's OWN faction
        /// and for any ALLIED faction. Shared by the primary-target validity check in <see cref="Tick"/> and by
        /// <see cref="ApplySplash"/>, so one impact can never damage on one path what it spares on the other.
        /// A null / FFA alliance mask reduces this to the same-faction term (byte-identical to pre-Story-9.14);
        /// <see cref="Faction.Neutral"/> is never allied to a player faction, so it stays a legal target on both paths.
        /// <para>The caller MUST have established that <paramref name="victim"/> is a live entity id (this indexes
        /// <c>world.FactionOf</c> without a bounds check, exactly like the rest of the damage-delivery path).</para>
        /// </summary>
        private bool IsFriendlyToOwner(EntityWorld world, Faction owner, int victim)
        {
            Faction victimFaction = world.FactionOf[victim];
            if (victimFaction == owner) return true;
            return _alliances != null && _alliances.AreAllied(owner, victimFaction);
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
                // Don't splash friendlies — the owner's own faction, and (Story 9.14) any ALLIED faction. DW-444
                // routed both through the shared IsFriendlyToOwner test that the primary-hit validity check also
                // uses (null/FFA ⇒ same-faction only; Neutral is never allied so it still takes splash).
                if (IsFriendlyToOwner(world, owner, i)) continue;

                Fixed distSqr = FixedVec3.SqrDistance(hitPos, world.Position[i]);
                if (distSqr > radiusSqr) continue;

                // Secondary splash targets use LIVE armor (caller-supplied), and emit no pre-hit event.
                // Story 7.5: splash is part of the same impact — the spawn-snapshotted source ref credits its kills
                // too. Story 15-23: the same attribution resolve as the primary half (dead-ok, recycle → -1).
                int splashSource = world.TryResolveRefIncludingDead(_store.SourceId[projId], out int splashSrcEnt)
                                   ? splashSrcEnt : -1;
                var ctx = new DamageContext(world, i, world.ArmorTypeOf[i], owner, _table, _events, _stats, _deaths,
                                            attackerId: splashSource, dslSimEvents: _dslSimEvents);
                DamageResolver.Apply(in ctx, damage, dmgType);
            }
        }
    }
}
