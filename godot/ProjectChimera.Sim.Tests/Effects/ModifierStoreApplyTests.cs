#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (AC5) — stat-modifier apply/remove through the 2.2a <c>AccumulateBonus</c> seam, plus the
    /// negative-stat Zero-floor and the MaxHealth Health semantics. Proves:
    ///   • apply adds the deltas so <c>Effective* == Base + Σ</c> the same tick; expiry reverts to <c>Base</c>;
    ///   • status flags set on apply, recomputed (NOT blindly cleared) on remove — a flag a second modifier still
    ///     holds survives;
    ///   • a debuff can never drive a stat below <see cref="Fixed.Zero"/> (the Zero-floor — RED without it);
    ///   • MaxHealth semantics (Decision #3 = heal-on-apply, refined in 2.2b review to heal-on-BUFF-apply ONLY): a
    ///     +MaxHealth buff RAISES current Health by the same amount (a burst heal); removal clamps Health DOWN to the
    ///     new ceiling (no phantom HP); a −MaxHealth DEBUFF round-trip restores the ceiling WITHOUT restoring HP (no
    ///     free heal from a wearing-off enemy debuff — RED under the old symmetric model).
    /// Bare worlds via <see cref="EntityWorld.Create"/>; <see cref="Fixed.FromInt"/> only; independently-derived raws.
    /// </summary>
    public class ModifierStoreApplyTests
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

        /// <summary>A pure stat/status modifier (no period).</summary>
        private static Modifier StatMod(int id, int duration, StackRule rule, int maxStacks,
            int maxHp, int atk, int move, StatusFlags status = StatusFlags.None) =>
            new Modifier(id, duration, rule, maxStacks, Fixed.FromInt(maxHp), Fixed.FromInt(atk),
                         Fixed.FromInt(move), status, periodEffect: null, periodTicks: 0);

        [Fact]
        public void Apply_AddsDeltas_SameTick_Then_Expiry_RevertsToBase()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(10);
            world.BaseMoveSpeed[id]    = Fixed.FromInt(4);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(10);
            world.EffectiveMoveSpeed[id]    = Fixed.FromInt(4);

            // duration 1 → expires on the first Advance.
            store.Apply(id, StatMod(1, 1, StackRule.Refresh, 1, maxHp: 0, atk: 5, move: 2), id, Faction.Player1);

            // Eager recompute inside Apply: Effective == Base + delta with NO Tick needed (the same-tick guarantee).
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw); // 10 + 5
            Assert.Equal(Fixed.FromInt(6).Raw,  world.EffectiveMoveSpeed[id].Raw);    // 4 + 2
            Assert.Equal(1, store.CountAt(id));

            sys.Tick(world, Dt); // duration 1 → expires → RemoveSlot reverts the bonus

            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw); // back to Base
            Assert.Equal(Fixed.FromInt(4).Raw,  world.EffectiveMoveSpeed[id].Raw);
            Assert.Equal(0, store.CountAt(id));
        }

        [Fact]
        public void NegativeDebuff_FloorsEffectiveAtZero_NotNegative()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(10);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(10);

            // A −9999 attack debuff. Without the Zero-floor this would be 10 + (−9999) = −9989 → RED.
            store.Apply(id, StatMod(1, 10, StackRule.Refresh, 1, maxHp: 0, atk: -9999, move: 0), id, Faction.Player1);

            Assert.Equal(Fixed.Zero.Raw, world.EffectiveAttackDamage[id].Raw); // floored at 0, never negative
        }

        [Fact]
        public void MaxHealthBuff_HealsOnApply_And_ClampsDownOnRemove() // Decision #3 (Alec): heal-proportionally-on-apply
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            // Full-HP unit: 100/100.
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);

            store.Apply(id, StatMod(1, 1, StackRule.Refresh, 1, maxHp: 50, atk: 0, move: 0), id, Faction.Player1);

            // Heal-on-apply: the rising ceiling raises current Health by the same amount → 150/150.
            Assert.Equal(Fixed.FromInt(150).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(150).Raw, world.Health[id].Raw);

            sys.Tick(world, Dt); // duration 1 → remove

            // Remove clamps Health DOWN to the new (base) ceiling — no phantom HP.
            Assert.Equal(Fixed.FromInt(100).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void MaxHealthBuff_OnDamagedUnit_HealsAdditively_NotToFull()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(50); // damaged: 50/100

            store.Apply(id, StatMod(1, 10, StackRule.Refresh, 1, maxHp: 50, atk: 0, move: 0), id, Faction.Player1);

            // Additive heal (current += maxDelta), NOT fill-to-full: 50 + 50 = 100 of a new 150 ceiling → 100/150.
            Assert.Equal(Fixed.FromInt(150).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void MaxHealthDebuff_RoundTrip_RestoresCeiling_WithoutHealing() // 2.2b review (D1): heal-on-BUFF-apply only
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(50); // damaged: 50/100

            // A −50 MaxHealth debuff (duration 1). Apply drops the ceiling to 50 and clamps Health to 50 → 50/50.
            store.Apply(id, StatMod(1, 1, StackRule.Refresh, 1, maxHp: -50, atk: 0, move: 0), id, Faction.Player1);
            Assert.Equal(Fixed.FromInt(50).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(50).Raw, world.Health[id].Raw);

            sys.Tick(world, Dt); // duration 1 → debuff removed

            // Ceiling restored to 100 — but Health is NOT healed up (clamp-only on removal). Old symmetric model
            // would have added +50 here (→ 100/100, a free heal from a wearing-off enemy debuff). RED without the fix.
            Assert.Equal(Fixed.FromInt(100).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(50).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void StatusFlags_SetOnApply_SurviveRemoveWhileAnotherModifierHoldsThem()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            // A: Stunned for 2 ticks. B (distinct id): Stunned for 1 tick.
            store.Apply(id, StatMod(1, 2, StackRule.Refresh, 1, 0, 0, 0, StatusFlags.Stunned), id, Faction.Player1);
            store.Apply(id, StatMod(2, 1, StackRule.Refresh, 1, 0, 0, 0, StatusFlags.Stunned), id, Faction.Player1);
            Assert.True((world.StatusFlagsOf[id] & StatusFlags.Stunned) != 0);

            sys.Tick(world, Dt); // B expires; A still holds Stunned → the flag must SURVIVE (RED if remove blindly clears)
            Assert.True((world.StatusFlagsOf[id] & StatusFlags.Stunned) != 0);
            Assert.Equal(1, store.CountAt(id));

            sys.Tick(world, Dt); // A expires → no modifier holds Stunned → cleared
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[id]);
            Assert.Equal(0, store.CountAt(id));
        }

        [Fact]
        public void Apply_OnDeadOrStaleId_IsNoOp_NoThrow()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.Destroy(id);

            store.Apply(id, StatMod(1, 5, StackRule.Refresh, 1, 0, 5, 0), id, Faction.Player1); // dead target
            store.Apply(9999, StatMod(1, 5, StackRule.Refresh, 1, 0, 5, 0), 0, Faction.Player1); // out-of-range id

            Assert.Equal(0, store.CountAt(id));
        }
    }
}
