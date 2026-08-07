#nullable enable
using ProjectChimera.Combat;   // DamageType, ArmorType, DamageTable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-264 / DW-272 / Story 15.12 — the new stacking vocabulary. Covers the I/O-matrix rows:
    ///   • <see cref="StackRule.StackIndependent"/> installs one ring slot per application, each expiring on its OWN
    ///     duration and reverting one stack's contribution at a time; bounded by MaxStacks (a further apply is ignored).
    ///   • grouped <see cref="StackRule.Stack"/> is unchanged (one slot, a shared duration, <c>_stackCount</c> up to cap).
    ///   • <see cref="PeriodicStackMode"/> None/Repeat/Multiply scale a stacked periodic pulse, cap-bounded by
    ///     <see cref="EffectCaps.MaxPeriodicStackScale"/>; Repeat = N armor-per-hit, Multiply = one armor-once hit.
    /// Driven directly through <see cref="ModifierStore.Advance"/> (one call == one tick) for clean isolation.
    /// </summary>
    public class StackMechanicsAndPeriodicStackTests
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

        // ── StackIndependent: per-stack independent expiry ─────────────────────────────────────────────

        private static Modifier IndependentAtk(int maxStacks, int duration, int atk) =>
            new Modifier(id: 7, durationTicks: duration, StackRule.StackIndependent, maxStacks,
                         Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero, StatusFlags.None,
                         periodEffect: null, periodTicks: 0);

        [Fact]
        public void StackIndependent_EachStackExpiresOnItsOwnTimer_RevertingOneAtATime()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // BaseAtk 0

            // Apply stack A at tick 0, B at tick 5, C at tick 10 — each +5 attack, each DurationTicks=20.
            store.Apply(id, IndependentAtk(3, 20, 5), id, Faction.Player1);         // A
            Assert.Equal(1, store.CountAt(id));
            Assert.Equal(1, CountSameId(store, id));                                 // one same-id independent slot
            Assert.Equal(Fixed.FromInt(5).Raw, world.EffectiveAttackDamage[id].Raw);

            for (int t = 1; t <= 5; t++) store.Advance(world, Dt);
            store.Apply(id, IndependentAtk(3, 20, 5), id, Faction.Player1);         // B (installed at tick 5)
            Assert.Equal(2, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw);

            for (int t = 6; t <= 10; t++) store.Advance(world, Dt);
            store.Apply(id, IndependentAtk(3, 20, 5), id, Faction.Player1);         // C (installed at tick 10)
            Assert.Equal(3, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw);
            // Every slot is a single independent stack.
            for (int s = 0; s < store.CountAt(id); s++) Assert.Equal(1, store.StackCountAt(id, s));

            // A (installed pre-tick-0) expires on the 20th Advance → tick 20.
            for (int t = 11; t <= 19; t++) { store.Advance(world, Dt); Assert.Equal(3, store.CountAt(id)); }
            store.Advance(world, Dt); // tick 20 → A expires
            Assert.Equal(2, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw); // reverted ONE stack

            // B (installed at tick 5) expires 20 later → tick 25.
            for (int t = 21; t <= 24; t++) { store.Advance(world, Dt); Assert.Equal(2, store.CountAt(id)); }
            store.Advance(world, Dt); // tick 25 → B expires
            Assert.Equal(1, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(5).Raw, world.EffectiveAttackDamage[id].Raw);

            // C (installed at tick 10) expires 20 later → tick 30.
            for (int t = 26; t <= 29; t++) { store.Advance(world, Dt); Assert.Equal(1, store.CountAt(id)); }
            store.Advance(world, Dt); // tick 30 → C expires
            Assert.Equal(0, store.CountAt(id));
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveAttackDamage[id].Raw);
        }

        [Fact]
        public void StackIndependent_AtMaxStacksCap_FurtherApplyIsIgnored_NoRefresh()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            store.Apply(id, IndependentAtk(2, 20, 5), id, Faction.Player1);
            store.Apply(id, IndependentAtk(2, 20, 5), id, Faction.Player1);
            Assert.Equal(2, store.CountAt(id));                       // at MaxStacks=2
            long remA = store.RemainingTicksAt(id, 0);

            store.Advance(world, Dt);                                 // both slots count down to 19
            bool accepted = store.Apply(id, IndependentAtk(2, 20, 5), id, Faction.Player1); // 3rd → ignored (cap)
            Assert.True(accepted);                                    // "handled" (ignored), not a ring-full refusal
            Assert.Equal(2, store.CountAt(id));                       // still 2 — no new slot
            Assert.Equal(remA - 1, store.RemainingTicksAt(id, 0));    // and NO refresh of an existing slot's duration
        }

        // ── Grouped Stack: unchanged (one slot, shared duration, _stackCount up to cap) ─────────────────

        [Fact]
        public void GroupedStack_KeepsOneSlot_WithSharedStackCount()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Modifier m = new Modifier(id: 3, durationTicks: 120, StackRule.Stack, maxStacks: 3,
                                      Fixed.Zero, Fixed.FromInt(2), Fixed.Zero, StatusFlags.None, null, 0);
            store.Apply(id, m, id, Faction.Player1);
            store.Apply(id, m, id, Faction.Player1);
            store.Apply(id, m, id, Faction.Player1);
            store.Apply(id, m, id, Faction.Player1); // 4th → at cap, refresh-only (no 4th stack)

            Assert.Equal(1, store.CountAt(id));                       // ONE slot (verbatim today's behavior)
            Assert.Equal(3, store.StackCountAt(id, 0));               // _stackCount capped at MaxStacks
            Assert.Equal(Fixed.FromInt(6).Raw, world.EffectiveAttackDamage[id].Raw); // 3 × +2
        }

        // ── Periodic stacking: None / Repeat / Multiply, cap-bounded ────────────────────────────────────

        private static Modifier PeriodicDamage(int maxStacks, PeriodicStackMode mode, int dmg, int periodTicks) =>
            new Modifier(id: 9, durationTicks: 100000, StackRule.Stack, maxStacks,
                         Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None,
                         new DamageEffect(Fixed.FromInt(dmg), DamageType.Normal), periodTicks,
                         Fixed.Zero, mode);

        private static int DamageTarget(EntityWorld w, int armor)
        {
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1000), Fixed.FromInt(3));
            w.ArmorTypeOf[id]    = ArmorType.Unarmored;
            // Set BOTH: the modifier apply triggers ModifierSystem.RecomputeEntity, which re-derives EffectiveArmor from
            // BaseArmor (+ modifier bonus). Setting only EffectiveArmor would be reset to BaseArmor(0) on the first apply.
            w.BaseArmor[id]      = Fixed.FromInt(armor);
            w.EffectiveArmor[id] = Fixed.FromInt(armor); // flat post-matrix armor (Story 2.6)
            return id;
        }

        private static void ApplyNStacks(ModifierStore store, int id, Modifier m, int n)
        {
            for (int i = 0; i < n; i++) store.Apply(id, m, id, Faction.Player1);
        }

        [Fact]
        public void PeriodicNone_PulsesOncePerPeriod_RegardlessOfStacks()
        {
            var (world, store) = Wire();
            int id = DamageTarget(world, armor: 0);
            ApplyNStacks(store, id, PeriodicDamage(3, PeriodicStackMode.None, dmg: 10, periodTicks: 5), 3);

            for (int t = 1; t <= 4; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); // tick 5 → ONE pulse of 10 (today's byte-for-byte behavior)
            Assert.Equal(Fixed.FromInt(990).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void PeriodicRepeat_RunsThePulse_MinStacksCapTimes_ArmorPerHit()
        {
            var (world, store) = Wire();
            int id = DamageTarget(world, armor: 3);
            ApplyNStacks(store, id, PeriodicDamage(3, PeriodicStackMode.Repeat, dmg: 10, periodTicks: 5), 3);

            Fixed perHit = DamageTable.Default.FinalDamage(Fixed.FromInt(10), DamageType.Normal, ArmorType.Unarmored, Fixed.FromInt(3));
            for (int t = 1; t <= 4; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); // tick 5 → THREE hits, armor subtracted each
            Assert.Equal((Fixed.FromInt(1000) - perHit * Fixed.FromInt(3)).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void PeriodicMultiply_RunsOnce_AtScaledMagnitude_ArmorOnce()
        {
            var (world, store) = Wire();
            int id = DamageTarget(world, armor: 3);
            ApplyNStacks(store, id, PeriodicDamage(3, PeriodicStackMode.Multiply, dmg: 10, periodTicks: 5), 3);

            Fixed oneBigHit = DamageTable.Default.FinalDamage(Fixed.FromInt(30), DamageType.Normal, ArmorType.Unarmored, Fixed.FromInt(3));
            for (int t = 1; t <= 4; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); // tick 5 → ONE hit at base×3, armor subtracted once
            Assert.Equal((Fixed.FromInt(1000) - oneBigHit).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void RepeatAndMultiply_DifferGivenArmor_ProvingTheDistinctionIsReal()
        {
            Fixed repeat   = DamageTable.Default.FinalDamage(Fixed.FromInt(10), DamageType.Normal, ArmorType.Unarmored, Fixed.FromInt(3)) * Fixed.FromInt(3);
            Fixed multiply = DamageTable.Default.FinalDamage(Fixed.FromInt(30), DamageType.Normal, ArmorType.Unarmored, Fixed.FromInt(3));
            Assert.NotEqual(repeat.Raw, multiply.Raw); // N small hits (armor N times) != one big hit (armor once)
        }

        [Fact]
        public void PeriodicMultiply_ScalesAHealPulse_OneBigHeal()
        {
            // The heal analog of PeriodicMultiply damage: a Multiply HoT heals base × min(stacks, cap) in ONE pulse.
            // Ceiling well above the healed amount so no clamp masks the scale.
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1000), Fixed.FromInt(3)); // ceiling 1000
            world.Health[id] = Fixed.FromInt(100);
            var m = new Modifier(id: 12, durationTicks: 100000, StackRule.Stack, maxStacks: 3,
                                 Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None,
                                 new HealEffect(Fixed.FromInt(10)), periodTicks: 5, Fixed.Zero, PeriodicStackMode.Multiply);
            ApplyNStacks(store, id, m, 3);
            Assert.Equal(3, store.StackCountAt(id, 0));

            for (int t = 1; t <= 4; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); // tick 5 → ONE pulse of +10 × 3 = +30
            Assert.Equal(Fixed.FromInt(130).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void PeriodicMultiply_ScaleIsBoundedByTheCap()
        {
            var (world, store) = Wire();
            int id = DamageTarget(world, armor: 0);
            // 10 grouped stacks, but the cap is MaxPeriodicStackScale (8): the pulse scales to ×8, not ×10.
            int stacks = EffectCaps.MaxPeriodicStackScale + 2;
            ApplyNStacks(store, id, PeriodicDamage(stacks, PeriodicStackMode.Multiply, dmg: 10, periodTicks: 5), stacks);
            Assert.Equal(stacks, store.StackCountAt(id, 0)); // the store holds all 10 stacks

            for (int t = 1; t <= 4; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); // tick 5 → one hit at 10 × min(10, 8) = 80, NOT 100
            Assert.Equal(Fixed.FromInt(1000 - 10 * EffectCaps.MaxPeriodicStackScale).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void PeriodicMode_OnStackIndependent_IsANoOp_PulseScalesViaTheStacksThemselves()
        {
            // Each StackIndependent slot has _stackCount == 1, so its per-slot pulse scale is 1 regardless of the mode;
            // three same-id slots each pulse once → three hits, exactly like three unstacked periodic modifiers.
            var (world, store) = Wire();
            int id = DamageTarget(world, armor: 0);
            var m = new Modifier(id: 11, durationTicks: 100000, StackRule.StackIndependent, maxStacks: 3,
                                 Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None,
                                 new DamageEffect(Fixed.FromInt(10), DamageType.Normal), periodTicks: 5,
                                 Fixed.Zero, PeriodicStackMode.Multiply); // Multiply is inert here
            ApplyNStacks(store, id, m, 3);
            Assert.Equal(3, store.CountAt(id)); // three independent slots

            for (int t = 1; t <= 4; t++) store.Advance(world, Dt);
            store.Advance(world, Dt); // tick 5 → each of the 3 slots pulses once = 30 total (mode had no extra effect)
            Assert.Equal(Fixed.FromInt(970).Raw, world.Health[id].Raw);
        }

        private static int CountSameId(ModifierStore store, int id)
        {
            int n = 0;
            for (int s = 0; s < store.CountAt(id); s++) if (store.ModifierIdAt(id, s) == 7) n++;
            return n;
        }
    }
}
