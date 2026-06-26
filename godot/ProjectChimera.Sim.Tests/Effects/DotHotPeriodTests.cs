#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (AC1) — DoT/HoT via <see cref="PersistentEffect"/> + the period schedule. Proves the host's Health
    /// changes by the expected <see cref="Fixed"/> amount on EXACTLY each period boundary (not every tick), clamped
    /// into <c>[0, EffectiveMaxHealth]</c>, and the instance EXPIRES after its configured lifetime (no further pulses):
    ///   • a DoT (<see cref="DirectHpDeltaEffect"/> −5, every 10 ticks, 3 periods) → −5 at ticks 10/20/30 only, total −15;
    ///   • a HoT (<see cref="HealEffect"/> +8) heals on its boundaries and CLAMPS at the ceiling (no overheal);
    ///   • a DoT clamps at <see cref="Fixed.Zero"/> WITHOUT firing the death sequence (the 2.1 DirectHpDelta semantics:
    ///     overkill leaves the unit at 0 HP alive).
    /// Driven directly through <see cref="ModifierStore.Advance"/> (one call == one tick) for clean isolation.
    /// </summary>
    public class DotHotPeriodTests
    {
        private static readonly Fixed Dt = Fixed.Zero;

        private static (EntityWorld world, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, store);
        }

        private static PersistentEffect Periodic(EffectNode period, int periodTicks, int periodCount) =>
            new PersistentEffect(initialEffect: null, periodEffect: period, expireEffect: null,
                                 periodTicks: periodTicks, periodCount: periodCount);

        [Fact]
        public void Dot_PulsesOnExactPeriodBoundaries_TotalThenExpires()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4)); // 100/100

            store.InstallPersistent(id, Periodic(new DirectHpDeltaEffect(Fixed.FromInt(-5)), periodTicks: 10, periodCount: 3),
                                    casterId: id, casterFaction: Faction.Player1);

            for (int t = 1; t <= 9; t++) { store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw); }
            store.Advance(world, Dt); // tick 10 → first pulse
            Assert.Equal(Fixed.FromInt(95).Raw, world.Health[id].Raw);

            for (int t = 11; t <= 19; t++) { store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(95).Raw, world.Health[id].Raw); }
            store.Advance(world, Dt); // tick 20
            Assert.Equal(Fixed.FromInt(90).Raw, world.Health[id].Raw);

            for (int t = 21; t <= 29; t++) { store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(90).Raw, world.Health[id].Raw); }
            store.Advance(world, Dt); // tick 30 → third (final) pulse, then expires same tick
            Assert.Equal(Fixed.FromInt(85).Raw, world.Health[id].Raw);   // total −15
            Assert.Equal(0, store.CountAt(id));                          // expired after the configured lifetime

            for (int t = 31; t <= 45; t++) { store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(85).Raw, world.Health[id].Raw); }
        }

        [Fact]
        public void Hot_HealsOnBoundaries_DamagedUnit()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(50); // 50/100

            store.InstallPersistent(id, Periodic(new HealEffect(Fixed.FromInt(8)), periodTicks: 5, periodCount: 3),
                                    id, Faction.Player1);

            for (int t = 1; t <= 4; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(58).Raw, world.Health[id].Raw); // tick 5
            for (int t = 6; t <= 9; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(66).Raw, world.Health[id].Raw); // tick 10
            for (int t = 11; t <= 14; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(74).Raw, world.Health[id].Raw); // tick 15
            Assert.Equal(0, store.CountAt(id));
        }

        [Fact]
        public void Hot_OnNearFullUnit_ClampsAtCeiling_NoOverheal()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(98); // 98/100

            store.InstallPersistent(id, Periodic(new HealEffect(Fixed.FromInt(8)), periodTicks: 5, periodCount: 1),
                                    id, Faction.Player1);

            for (int t = 1; t <= 5; t++) store.Advance(world, Dt); // pulse at tick 5: 98 + 8 → clamp at 100, not 106
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void Dot_Overkill_ClampsAtZero_LeavesUnitAlive()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(4)); // 10/10

            store.InstallPersistent(id, Periodic(new DirectHpDeltaEffect(Fixed.FromInt(-100)), periodTicks: 1, periodCount: 1),
                                    id, Faction.Player1);

            store.Advance(world, Dt); // tick 1 → −100 clamps to 0; DirectHpDelta fires no death sequence
            Assert.Equal(Fixed.Zero.Raw, world.Health[id].Raw);
            Assert.True(world.IsAlive(id)); // 0-HP-alive (2.1 DirectHpDelta semantics)
        }
    }
}
