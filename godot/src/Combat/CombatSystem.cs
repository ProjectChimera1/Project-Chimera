#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;  // AbilityRegistry / AbilityDefinition (Story 2.6 on-hit rider)
using ProjectChimera.Effects;           // EffectExecutor / EffectContext / ModifierStore (Story 2.6 on-hit rider)
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
    ///
    /// Status (DW-266): a <see cref="StatusFlags.Stunned"/> unit takes NO combat action at all; a
    /// <see cref="StatusFlags.Disarmed"/> unit still acquires and chases but can never land a hit.
    ///
    /// NON-COMBATANTS (EffectiveAttackDamage == 0, non-gatherer) never enter an acquisition or engagement
    /// path, but they ARE still driven by the pure-MOVEMENT half of the vocabulary — see
    /// <see cref="TickNonCombatant"/> (Story 15.4, DW-242/DW-202).
    /// </summary>
    public class CombatSystem : ISimSystem
    {
        /// <summary>
        /// DW-266 — the statuses that FORBID landing an attack. <see cref="StatusFlags.Disarmed"/> is exactly this
        /// ("cannot attack", the meaning <c>ModifierSystem</c> has always documented); <see cref="StatusFlags.Stunned"/>
        /// subsumes it and is included so the damage choke point is fail-closed even if a future caller reaches it
        /// past the whole-unit stun gate in <see cref="Tick"/>.
        /// </summary>
        private const StatusFlags ATTACK_BLOCKING = StatusFlags.Disarmed | StatusFlags.Stunned;

        private readonly SpatialHash      _spatialHash = new SpatialHash();
        private readonly ProjectileStore  _projectiles;
        private readonly CombatEventQueue? _events;
        private readonly MatchStats?       _stats;
        private readonly DamageTable       _table;

        // Story 2.6 — the on-hit rider (melee-first). Optional: null in bare combat tests (no on-hit run). The
        // executor is DEDICATED (not the ModifierStore's period executor — re-entrancy safety, as for the cast spine).
        private readonly AbilityRegistry?  _registry;
        private readonly ModifierStore?    _modifiers;
        private readonly EffectExecutor    _onHitExecutor = new EffectExecutor();

        // Story 2.9a — the building store, threaded so an explicit AttackBuilding order can damage/raze structures.
        // Optional: null in bare combat tests (building-attack orders no-op when it is absent). Only the public
        // Alive/FactionOf/Position/Health/Count/Destroy members are used (all on BuildingStore, no BuildingSystem).
        private readonly BuildingStore?    _buildings;

        // Story 9.14 — the sim-owned alliance mask, threaded so combat excludes ALLIED factions from acquisition,
        // force-fire, and building-attack. Optional: null in bare combat tests (FFA — no distinct factions ally, so
        // every allied branch is a no-op and the pre-9.14 goldens stay byte-identical). Read-only here.
        private readonly AllianceStore?    _alliances;

        // Story 3.13 — the transient death feed the hero-XP runtime drains. Optional: null in bare combat tests (no XP
        // credited). Threaded into every hitscan DamageContext so a lethal instant hit records the victim's death.
        private readonly DeathFeed?        _deaths;

        // Story 7.13 — the trigger-DSL sim-event feed (unit_damaged raised at the damage site via DamageContext).
        // Wired by SimulationHost after construction (a setter keeps the ctor signature untouched); null ⇒ no raise.
        private DslSimEventFeed? _dslSimEvents;
        /// <summary>Story 7.13 — wire the trigger-DSL sim-event feed so hitscan damage raises unit_damaged.</summary>
        public void SetDslSimEvents(DslSimEventFeed? feed) => _dslSimEvents = feed;

        public CombatSystem(ProjectileStore projectiles, CombatEventQueue? events = null, MatchStats? stats = null,
            DamageTable? table = null, AbilityRegistry? registry = null, ModifierStore? modifiers = null,
            BuildingStore? buildings = null, DeathFeed? deaths = null, AllianceStore? alliances = null)
        {
            _projectiles = projectiles;
            _events      = events;
            _stats       = stats;
            _table       = table ?? DamageTable.Default;
            _registry    = registry;   // Story 2.6 (optional — the on-hit rider runs only when both are wired)
            _modifiers   = modifiers;
            _buildings   = buildings;   // Story 2.9a (optional — building attacks no-op when absent, e.g. bare tests)
            _deaths      = deaths;      // Story 3.13 (optional — XP credited only when wired)
            _alliances   = alliances;   // Story 9.14 (optional — FFA/null → no allied exclusion, byte-identical)
        }

        // Squared arrive threshold for AttackMove→Idle + Patrol waypoint advance. Story 2.13 (AC2, D-1): widened
        // from 0.5u to the shared 2u goal-arrival radius so a crowded wave clears the ~1.0u separation-vs-seek
        // equilibrium ring (deferred-work #7) instead of hovering. Single-sourced with OrderQueueSystem so they
        // cannot drift; deliberately NOT MovementSystem's physical stop (that stays 0.5u to preserve melee — see
        // ArrivalTuning). This is a goal-distance transition, never a combat-range gate, so melee is unaffected.
        private static readonly Fixed AMOVE_ARRIVE_SQR = ArrivalTuning.GoalArriveRadiusSqr;

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
                // DW-266 — STUN GATE. A stunned unit is fully incapacitated: no cooldown tick, no acquisition, no
                // chase, no attack, no on-hit rider (all of which live below this line). Sits ABOVE the gatherer and
                // zero-damage guards so it covers EVERY alive entity, worker included. It drops the attack target and
                // clears the Attacking flag (presentation must stop the swing) but deliberately leaves CommandState /
                // MoveTarget / the Moving flag alone — MovementSystem's matching anchor holds the unit in place, and
                // the untouched order resumes by itself when the stun expires. StatusFlagsOf is None for every entity
                // in every recorded golden, so this branch is never entered there and no checksum moves.
                if ((world.StatusFlagsOf[i] & StatusFlags.Stunned) != 0)
                {
                    world.AttackTarget[i] = -1;
                    world.Flags[i]       &= ~EntityFlags.Attacking;
                    continue;
                }
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
                        world.CommandState[i] == UnitCommand.PatrolAppend ||
                        world.CommandState[i] == UnitCommand.AttackBuilding) // Story 2.9a
                        world.CommandState[i] = UnitCommand.Idle;
                    continue;
                }
                // Story 15.4 (DW-242, closing the Story-1.12 edge note): a zero-damage NON-COMBATANT still gets
                // its command routed — through the MOVEMENT-ONLY router, never the engagement branches below.
                // Before 15.4 this was a blanket `continue`, so a zero-damage non-gatherer parked in
                // AttackMove/Patrol/Follow/AttackTarget/AttackBuilding sat inert forever with no system able to
                // advance or normalize the order (and, when AI-owned, leaked permanently out of the wave pool,
                // which only re-counts Idle/Stop units — DW-202).
                // DW-643: asked through the SHARED EntityWorld predicate rather than a local `== Fixed.Zero`. That
                // spelling and AiOpponentSystem's `> Fixed.Zero` conscriptable test were complements only while the
                // stat stayed non-negative; a negative value made this branch call the unit a COMBATANT while the AI
                // called it a non-combatant. Behaviour is unchanged for every non-negative value (which is all of
                // them: the validator bounds authored damage at [0, …) and every writer now floors at zero).
                if (world.IsNonCombatant(i)) // non-combatant
                {
                    TickNonCombatant(world, i, dt);
                    continue;
                }

                switch (world.CommandState[i])
                {
                    case UnitCommand.Move:
                        continue; // pure navigation — no combat processing

                    case UnitCommand.PickupItem:
                        continue; // Story 3.15: a hero walking to a ground item ignores enemies (like Move); ItemSystem drives MoveTarget + the proximity claim

                    case UnitCommand.Build:
                        // Story 15.4 (DW-206): a unit walking to a build site is pure navigation — BuildingSystem
                        // drives it (BuildingSystem.QueueWorkerBuild sets this state and TickWorkerBuild completes
                        // it). It used to fall through to `default:` → TickIdleCombat, which was harmless ONLY
                        // because the gatherer guard above exits first and today only gatherers ever receive Build.
                        // That guard tests GatherState, not "is a worker": any non-gatherer that ever receives a
                        // Build order would auto-chase enemies and have its MoveTarget overwritten mid-walk. This
                        // case is the explicit route, so the invariant no longer rests on an accident of ordering.
                        continue;

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

                    case UnitCommand.AttackBuilding: // Story 2.9a — force-attack an enemy building
                        TickAttackBuildingCombat(world, i, dt);
                        break;

                    case UnitCommand.Patrol:
                        TickPatrolCombat(world, i, dt);
                        break;

                    case UnitCommand.Follow:
                        TickFollowCombat(world, i, dt);
                        break;

                    default: // Idle (and the never-persisted wire-only commands, which never reach a CommandState)
                        TickIdleCombat(world, i, dt);
                        break;
                }
            }
        }

        // ── Non-combatant routing (Story 15.4 — DW-242 / DW-202) ──────────────────
        //
        // A unit with zero EffectiveAttackDamage cannot deal damage, so it stays excluded from every
        // acquisition/engagement path — that long-standing rule is PRESERVED here, deliberately: hoisting the
        // engagement branches instead would make support units auto-attack, and would re-open the AI wave leak
        // from the other side. What a non-combatant is NOT excluded from is MOVEMENT. AttackMove, Patrol and
        // Follow are part navigation, and with no combat tick at all their movement half never ran, so the order
        // could never advance, complete, or normalize — the unit was stuck in it for the rest of the match.
        //
        // The two force-attack orders have no movement half worth running (the unit would chase something it can
        // never damage, forever), so they normalize to Idle — the same disposal the gatherer guard above applies
        // to the same orders.
        //
        // Every other state (Idle / Move / Stop / HoldPosition / Build / PickupItem) is either a stable resting
        // state or driven by another system, so it stays a no-op: byte-identical to the pre-15.4 blanket skip.
        // That is what keeps the committed goldens — whose zero-damage units are all Idle or Move — unmoved.
        private void TickNonCombatant(EntityWorld world, int i, Fixed dt)
        {
            switch (world.CommandState[i])
            {
                case UnitCommand.AttackMove:
                    // Walk the goal leg only, never acquire. ResumeAttackMove normalizes to Idle on arrival, so an
                    // AI-owned non-combatant returns to the wave pool instead of leaking out of it forever (DW-202).
                    world.Flags[i] &= ~EntityFlags.Attacking;
                    ResumeAttackMove(world, i);
                    break;

                case UnitCommand.Patrol:
                    // Walk the route only, never engage en route.
                    world.Flags[i] &= ~EntityFlags.Attacking;
                    ResumePatrol(world, i);
                    break;

                case UnitCommand.Follow:
                    // Follow is pure tracking for EVERY unit — it never acquires and never deals damage (Story
                    // 1.12) — so the combatant body is reused verbatim, including its followed-unit-died→Idle drop.
                    TickFollowCombat(world, i, dt);
                    break;

                case UnitCommand.AttackTarget:
                case UnitCommand.AttackBuilding:
                    // Un-executable for a non-combatant: it would chase and then stand at the target dealing
                    // nothing. Normalize to Idle, clearing both target refs and both flags (the same clean revert
                    // TickAttackBuildingCombat/TickFollowCombat do), so the unit stops being stuck.
                    world.CommandState[i]  = UnitCommand.Idle;
                    world.CommandTarget[i] = -1;
                    world.AttackTarget[i]  = -1;
                    world.Flags[i]        &= ~(EntityFlags.Moving | EntityFlags.Attacking);
                    break;
            }
        }

        // ── Idle ──────────────────────────────────────────────────────────────────
        // Auto-attack nearest in-range enemy; chase globally if none in range.

        private void TickIdleCombat(EntityWorld world, int i, Fixed dt)
        {
            TickCooldown(world, i, dt);

            int target = ValidateOrClearTarget(world, i);
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i, _alliances);
            world.AttackTarget[i] = target;

            if (target < 0)
            {
                // Story 2.13 (AC1.2) — no enemy UNIT in range: try to AUTO-ACQUIRE an in-range enemy BUILDING
                // before falling through to the global unit-chase. Reuses TickAttackBuildingCombat verbatim (set
                // state + building ref; clear the entity-space AttackTarget, which must never hold a building id);
                // the next tick's switch drives the chase/damage/revert.
                int bId = FindNearestEnemyBuildingInRange(world, i);
                if (bId >= 0)
                {
                    world.CommandState[i]  = UnitCommand.AttackBuilding;
                    world.CommandTarget[i] = _buildings!.PackRef(bId); // Story 2.13 D-3: packed ref (golden-neutral at gen 0)
                    world.AttackTarget[i]  = -1;
                    return;
                }

                // No enemy in attack range — advance toward nearest enemy anywhere
                int anyEnemy = _spatialHash.FindNearestEnemyGlobal(world, i, _alliances);
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
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i, _alliances);
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
            // Story 9.14: also reject force-firing an ALLIED faction (AreAllied covers same-faction too when a mask is
            // present). Neutral stays force-fireable — AreAllied(Player,Neutral)==false. Null mask / FFA ⇒ only the
            // same-faction term applies, byte-identical to pre-9.14. The allied read is guarded by the IsAlive term above.
            if (forced < 0 || !world.IsAlive(forced) || forced == i || world.FactionOf[forced] == world.FactionOf[i]
                || (_alliances != null && _alliances.AreAllied(world.FactionOf[i], world.FactionOf[forced])))
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

        // ── AttackBuilding (Story 2.9a, AC2; auto-acquire Story 2.13, AC1) ─────────
        // Force-attack ONE specific enemy building (CommandTarget holds the building REF under
        // CommandState==AttackBuilding). Chase the building's centre point, then deal matrix damage (Fortified) —
        // melee instant, ranged via a real projectile (Task 4b). ENTERED BY: an explicit AttackBuilding order
        // (SelectionSystem picker / AI raze), OR Story 2.13 Idle+AttackMove AUTO-ACQUISITION — when a unit has no
        // enemy UNIT in range but an in-range enemy building, TickIdleCombat/TickAttackMoveCombat set it here
        // (Decision D-6 scopes auto-acquire to Idle+AttackMove; Stop/Hold/Patrol still never auto-acquire buildings).

        private void TickAttackBuildingCombat(EntityWorld world, int i, Fixed dt)
        {
            // VALIDATE FIRST — the guard is MANDATORY. CommandTarget holds a PACKED building ref (Story 2.13 D-3);
            // TryResolveRef validates bounds + Alive + GENERATION in one call, so a stale ref to a since-recycled slot
            // (generation mismatch) or the -1 sentinel fails HERE and reverts cleanly — never ABA-retargeting the new
            // occupant, and never IndexOutOfRange-crashing (BuildingStore has no IsAlive short-circuit). The
            // friendly-faction guard enforces AC2 (a friendly building is NEVER targeted) and AC2.4 gates the
            // Structure domain; a rejected order reverts to Idle with NO attack spent (no TickIdleCombat this tick —
            // that would let it acquire+hit a nearby unit, violating AC2.4).
            if (_buildings == null || !_buildings.TryResolveRef(world.CommandTarget[i], out int b)
                || _buildings.FactionOf[b] == world.FactionOf[i]
                || (_alliances != null && _alliances.AreAllied(world.FactionOf[i], _buildings.FactionOf[b])) // Story 9.14: an ALLIED building is never force-attacked (null/FFA ⇒ no-op; Neutral stays targetable)
                || (world.AttackDomainOf[i] & AttackDomain.Structure) == AttackDomain.None)
            {
                world.CommandState[i]  = UnitCommand.Idle;
                world.CommandTarget[i] = -1;
                // Clear BOTH flags: if the building became invalid while this unit was out-of-range CHASING it
                // (Moving set at the chase branch below), leaving Moving set would drift the unit to the razed
                // building's stale centre for one path. Mirrors TickFollowCombat's revert (:411). (Code-review
                // 2.9a — golden-safe: the anti-building golden reverts from IN range, so Moving is already clear.)
                world.Flags[i]        &= ~(EntityFlags.Moving | EntityFlags.Attacking);
                return;
            }

            TickCooldown(world, i, dt);

            Fixed sqrDist  = FixedVec3.SqrDistance(world.Position[i], _buildings.Position[b]);
            Fixed sqrRange = world.AttackRange[i] * world.AttackRange[i];

            if (sqrDist > sqrRange)
            {
                // Out of range — chase the building's (static) Fixed centre point (AC2.3).
                world.MoveTarget[i] = _buildings.Position[b];
                world.Flags[i]      = (world.Flags[i] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                return;
            }

            world.Flags[i] = (world.Flags[i] | EntityFlags.Attacking) & ~EntityFlags.Moving;
            TryDealBuildingDamage(world, i, b);
        }

        // ── Patrol (Story 1.12) ────────────────────────────────────────────────────
        // Walk an ordered waypoint route, engaging enemies in range exactly like AttackMove, then resuming
        // toward the current waypoint; advance the route on arrival, reversing at both ends.

        private void TickPatrolCombat(EntityWorld world, int i, Fixed dt)
        {
            TickCooldown(world, i, dt);

            int target = ValidateOrClearTarget(world, i);
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i, _alliances);
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
            if (target < 0) target = _spatialHash.FindNearestEnemy(world, i, _alliances);
            world.AttackTarget[i] = target;

            if (target < 0)
            {
                // Story 2.13 (AC1.3) — no enemy UNIT in range: try to AUTO-ACQUIRE an in-range enemy BUILDING
                // before resuming toward the goal. Per Decision D-2 the raze reverts to Idle (the AttackBuilding
                // guard's →Idle), not back to AttackMove; the AI re-waves idle units.
                int bId = FindNearestEnemyBuildingInRange(world, i);
                if (bId >= 0)
                {
                    world.CommandState[i]  = UnitCommand.AttackBuilding;
                    world.CommandTarget[i] = _buildings!.PackRef(bId); // Story 2.13 D-3: packed ref (golden-neutral at gen 0)
                    world.AttackTarget[i]  = -1;
                    world.Flags[i]        &= ~EntityFlags.Attacking;
                    return;
                }

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
        /// Returns the current AttackTarget if it is still a LEGAL target, or clears it and returns -1.
        ///
        /// <para>DW-446 — "still legal" is not the same as "still alive". Entity ids are RECYCLED (EntityWorld keeps a
        /// LIFO free list), so between two ticks the slot a unit is holding as its auto-acquired target can be
        /// re-allocated to a brand-new unit of MY OWN faction or of an ALLIED one (a teammate training into a freed
        /// enemy slot in a 2v2). Acquisition already excludes both (<see cref="SpatialHash.FindNearestEnemy"/>) and the
        /// per-tick FORCED paths re-check both every tick (<see cref="TickAttackTargetCombat"/>,
        /// <see cref="TickAttackBuildingCombat"/>) — Story 9.14 simply never guarded the RETAINED path, so the
        /// attacker would fire on the now-friendly occupant for a tick, violating "an ally is never auto-attacked".
        /// Clearing here hands the caller straight back to <c>FindNearestEnemy</c>, which re-acquires a legal target in
        /// the same tick, so there is no stutter.</para>
        ///
        /// <para>The same-faction term is unconditional (that recycle-into-friendly gap pre-dates alliances — see the
        /// force-fire guard's comment); the allied term is a no-op under a null / FFA mask. Every recorded golden holds
        /// only cross-faction targets acquired through the allied-aware pickers, so no checksum moves.</para>
        /// </summary>
        private int ValidateOrClearTarget(EntityWorld world, int id)
        {
            int target = world.AttackTarget[id];
            if (target < 0) return target;

            // IsAlive comes FIRST and short-circuits, so a stale/out-of-range id never indexes FactionOf.
            if (!world.IsAlive(target)
                || world.FactionOf[target] == world.FactionOf[id]
                || (_alliances != null && _alliances.AreAllied(world.FactionOf[id], world.FactionOf[target])))
            {
                world.AttackTarget[id] = -1;
                world.Flags[id]       &= ~EntityFlags.Attacking;
                return -1;
            }
            return target;
        }

        /// <summary>
        /// Story 2.13 (AC1.1) — deterministic sim-side nearest-enemy-BUILDING search for Idle/AttackMove
        /// auto-acquisition. Linear ascending-id O(≤64) scan of the threaded <see cref="_buildings"/> store,
        /// <see cref="Fixed"/>/int only (the presentation <c>SelectionSystem.FindNearestEnemyBuilding</c> is
        /// Godot/<c>float</c>/<c>Player1</c>-hardcoded and sim-illegal — deliberately NOT reused). Returns the
        /// nearest IN-RANGE, alive, enemy (<c>FactionOf != mine, never Neutral</c>), Structure-attackable building for entity
        /// <paramref name="i"/>, tie-broken by ASCENDING ID (never a float distance); -1 if none, no store, or the
        /// unit cannot hit structures. Range gate: <c>SqrDistance(Position, building.Position) &lt;= AttackRange²</c>.
        /// </summary>
        private int FindNearestEnemyBuildingInRange(EntityWorld world, int i)
        {
            if (_buildings == null) return -1;
            // A unit whose attack_domains exclude Structure never auto-acquires a building (matches the explicit
            // AttackBuilding guard's Structure-domain gate — TickAttackBuildingCombat).
            if ((world.AttackDomainOf[i] & AttackDomain.Structure) == AttackDomain.None) return -1;

            Fixed   sqrRange  = world.AttackRange[i] * world.AttackRange[i];
            Faction myFaction = world.FactionOf[i];
            FixedVec3 myPos   = world.Position[i];

            int   best    = -1;
            Fixed bestSqr = Fixed.Zero;
            int   count   = _buildings.Count;
            for (int b = 0; b < count; b++)
            {
                if (!_buildings.Alive[b]) continue;
                if (_buildings.FactionOf[b] == myFaction || _buildings.FactionOf[b] == Faction.Neutral) continue; // enemy ONLY — never me, never Neutral (2.13 review, Alec): matches the AI raze picker + SelectionSystem convention; Neutral is use/claim, not auto-attack
                if (_alliances != null && _alliances.AreAllied(myFaction, _buildings.FactionOf[b])) continue; // Story 9.14: never auto-acquire an ALLIED building (null/FFA ⇒ no-op)
                Fixed sqrDist = FixedVec3.SqrDistance(myPos, _buildings.Position[b]);
                if (sqrDist > sqrRange) continue;                                // out of attack range
                if (best < 0 || sqrDist < bestSqr) { best = b; bestSqr = sqrDist; } // strict < ⇒ ascending-id tie-break
            }
            return best;
        }

        /// <summary>
        /// Fires an attack from <paramref name="attacker"/> toward <paramref name="target"/> if
        /// the cooldown has expired.
        ///
        /// Story 3.12: branches on the attacker's authored <c>world.Delivery</c> (NOT a range threshold). A
        /// <see cref="AttackDelivery.Projectile"/> unit spawns a tracking projectile — travelling at its per-unit
        /// <c>world.ProjectileSpeed</c> — that resolves damage on arrival, regardless of AttackRange; a
        /// <see cref="AttackDelivery.Hitscan"/> unit deals instant damage and destroys the target if HP reaches zero,
        /// regardless of AttackRange.
        /// </summary>
        private void TryDealDamage(EntityWorld world, int attacker, int target)
        {
            // DW-266 — DISARM GATE. Checked BEFORE the cooldown is consumed, so a disarm neither burns the attack
            // timer, nor spawns a projectile, nor runs the Story 2.6 on-hit rider (all downstream of this line). The
            // unit keeps its target and its chase — it simply cannot land a blow — so it strikes on the first tick
            // after the debuff drops. Clearing Attacking stops the presentation swing.
            if ((world.StatusFlagsOf[attacker] & ATTACK_BLOCKING) != 0)
            {
                world.Flags[attacker] &= ~EntityFlags.Attacking;
                return;
            }

            if (world.AttackCooldown[attacker] > Fixed.Zero) return;

            world.AttackCooldown[attacker] = world.AttackSpeed[attacker];

            if (world.Delivery[attacker] == AttackDelivery.Projectile)
            {
                // Projectile delivery — spawn a tracking projectile at the unit's per-unit speed; damage resolved by
                // ProjectileSystem on hit.
                _projectiles.Spawn(
                    world.Position[attacker],
                    target,
                    world.Position[target],
                    world.EffectiveAttackDamage[attacker],
                    world.DamageTypeOf[attacker],
                    world.ArmorTypeOf[target],
                    world.FactionOf[attacker],
                    world.ProjectileSpeed[attacker], // Story 3.12 — per-unit projectile speed
                    world.SplashRadius[attacker],
                    world.FeedbackProfile[attacker], // Story 2.7 SD-4: snapshot the firing unit's override (attacker id is lost by impact)
                    sourceId: attacker);             // Story 7.5 — snapshot the attacker id beside Owner for kill attribution at impact
            }
            else
            {
                // Hitscan — instant damage. Event BEFORE Apply; attacker-cleanup AFTER, gated on death —
                // operation order preserved exactly so the golden checksums stay byte-identical (Story 1.6 AC2).
                _events?.Push(CombatEventType.MeleeHit, world.Position[target], world.FactionOf[target], world.FeedbackProfile[attacker]); // Story 2.7; Story 11.4: stamp the victim faction

                var ctx = new DamageContext(world, target, world.ArmorTypeOf[target],
                                            world.FactionOf[attacker], _table, _events, _stats, _deaths,
                                            attackerId: attacker, dslSimEvents: _dslSimEvents); // Story 7.5 attacker; 7.13 unit_damaged feed
                if (DamageResolver.Apply(in ctx, world.EffectiveAttackDamage[attacker], world.DamageTypeOf[attacker]))
                {
                    world.AttackTarget[attacker] = -1;
                    world.Flags[attacker]       &= ~EntityFlags.Attacking;
                }

                // Story 2.6 — the ON-HIT rider (melee-first, AC2). Fires on the landed hit and not otherwise (driven by
                // the same AttackCooldown gate above — no new counter). primaryTarget = the struck unit; runs AFTER the
                // base damage resolves, so a lethal base hit leaves the rider's IsAlive-guarded leaves as safe no-ops.
                RunOnHit(world, attacker, target);
            }
        }

        /// <summary>
        /// Story 2.9a (AC2/AC2.6) — deal cooldown-gated damage to a BUILDING, mirroring <see cref="TryDealDamage"/>'s
        /// hitscan/projectile split. Story 3.12: branches on the attacker's authored <c>world.Delivery</c> (NOT a range
        /// threshold). Hitscan resolves instant matrix damage via the shared <see cref="DamageResolver.ApplyToBuilding"/>
        /// helper; Projectile spawns a real building-target projectile — at the unit's per-unit speed — that flies to the
        /// building and resolves on impact (<see cref="ProjectileSystem"/>, D-4). Buildings are always
        /// <see cref="ArmorType.Fortified"/> (D-3) with no flat armor. Caller guarantees <c>_buildings != null</c> and a
        /// valid, alive, in-range <paramref name="b"/> (validated in <see cref="TickAttackBuildingCombat"/>).
        /// </summary>
        private void TryDealBuildingDamage(EntityWorld world, int attacker, int b)
        {
            // DW-266 — the same DISARM GATE as the unit path (a disarmed siege unit cannot raze either), before the
            // cooldown is consumed so the refusal is free of side effects.
            if ((world.StatusFlagsOf[attacker] & ATTACK_BLOCKING) != 0)
            {
                world.Flags[attacker] &= ~EntityFlags.Attacking;
                return;
            }

            if (world.AttackCooldown[attacker] > Fixed.Zero) return;

            world.AttackCooldown[attacker] = world.AttackSpeed[attacker];

            if (world.Delivery[attacker] == AttackDelivery.Projectile)
            {
                // Projectile — spawn a projectile that flies to the building and resolves Fortified matrix damage on impact.
                _projectiles.Spawn(
                    world.Position[attacker],
                    _buildings!.PackRef(b),         // Story 2.13 D-3: PACKED building ref (targetIsBuilding disambiguates it from an entity id)
                    _buildings!.Position[b],
                    world.EffectiveAttackDamage[attacker],
                    world.DamageTypeOf[attacker],
                    ArmorType.Fortified,            // D-3: 100% of building JSON authors Fortified; no per-building armor SoA
                    world.FactionOf[attacker],
                    world.ProjectileSpeed[attacker], // Story 3.12 — per-unit projectile speed
                    world.SplashRadius[attacker],
                    world.FeedbackProfile[attacker],
                    targetIsBuilding: true,         // Story 2.9a (Task 4b)
                    sourceId: attacker);            // Story 7.5 — snapshot the attacker id (parity with the unit-target spawn)
            }
            else
            {
                // Hitscan — instant matrix damage via the SINGLE shared building-damage entry point.
                _events?.Push(CombatEventType.MeleeHit, _buildings!.Position[b], _buildings!.FactionOf[b], world.FeedbackProfile[attacker]); // Story 11.4: stamp the victim building's faction
                if (DamageResolver.ApplyToBuilding(_buildings!, b, world.EffectiveAttackDamage[attacker],
                                                   world.DamageTypeOf[attacker], _table, _events,
                                                   world.FactionOf[attacker], _stats)) // Story 11.2 — credit the razing faction
                {
                    // Building razed — drop the Attacking flag now; the next tick's guard reverts CommandState to Idle
                    // (BuildingStore has no IsAlive to catch it, but the in-tick bounds+Alive guard does — see above).
                    world.Flags[attacker] &= ~EntityFlags.Attacking;
                }
            }
        }

        /// <summary>
        /// Story 2.6 ON-HIT rider (AC2, melee-first). If <paramref name="attacker"/> carries an on-hit passive
        /// (<see cref="EntityWorld.OnHitAbilityIndex"/> set) and the rider machinery is wired (<see cref="_registry"/>
        /// + <see cref="_modifiers"/> — null in bare combat tests), run its effect graph with the struck
        /// <paramref name="target"/> as the primary target. No-op when no on-hit passive exists (so the existing
        /// passive-free goldens never enter this path). Uses a DEDICATED executor (never the ModifierStore's period
        /// executor) and the already-rebuilt Tick spatial hash (read-only) for any rider SearchArea.
        /// </summary>
        private void RunOnHit(EntityWorld world, int attacker, int target)
        {
            if (_registry is null || _modifiers is null) return;        // on-hit not wired (bare combat tests)
            int idx = world.OnHitAbilityIndex[attacker];
            if (idx < 0 || idx >= _registry.Count) return;             // attacker has no on-hit passive
            AbilityDefinition onHit = _registry.Get(idx);
            var ctx = new EffectContext(world, casterId: attacker, primaryTargetId: target,
                                        casterFaction: world.FactionOf[attacker], _table,
                                        spatial: _spatialHash, _events, _stats, modifierStore: _modifiers,
                                        alliances: _alliances); // Story 9.14: team-aware Ally/Enemy on the on-hit rider
            _onHitExecutor.Run(onHit.EffectGraph, in ctx);
        }
    }
}
