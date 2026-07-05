#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 2.13 (AC2, Decision D-1) — the AttackMove/queued-Move ARRIVAL-HOVER deadlock and its regression guard.
    ///
    /// A crowd sharing one CommandGoal settles on a ~1.0u separation-vs-seek EQUILIBRIUM RING; the old 0.5u arrive
    /// radius (0.25 squared) sits INSIDE that ring, so <c>ResumeAttackMove</c> never flips the wave to Idle and the
    /// queue never pops — the units hover forever. Widening the two GOAL thresholds (AMOVE_ARRIVE_SQR /
    /// ORDER_ARRIVE_SQR) to the shared 2u radius clears the ring.
    ///
    /// The physical stop <c>MovementSystem.ARRIVE_THRESHOLD_SQR</c> is DELIBERATELY left at 0.5u (a Story 2.13
    /// deviation from D-1's "all three"): widening it to 2u would halt every melee chaser at 2u — outside its 1.5u
    /// attack range — and break all melee combat. <see cref="MeleeUnitBelowArriveRadius_StillClosesAndStrikes"/> is
    /// the guard that fails if the physical stop is ever widened to reach the melee range. Godot-free, Fixed-only.
    /// </summary>
    public class ArrivalRadiusTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>Tick the two systems that produce the equilibrium ring, in sim order (Movement@2 before Combat@6).</summary>
        private static void Step(EntityWorld w, MovementSystem move, CombatSystem combat)
        {
            move.Tick(w, Dt);
            combat.Tick(w, Dt);
        }

        // ── AC2.1 / AC2.3 — a crowded AttackMove wave converging on one goal SETTLES (→ Idle), not hovers ──

        [Fact]
        public void AttackMoveWave_ConvergingOnOneGoal_AllReachIdle()
        {
            var w = new EntityWorld();
            var move = new MovementSystem();
            var combat = new CombatSystem(new ProjectileStore());
            FixedVec3 goal = V(0, 0);

            // 8 units (≥5, AC2.3) ringed 4u out, all AttackMove to the same goal, all separating with the MAX
            // collision radius (1.0 ⇒ 2.0u pairwise contact). Seek pulls them in; separation holds them on a stable
            // ~1.3–1.7u ring — well outside the old 0.5u radius, so pre-fix they hover there indefinitely, but inside
            // the new 2u radius, so post-fix they all settle.
            int[] pos = { 4, 0,  -4, 0,  0, 4,  0, -4,  3, 3,  -3, 3,  3, -3,  -3, -3 };
            var units = new int[pos.Length / 2];
            for (int k = 0; k < units.Length; k++)
            {
                int u = w.Create(V(pos[2 * k], pos[2 * k + 1]), Faction.Player1, Fixed.FromInt(80), Fixed.FromInt(3));
                w.EffectiveAttackDamage[u] = Fixed.FromInt(6);        // non-zero ⇒ CombatSystem processes it (not the zero-damage skip)
                w.AttackRange[u]      = Fixed.FromInt(2);
                w.CollisionRadius[u]  = Fixed.One;                    // 1.0 (max) ⇒ 2.0u pairwise contact ⇒ a stable wide ring
                w.CommandState[u]     = UnitCommand.AttackMove;
                w.CommandGoal[u]      = goal;
                w.MoveTarget[u]       = goal;
                w.Flags[u]           |= EntityFlags.Moving;
                units[k] = u;
            }

            for (int t = 0; t < 400; t++) Step(w, move, combat);

            foreach (int u in units)
                Assert.Equal(UnitCommand.Idle, w.CommandState[u]); // settled within the 2u radius, not hovering in AttackMove
        }

        // ── AC2.3 — a queued Move completes at the widened radius (the OrderQueueSystem path) ──

        [Fact]
        public void QueuedMove_UnitInsideWidenedRadius_PopsNextOrder()
        {
            var w = new EntityWorld();
            var oq = new OrderQueueSystem();
            FixedVec3 goalA = V(0, 0);
            FixedVec3 goalB = V(20, 0);

            // Unit resting 1.5u from goalA — INSIDE the new 2u radius (sqr 2.25 ≤ 4.0), OUTSIDE the old 0.5u (2.25 > 0.25).
            int u = w.Create(new FixedVec3(Fixed.FromInt(3) / Fixed.FromInt(2), Fixed.Zero, Fixed.Zero),
                             Faction.Player1, Fixed.FromInt(80), Fixed.FromInt(3));
            w.CommandGoal[u]    = goalA;
            w.ActiveOrderCmd[u] = (byte)UnitCommand.Move;             // the active order is a Move to goalA
            int baseIdx = u * EntityWorld.MAX_ORDER_QUEUE;
            w.OrderQueueCmd[baseIdx]     = (byte)UnitCommand.Move;    // one queued order: Move to goalB (targets are Fixed.Raw)
            w.OrderQueueTargetX[baseIdx] = goalB.X.Raw;
            w.OrderQueueTargetZ[baseIdx] = goalB.Z.Raw;
            w.OrderQueueCount[u]         = 1;

            oq.Tick(w, Dt);

            Assert.Equal(0, w.OrderQueueCount[u]);                    // the active Move completed at 1.5u ⇒ the queue popped
            Assert.Equal(goalB.X.Raw, w.CommandGoal[u].X.Raw);        // and dispatched the queued Move-to-B
        }

        // ── Regression guard — a sub-2u-range melee unit MUST still close and strike (MovementSystem stop stays 0.5u) ──

        [Fact]
        public void MeleeUnitBelowArriveRadius_StillClosesAndStrikes()
        {
            var w = new EntityWorld();
            var move = new MovementSystem();
            var combat = new CombatSystem(new ProjectileStore());

            int attacker = w.Create(V(6, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[attacker] = Fixed.FromInt(20);
            w.AttackRange[attacker]  = Fixed.FromInt(3) / Fixed.FromInt(2); // 1.5u — the real melee range (< the 2u arrive radius)
            w.AttackSpeed[attacker]  = Fixed.FromInt(1);
            w.DamageTypeOf[attacker] = DamageType.Normal;

            int enemy = w.Create(V(0, 0), Faction.Player2, Fixed.FromInt(300), Fixed.FromInt(3)); // stationary target
            w.ArmorTypeOf[enemy] = ArmorType.Medium;
            Fixed hp0 = w.Health[enemy];

            for (int t = 0; t < 300; t++) Step(w, move, combat);

            Assert.True(w.Health[enemy].Raw < hp0.Raw,
                "a 1.5u-range melee unit must close to strike — MovementSystem's physical stop must stay BELOW the " +
                "attack range (widening ARRIVE_THRESHOLD_SQR to 2u would strand it and break all melee combat).");
        }
    }
}
