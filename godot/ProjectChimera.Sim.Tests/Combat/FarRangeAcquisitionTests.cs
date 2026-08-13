#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-989 / DW-990 — the far-range half of the SqrDistance-saturation class (the third and fourth strikes;
    /// DW-984 fixed the gather side). <c>FixedVec3.SqrDistance</c> clamps at ~181.02 units, so any nearest-scan
    /// comparing CLAMPED values goes blind past it: <c>FindNearestEnemyGlobal</c>'s <c>Fixed.MaxValue</c> seed
    /// made it return −1 with the enemy base 250 units away (match-start advance-to-contact silently no-oped),
    /// and the AI's argmin scans degraded from nearest to lowest-id. The fix compares RAW widened squares
    /// (<c>FixedVec3.SqrDistanceRaw</c>, strictly widening below the clamp — near-range picks bit-identical).
    /// These are the teeth the DW-989 closure note demands: the 250-unit enemy is FOUND and the unit ADVANCES.
    /// </summary>
    public class FarRangeAcquisitionTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(10);
            w.AttackRange[id]  = Fixed.FromInt(5);
            w.AttackSpeed[id]  = Fixed.Zero;
            w.Delivery[id]     = AttackDelivery.Hitscan;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        [Fact]
        public void FindNearestEnemyGlobal_EnemyPast181Units_IsFound()
        {
            var w = new EntityWorld();
            int me    = Combatant(w, V(0, 0), Faction.Player1);
            int enemy = Combatant(w, V(250, 0), Faction.Player2); // past the ~181u clamp — the old code returned −1

            var hash = new SpatialHash();
            hash.Rebuild(w);
            Assert.Equal(enemy, hash.FindNearestEnemyGlobal(w, me));
        }

        [Fact]
        public void FindNearestEnemyGlobal_OrdersByDistance_BeyondTheClamp()
        {
            var w = new EntityWorld();
            int me = Combatant(w, V(0, 0), Faction.Player1);
            // Lower id is FARTHER — under the clamped compare both read Fixed.MaxValue, so distance ordering
            // was unobservable out here. The raw compare must pick the genuinely nearer one.
            Combatant(w, V(260, 0), Faction.Player2);
            int nearer = Combatant(w, V(200, 0), Faction.Player2);

            var hash = new SpatialHash();
            hash.Rebuild(w);
            Assert.Equal(nearer, hash.FindNearestEnemyGlobal(w, me));
        }

        [Fact]
        public void AttackMove_OnlyEnemy250UnitsAway_UnitActuallyAdvances()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int u = Combatant(w, V(0, 0), Faction.Player1);
            Combatant(w, V(250, 0), Faction.Player2);

            w.CommandState[u] = UnitCommand.AttackMove;
            w.CommandGoal[u]  = V(250, 0);
            combat.Tick(w, Dt);

            // The DW-989 defect: FindNearestEnemyGlobal returned −1 out here, no MoveTarget was written, and a
            // match-start attack-move across any shipped map stood still forever.
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0,
                "a unit under AttackMove with its only enemy 250 units away must advance to contact (DW-989)");
            Assert.Equal(Fixed.FromInt(250).Raw, w.MoveTarget[u].X.Raw);
        }
    }
}
