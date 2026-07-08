#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 1.12 (DG-1 / FR-53) — behavior tests for the full RTS command vocabulary: single-target
    /// AttackTarget (AC1c/AC2/AC3), Patrol (AC4a), Follow (AC4b), and the Hold-vs-Stop distinction (AC5).
    /// Each builds a small <see cref="EntityWorld"/> + <see cref="CombatSystem"/> (and a
    /// <see cref="MovementSystem"/> where the AC needs physical displacement) directly — no Godot, no
    /// SimulationHost. All scenario state is authored in <see cref="Fixed"/> (no <c>Fixed.FromFloat</c>) and
    /// iterates ascending-id, so these run on every OS including the WSL cross-platform leg.
    /// </summary>
    public class CommandVocabularyTests
    {
        // 1/30 s — one tick at the 30 tps sim rate. Only affects cooldown decrement; assertions never depend on it.
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        // ── AC1c — each new command routes to a DISTINCT branch, NOT the default Idle fall-through ──────────

        [Fact]
        public void NewCommands_RouteToDistinctBranches_NotIdleFallThrough()
        {
            // AttackTarget: chase the FORCED (far) target even though a nearer enemy exists — Idle would chase the nearer.
            {
                var (w, combat) = NewSim();
                int a    = Combatant(w, V(0, 0, 0), Faction.Player1, range: 2);
                /* nearer decoy */ Combatant(w, V(1, 0, 0), Faction.Player2);
                int far  = Combatant(w, V(10, 0, 0), Faction.Player2);
                w.CommandState[a]  = UnitCommand.AttackTarget;
                w.CommandTarget[a] = far;

                combat.Tick(w, Dt);

                AssertVec(w.Position[far], w.MoveTarget[a]); // chasing the forced FAR target, not the decoy
                Assert.True((w.Flags[a] & EntityFlags.Moving) != 0);
            }

            // Patrol: with NO enemies at all, keeps moving its lane — Idle would stand still (no enemy to chase).
            {
                var (w, combat) = NewSim();
                int p = Combatant(w, V(0, 0, 0), Faction.Player1);
                SetPatrolRoute(w, p, V(0, 0, 0), V(10, 0, 0));
                w.CommandState[p] = UnitCommand.Patrol;

                combat.Tick(w, Dt);

                Assert.True((w.Flags[p] & EntityFlags.Moving) != 0);
                AssertVec(V(10, 0, 0), w.MoveTarget[p]);
            }

            // Follow: tracks a FRIENDLY — Idle never targets friendlies (with no enemy it would idle).
            {
                var (w, combat) = NewSim();
                int f      = Combatant(w, V(0, 0, 0), Faction.Player1);
                int friend = Combatant(w, V(10, 0, 0), Faction.Player1);
                w.CommandState[f]  = UnitCommand.Follow;
                w.CommandTarget[f] = friend;

                combat.Tick(w, Dt);

                Assert.True((w.Flags[f] & EntityFlags.Moving) != 0);
                AssertVec(w.Position[friend], w.MoveTarget[f]);
            }
        }

        // ── AC2 — single-target force-fire ignores a nearer enemy ──────────────────────────────────────────

        [Fact]
        public void AttackTarget_ForceFires_IgnoringNearerEnemy()
        {
            var (w, combat) = NewSim();
            int attacker = Combatant(w, V(0, 0, 0), Faction.Player1, range: 2);
            int near     = Combatant(w, V(1, 0, 0), Faction.Player2);  // IN range — Idle would attack this one
            int forced   = Combatant(w, V(10, 0, 0), Faction.Player2); // OUT of range — the player's chosen target

            w.CommandState[attacker]  = UnitCommand.AttackTarget;
            w.CommandTarget[attacker] = forced;

            Fixed nearHpBefore = w.Health[near];
            combat.Tick(w, Dt);

            // Chases ONLY the forced target; never touches the nearer enemy.
            AssertVec(w.Position[forced], w.MoveTarget[attacker]);
            Assert.Equal(nearHpBefore.Raw, w.Health[near].Raw);
            Assert.Equal(forced, w.AttackTarget[attacker]);
        }

        [Fact]
        public void AttackTarget_DamagesForcedTargetInRange_NotNearer()
        {
            var (w, combat) = NewSim();
            int attacker = Combatant(w, V(0, 0, 0), Faction.Player1, range: 2); // melee (<= MELEE_THRESHOLD) → instant damage
            int near     = Combatant(w, V(1, 0, 0), Faction.Player2);           // nearer enemy, also in range
            int forced   = Combatant(w, V(2, 0, 0), Faction.Player2);           // the player's chosen target, in range (dist 2 <= 2)

            w.CommandState[attacker]  = UnitCommand.AttackTarget;
            w.CommandTarget[attacker] = forced;

            Fixed nearBefore   = w.Health[near];
            Fixed forcedBefore = w.Health[forced];
            combat.Tick(w, Dt);

            // Force-fire damages ONLY the forced target — proving AC2's in-range half (Task 7 wording), the
            // complement of AttackTarget_ForceFires_IgnoringNearerEnemy (which proves the out-of-range chase half).
            Assert.True(w.Health[forced].Raw < forcedBefore.Raw, "force-fire must damage the forced target in range");
            Assert.Equal(nearBefore.Raw, w.Health[near].Raw);                  // nearer enemy untouched
            Assert.True((w.Flags[attacker] & EntityFlags.Attacking) != 0);
        }

        // ── AC3 — forced target dies → clear, fall back to Idle, re-acquire (no freeze/stutter/dangle) ──────

        [Fact]
        public void AttackTarget_ForcedTargetDies_FallsBackToIdleAndReacquires()
        {
            var (w, combat) = NewSim();
            int attacker = Combatant(w, V(0, 0, 0), Faction.Player1, range: 5);
            int forced   = Combatant(w, V(1, 0, 0), Faction.Player2);
            int other    = Combatant(w, V(2, 0, 0), Faction.Player2); // a DIFFERENT in-range enemy to re-acquire

            w.CommandState[attacker]  = UnitCommand.AttackTarget;
            w.CommandTarget[attacker] = forced;

            w.Destroy(forced); // forced target gone before the tick

            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[attacker]); // fell back to Idle
            Assert.Equal(-1, w.CommandTarget[attacker]);              // slot cleared (no dangling id)
            Assert.Equal(other, w.AttackTarget[attacker]);            // re-acquired nearest in-range enemy
        }

        // ── AC4a — Patrol walks an ordered route and reverses at both ends ─────────────────────────────────

        [Fact]
        public void Patrol_ThreeWaypoints_WalksRouteAndReversesAtEnds()
        {
            var (w, combat) = NewSim();
            int u = Combatant(w, V(0, 0, 0), Faction.Player1);
            SetPatrolRoute(w, u, V(0, 0, 0), V(10, 0, 0), V(20, 0, 0)); // W0,W1,W2; index 1, dir +1
            w.CommandState[u] = UnitCommand.Patrol;

            // Tick 1: at W0, heading to W1 (not yet arrived) — index stays 1.
            combat.Tick(w, Dt);
            Assert.Equal(1, w.PatrolIndex[u]);
            AssertVec(V(10, 0, 0), w.MoveTarget[u]);

            // Arrive W1 → advance to W2.
            w.Position[u] = V(10, 0, 0); combat.Tick(w, Dt);
            Assert.Equal(2, w.PatrolIndex[u]); Assert.Equal(1, w.PatrolDir[u]);
            AssertVec(V(20, 0, 0), w.MoveTarget[u]);

            // Arrive W2 (top end) → reverse, head back to W1.
            w.Position[u] = V(20, 0, 0); combat.Tick(w, Dt);
            Assert.Equal(1, w.PatrolIndex[u]); Assert.Equal(-1, w.PatrolDir[u]);
            AssertVec(V(10, 0, 0), w.MoveTarget[u]);

            // Arrive W1 → continue down to W0.
            w.Position[u] = V(10, 0, 0); combat.Tick(w, Dt);
            Assert.Equal(0, w.PatrolIndex[u]); Assert.Equal(-1, w.PatrolDir[u]);
            AssertVec(V(0, 0, 0), w.MoveTarget[u]);

            // Arrive W0 (bottom end) → reverse, head to W1.
            w.Position[u] = V(0, 0, 0); combat.Tick(w, Dt);
            Assert.Equal(1, w.PatrolIndex[u]); Assert.Equal(1, w.PatrolDir[u]);
            AssertVec(V(10, 0, 0), w.MoveTarget[u]);
        }

        [Fact]
        public void Patrol_TwoWaypoint_PingPongFloor()
        {
            var (w, combat) = NewSim();
            int u = Combatant(w, V(0, 0, 0), Faction.Player1);
            SetPatrolRoute(w, u, V(0, 0, 0), V(10, 0, 0)); // N=2 — the classic A↔B ping-pong
            w.CommandState[u] = UnitCommand.Patrol;

            w.Position[u] = V(10, 0, 0); combat.Tick(w, Dt); // arrive W1 → reverse to W0
            Assert.Equal(0, w.PatrolIndex[u]); Assert.Equal(-1, w.PatrolDir[u]); AssertVec(V(0, 0, 0), w.MoveTarget[u]);

            w.Position[u] = V(0, 0, 0); combat.Tick(w, Dt);  // arrive W0 → reverse to W1
            Assert.Equal(1, w.PatrolIndex[u]); Assert.Equal(1, w.PatrolDir[u]); AssertVec(V(10, 0, 0), w.MoveTarget[u]);
        }

        [Fact]
        public void Patrol_EngagesEnemyOnLane_ThenResumes()
        {
            var (w, combat) = NewSim();
            int u = Combatant(w, V(0, 0, 0), Faction.Player1, range: 2);
            SetPatrolRoute(w, u, V(0, 0, 0), V(20, 0, 0));
            w.CommandState[u] = UnitCommand.Patrol;
            int enemy = Combatant(w, V(1, 0, 0), Faction.Player2); // on the lane, in range

            Fixed before = w.Health[enemy];
            combat.Tick(w, Dt); // engage like AttackMove — deal damage, do not advance
            Assert.True(w.Health[enemy].Raw < before.Raw, "patrol unit must engage an in-range enemy");
            Assert.True((w.Flags[u] & EntityFlags.Attacking) != 0);

            w.Destroy(enemy);
            combat.Tick(w, Dt); // enemy gone → resume toward the waypoint
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0);
            AssertVec(V(20, 0, 0), w.MoveTarget[u]);
        }

        [Fact]
        public void PatrolAppend_ExtendsFarEnd_WithoutResettingCurrentLeg()
        {
            var w = new EntityWorld();
            int u = Combatant(w, V(0, 0, 0), Faction.Player1);

            ApplyGround(w, u, UnitCommand.Patrol, V(10, 0, 0)); // fresh route: [W0=current, W1=(10)]; index 1
            Assert.Equal(2, w.PatrolCount[u]);
            Assert.Equal(1, w.PatrolIndex[u]);
            var moveTargetBefore = w.MoveTarget[u];

            ApplyGround(w, u, UnitCommand.PatrolAppend, V(20, 0, 0)); // append far end
            Assert.Equal(3, w.PatrolCount[u]);
            Assert.Equal(1, w.PatrolIndex[u]);                                  // current leg UNCHANGED
            Assert.Equal(UnitCommand.Patrol, w.CommandState[u]);                // PatrolAppend rewritten to Patrol
            Assert.Equal(moveTargetBefore.X.Raw, w.MoveTarget[u].X.Raw);        // MoveTarget untouched
            AssertWaypoint(w, u, 2, V(20, 0, 0));
        }

        // ── AC4b — Follow tracks beyond a leash, idles within, drops to Idle on death ──────────────────────

        [Fact]
        public void Follow_TracksBeyondLeash_IdlesWithin_DropsOnDeath()
        {
            var (w, combat) = NewSim();
            int follower = Combatant(w, V(0, 0, 0), Faction.Player1);
            int leader   = Combatant(w, V(10, 0, 0), Faction.Player1); // friendly, beyond the 3u leash

            w.CommandState[follower]  = UnitCommand.Follow;
            w.CommandTarget[follower] = leader;

            combat.Tick(w, Dt); // beyond leash → re-path toward the leader
            Assert.True((w.Flags[follower] & EntityFlags.Moving) != 0);
            AssertVec(V(10, 0, 0), w.MoveTarget[follower]);

            w.Position[leader] = V(2, 0, 0); // now within the leash (dist 2 < 3)
            combat.Tick(w, Dt);
            Assert.True((w.Flags[follower] & EntityFlags.Moving) == 0); // idle in place

            w.Destroy(leader);
            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[follower]); // dropped to Idle
            Assert.Equal(-1, w.CommandTarget[follower]);
        }

        // ── AC5 — Hold is genuinely distinct from Stop ─────────────────────────────────────────────────────

        [Fact]
        public void HoldPosition_NeverDisplacedBySeparation_WhereasStopIs()
        {
            var movement = new MovementSystem();

            // Hold: a crowding neighbour cannot push the Hold unit off its tile.
            var wHold = new EntityWorld();
            int hold = Unit(wHold, V(0, 0, 0), Faction.Player1);
            Unit(wHold, V(1, 0, 0), Faction.Player1); // within SEPARATION_RADIUS (2.0)
            wHold.CommandState[hold] = UnitCommand.HoldPosition;
            FixedVec3 holdBefore = wHold.Position[hold];

            movement.Tick(wHold, Dt);
            movement.Tick(wHold, Dt);
            Assert.Equal(holdBefore.X.Raw, wHold.Position[hold].X.Raw);
            Assert.Equal(holdBefore.Z.Raw, wHold.Position[hold].Z.Raw);

            // Stop: identical setup, but a Stop unit CAN be shoved — proving Hold no longer aliases Stop.
            var wStop = new EntityWorld();
            int stop = Unit(wStop, V(0, 0, 0), Faction.Player1);
            Unit(wStop, V(1, 0, 0), Faction.Player1);
            wStop.CommandState[stop] = UnitCommand.Stop;
            FixedVec3 stopBefore = wStop.Position[stop];

            movement.Tick(wStop, Dt);
            movement.Tick(wStop, Dt);
            Assert.True(wStop.Position[stop].X.Raw != stopBefore.X.Raw
                     || wStop.Position[stop].Z.Raw != stopBefore.Z.Raw,
                "Stop unit should be displaced by separation — Hold's anchor is the real distinction.");
        }

        [Fact]
        public void HoldAndStop_BothAttackInRangeEnemy_NeitherChases()
        {
            foreach (UnitCommand cmd in new[] { UnitCommand.HoldPosition, UnitCommand.Stop })
            {
                var (w, combat) = NewSim();
                int u     = Combatant(w, V(0, 0, 0), Faction.Player1, range: 2); // melee (<= MELEE_THRESHOLD) → instant damage
                int enemy = Combatant(w, V(1, 0, 0), Faction.Player2);           // in range (dist 1 <= 2)
                w.CommandState[u] = cmd;

                Fixed before = w.Health[enemy];
                combat.Tick(w, Dt);

                Assert.True(w.Health[enemy].Raw < before.Raw, $"{cmd} should attack an in-range enemy");
                Assert.True((w.Flags[u] & EntityFlags.Moving) == 0, $"{cmd} must never set Moving (no chase)");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────

        private static (EntityWorld world, CombatSystem combat) NewSim()
            => (new EntityWorld(), new CombatSystem(new ProjectileStore()));

        private static FixedVec3 V(int x, int y, int z)
            => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        /// <summary>A melee combat unit (range &lt;= MELEE_THRESHOLD → instant damage, no ProjectileSystem needed).</summary>
        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f, int dmg = 10, int range = 2)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(dmg);
            w.AttackRange[id]  = Fixed.FromInt(range);
            // Story 3.12: direct-SoA units skip ApplyUnitDefinition, so mirror the old range→delivery inference to
            // preserve behavior (a ranged range > 2.5 fired projectiles; the Create default is now Hitscan).
            w.Delivery[id] = w.AttackRange[id] > Fixed.FromFloat(2.5f) ? AttackDelivery.Projectile : AttackDelivery.Hitscan;
            w.AttackSpeed[id]  = Fixed.FromInt(1);
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        /// <summary>A bare unit (no attack stats) — for movement-only assertions.</summary>
        private static int Unit(EntityWorld w, FixedVec3 pos, Faction f)
            => w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));

        private static void SetPatrolRoute(EntityWorld w, int id, params FixedVec3[] wps)
        {
            int b = id * EntityWorld.MAX_PATROL_WAYPOINTS;
            for (int k = 0; k < wps.Length; k++) w.PatrolWaypoints[b + k] = wps[k];
            w.PatrolCount[id] = (byte)wps.Length;
            w.PatrolIndex[id] = 1;
            w.PatrolDir[id]   = 1;
        }

        /// <summary>Apply a ground-point command (Patrol/PatrolAppend) through the SAME shared OrderApplier the
        /// lockstep/replay paths use — so the test exercises production apply logic, not a test-only copy.</summary>
        private static void ApplyGround(EntityWorld w, int id, UnitCommand cmd, FixedVec3 groundPoint)
            => OrderApplier.Apply(w, new UnitOrder(id, cmd, groundPoint.X, groundPoint.Z), w.FactionOf[id]);

        private static void AssertVec(FixedVec3 expected, FixedVec3 actual)
        {
            Assert.Equal(expected.X.Raw, actual.X.Raw);
            Assert.Equal(expected.Z.Raw, actual.Z.Raw);
        }

        private static void AssertWaypoint(EntityWorld w, int id, int k, FixedVec3 expected)
            => AssertVec(expected, w.PatrolWaypoints[id * EntityWorld.MAX_PATROL_WAYPOINTS + k]);
    }
}
