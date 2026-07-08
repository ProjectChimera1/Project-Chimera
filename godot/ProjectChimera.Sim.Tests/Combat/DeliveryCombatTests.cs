#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 3.12 — DIRECT behavioral oracles for the two runtime consumers of the new delivery/speed fields. The
    /// self-recorded DeliveryScenario golden pins a SimChecksum sequence but exercises these only transitively (and,
    /// because ProjectileSpeed is folded directly, the golden would even move if the speed never reached the flight
    /// step). These assert the mechanism, not just the hash:
    ///   • CombatSystem branches on Delivery, DECOUPLED from range — a long-range Hitscan unit spawns NO projectile and
    ///     lands instantly; a short-range Projectile unit spawns one and does NOT damage until the shell arrives.
    ///   • ProjectileSystem advances at the per-unit ProjectileStore.Speed, NOT the global PROJECTILE_SPEED (18).
    /// Godot-free, <see cref="Fixed"/>-only, ascending-id — runs on every OS leg.
    /// </summary>
    public class DeliveryCombatTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int Attacker(EntityWorld w, FixedVec3 pos, AttackDelivery delivery, int range)
        {
            int id = w.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(20);
            w.AttackRange[id]  = Fixed.FromInt(range);
            w.AttackSpeed[id]  = Fixed.Zero;             // cooldown resets to 0 ⇒ fires every tick
            w.Delivery[id]     = delivery;               // the field under test — set explicitly, NOT via range
            w.DamageTypeOf[id] = DamageType.Normal;
            w.CommandState[id] = UnitCommand.Idle;       // auto-acquire the nearest enemy in range
            return id;
        }

        private static int Target(EntityWorld w, FixedVec3 pos)
        {
            int id = w.Create(pos, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ArmorTypeOf[id] = ArmorType.Unarmored;
            return id;
        }

        // ── VG1: delivery decoupled from range ──

        [Fact]
        public void LongRangeHitscan_SpawnsNoProjectile_AndDamagesInstantly()
        {
            var w = new EntityWorld();
            var projectiles = new ProjectileStore();
            var combat = new CombatSystem(projectiles);
            Attacker(w, V(0, 0), AttackDelivery.Hitscan, range: 12);   // long range, but authored Hitscan
            int target = Target(w, V(10, 0));                          // distance 10 ≤ range 12

            Fixed hp0 = w.Health[target];
            combat.Tick(w, Dt);

            Assert.Equal(0, projectiles.HighWaterMark);                // NO projectile spawned despite the long range
            Assert.True(w.Health[target] < hp0);                       // instant hitscan damage landed this same tick
        }

        [Fact]
        public void ShortRangeProjectile_SpawnsProjectile_AndDoesNotDamageUntilArrival()
        {
            var w = new EntityWorld();
            var projectiles = new ProjectileStore();
            var combat = new CombatSystem(projectiles);
            Attacker(w, V(0, 0), AttackDelivery.Projectile, range: 3);  // short range, but authored Projectile
            int target = Target(w, V(2, 0));                           // distance 2 ≤ range 3

            Fixed hp0 = w.Health[target];
            combat.Tick(w, Dt);

            Assert.True(projectiles.HighWaterMark > 0);                // a projectile WAS spawned despite the short range
            Assert.Equal(hp0, w.Health[target]);                       // no damage yet — the shell is still in flight
        }

        // ── VG2: per-unit projectile speed honoured in flight (not the hardcoded global 18) ──

        [Fact]
        public void ProjectileAdvancesAtPerUnitSpeed_NotTheGlobalDefault()
        {
            var w = new EntityWorld();
            int target = w.Create(V(100, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // far — no hit this tick
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);

            int slow = store.Spawn(V(0, 0), target, V(100, 0), Fixed.FromInt(10), DamageType.Normal,
                                   ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(6));
            int fast = store.Spawn(V(0, 0), target, V(100, 0), Fixed.FromInt(10), DamageType.Normal,
                                   ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(18));

            system.Tick(w, Fixed.One);   // one second, unit +X direction ⇒ advance distance == speed

            // The slow shell advanced ~6 (its authored speed) — crucially NOT ~18, which is what a still-hardcoded
            // global PROJECTILE_SPEED would have produced. This is the assertion that proves Speed[] reaches the advance.
            Assert.True(store.Position[slow].X > Fixed.FromInt(5) && store.Position[slow].X < Fixed.FromInt(7),
                $"slow shell advanced to X={store.Position[slow].X} — expected ~6 (per-unit speed), not the global 18");
            Assert.True(store.Position[fast].X > Fixed.FromInt(17) && store.Position[fast].X < Fixed.FromInt(19),
                $"fast shell advanced to X={store.Position[fast].X} — expected ~18");
            Assert.True(store.Position[fast].X > store.Position[slow].X); // the faster unit's shell is strictly ahead
        }
    }
}
