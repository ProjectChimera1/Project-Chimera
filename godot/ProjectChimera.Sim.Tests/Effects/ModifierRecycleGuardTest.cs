#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (AC4) — recycle safety. The <see cref="ModifierStore"/> is an EXTERNAL per-entity store that
    /// <see cref="EntityWorld.Create"/> cannot reset on slot recycle (and the A2 <c>ApplyUnitDefinitionGuardTest</c>
    /// cannot see), so its recycle safety is proven here — the store-analogue of the A2 guard. An entity carrying
    /// active modifiers (nonzero store slots AND a nonzero <see cref="ModifierSystem"/> stat-bonus accumulator) is
    /// destroyed and its slot recycled; the recycled entity must carry NO residual modifier instance and NO residual
    /// stat bonus.
    ///
    /// <para><b>Teeth (record inject→observe→revert in the DAR):</b> comment out <c>world.OnDestroy += ClearEntity</c>
    /// in the <see cref="ModifierStore"/> ctor → both tests go RED (store slots survive the recycle). Comment out the
    /// <c>_system?.ClearEntity(id)</c> call inside <see cref="ModifierStore.ClearEntity"/> →
    /// <see cref="RecycledSlot_CarriesNoResidualModifierOrBonus"/> goes RED (the leftover +5 accumulator surfaces on
    /// the recycled entity's next recompute as Base+8 instead of Base+3).</para>
    /// </summary>
    public class ModifierRecycleGuardTest
    {
        private static readonly Fixed Dt = Fixed.Zero;

        private static Modifier PermAtk(int id, int atk) =>
            new Modifier(id, durationTicks: -1, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        private static PersistentEffect Dot(int dmg, int periodTicks, int periodCount) =>
            new PersistentEffect(null, new DirectHpDeltaEffect(Fixed.FromInt(-dmg)), null, periodTicks, periodCount);

        [Fact]
        public void RecycledSlot_CarriesNoResidualModifierOrBonus()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);

            int x = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[x] = Fixed.FromInt(10);
            world.EffectiveAttackDamage[x] = Fixed.FromInt(10);

            store.Apply(x, PermAtk(1, 5), x, Faction.Player1);                       // +5 permanent stat bonus
            store.InstallPersistent(x, Dot(3, periodTicks: 1, periodCount: 5), x, Faction.Player1); // a DoT
            store.Advance(world, Dt); // one DoT pulse

            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[x].Raw); // nonzero bonus present
            Assert.Equal(2, store.CountAt(x));                                        // two active instances
            Assert.True(world.Health[x] < Fixed.FromInt(100));                        // DoT pulsed

            world.Destroy(x); // → OnDestroy → ClearEntity (revert store + accumulators)

            int y = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4)); // recycles slot x
            Assert.Equal(x, y);                  // same slot recycled
            Assert.Equal(0, store.CountAt(y));   // NO residual store instances

            // Re-dirty the recycled slot with a fresh +3 and recompute: it must read Base+3, never Base+3+5-leftover.
            world.BaseAttackDamage[y] = Fixed.FromInt(10);
            world.EffectiveAttackDamage[y] = Fixed.FromInt(10);
            store.Apply(y, PermAtk(2, 3), y, Faction.Player1);
            sys.Tick(world, Dt);

            Assert.Equal(Fixed.FromInt(13).Raw, world.EffectiveAttackDamage[y].Raw); // 10 + 3, NOT 10 + 3 + 5
            Assert.Equal(1, store.CountAt(y));                                        // only the fresh modifier
        }

        [Fact]
        public void RecycledThenRebuilt_HashesIdenticallyToFreshlyBuilt()
        {
            uint recycled = BuildRecycledWorldHash();
            uint fresh = BuildFreshWorldHash();
            Assert.Equal(fresh, recycled); // AC4 checksum clause — a recycled-then-rebuilt entity hashes like a fresh one
        }

        /// <summary>Create an entity, load it with modifiers + a DoT, destroy it, recycle the slot with canonical args, hash.</summary>
        private static uint BuildRecycledWorldHash()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);

            int x = world.Create(new FixedVec3(Fixed.FromInt(7), Fixed.Zero, Fixed.FromInt(-3)),
                                 Faction.Player1, Fixed.FromInt(80), Fixed.FromInt(3));
            store.Apply(x, PermAtk(1, 5), x, Faction.Player1);
            store.InstallPersistent(x, Dot(4, 1, 9), x, Faction.Player1);
            store.Advance(world, Dt);
            store.Advance(world, Dt);

            world.Destroy(x);
            Recreate(world); // recycles slot x with the SAME canonical args as the fresh world below
            sys.Tick(world, Dt);
            return Hash(world, store);
        }

        /// <summary>Create one entity with the canonical args directly, hash.</summary>
        private static uint BuildFreshWorldHash()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);

            Recreate(world);
            sys.Tick(world, Dt);
            return Hash(world, store);
        }

        private static void Recreate(EntityWorld world) =>
            world.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.FromInt(9)),
                         Faction.Player1, Fixed.FromInt(55), Fixed.FromInt(4));

        private static uint Hash(EntityWorld world, ModifierStore store) =>
            SimChecksum.Compute(world, new BuildingStore(), new ResourceStore(Fixed.Zero), new FactionRegistry(2), store);
    }
}
