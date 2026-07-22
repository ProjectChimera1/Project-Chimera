#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction
using ProjectChimera.Core.Definitions;  // UnitDefinition
using Xunit;
using static ProjectChimera.Sim.Tests.AI.EntityDraftTestData;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.4 — the quantize-before-hash contract is satisfied by REUSE, not a second float→Fixed path:
    ///   • an ability draft number round-trips equal to <c>Fixed.FromFloat(...)</c> after <see cref="LLMService.ValidateAbilityDraft"/>
    ///     (quantized at parse by <c>ContentJson.Options</c>/<c>FixedJsonConverter</c> — the SAME boundary hand-authored abilities use);
    ///   • a unit draft's in-range float is accepted and, applied through the existing <c>EntityWorld.ApplyUnitDefinition</c>
    ///     boundary, yields the SAME SoA <see cref="Fixed"/> as an equivalent hand-authored def, while an out-of-Fixed-range
    ///     float is rejected at the validator (never reaching the sim boundary).
    /// </summary>
    public class EntityDraftQuantizeTests
    {
        [Fact]
        public void ValidateAbilityDraft_Number_QuantizesThroughFixedJsonConverter()
        {
            string json = "{\"id\":\"tick\",\"targeting\":\"Self\",\"cooldown\":1.333333," +
                          "\"effect\":{\"kind\":\"heal\",\"amount\":40}}";
            var (def, err) = LLMService.ValidateAbilityDraft(json, AbilityCtx());

            Assert.Null(err);
            Assert.NotNull(def);
            // Identical to the sanctioned parse-time quantization — no bespoke second path.
            Assert.Equal(Fixed.FromFloat(1.333333f).Raw, def!.Cooldown.Raw);
        }

        [Fact]
        public void UnitDraft_InRangeFloat_AppliesToSameSoAFixedAsHandAuthored()
        {
            string json = "{\"id\":\"grunt\",\"category\":\"Melee\",\"hp\":123,\"speed\":4.25," +
                          "\"attack_damage\":17.5,\"attack_range\":6,\"attack_speed\":1.25}";
            var (draft, err) = LLMService.ValidateUnitDraft(json, UnitCtx());
            Assert.Null(err);
            Assert.NotNull(draft);

            // An equivalent HAND-AUTHORED def with the identical float stats.
            var hand = new UnitDefinition
            {
                Id = "grunt", Category = "Melee",
                Hp = 123f, Speed = 4.25f, AttackDamage = 17.5f, AttackRange = 6f, AttackSpeed = 1.25f,
            };

            Assert.Equal(ApplyAndReadAttackDamage(hand).Raw, ApplyAndReadAttackDamage(draft!).Raw);
            // And the absolute quantization matches Fixed.FromFloat at the single ApplyUnitDefinition boundary.
            Assert.Equal(Fixed.FromFloat(17.5f).Raw, ApplyAndReadAttackDamage(draft!).Raw);
        }

        [Fact]
        public void UnitDraft_OutOfFixedRangeFloat_RejectedBeforeSimBoundary()
        {
            string json = "{\"id\":\"grunt\",\"category\":\"Melee\",\"attack_damage\":40000}";
            var (def, err) = LLMService.ValidateUnitDraft(json, UnitCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("attack_damage", err);
        }

        /// <summary>Apply <paramref name="def"/> through the single def→SoA mapper and read the quantized BaseAttackDamage.</summary>
        private static Fixed ApplyAndReadAttackDamage(UnitDefinition def)
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);
            return w.BaseAttackDamage[id];
        }
    }
}
