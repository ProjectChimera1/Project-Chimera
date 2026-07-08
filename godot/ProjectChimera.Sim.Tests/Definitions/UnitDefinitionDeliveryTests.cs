#nullable enable
using ProjectChimera.Combat;            // AttackDelivery
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // UnitDefinition
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.12 — the authored attack-delivery resolution (<see cref="UnitDefinition.ResolveDelivery"/> +
    /// <see cref="UnitDefinition.EffectiveDeliveryString"/>). Directly pins the legacy-safe DEFAULT: a null/omitted
    /// <c>delivery</c> falls back to the EXACT old range inference (quantized AttackRange &gt; 2.5 ⇒ Projectile), so
    /// every shipped unit keeps its current behaviour (AC4), while an explicit value WINS regardless of range (AC2 —
    /// delivery decoupled from range). Complements the golden/validator coverage: this exercises the null-inference
    /// branch that the explicit-Projectile guard-test def never runs.
    /// </summary>
    public class UnitDefinitionDeliveryTests
    {
        // ── Legacy inference: omitted delivery resolves from range, preserving today's behaviour ──

        [Theory]
        [InlineData(6.5f, AttackDelivery.Projectile)]  // archer/ranged (> 2.5) — stays projectile
        [InlineData(10f,  AttackDelivery.Projectile)]  // siege (> 2.5)         — stays projectile
        [InlineData(2.6f, AttackDelivery.Projectile)]  // just above the threshold
        [InlineData(1.5f, AttackDelivery.Hitscan)]     // melee (< 2.5)         — stays instant
        [InlineData(2.0f, AttackDelivery.Hitscan)]     // melee-range flyer (< 2.5)
        [InlineData(2.5f, AttackDelivery.Hitscan)]     // AT the threshold — strict '>' keeps it Hitscan (old rule)
        public void NullDelivery_InfersFromRange(float range, AttackDelivery expected)
        {
            var def = new UnitDefinition { Delivery = null, AttackRange = range };
            Assert.Equal(expected, def.ResolveDelivery(Fixed.FromFloat(range)));
        }

        // ── Explicit authored value wins over range (delivery decoupled from range) ──

        [Fact]
        public void ExplicitHitscan_OverridesLongRange()
        {
            var def = new UnitDefinition { Delivery = "Hitscan", AttackRange = 12f };
            Assert.Equal(AttackDelivery.Hitscan, def.ResolveDelivery(Fixed.FromInt(12)));
        }

        [Fact]
        public void ExplicitProjectile_OverridesShortRange()
        {
            var def = new UnitDefinition { Delivery = "Projectile", AttackRange = 1f };
            Assert.Equal(AttackDelivery.Projectile, def.ResolveDelivery(Fixed.FromInt(1)));
        }

        // ── Unknown string fails OPEN to the range inference (accessor/validator split — the validator rejects it) ──

        [Fact]
        public void UnknownDelivery_FailsOpenToRangeInference()
        {
            var def = new UnitDefinition { Delivery = "Beam", AttackRange = 6f };
            Assert.Equal(AttackDelivery.Projectile, def.ResolveDelivery(Fixed.FromInt(6)));
        }

        // ── Editor display string mirrors the same resolution over the def's authored range ──

        [Theory]
        [InlineData(null, 6.5f, "Projectile")]        // unauthored ranged unit still displays Projectile
        [InlineData(null, 1.5f, "Hitscan")]           // unauthored melee unit displays Hitscan
        [InlineData("Hitscan", 12f, "Hitscan")]       // explicit wins over range
        [InlineData("Projectile", 1f, "Projectile")]  // explicit wins over range
        public void EffectiveDeliveryString_MatchesResolution(string? delivery, float range, string expected)
        {
            var def = new UnitDefinition { Delivery = delivery, AttackRange = range };
            Assert.Equal(expected, def.EffectiveDeliveryString());
        }
    }
}
