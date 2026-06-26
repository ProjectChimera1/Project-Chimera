#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (Task 3.4/3.5) — the Story 2.1 deferred executor guards now RESOLVE against
    /// <c>ctx.ModifierStore</c>: an <see cref="ApplyModifierEffect"/> installs its modifier and a
    /// <see cref="PersistentEffect"/> installs a time-axis instance (running its <c>InitialEffect</c>) when a store is
    /// in context — and BOTH fail CLOSED (throw) when the context carries no store. Exercised through a real,
    /// graph-running <see cref="EffectExecutor"/> distinct from the store's dedicated one (the 2.4 ability-cast shape).
    /// </summary>
    public class ModifierExecutorResolutionTests
    {
        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        private static EffectContext Ctx(EntityWorld world, int id, ModifierStore? store) =>
            new EffectContext(world, casterId: id, primaryTargetId: id, casterFaction: Faction.Player1,
                              damageTable: DamageTable.Default, spatial: null, events: null, stats: null,
                              modifierStore: store);

        [Fact]
        public void ApplyModifierEffect_ThroughExecutor_InstallsAndBuffs()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(10);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(10);

            var leaf = new ApplyModifierEffect(new Modifier(1, 10, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5),
                                                            Fixed.Zero, StatusFlags.None, null, 0));
            new EffectExecutor().Run(leaf, Ctx(world, id, store));

            Assert.Equal(1, store.CountAt(id));
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw); // 10 + 5 (eager recompute)
        }

        [Fact]
        public void PersistentEffect_ThroughExecutor_InstallsAndRunsInitialEffect()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(50);

            var pe = new PersistentEffect(initialEffect: new HealEffect(Fixed.FromInt(10)),
                                          periodEffect: new HealEffect(Fixed.FromInt(2)), expireEffect: null,
                                          periodTicks: 5, periodCount: 2);
            new EffectExecutor().Run(pe, Ctx(world, id, store));

            Assert.Equal(1, store.CountAt(id));                            // installed
            Assert.Equal(Fixed.FromInt(60).Raw, world.Health[id].Raw);    // InitialEffect ran (+10)
        }

        [Fact]
        public void ApplyModifierEffect_WithNoStoreInContext_FailsClosed()
        {
            var (world, _, _) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            var leaf = new ApplyModifierEffect(new Modifier(1, 10, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5),
                                                            Fixed.Zero, StatusFlags.None, null, 0));
            Assert.Throws<System.NotSupportedException>(() => new EffectExecutor().Run(leaf, Ctx(world, id, null)));
        }

        [Fact]
        public void PersistentEffect_WithNoStoreInContext_FailsClosed()
        {
            var (world, _, _) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            var pe = new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, 5, 2);
            Assert.Throws<System.NotSupportedException>(() => new EffectExecutor().Run(pe, Ctx(world, id, null)));
        }
    }
}
