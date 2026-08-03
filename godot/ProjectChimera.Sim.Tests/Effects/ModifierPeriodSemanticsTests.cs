#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-270 + DW-271 — <see cref="Modifier"/> duration/period SEMANTICS in <see cref="ModifierStore"/>, the two
    /// halves the 2.2b review deferred:
    ///
    /// <para><b>DW-271 (the defect).</b> <c>ResetPeriodSchedule</c> arms a periodic modifier with
    /// <see cref="EffectCaps.MaxPersistentPeriods"/> pulses, but a Modifier's LIFETIME is its duration, not that budget.
    /// Draining the budget used to leave a still-active modifier silently pulse-less while it kept its stat bonus, so
    /// <c>periodTicks 1 + duration 300</c> paid only 256 pulses and any PERMANENT periodic modifier went dead at pulse
    /// 256. <c>Advance</c> now re-arms the budget in place (the Story 2.13 lifelong-persistent pattern). Remove that
    /// re-arm and the two cap tests below go RED by exactly the truncated pulses.</para>
    ///
    /// <para><b>DW-270 (the doc/code mismatch).</b> <c>DurationTicks == 0</c> was documented "instantaneous/one-shot"
    /// but the store has always stored 0 verbatim and expired it via <c>0 → −1</c> on the next <c>Advance</c> — i.e.
    /// ONE FULL TICK of effect. The doc now states that; these tests PIN the behaviour so the two cannot drift apart
    /// again (and <c>AbilityValidator</c> warns on an authored 0 — see AbilityInertContentWarningTests).</para>
    ///
    /// Driven directly through <see cref="ModifierStore.Advance"/> (one call == one tick). Godot-free, Fixed-only.
    /// </summary>
    public class ModifierPeriodSemanticsTests
    {
        private static readonly Fixed Dt = Fixed.Zero; // periods are tick-counted; the dt arg is unused

        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        /// <summary>A periodic modifier: <paramref name="hpPerPulse"/> HP every <paramref name="periodTicks"/> ticks, plus a +atk stat delta.</summary>
        private static Modifier Periodic(int id, int duration, int periodTicks, int hpPerPulse, int atk = 0,
                                         StackRule rule = StackRule.Refresh, int maxStacks = 1) =>
            new Modifier(id, duration, rule, maxStacks,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.FromInt(atk), moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None,
                         periodEffect: new DirectHpDeltaEffect(Fixed.FromInt(hpPerPulse)), periodTicks: periodTicks);

        private static int Unit(EntityWorld w, int maxHp)
        {
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(maxHp), Fixed.FromInt(4));
            w.BaseAttackDamage[id]      = Fixed.FromInt(10);
            w.EffectiveAttackDamage[id] = Fixed.FromInt(10);
            return id;
        }

        // ── DW-271: the pulse budget is a schedule width, not a lifetime ──

        [Fact]
        public void PeriodicModifier_KeepsPulsingForItsWholeDuration_PastTheMaxPersistentPeriodsCap()
        {
            var (world, _, store) = Wire();
            int id = Unit(world, maxHp: 1000);

            // 1 HP drained per tick for 300 ticks. 300 > MaxPersistentPeriods (256), so the pre-fix schedule ran out of
            // pulses at tick 256 and the last 44 ticks drained NOTHING while the +2 atk bonus stayed live.
            const int duration = 300;
            Assert.True(duration > EffectCaps.MaxPersistentPeriods); // the test is only meaningful past the cap
            Assert.True(store.Apply(id, Periodic(1, duration, periodTicks: 1, hpPerPulse: -1, atk: 2), id, Faction.Player1));
            Assert.Equal(Fixed.FromInt(12).Raw, world.EffectiveAttackDamage[id].Raw); // 10 + 2, live from install

            for (int t = 1; t <= duration; t++) store.Advance(world, Dt);

            // One pulse per tick for the WHOLE duration: 1000 − 300. RED at 744 (1000 − 256) without the re-arm.
            Assert.Equal(Fixed.FromInt(700).Raw, world.Health[id].Raw);
            Assert.Equal(0, store.CountAt(id));                                       // and it expired on its duration
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw); // bonus reverted
        }

        [Fact]
        public void PermanentPeriodicModifier_NeverGoesPulseLess()
        {
            var (world, _, store) = Wire();
            int id = Unit(world, maxHp: 1000);

            // A PERMANENT (duration < 0) periodic modifier: nothing but death/dispel may ever stop its pulse. Pre-fix it
            // went silently dead at pulse 256 while keeping its slot and its stat bonus forever.
            Assert.True(store.Apply(id, Periodic(7, duration: -1, periodTicks: 1, hpPerPulse: -1), id, Faction.Player1));

            const int ticks = EffectCaps.MaxPersistentPeriods * 2 + 40; // 552 — well past two cap windows
            for (int t = 1; t <= ticks; t++) store.Advance(world, Dt);

            Assert.Equal(1, store.CountAt(id));                                     // still installed (permanent)
            Assert.Equal(Fixed.FromInt(1000 - ticks).Raw, world.Health[id].Raw);    // RED at 1000 − 256 without the re-arm
        }

        [Fact]
        public void PeriodicModifier_ReArmDoesNotExtendItsLife_StillExpiresOnDuration()
        {
            var (world, _, store) = Wire();
            int id = Unit(world, maxHp: 100);

            // Teeth for the re-arm: refilling the PULSE budget must never touch EXPIRY. A 10-tick modifier pays exactly
            // 10 pulses and is gone — an over-eager re-arm that also reset the duration would drain far past 10.
            store.Apply(id, Periodic(2, duration: 10, periodTicks: 1, hpPerPulse: -1), id, Faction.Player1);

            for (int t = 1; t <= 10; t++) store.Advance(world, Dt);
            Assert.Equal(0, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(90).Raw, world.Health[id].Raw);

            for (int t = 1; t <= 50; t++) store.Advance(world, Dt); // no further pulses after expiry
            Assert.Equal(Fixed.FromInt(90).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void NonLifelongPersistent_StillTruncatesAtItsPeriodCount_TheReArmIsModifierOnly()
        {
            var (world, _, store) = Wire();
            int id = Unit(world, maxHp: 1000);

            // Teeth for the OTHER side: a PersistentEffect's period count IS its lifetime (only `Lifelong` refills it,
            // Story 2.13). An unconditional re-arm would leak the modifier fix onto persistents and never expire this.
            store.InstallPersistent(id, new PersistentEffect(initialEffect: null,
                                        periodEffect: new DirectHpDeltaEffect(Fixed.FromInt(-1)),
                                        expireEffect: null, periodTicks: 1,
                                        periodCount: 400 /* clamped to MaxPersistentPeriods at install */),
                                    id, Faction.Player1);

            for (int t = 1; t <= 400; t++) store.Advance(world, Dt);

            Assert.Equal(0, store.CountAt(id)); // expired at the clamped count, NOT re-armed
            Assert.Equal(Fixed.FromInt(1000 - EffectCaps.MaxPersistentPeriods).Raw, world.Health[id].Raw); // 744, not 600
        }

        // ── DW-270: duration 0 is ONE TICK, not instantaneous ──

        [Fact]
        public void ZeroDurationModifier_IsLiveForExactlyOneTick_NotInstantaneous()
        {
            var (world, _, store) = Wire();
            int id = Unit(world, maxHp: 100);

            // Installs like any other modifier and its bonus is readable IMMEDIATELY (combat later in this same tick
            // sees it) — "instantaneous" it is not. It occupies a ring slot until the next Advance.
            store.Apply(id, new Modifier(5, durationTicks: 0, StackRule.Refresh, 1,
                                         Fixed.Zero, Fixed.FromInt(5), Fixed.Zero,
                                         StatusFlags.Stunned, periodEffect: null, periodTicks: 0),
                        id, Faction.Player1);

            Assert.Equal(1, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw);      // 10 + 5 — one full tick of buff
            Assert.Equal(StatusFlags.Stunned, world.StatusFlagsOf[id]);
            Assert.Equal(0, store.RemainingTicksAt(id, 0));                                // stored verbatim, NOT the PERMANENT sentinel

            store.Advance(world, Dt); // 0 → −1 → expired at the end of this one tick

            Assert.Equal(0, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw);
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[id]);
        }

        [Fact]
        public void ZeroDurationPeriodicModifier_FiresExactlyOnePulse_TheTickItLives()
        {
            var (world, _, store) = Wire();
            int id = Unit(world, maxHp: 100);

            // The corollary of the one-tick lifetime: a period of 1 gets exactly ONE boundary before expiry.
            store.Apply(id, Periodic(6, duration: 0, periodTicks: 1, hpPerPulse: -4), id, Faction.Player1);

            store.Advance(world, Dt);
            Assert.Equal(Fixed.FromInt(96).Raw, world.Health[id].Raw); // one pulse fired on the tick it was alive
            Assert.Equal(0, store.CountAt(id));

            for (int t = 1; t <= 10; t++) store.Advance(world, Dt);
            Assert.Equal(Fixed.FromInt(96).Raw, world.Health[id].Raw); // and never again
        }
    }
}
