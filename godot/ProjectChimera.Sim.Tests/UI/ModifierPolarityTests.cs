#nullable enable
using ProjectChimera.Combat; // DamageType
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Story 11.5 (FR-74) — the Godot-free buff/debuff polarity classifier. Proves a beneficial modifier classifies
    /// Buff, a harmful/status modifier classifies Debuff, and the neutral / mixed edge cases.
    /// </summary>
    public class ModifierPolarityTests
    {
        // Modifier(id, durationTicks, stacking, maxStacks, maxHealthDelta, attackDamageDelta, moveSpeedDelta,
        //          status, periodEffect, periodTicks, armorDelta)
        private static Modifier Mod(Fixed maxHp = default, Fixed atk = default, Fixed move = default,
                                    StatusFlags status = StatusFlags.None, Fixed armor = default,
                                    EffectNode? period = null, int periodTicks = 0)
            => new Modifier(1, 60, StackRule.Refresh, 1, maxHp, atk, move, status, period, periodTicks, armor);

        [Fact]
        public void Beneficial_stat_modifier_classifies_as_Buff()
        {
            var mod = Mod(atk: Fixed.FromInt(5));
            Assert.Equal(ModifierPolarity.Polarity.Buff, ModifierPolarity.Classify(mod));
        }

        [Fact]
        public void Harmful_stat_modifier_classifies_as_Debuff()
        {
            var mod = Mod(atk: Fixed.FromInt(-4));
            Assert.Equal(ModifierPolarity.Polarity.Debuff, ModifierPolarity.Classify(mod));
        }

        [Fact]
        public void Harmful_status_flag_classifies_as_Debuff_even_with_a_positive_stat()
        {
            // A stun that also grants attack is still a debuff — harmful status dominates.
            var mod = Mod(atk: Fixed.FromInt(3), status: StatusFlags.Stunned);
            Assert.Equal(ModifierPolarity.Polarity.Debuff, ModifierPolarity.Classify(mod));
        }

        [Fact]
        public void Invulnerable_status_classifies_as_Buff()
        {
            var mod = Mod(status: StatusFlags.Invulnerable);
            Assert.Equal(ModifierPolarity.Polarity.Buff, ModifierPolarity.Classify(mod));
        }

        [Fact]
        public void No_deltas_no_status_classifies_as_Neutral()
        {
            var mod = Mod();
            Assert.Equal(ModifierPolarity.Polarity.Neutral, ModifierPolarity.Classify(mod));
        }

        [Fact]
        public void Net_positive_across_mixed_deltas_classifies_as_Buff()
        {
            // +10 HP, -2 attack → net positive → Buff.
            var mod = Mod(maxHp: Fixed.FromInt(10), atk: Fixed.FromInt(-2));
            Assert.Equal(ModifierPolarity.Polarity.Buff, ModifierPolarity.Classify(mod));
        }

        [Fact]
        public void Invulnerable_with_a_net_negative_stat_still_classifies_as_Buff()
        {
            // Review #2: a beneficial status dominates the net-negative early return.
            var mod = Mod(atk: Fixed.FromInt(-4), status: StatusFlags.Invulnerable);
            Assert.Equal(ModifierPolarity.Polarity.Buff, ModifierPolarity.Classify(mod));
        }

        [Fact]
        public void Null_modifier_is_Neutral()
        {
            Assert.Equal(ModifierPolarity.Polarity.Neutral, ModifierPolarity.Classify(null!));
        }

        // ── Periodic (DoT/HoT) polarity (review #1) — a pure periodic modifier must not fall to Neutral ─────────────

        private static Modifier PeriodicMod(EffectNode period)
            => new Modifier(1, 60, StackRule.Refresh, 1, default, default, default,
                            StatusFlags.None, period, periodTicks: 30, default);

        [Fact]
        public void Pure_DoT_classifies_as_Debuff_and_glyphs_DoT()
        {
            var dot = PeriodicMod(new DamageEffect(Fixed.FromInt(5), DamageType.Normal));
            Assert.True(ModifierPolarity.HasPeriod(dot));
            Assert.Equal(ModifierPolarity.Polarity.Debuff, ModifierPolarity.Classify(dot));
            Assert.Equal("DoT", ModifierPolarity.Glyph(dot));
        }

        [Fact]
        public void Pure_HoT_classifies_as_Buff_and_glyphs_HoT()
        {
            var hot = PeriodicMod(new HealEffect(Fixed.FromInt(5)));
            Assert.True(ModifierPolarity.HasPeriod(hot));
            Assert.Equal(ModifierPolarity.Polarity.Buff, ModifierPolarity.Classify(hot));
            Assert.Equal("HoT", ModifierPolarity.Glyph(hot));
        }

        [Fact]
        public void DirectHpDelta_period_sign_follows_the_delta_sign()
        {
            var damaging = PeriodicMod(new DirectHpDeltaEffect(Fixed.FromInt(-3)));
            var healing  = PeriodicMod(new DirectHpDeltaEffect(Fixed.FromInt(3)));
            Assert.Equal(ModifierPolarity.Polarity.Debuff, ModifierPolarity.Classify(damaging));
            Assert.Equal(ModifierPolarity.Polarity.Buff,   ModifierPolarity.Classify(healing));
        }

        [Fact]
        public void Indeterminate_periodic_glyphs_neutral_not_a_false_HoT()
        {
            // Review follow-up: a periodic modifier whose period sign is 0 (net-neutral / unrecognized effect) must
            // NOT render as a green "HoT" heal — it glyphs the neutral bullet, matching its Neutral classification.
            var neutralPeriodic = PeriodicMod(new DirectHpDeltaEffect(Fixed.Zero));
            Assert.True(ModifierPolarity.HasPeriod(neutralPeriodic));
            Assert.Equal(ModifierPolarity.Polarity.Neutral, ModifierPolarity.Classify(neutralPeriodic));
            Assert.Equal("•", ModifierPolarity.Glyph(neutralPeriodic));
        }
    }
}
