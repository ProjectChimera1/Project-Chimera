#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.13 (AC5, Decision D-4) — the self-lethal cast gate. A <c>cost_health</c> cast that would bring the
    /// caster to ≤0 is REFUSED (atomically, no side effects) unless <c>allow_self_lethal</c>; an intentional
    /// suicide-bomber's effect fires THEN the caster dies via the existing entity-death sequence (deferred death).
    /// A <c>cost_health: 0</c> ability is never affected. Godot-free, deterministic.
    /// </summary>
    public class SelfLethalCastTests
    {
        private static AbilityDefinition ProtectedSpike() => new AbilityDefinition
        {
            Id = "protected_spike", DisplayName = "Protected Spike", Targeting = "Self",
            Cooldown = Fixed.FromInt(1), CostHealth = 25, AllowSelfLethal = false,
            EffectGraph = new ApplyModifierEffect(new Modifier(
                2200, 60, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5), Fixed.Zero, StatusFlags.None, null, 0)),
        };

        private static AbilityDefinition SuicideBomb() => new AbilityDefinition
        {
            Id = "suicide_bomb", DisplayName = "Suicide Bomb", Targeting = "TargetUnit",
            Cooldown = Fixed.FromInt(1), CostHealth = 200, AllowSelfLethal = true, // 200 > any caster HP ⇒ lethal
            EffectGraph = new DamageEffect(Fixed.FromInt(50), DamageType.Magic),
        };

        // ── AC5.3 — a protected self-cost cast AT/BELOW the HP floor is refused atomically ──

        [Fact]
        public void ProtectedSelfCost_AtHpFloor_RefusesAtomically()
        {
            var h = new CastHarness(ProtectedSpike());
            int caster = h.Caster("protected_spike", energy: 0);
            h.World.Health[caster] = Fixed.FromInt(20); // ≤ cost_health 25 ⇒ the cast would reach ≤0

            h.IssueAndTick(caster, -1); // Self cast

            Assert.Equal(Fixed.FromInt(20).Raw, h.World.Health[caster].Raw); // NO HP debited (refused before any debit)
            Assert.Equal(0, h.Cooldown(caster));                             // no cooldown started (atomic refuse)
            Assert.Equal(0, h.Modifiers.CountAt(caster));                    // the effect graph did NOT run
            Assert.True(h.World.IsAlive(caster));                            // still alive — no 0-HP-alive strand
        }

        // ── AC5.3 — above the floor it casts normally and debits the HP ──

        [Fact]
        public void ProtectedSelfCost_AboveHpFloor_Casts_DebitsHealth()
        {
            var h = new CastHarness(ProtectedSpike());
            int caster = h.Caster("protected_spike", energy: 0); // Health defaults to 100

            h.IssueAndTick(caster, -1);

            Assert.Equal(Fixed.FromInt(75).Raw, h.World.Health[caster].Raw); // 100 − 25
            Assert.Equal(1, h.Modifiers.CountAt(caster));                    // the buff installed (effect ran)
            Assert.True(h.Cooldown(caster) > 0);                            // cooldown started
            Assert.True(h.World.IsAlive(caster));
        }

        // ── AC5.4 — a self-lethal cast: the effect fires, THEN the caster dies (deferred death) ──

        [Fact]
        public void SelfLethalCast_EffectRunsThenCasterDies()
        {
            var h = new CastHarness(SuicideBomb());
            int caster = h.Caster("suicide_bomb", energy: 0); // Health 100 < cost_health 200 ⇒ lethal
            int target = h.Target(health: 200);
            Fixed targetHp0 = h.World.Health[target];

            h.IssueAndTick(caster, target);

            Assert.False(h.World.IsAlive(caster)); // the deferred self-death fired (RED without death-at-0)
            Assert.True(h.World.Health[target].Raw < targetHp0.Raw,
                "the suicide effect must resolve BEFORE the caster dies (the target took damage)");
        }

        // ── Regression — a cost_health:0 ability is never touched by the min-HP gate ──

        [Fact]
        public void ZeroCostHealthAbility_NeverRefusedByTheGate()
        {
            var h = new CastHarness(AbilityTestAbilities.MinorHeal()); // cost_health defaults to 0
            int caster = h.Caster("minor_heal", energy: 20);
            h.World.Health[caster] = Fixed.FromInt(1); // even at 1 HP the gate must not fire (0 cost)

            h.IssueAndTick(caster, -1);

            Assert.True(h.Cooldown(caster) > 0); // the cast fired — the gate did not refuse a zero-cost_health ability
        }
    }
}
