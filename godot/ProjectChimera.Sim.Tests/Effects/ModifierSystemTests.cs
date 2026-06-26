#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2a (AC2) — the dirty-flag effective-stat recompute. Proves <see cref="ModifierSystem"/>:
    ///   • recomputes each <c>Effective* = Base* + Σ deltas</c> for a dirty entity and clears the flag;
    ///   • does NOT recompute a clean entity (the dirty gate — RED if the gate is removed);
    ///   • is order-independent (two commutative deltas yield the identical effective stat);
    ///   • skips dead/recycled slots without throwing.
    /// Bare worlds via <see cref="EntityWorld.Create"/> + direct <c>Base*</c> writes; <see cref="Fixed.FromInt"/>
    /// only (no FromFloat); asserts against independently-derived <c>Fixed.Raw</c> (no tautological asserts).
    /// </summary>
    public class ModifierSystemTests
    {
        // ModifierSystem.Tick ignores dt (the recompute is time-independent); pass Zero explicitly.
        private static readonly Fixed Dt = Fixed.Zero;

        /// <summary>Recompute over an isolated world: Base attack = <paramref name="baseDmg"/>, then the given
        /// attack-damage deltas applied IN ORDER, one Tick. Returns the resulting EffectiveAttackDamage.</summary>
        private static Fixed AttackAfterDeltas(int baseDmg, params int[] deltas)
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(baseDmg);
            foreach (int d in deltas) sys.AccumulateBonus(id, Fixed.FromInt(d), Fixed.Zero, Fixed.Zero);
            sys.Tick(world, Dt);
            return world.EffectiveAttackDamage[id];
        }

        [Fact]
        public void Tick_DirtyEntity_RecomputesEffectiveAsBasePlusDelta_AndClearsDirty()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            // Authored bases set explicitly (independent of the Create defaults).
            world.BaseAttackDamage[id] = Fixed.FromInt(10);
            world.BaseMaxHealth[id]    = Fixed.FromInt(100);
            world.BaseMoveSpeed[id]    = Fixed.FromInt(4);

            sys.AccumulateBonus(id, Fixed.FromInt(5), Fixed.FromInt(20), Fixed.FromInt(1));
            sys.Tick(world, Dt);

            // Effective == Base + delta, each independently derived.
            Assert.Equal(Fixed.FromInt(15).Raw,  world.EffectiveAttackDamage[id].Raw); // 10 + 5
            Assert.Equal(Fixed.FromInt(120).Raw, world.EffectiveMaxHealth[id].Raw);    // 100 + 20
            Assert.Equal(Fixed.FromInt(5).Raw,   world.EffectiveMoveSpeed[id].Raw);    // 4 + 1

            // The dirty flag was cleared by the recompute: corrupt an Effective value and re-Tick — it must
            // survive untouched (the entity is now clean, so the recompute skips it).
            world.EffectiveAttackDamage[id] = Fixed.FromInt(-999);
            sys.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(-999).Raw, world.EffectiveAttackDamage[id].Raw);
        }

        [Fact]
        public void Tick_CleanEntity_DoesNotRecompute_LeavesExternallySetEffectiveUntouched()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(10);

            // Never dirtied. An externally-set Effective sentinel must survive a Tick — the dirty gate skips it.
            // RED if the gate is removed: the recompute would reset Effective to Base (10).
            Fixed sentinel = Fixed.FromInt(777);
            world.EffectiveAttackDamage[id] = sentinel;
            sys.Tick(world, Dt);
            Assert.Equal(sentinel.Raw, world.EffectiveAttackDamage[id].Raw);
        }

        [Fact]
        public void AccumulateBonus_IsOrderIndependent_CommutativeDeltas()
        {
            // +5 then +3 must equal +3 then +5 (summation ⇒ commutative), both == Base 10 + 8.
            Assert.Equal(AttackAfterDeltas(10, 5, 3).Raw, AttackAfterDeltas(10, 3, 5).Raw);
            Assert.Equal(Fixed.FromInt(18).Raw, AttackAfterDeltas(10, 5, 3).Raw);
        }

        [Fact]
        public void Tick_SkipsDeadSlots_WithoutThrowing()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.BaseAttackDamage[id]      = Fixed.FromInt(10);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(10);

            sys.AccumulateBonus(id, Fixed.FromInt(5), Fixed.Zero, Fixed.Zero); // dirty + a pending +5 bonus
            world.Destroy(id); // dead, but still within HighWaterMark and still flagged dirty inside the system

            sys.Tick(world, Dt); // must not throw; the IsAlive guard skips the dead slot

            Assert.False(world.IsAlive(id));
            // The dead slot was skipped → Effective was NOT recomputed to Base+bonus (which would be 15).
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw);
        }
    }
}
