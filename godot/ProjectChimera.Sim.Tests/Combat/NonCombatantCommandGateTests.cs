#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 15.4 — the non-combatant command gate (deferred work DW-202 / DW-206 / DW-242).
    ///
    /// Three defects with one shared root: "zero attack damage" was treated as "no command processing at all".
    ///   • DW-242 — <see cref="CombatSystem"/>'s <c>EffectiveAttackDamage == 0 → continue</c> sat ABOVE the whole
    ///     command switch, so a zero-damage non-gatherer in AttackMove/Patrol/Follow/AttackTarget/AttackBuilding
    ///     sat inert with nothing able to advance, complete, or normalize the order.
    ///   • DW-202 — <see cref="AiOpponentSystem"/> counted those same units as combat units and conscripted them
    ///     into waves. Flipped to AttackMove they got no combat tick, so they never returned to Idle/Stop and were
    ///     leaked from the available pool permanently; meanwhile the inflated count launched under-strength waves.
    ///   • DW-206 — <see cref="UnitCommand.Build"/> fell to the switch's <c>default:</c> (idle auto-combat).
    ///
    /// Godot-free, <see cref="Fixed"/>-only, ascending-id — runs on every OS leg. The counterpart assertions
    /// ("...StillNeverAttacks") are the fence: routing MOVEMENT for a non-combatant must never leak into routing
    /// ENGAGEMENT for it.
    /// </summary>
    public class NonCombatantCommandGateTests
    {
        /// <summary>One tick at the 30 tps sim rate. Only affects cooldown decrement; no assertion depends on it.</summary>
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static (EntityWorld world, CombatSystem combat) NewSim()
            => (new EntityWorld(), new CombatSystem(new ProjectileStore()));

        /// <summary>A zero-damage NON-COMBATANT (a support/hauler-shaped unit): full move stats, no attack.</summary>
        private static int NonCombatant(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AttackRange[id] = Fixed.FromInt(2); // in-range enemies exist — only the ZERO damage must stop it
            w.AttackSpeed[id] = Fixed.Zero;       // cooldown never blocks: any engagement would fire immediately
            Assert.Equal(Fixed.Zero.Raw, w.EffectiveAttackDamage[id].Raw); // the Create default this suite relies on
            return id;
        }

        /// <summary>A melee combat unit (range ≤ 2.5 ⇒ hitscan, so damage is instant and assertable in one tick).</summary>
        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f, int dmg = 10, int range = 2)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(dmg);
            w.AttackRange[id]  = Fixed.FromInt(range);
            w.AttackSpeed[id]  = Fixed.Zero; // fires every tick
            w.Delivery[id]     = AttackDelivery.Hitscan;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        private static void SetPatrolRoute(EntityWorld w, int id, params FixedVec3[] wps)
        {
            int b = id * EntityWorld.MAX_PATROL_WAYPOINTS;
            for (int k = 0; k < wps.Length; k++) w.PatrolWaypoints[b + k] = wps[k];
            w.PatrolCount[id] = (byte)wps.Length;
            w.PatrolIndex[id] = 1;
            w.PatrolDir[id]   = 1;
        }

        private static void AssertVec(FixedVec3 expected, FixedVec3 actual)
        {
            Assert.Equal(expected.X.Raw, actual.X.Raw);
            Assert.Equal(expected.Z.Raw, actual.Z.Raw);
        }

        // ── DW-242 — AttackMove: the movement half runs, and the order can finally COMPLETE ────────────────

        [Fact]
        public void NonCombatant_InAttackMove_WalksTowardItsGoal()
        {
            var (w, combat) = NewSim();
            int u = NonCombatant(w, V(0, 0), Faction.Player1);
            w.CommandState[u] = UnitCommand.AttackMove;
            w.CommandGoal[u]  = V(20, 0);

            combat.Tick(w, Dt);

            AssertVec(V(20, 0), w.MoveTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0,
                "a non-combatant under AttackMove must still be steered toward its goal (DW-242)");
        }

        [Fact]
        public void NonCombatant_InAttackMove_NormalizesToIdleOnArrival()
        {
            var (w, combat) = NewSim();
            int u = NonCombatant(w, V(0, 0), Faction.Player1);
            w.CommandState[u] = UnitCommand.AttackMove;
            w.CommandGoal[u]  = V(0, 0);   // already AT the goal (inside the shared arrive radius)
            w.Flags[u]       |= EntityFlags.Moving;

            combat.Tick(w, Dt);

            // The DW-202 leak closes HERE: only a unit at Idle/Stop is ever re-counted into the AI's wave pool,
            // so a non-combatant that could never leave AttackMove was gone from that pool for the whole match.
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            Assert.True((w.Flags[u] & EntityFlags.Moving) == 0, "arrival must clear Moving");
        }

        [Fact]
        public void NonCombatant_InAttackMove_StillNeverAttacks()
        {
            var (w, combat) = NewSim();
            int u     = NonCombatant(w, V(0, 0), Faction.Player1);
            int enemy = Combatant(w, V(1, 0), Faction.Player2); // well inside the non-combatant's range 2
            w.CommandState[u] = UnitCommand.AttackMove;
            w.CommandGoal[u]  = V(20, 0);

            Fixed before = w.Health[enemy];
            combat.Tick(w, Dt);

            Assert.Equal(before.Raw, w.Health[enemy].Raw);
            Assert.Equal(-1, w.AttackTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Attacking) == 0);
            AssertVec(V(20, 0), w.MoveTarget[u]); // it walks past, it does not stop to fight
        }

        // ── DW-242 — Patrol: the route advances; the lane enemy is still ignored ───────────────────────────

        [Fact]
        public void NonCombatant_OnPatrol_WalksItsRoute()
        {
            var (w, combat) = NewSim();
            int u = NonCombatant(w, V(0, 0), Faction.Player1);
            SetPatrolRoute(w, u, V(0, 0), V(10, 0));
            w.CommandState[u] = UnitCommand.Patrol;

            combat.Tick(w, Dt);

            AssertVec(V(10, 0), w.MoveTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0,
                "a non-combatant under Patrol must still walk its route (DW-242)");
        }

        [Fact]
        public void NonCombatant_OnPatrol_AdvancesAndReversesAtTheEnd()
        {
            var (w, combat) = NewSim();
            int u = NonCombatant(w, V(10, 0), Faction.Player1); // standing ON the final waypoint
            SetPatrolRoute(w, u, V(0, 0), V(10, 0));
            w.CommandState[u] = UnitCommand.Patrol;

            combat.Tick(w, Dt);

            Assert.Equal(0, (int)w.PatrolIndex[u]);   // advanced back down the route…
            Assert.Equal(-1, (int)w.PatrolDir[u]);    // …after reversing at the top leg
            AssertVec(V(0, 0), w.MoveTarget[u]);
        }

        [Fact]
        public void NonCombatant_OnPatrol_StillNeverAttacks()
        {
            var (w, combat) = NewSim();
            int u     = NonCombatant(w, V(0, 0), Faction.Player1);
            int enemy = Combatant(w, V(1, 0), Faction.Player2); // on the patrol lane, in range
            SetPatrolRoute(w, u, V(0, 0), V(10, 0));
            w.CommandState[u] = UnitCommand.Patrol;

            Fixed before = w.Health[enemy];
            combat.Tick(w, Dt);

            Assert.Equal(before.Raw, w.Health[enemy].Raw);
            Assert.Equal(-1, w.AttackTarget[u]);
            AssertVec(V(10, 0), w.MoveTarget[u]); // keeps walking the lane
        }

        // ── DW-242 — Follow: pure tracking, identical to the combatant body ───────────────────────────────

        [Fact]
        public void NonCombatant_Following_RePathsTowardTheFriendlyBeyondTheLeash()
        {
            var (w, combat) = NewSim();
            int u      = NonCombatant(w, V(0, 0), Faction.Player1);
            int friend = Combatant(w, V(10, 0), Faction.Player1); // 10u > the 3u Follow leash
            w.CommandState[u]  = UnitCommand.Follow;
            w.CommandTarget[u] = friend;

            combat.Tick(w, Dt);

            AssertVec(w.Position[friend], w.MoveTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0,
                "a non-combatant under Follow must still track its escort (DW-242)");
        }

        [Fact]
        public void NonCombatant_Following_DropsToIdleWhenTheFollowedUnitDies()
        {
            var (w, combat) = NewSim();
            int u      = NonCombatant(w, V(0, 0), Faction.Player1);
            int friend = Combatant(w, V(10, 0), Faction.Player1);
            w.CommandState[u]  = UnitCommand.Follow;
            w.CommandTarget[u] = friend;
            w.Destroy(friend);

            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            Assert.Equal(-1, w.CommandTarget[u]);
        }

        // ── DW-242 — the two force-attack orders normalize instead of hanging ─────────────────────────────

        [Fact]
        public void NonCombatant_OrderedToAttackTarget_NormalizesToIdle()
        {
            var (w, combat) = NewSim();
            int u     = NonCombatant(w, V(0, 0), Faction.Player1);
            int enemy = Combatant(w, V(10, 0), Faction.Player2); // a perfectly VALID force-fire target
            w.CommandState[u]  = UnitCommand.AttackTarget;
            w.CommandTarget[u] = enemy;
            w.Flags[u]        |= EntityFlags.Moving;

            Fixed before = w.Health[enemy];
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            Assert.Equal(-1, w.CommandTarget[u]);
            Assert.Equal(-1, w.AttackTarget[u]);
            Assert.True((w.Flags[u] & (EntityFlags.Moving | EntityFlags.Attacking)) == 0,
                "the revert must clear Moving too, or the unit drifts toward the target it can never damage");
            Assert.Equal(before.Raw, w.Health[enemy].Raw);
        }

        [Fact]
        public void NonCombatant_OrderedToAttackBuilding_NormalizesToIdle()
        {
            var w         = new EntityWorld();
            var buildings = new BuildingStore();
            var combat    = new CombatSystem(new ProjectileStore(), buildings: buildings);

            int b = buildings.Create(V(10, 0), Faction.Player2, BuildingType.Barracks); // a VALID enemy building
            int u = NonCombatant(w, V(0, 0), Faction.Player1);
            w.CommandState[u]  = UnitCommand.AttackBuilding;
            w.CommandTarget[u] = buildings.PackRef(b);

            Fixed before = buildings.Health[b];
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            Assert.Equal(-1, w.CommandTarget[u]);
            Assert.Equal(before.Raw, buildings.Health[b].Raw);
        }

        // ── DW-242 fence — the resting states are untouched (this is what keeps the goldens byte-identical) ─

        [Fact]
        public void NonCombatant_AtIdle_NeverAutoAttacksAndNeverChases()
        {
            var (w, combat) = NewSim();
            int u       = NonCombatant(w, V(0, 0), Faction.Player1);
            int inRange = Combatant(w, V(1, 0), Faction.Player2);
            int far     = Combatant(w, V(50, 0), Faction.Player2);

            Fixed hpNear = w.Health[inRange];
            Fixed hpFar  = w.Health[far];
            FixedVec3 moveTargetBefore = w.MoveTarget[u];
            EntityFlags flagsBefore    = w.Flags[u];

            combat.Tick(w, Dt);

            Assert.Equal(hpNear.Raw, w.Health[inRange].Raw);
            Assert.Equal(hpFar.Raw,  w.Health[far].Raw);
            Assert.Equal(-1, w.AttackTarget[u]);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            AssertVec(moveTargetBefore, w.MoveTarget[u]);
            Assert.Equal(flagsBefore, w.Flags[u]);
        }

        [Fact]
        public void NonCombatant_UnderMove_IsUntouchedByCombat()
        {
            var (w, combat) = NewSim();
            int u = NonCombatant(w, V(0, 0), Faction.Player1);
            Combatant(w, V(1, 0), Faction.Player2);
            w.CommandState[u] = UnitCommand.Move;
            w.MoveTarget[u]   = V(20, 0);
            w.Flags[u]       |= EntityFlags.Moving;

            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Move, w.CommandState[u]);
            AssertVec(V(20, 0), w.MoveTarget[u]);
            Assert.Equal(-1, w.AttackTarget[u]);
        }

        // ── DW-206 — Build no longer falls through to the idle auto-combat default ────────────────────────

        [Fact]
        public void NonGathererUnderBuildOrder_DoesNotAttackAnInRangeEnemy()
        {
            var (w, combat) = NewSim();
            // A damage-bearing NON-gatherer (GatherState stays Inactive, so the gatherer guard above does NOT
            // catch it) walking to a build site — the exact shape DW-206 flagged.
            int builder = Combatant(w, V(0, 0), Faction.Player1);
            int enemy   = Combatant(w, V(1, 0), Faction.Player2);
            w.CommandState[builder] = UnitCommand.Build;
            w.MoveTarget[builder]   = V(20, 0); // the build site
            w.Flags[builder]       |= EntityFlags.Moving;

            Fixed before = w.Health[enemy];
            combat.Tick(w, Dt);

            Assert.Equal(before.Raw, w.Health[enemy].Raw);
            Assert.Equal(-1, w.AttackTarget[builder]);
            Assert.True((w.Flags[builder] & EntityFlags.Attacking) == 0);
        }

        [Fact]
        public void NonGathererUnderBuildOrder_KeepsItsBuildSiteMoveTarget()
        {
            var (w, combat) = NewSim();
            int builder = Combatant(w, V(0, 0), Faction.Player1);
            Combatant(w, V(50, 0), Faction.Player2); // far away ⇒ the old default: chase = overwrite MoveTarget
            w.CommandState[builder] = UnitCommand.Build;
            w.MoveTarget[builder]   = V(20, 0);
            w.Flags[builder]       |= EntityFlags.Moving;

            combat.Tick(w, Dt);

            AssertVec(V(20, 0), w.MoveTarget[builder]);
            Assert.Equal(UnitCommand.Build, w.CommandState[builder]);
        }

        // ── DW-202 — the AI's wave pool: count and dispatch both exclude non-combatants ───────────────────

        /// <summary>The Normal attack threshold read symbolically (DW-125 convention) — never the literal 5.</summary>
        private static readonly int NormalAttackThreshold =
            AiOpponentSystem.DifficultyProfile(AiDifficulty.Normal).AttackThreshold;

        /// <summary>
        /// A bare AI host: <see cref="Faction.Player2"/> is the AI. No ore and (by default) no buildings, so every
        /// build/expand score is 0 and the AI's only reachable actions are the two wave dispatchers — which is
        /// exactly what these tests measure. The AI is ticked DIRECTLY (not via <c>StepOnce</c>) so the assertions
        /// read the AI's decision, not a tick's worth of movement and combat on top of it.
        /// </summary>
        private static SimulationHost NewAiHost()
            => SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                     new FactionDefinition(), new FactionDefinition(),
                                     damageTable: null, aiLevel: AiDifficulty.Normal);

        /// <summary>An idle, damage-bearing Player2 wave unit — conscriptable by construction.</summary>
        private static int AiWaveUnit(EntityWorld w, FixedVec3 pos)
        {
            int id = w.Create(pos, Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(6);
            w.AttackRange[id]  = Fixed.FromInt(2);
            w.AttackSpeed[id]  = Fixed.FromInt(1);
            w.DamageTypeOf[id] = DamageType.Siege;      // razes a Fortified building without extra tuning
            w.ArmorTypeOf[id]  = ArmorType.Medium;
            return id;
        }

        [Fact]
        public void AiWave_LeavesZeroDamageUnitsBehind()
        {
            SimulationHost host = NewAiHost();
            EntityWorld    w    = host.World;

            var wave = new int[NormalAttackThreshold];
            for (int i = 0; i < wave.Length; i++) wave[i] = AiWaveUnit(w, V(40, i * 2 - 4));
            int support = NonCombatant(w, V(40, 20), Faction.Player2);

            host.Ai.Tick(w, Dt);

            foreach (int id in wave)
                Assert.Equal(UnitCommand.AttackMove, w.CommandState[id]); // the wave DID launch
            Assert.Equal(UnitCommand.Idle, w.CommandState[support]);
            AssertVec(w.Position[support], w.CommandGoal[support]);       // never re-goaled at the P1 base
        }

        [Fact]
        public void AiWave_DoesNotLaunchOnAZeroDamagePaddedCount()
        {
            SimulationHost host = NewAiHost();
            EntityWorld    w    = host.World;

            // One unit SHORT of the threshold in real combat units, padded well past it with non-combatants.
            var wave = new int[NormalAttackThreshold - 1];
            for (int i = 0; i < wave.Length; i++) wave[i] = AiWaveUnit(w, V(40, i * 2 - 4));
            var support = new int[3];
            for (int i = 0; i < support.Length; i++) support[i] = NonCombatant(w, V(40, 20 + i), Faction.Player2);

            host.Ai.Tick(w, Dt);

            foreach (int id in wave)
                Assert.Equal(UnitCommand.Idle, w.CommandState[id]);
            foreach (int id in support)
                Assert.Equal(UnitCommand.Idle, w.CommandState[id]);
        }

        [Fact]
        public void AiRaze_LeavesZeroDamageUnitsBehind()
        {
            SimulationHost host = NewAiHost();
            EntityWorld    w    = host.World;

            // The AI owns its base (the stall-breaker's fence) but no production building and no ore, and the
            // enemy has a base left with no defenders ⇒ ScoreRazeBuildings commits the remnant.
            int p2cc = host.Buildings.Create(V(0, 0), Faction.Player2, BuildingType.CommandCenter);
            host.Buildings.ConstructionTimer[p2cc] = Fixed.Zero;
            int p1cc = host.Buildings.Create(V(10, 0), Faction.Player1, BuildingType.CommandCenter);
            host.Buildings.ConstructionTimer[p1cc] = Fixed.Zero;

            int razer   = AiWaveUnit(w, V(5, 0));
            int support = NonCombatant(w, V(5, 3), Faction.Player2);

            host.Ai.Tick(w, Dt);

            Assert.Equal(UnitCommand.AttackBuilding, w.CommandState[razer]); // the raze DID commit
            Assert.Equal(UnitCommand.Idle, w.CommandState[support]);
            Assert.Equal(-1, w.CommandTarget[support]);
        }

        [Fact]
        public void AiWave_ReclaimsANonCombatantThatWasAlreadyStuckInAttackMove()
        {
            // The full DW-202 leak, end to end: a non-combatant already parked in AttackMove (the state the
            // pre-fix AI put it in) is walked home by CombatSystem and normalized back to Idle, so the AI's
            // availability scan sees it again instead of it vanishing from the pool for the rest of the match.
            SimulationHost host   = NewAiHost();
            EntityWorld    w      = host.World;
            var            combat = new CombatSystem(new ProjectileStore());

            int support = NonCombatant(w, V(40, 0), Faction.Player2);
            w.CommandState[support] = UnitCommand.AttackMove;
            w.CommandGoal[support]  = V(40, 0); // standing at its goal — one tick is enough to normalize

            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[support]);
        }
    }
}
