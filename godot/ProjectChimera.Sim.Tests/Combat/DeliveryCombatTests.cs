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

        // ── DW-25: snap-to-goal clamp + max-lifetime TTL (no more orbiting shells / pool leak) ──

        [Fact]
        public void HighSpeedShell_SnapsOntoGoal_AndIsDestroyed_NoOrbit()
        {
            // The bug: a per-tick step exceeding the hit radius on final approach overshoots every tick and orbits
            // forever, leaking its pool slot. speed 5000 ⇒ step ~166 u/tick vs a 4 u gap.
            var w = new EntityWorld();
            int target = w.Create(V(4, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // stationary, reachable
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);

            int shell = store.Spawn(V(0, 0), target, V(4, 0), Fixed.FromInt(10), DamageType.Normal,
                                    ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(5000));

            // Tick 1: step ≫ remaining distance ⇒ snap EXACTLY onto the goal (no overshoot / no orbit).
            system.Tick(w, Dt);
            Assert.True(store.Alive[shell], "shell lands ON the goal this tick (hit resolves next tick), so it is still alive here");
            Assert.Equal(w.Position[target].X, store.Position[shell].X); // landed exactly on the goal, never past it
            Assert.Equal(w.Position[target].Z, store.Position[shell].Z);

            // Tick 2: distSqr == 0 ≤ HIT_SQR ⇒ hit resolves, slot freed. It never orbits.
            system.Tick(w, Dt);
            Assert.False(store.Alive[shell], "shell destroyed (pool slot freed) once it reaches the goal — no eternal orbit");
        }

        [Fact]
        public void UnreachableTarget_DroppedByTtl_HarmlesslyAndSlotFreed()
        {
            // A target the shell can never reach within its lifetime: at speed 6 the shell covers only ~60 u in the 10 s
            // TTL window, so a stationary target 100 u away keeps distSqr comfortably above HIT_SQR every tick — the snap
            // clamp cannot help (the goal is out of range). The TTL backstop must drop the shell after MAX_LIFETIME
            // instead of it flying forever and leaking its pool slot. (Kept at ~100 u to stay inside 16.16 range —
            // SqrMagnitude overflows for distances beyond ~180 u.)
            var w = new EntityWorld();
            int target = w.Create(V(100, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // stationary, out of reach
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);

            int shell = store.Spawn(V(0, 0), target, V(100, 0), Fixed.FromInt(10), DamageType.Normal,
                                    ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(6));

            Fixed hp0 = w.Health[target];

            // dt = 1/30 s, MAX_LIFETIME = 10 s ⇒ ~300 ticks before the TTL fires. Bound the loop well past that.
            bool observedFlyingBelowCap = false;
            int ticks = 0;
            while (store.Alive[shell] && ticks < 1000)
            {
                if (store.Age[shell] < ProjectileSystem.MAX_LIFETIME) observedFlyingBelowCap = true;
                system.Tick(w, Dt);
                ticks++;
            }

            Assert.False(store.Alive[shell], "shell should be dropped by the TTL backstop, not fly forever");
            Assert.True(observedFlyingBelowCap, "shell should have aged up toward the cap while still flying (not dropped on tick 1)");
            Assert.True(store.Age[shell] >= ProjectileSystem.MAX_LIFETIME, "shell was dropped precisely because Age reached MAX_LIFETIME");
            Assert.True(ticks > 100, $"shell should have survived roughly 300 ticks before TTL, not {ticks}"); // proves it flew, not an early hit
            Assert.Equal(hp0, w.Health[target]); // TTL drop is harmless — no damage dealt to the unreachable target
        }

        [Fact]
        public void RecycledSlot_StartsWithZeroAge()
        {
            // A freed slot re-Spawned must NOT inherit the prior occupant's accumulated age.
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);
            int target = w.Create(V(50, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));

            int first = store.Spawn(V(0, 0), target, V(50, 0), Fixed.FromInt(10), DamageType.Normal,
                                    ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(6));
            system.Tick(w, Dt);                       // age the first shell by one dt
            Assert.True(store.Age[first] > Fixed.Zero);
            store.Destroy(first);                     // free the slot

            int recycled = store.Spawn(V(0, 0), target, V(50, 0), Fixed.FromInt(10), DamageType.Normal,
                                       ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(6));
            Assert.Equal(first, recycled);            // same physical slot came back off the free list
            Assert.Equal(Fixed.Zero, store.Age[recycled]); // re-Spawn reset Age — no inherited flight time
        }

        [Fact]
        public void NonOvershootAdvance_IsByteIdenticalToTheRawExpression()
        {
            // Guards the "non-overshoot advance path stays byte-identical" invariant: a slow shell far from its goal
            // must land at exactly the pre-DW-25 position (same operand order — Fixed multiply is not associative).
            var w = new EntityWorld();
            int target = w.Create(V(100, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // far ⇒ step ≪ dist
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);

            FixedVec3 start = V(0, 0);
            Fixed speed = Fixed.FromInt(6);
            int shell = store.Spawn(start, target, V(100, 0), Fixed.FromInt(10), DamageType.Normal,
                                    ArmorType.Unarmored, Faction.Player1, speed: speed);

            // Recompute today's advance byte-for-byte: start + dir * speed * dt (identical operand order to the else branch).
            FixedVec3 delta = w.Position[target] - start;
            Fixed dist = delta.Magnitude();
            FixedVec3 dir = delta / dist;
            FixedVec3 expected = start + dir * speed * Dt;

            system.Tick(w, Dt);

            Assert.Equal(expected.X, store.Position[shell].X);
            Assert.Equal(expected.Y, store.Position[shell].Y);
            Assert.Equal(expected.Z, store.Position[shell].Z);
        }
    }
}
