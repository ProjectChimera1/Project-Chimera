#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.13 (AC4) — a LIFELONG <see cref="PersistentEffect"/> (the Sanguine Furnace HoT) keeps pulsing past the
    /// <see cref="EffectCaps.MaxPersistentPeriods"/> (256) cap via IN-PLACE re-arm in <see cref="ModifierStore.Advance"/>,
    /// with the 2.10 heal cadence (amount + period) unchanged. A non-lifelong persistent still expires at the cap
    /// (teeth). Driven directly through <c>ModifierStore.Advance</c> (one call == one tick). Godot-free, deterministic.
    /// </summary>
    public class LifelongHotTests
    {
        private static readonly Fixed Dt = Fixed.Zero; // periods are tick-counted; dt unused

        private static (EntityWorld world, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, store);
        }

        private static PersistentEffect Hot(int amount, int periodTicks, int periodCount, bool lifelong) =>
            new PersistentEffect(initialEffect: null, periodEffect: new HealEffect(Fixed.FromInt(amount)),
                                 expireEffect: null, periodTicks: periodTicks, periodCount: periodCount, lifelong: lifelong);

        // ── AC4.1 — re-arm at the period-count boundary: keeps pulsing on the SAME cadence, never expires ──

        [Fact]
        public void LifelongHot_ReArmsAtItsPeriodCount_ContinuesSameCadence()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(10);

            // Heal 2 every 5 ticks, count 3 (a small window so the re-arm boundary is quick to reach), lifelong.
            store.InstallPersistent(id, Hot(amount: 2, periodTicks: 5, periodCount: 3, lifelong: true), id, Faction.Player1);

            for (int t = 1; t <= 5; t++)  store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(12).Raw, world.Health[id].Raw); // pulse 1
            for (int t = 6; t <= 10; t++) store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(14).Raw, world.Health[id].Raw); // pulse 2
            for (int t = 11; t <= 15; t++) store.Advance(world, Dt); Assert.Equal(Fixed.FromInt(16).Raw, world.Health[id].Raw); // pulse 3 = count boundary
            Assert.Equal(1, store.CountAt(id)); // did NOT expire at the count boundary — re-armed in place

            for (int t = 16; t <= 20; t++) store.Advance(world, Dt); // 4th pulse (past the count) — proves re-arm + unchanged cadence
            Assert.Equal(Fixed.FromInt(18).Raw, world.Health[id].Raw); // same +2 every 5 ticks
        }

        // ── AC4.3 — soak to 3× the cap window: still active and still pulsing past 256 pulses ──

        [Fact]
        public void LifelongHot_StillPulsing_PastThreeTimesThe256Cap()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1000), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(1); // heavily damaged so heals are observable

            // The furnace shape scaled to the cap: heal 3 every 5 ticks, count 256. Cap window = 256*5 = 1280 ticks.
            store.InstallPersistent(id, Hot(amount: 3, periodTicks: 5, periodCount: 256, lifelong: true), id, Faction.Player1);

            const int capWindow = 256 * 5; // 1280 ticks = the point a NON-lifelong HoT would expire
            for (int t = 0; t < capWindow * 3; t++) store.Advance(world, Dt); // 3× the cap window

            Assert.Equal(1, store.CountAt(id)); // STILL active past 3× the cap (re-armed, never expired)

            // Prove pulses still FIRE past the cap: re-damage, advance one period, confirm it heals by the amount.
            world.Health[id] = Fixed.FromInt(1);
            for (int t = 0; t < 5; t++) store.Advance(world, Dt);
            Assert.Equal(Fixed.FromInt(4).Raw, world.Health[id].Raw); // 1 + 3 — the cadence holds far past 256 pulses
        }

        // ── Teeth — a NON-lifelong persistent still expires at its period count (RED if re-arm fired wrongly) ──

        [Fact]
        public void NonLifelongHot_ExpiresAtItsPeriodCount()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(1);

            store.InstallPersistent(id, Hot(amount: 3, periodTicks: 5, periodCount: 3, lifelong: false), id, Faction.Player1);

            for (int t = 0; t < 5 * 3 + 5; t++) store.Advance(world, Dt); // past the 3-period window
            Assert.Equal(0, store.CountAt(id)); // expired at the cap — the lifelong branch must NOT fire for a normal HoT

            Fixed afterExpiry = world.Health[id];
            world.Health[id] = Fixed.FromInt(1); // re-damage
            for (int t = 0; t < 20; t++) store.Advance(world, Dt);
            Assert.Equal(Fixed.FromInt(1).Raw, world.Health[id].Raw); // no further pulses after expiry
            _ = afterExpiry;
        }
    }
}
