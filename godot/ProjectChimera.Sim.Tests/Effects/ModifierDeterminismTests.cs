#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (AC2) — function-level determinism of the apply/stack/refresh/expire/DoT path (independent of the
    /// persisted golden). Two fresh identical worlds driven through an identical modifier schedule produce
    /// byte-identical per-tick <see cref="SimChecksum"/> sequences; and because the fold is ascending owner-id then
    /// slot, installing the SAME modifier set on DISTINCT entities in a different ORDER yields an identical hash
    /// (commutative install). Period effects are direct-target (<c>spatial: null</c>) — no <c>SearchArea</c>, so no
    /// per-tick spatial rebuild is needed for these guarantees.
    /// </summary>
    public class ModifierDeterminismTests
    {
        private static readonly Fixed Dt = Fixed.Zero;

        private static Modifier AtkMod(int id, int duration, StackRule rule, int maxStacks, int atk) =>
            new Modifier(id, duration, rule, maxStacks, Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        private static PersistentEffect Dot(int dmg, int periodTicks, int periodCount) =>
            new PersistentEffect(null, new DirectHpDeltaEffect(Fixed.FromInt(-dmg)), null, periodTicks, periodCount);

        private static FixedVec3 V(int x) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.Zero);

        /// <summary>Run a fixed apply/stack/refresh/expire/DoT schedule for 40 ticks, recording the per-tick checksum.</summary>
        private static List<uint> RunSchedule()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var registry = new FactionRegistry(2);

            int a = world.Create(V(0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int b = world.Create(V(5), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int c = world.Create(V(10), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[a] = Fixed.FromInt(10); world.EffectiveAttackDamage[a] = Fixed.FromInt(10);
            world.BaseAttackDamage[b] = Fixed.FromInt(10); world.EffectiveAttackDamage[b] = Fixed.FromInt(10);

            var hashes = new List<uint>(40);
            for (int t = 0; t < 40; t++)
            {
                if (t == 0)
                {
                    store.Apply(a, AtkMod(1, 15, StackRule.Refresh, 1, 5), a, Faction.Player1);
                    store.Apply(b, AtkMod(2, 25, StackRule.Stack, 3, 3), b, Faction.Player1);
                    store.Apply(b, AtkMod(2, 25, StackRule.Stack, 3, 3), b, Faction.Player1); // second stack
                    store.InstallPersistent(c, Dot(2, periodTicks: 5, periodCount: 4), c, Faction.Player1);
                }
                if (t == 10) store.Apply(a, AtkMod(1, 15, StackRule.Refresh, 1, 5), a, Faction.Player1); // refresh
                if (t == 12) store.Apply(b, AtkMod(2, 25, StackRule.Stack, 3, 3), b, Faction.Player1);    // third stack (cap 3)
                if (t == 14) store.Apply(b, AtkMod(2, 25, StackRule.Ignore, 3, 3), b, Faction.Player1);   // ignored re-apply

                sys.Tick(world, Dt);
                hashes.Add(SimChecksum.Compute(world, buildings, resources, registry, store));
            }
            return hashes;
        }

        [Fact]
        public void TwoIdenticalRuns_ProduceByteIdenticalChecksumSequences()
        {
            var a = RunSchedule();
            var b = RunSchedule();
            Assert.Equal(40, a.Count);
            Assert.True(a.SequenceEqual(b), "Two identical modifier schedules diverged — nondeterminism in the store path.");
            Assert.True(a.Distinct().Count() > 1, "Checksum sequence is constant — the schedule is not exercising the store (vacuous).");
        }

        [Fact]
        public void AscendingId_CommutativeInstallOnDistinctEntities()
        {
            uint inOrder    = HashAfterInstalls(applyInIdOrder: true);
            uint outOfOrder = HashAfterInstalls(applyInIdOrder: false);
            Assert.Equal(inOrder, outOfOrder); // install ORDER on distinct entities cannot change the ascending-id fold
        }

        private static uint HashAfterInstalls(bool applyInIdOrder)
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);

            int e0 = world.Create(V(0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int e1 = world.Create(V(5), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int e2 = world.Create(V(10), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            foreach (int e in new[] { e0, e1, e2 }) { world.BaseAttackDamage[e] = Fixed.FromInt(10); world.EffectiveAttackDamage[e] = Fixed.FromInt(10); }

            // Same assignment (e0←+1, e1←+2, e2←+3), different APPLICATION order.
            if (applyInIdOrder)
            {
                store.Apply(e0, AtkMod(10, 50, StackRule.Refresh, 1, 1), e0, Faction.Player1);
                store.Apply(e1, AtkMod(11, 50, StackRule.Refresh, 1, 2), e1, Faction.Player1);
                store.Apply(e2, AtkMod(12, 50, StackRule.Refresh, 1, 3), e2, Faction.Player1);
            }
            else
            {
                store.Apply(e2, AtkMod(12, 50, StackRule.Refresh, 1, 3), e2, Faction.Player1);
                store.Apply(e0, AtkMod(10, 50, StackRule.Refresh, 1, 1), e0, Faction.Player1);
                store.Apply(e1, AtkMod(11, 50, StackRule.Refresh, 1, 2), e1, Faction.Player1);
            }
            sys.Tick(world, Dt);
            return SimChecksum.Compute(world, new BuildingStore(), new ResourceStore(Fixed.Zero), new FactionRegistry(2), store);
        }
    }
}
