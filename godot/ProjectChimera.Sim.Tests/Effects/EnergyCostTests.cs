#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (AC1, Energy clause) — <see cref="ModifierStore.TryDebitEnergy"/> succeeds (and subtracts) only
    /// when <c>Energy &gt;= cost</c>, and REFUSES without mutating <c>Energy</c> when insufficient. Boundary
    /// (<c>cost == Energy</c>), refuse-no-mutation (teeth), negative cost, and a dead id are all covered.
    /// </summary>
    public class EnergyCostTests
    {
        private static (EntityWorld world, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var store = new ModifierStore(world); // fold-only/logic store is enough — Energy debit needs no system
            return (world, store);
        }

        [Fact]
        public void Debit_Succeeds_WhenAffordable_AndSubtracts()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Energy[id] = Fixed.FromInt(10);

            Assert.True(store.TryDebitEnergy(id, Fixed.FromInt(4)));
            Assert.Equal(Fixed.FromInt(6).Raw, world.Energy[id].Raw);
        }

        [Fact]
        public void Debit_Refuses_WhenInsufficient_WithoutMutating()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Energy[id] = Fixed.FromInt(6);

            Assert.False(store.TryDebitEnergy(id, Fixed.FromInt(7))); // 7 > 6
            Assert.Equal(Fixed.FromInt(6).Raw, world.Energy[id].Raw); // UNCHANGED — RED if refuse mutates
        }

        [Fact]
        public void Debit_ExactCost_Boundary_Succeeds_ToZero()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Energy[id] = Fixed.FromInt(6);

            Assert.True(store.TryDebitEnergy(id, Fixed.FromInt(6))); // cost == energy
            Assert.Equal(Fixed.Zero.Raw, world.Energy[id].Raw);
        }

        [Fact]
        public void Debit_NegativeCost_Refuses_NeverRefunds()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Energy[id] = Fixed.FromInt(5);

            Assert.False(store.TryDebitEnergy(id, Fixed.FromInt(-3)));
            Assert.Equal(Fixed.FromInt(5).Raw, world.Energy[id].Raw); // no refund / no mutation
        }

        [Fact]
        public void Debit_DeadId_ReturnsFalse_NoThrow()
        {
            var (world, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Energy[id] = Fixed.FromInt(5);
            world.Destroy(id);

            Assert.False(store.TryDebitEnergy(id, Fixed.FromInt(1)));
            Assert.False(store.TryDebitEnergy(9999, Fixed.FromInt(1))); // out of range
        }
    }
}
