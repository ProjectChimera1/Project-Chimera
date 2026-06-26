#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (AC2) — the <see cref="StackRule"/> semantics implemented verbatim from the enum docs:
    ///   • <b>Refresh</b> — a re-apply resets duration, single bonus (1×, never 2×);
    ///   • <b>Stack</b> — a re-apply adds a stack (2× bonus, one slot with <c>_stackCount==2</c>), all stacks expire
    ///     together; capped at <see cref="Modifier.MaxStacks"/>;
    ///   • <b>Ignore</b> — a re-apply is a no-op (duration NOT reset → it expires on the ORIGINAL schedule);
    ///   • slot-full — the <c>(MaxModifiersPerEntity+1)</c>th distinct modifier is refused deterministically.
    /// </summary>
    public class ModifierStackingTests
    {
        private static readonly Fixed Dt = Fixed.Zero;

        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        private static int Unit(EntityWorld world, int baseAtk = 10)
        {
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(baseAtk);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(baseAtk);
            return id;
        }

        private static Modifier AtkMod(int id, int duration, StackRule rule, int maxStacks, int atk) =>
            new Modifier(id, duration, rule, maxStacks, Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        [Fact]
        public void Refresh_ReApply_KeepsSingleBonus_OneSlot()
        {
            var (world, sys, store) = Wire();
            int id = Unit(world);

            store.Apply(id, AtkMod(1, 10, StackRule.Refresh, 1, 5), id, Faction.Player1);
            store.Apply(id, AtkMod(1, 10, StackRule.Refresh, 1, 5), id, Faction.Player1); // re-apply

            Assert.Equal(1, store.CountAt(id));
            Assert.Equal(1, store.StackCountAt(id, 0));
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw); // Base 10 + ONE ×5, not +10
        }

        [Fact]
        public void Stack_ReApply_AddsStack_BothExpireTogether()
        {
            var (world, sys, store) = Wire();
            int id = Unit(world);

            store.Apply(id, AtkMod(1, 1, StackRule.Stack, 3, 5), id, Faction.Player1);
            store.Apply(id, AtkMod(1, 1, StackRule.Stack, 3, 5), id, Faction.Player1); // second stack

            Assert.Equal(1, store.CountAt(id));            // single slot
            Assert.Equal(2, store.StackCountAt(id, 0));    // two stacks
            Assert.Equal(Fixed.FromInt(20).Raw, world.EffectiveAttackDamage[id].Raw); // 10 + 2×5

            sys.Tick(world, Dt); // shared duration 1 → both stacks expire together, full −10 reverted at once
            Assert.Equal(0, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw);
        }

        [Fact]
        public void Stack_CapsAtMaxStacks()
        {
            var (world, sys, store) = Wire();
            int id = Unit(world);

            for (int k = 0; k < 5; k++) // five applies, MaxStacks = 2
                store.Apply(id, AtkMod(1, 10, StackRule.Stack, 2, 5), id, Faction.Player1);

            Assert.Equal(2, store.StackCountAt(id, 0)); // capped at 2
            Assert.Equal(Fixed.FromInt(20).Raw, world.EffectiveAttackDamage[id].Raw); // 10 + 2×5, NOT 5×5
        }

        [Fact]
        public void Ignore_ReApply_IsNoOp_ExpiresOnOriginalSchedule()
        {
            var (world, sys, store) = Wire();
            int id = Unit(world);

            store.Apply(id, AtkMod(1, 2, StackRule.Ignore, 1, 5), id, Faction.Player1);
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw);

            sys.Tick(world, Dt); // remaining 2 → 1
            store.Apply(id, AtkMod(1, 2, StackRule.Ignore, 1, 5), id, Faction.Player1); // ignored — does NOT reset to 2
            Assert.Equal(1, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw); // still 1× bonus

            sys.Tick(world, Dt); // remaining 1 → 0 → expires on the ORIGINAL schedule (RED if Ignore had refreshed)
            Assert.Equal(0, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw);
        }

        [Fact]
        public void SlotFull_RefusesTheExtraModifier_Deterministically()
        {
            var (world, sys, store) = Wire();
            int id = Unit(world);

            // Apply MaxModifiersPerEntity + 1 DISTINCT modifiers; the last is dropped (never overflows the ring).
            for (int k = 0; k < EffectCaps.MaxModifiersPerEntity + 1; k++)
                store.Apply(id, AtkMod(100 + k, 10, StackRule.Refresh, 1, 1), id, Faction.Player1);

            Assert.Equal(EffectCaps.MaxModifiersPerEntity, store.CountAt(id));
            // Exactly MaxModifiersPerEntity ×(+1) applied (the 9th was refused) → Base 10 + 8.
            Assert.Equal(Fixed.FromInt(10 + EffectCaps.MaxModifiersPerEntity).Raw, world.EffectiveAttackDamage[id].Raw);
        }
    }
}
