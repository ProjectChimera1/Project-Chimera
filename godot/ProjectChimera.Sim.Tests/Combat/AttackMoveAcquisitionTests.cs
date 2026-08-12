#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-936 — AttackMove's WC3-style acquisition. Before this, an attack-moving unit only saw enemies inside its
    /// WEAPON range (SpatialHash.FindNearestEnemy is AttackRange-bounded) and TickAttackMoveCombat DROPPED any
    /// candidate beyond weapon reach instead of chasing — so armies ran straight past enemies a few units off the
    /// path and traded shots only with what they brushed against (the 2026-08-12 field report). Pins: an enemy
    /// inside the acquisition radius but beyond weapon range DIVERTS the unit (chase leg); an enemy beyond
    /// acquisition is still ignored (the pass-distant-enemies behavior is preserved, bounded); a killed target
    /// resumes the march toward CommandGoal; and a target that escapes the acquisition radius is dropped by the
    /// leash instead of kiting the unit across the map. Godot-free, <see cref="Fixed"/>-only.
    /// </summary>
    public class AttackMoveAcquisitionTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt; // one real sim tick (1/30s)
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A short-weapon (2u) attack-mover ordered toward <paramref name="goal"/>.</summary>
        private static int AttackMover(EntityWorld w, FixedVec3 pos, FixedVec3 goal)
        {
            int id = w.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(20);
            w.AttackRange[id]  = Fixed.FromInt(2);
            w.AttackSpeed[id]  = Fixed.Zero;               // fires every tick
            w.Delivery[id]     = AttackDelivery.Hitscan;   // instant damage this tick
            w.DamageTypeOf[id] = DamageType.Normal;
            w.CommandState[id] = UnitCommand.AttackMove;
            w.CommandGoal[id]  = goal;
            w.MoveTarget[id]   = goal;
            return id;
        }

        private static int Enemy(EntityWorld w, FixedVec3 pos, Fixed? hp = null)
        {
            int id = w.Create(pos, Faction.Player2, hp ?? Fixed.FromInt(100), Fixed.FromInt(3));
            w.ArmorTypeOf[id] = ArmorType.Unarmored;
            return id;
        }

        [Fact]
        public void Enemy_InsideAcquisition_BeyondWeaponRange_DivertsTheUnit()
        {
            // Enemy 8u off to the side: outside the 2u weapon, inside the 12u acquisition radius. Pre-DW-936 the
            // unit kept walking to the goal and never engaged; now it must divert (chase leg): MoveTarget aims at
            // the ENEMY, not the goal, and the target is held for the pursuit.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int atk = AttackMover(w, V(0, 0), goal: V(30, 0));
            int foe = Enemy(w, V(0, 8));

            combat.Tick(w, Dt);

            Assert.Equal(foe, w.AttackTarget[atk]);
            Assert.Equal(w.Position[foe], w.MoveTarget[atk]);
            Assert.True((w.Flags[atk] & EntityFlags.Moving) != 0, "the chase leg must walk toward the target");
            Assert.Equal((EntityFlags)0, w.Flags[atk] & EntityFlags.Attacking); // not in weapon range yet
        }

        [Fact]
        public void Enemy_BeyondAcquisition_IsIgnored_TheMarchContinues()
        {
            // Enemy 20u away — outside the 12u acquisition radius. The unit must NOT divert: attack-move notices a
            // bounded neighborhood, it does not sweep the map (that is Idle's global chase, deliberately not this).
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int atk = AttackMover(w, V(0, 0), goal: V(30, 0));
            Enemy(w, V(0, 20));

            combat.Tick(w, Dt);

            Assert.Equal(-1, w.AttackTarget[atk]);
            Assert.Equal(w.CommandGoal[atk], w.MoveTarget[atk]);
            Assert.True((w.Flags[atk] & EntityFlags.Moving) != 0);
        }

        [Fact]
        public void EnemyInWeaponRange_IsAttacked_NotWalkedPast()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int atk = AttackMover(w, V(0, 0), goal: V(30, 0));
            int foe = Enemy(w, V(2, 0));

            combat.Tick(w, Dt);

            Assert.True((w.Flags[atk] & EntityFlags.Attacking) != 0);
            Assert.Equal((EntityFlags)0, w.Flags[atk] & EntityFlags.Moving); // stopped to fight
            Assert.True(w.Health[foe] < Fixed.FromInt(100), "the hitscan hit must land this tick");
        }

        [Fact]
        public void TargetKilled_ResumesTowardTheOrderedGoal()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int atk = AttackMover(w, V(0, 0), goal: V(30, 0));
            w.EffectiveAttackDamage[atk] = Fixed.FromInt(200); // one-shot
            Enemy(w, V(2, 0), hp: Fixed.FromInt(100));

            combat.Tick(w, Dt); // kills the enemy this tick
            combat.Tick(w, Dt); // dead target cleared -> nothing else noticed -> resume the march

            Assert.Equal(UnitCommand.AttackMove, w.CommandState[atk]); // the ORDER survives the kill
            Assert.Equal(-1, w.AttackTarget[atk]);
            Assert.Equal(w.CommandGoal[atk], w.MoveTarget[atk]);
            Assert.True((w.Flags[atk] & EntityFlags.Moving) != 0);
        }

        [Fact]
        public void EscapedTarget_IsDroppedByTheLeash_TheMarchResumes()
        {
            // Acquire at 8u, then teleport the enemy far beyond acquisition: the leash must drop it (a fleeing
            // enemy cannot kite an attack-moving army away from its ordered goal) and the march resumes.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int atk = AttackMover(w, V(0, 0), goal: V(30, 0));
            int foe = Enemy(w, V(0, 8));

            combat.Tick(w, Dt);
            Assert.Equal(foe, w.AttackTarget[atk]); // acquired (premise)

            w.Position[foe] = V(0, 40); // escapes far beyond the 12u acquisition radius
            combat.Tick(w, Dt);

            Assert.Equal(-1, w.AttackTarget[atk]);
            Assert.Equal(w.CommandGoal[atk], w.MoveTarget[atk]);
            Assert.True((w.Flags[atk] & EntityFlags.Moving) != 0);
        }

        [Fact]
        public void LongWeapon_NeverBlinderThanItsOwnReach()
        {
            // An exotic weapon LONGER than the acquisition constant: the effective noticing radius is
            // max(AttackRange, ACQUISITION_RANGE), so a 15u-weapon unit must still engage a 14u enemy directly.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int atk = AttackMover(w, V(0, 0), goal: V(30, 0));
            w.AttackRange[atk] = Fixed.FromInt(15);
            int foe = Enemy(w, V(14, 0));

            combat.Tick(w, Dt);

            Assert.Equal(foe, w.AttackTarget[atk]);
            Assert.True((w.Flags[atk] & EntityFlags.Attacking) != 0); // inside its own weapon range -> fires
        }
    }
}
