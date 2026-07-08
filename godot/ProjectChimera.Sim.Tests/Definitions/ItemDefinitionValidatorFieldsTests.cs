#nullable enable
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.16 — the EDITOR-facing keyed-error surface of <see cref="ItemDefinitionValidator.ValidateFields"/>
    /// (mirrors the Unit editor's per-field badges): every rule emits a <c>(FieldPath, Message)</c> keyed to its JSON
    /// field, ALL errors are collected (not first-fail), the missing-icon-file check fails closed via the injected
    /// existence delegate, and a valid item yields zero errors. The sim first-fail <see cref="ItemDefinitionValidator.Validate"/>
    /// gate is unchanged (covered by the existing ItemDefinitionValidatorTests).
    /// </summary>
    public class ItemDefinitionValidatorFieldsTests
    {
        private static readonly ItemDefinitionValidator V = new();

        private static bool HasKey(ItemValidationResult r, string key) => r.Errors.Any(e => e.FieldPath == key);

        [Fact]
        public void ValidItem_HasNoErrors()
        {
            var def = new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50) };
            var r = V.ValidateFields(def);
            Assert.True(r.Ok);
            Assert.Empty(r.Errors);
        }

        [Fact]
        public void NegativeCharges_IsKeyedError()
        {
            var def = new ItemDefinition { Id = "x", Charges = -1 };
            Assert.True(HasKey(V.ValidateFields(def), "charges"));
        }

        [Fact]
        public void StatItemWithEffectGraph_IsKeyedEffectError()
        {
            var def = new ItemDefinition { Id = "x", Charges = 0, EffectGraph = new HealEffect(Fixed.FromInt(10)) };
            Assert.True(HasKey(V.ValidateFields(def), "effect"));
        }

        [Fact]
        public void ConsumableWithNoEffect_IsKeyedEffectError()
        {
            var def = new ItemDefinition { Id = "x", Charges = 2, EffectGraph = null };
            Assert.True(HasKey(V.ValidateFields(def), "effect"));
        }

        [Fact]
        public void OverCapDelta_IsKeyedError()
        {
            var def = new ItemDefinition { Id = "x", Charges = 0, AttackDamageDelta = Fixed.FromInt(5000) };
            Assert.True(HasKey(V.ValidateFields(def), "attack_damage_delta"));
        }

        [Fact]
        public void NegativeCost_IsKeyedError()
        {
            var def = new ItemDefinition { Id = "x", Charges = 0, CostOre = Fixed.FromInt(-5) };
            Assert.True(HasKey(V.ValidateFields(def), "cost_ore"));
        }

        [Fact]
        public void MissingIconFile_IsKeyedError_WhenExistenceDelegateSaysNo()
        {
            var def = new ItemDefinition { Id = "ring", Charges = 0, Icon = "res://missing.png" };
            Assert.True(HasKey(V.ValidateFields(def, _ => false), "icon"));
            // Delegate says it exists → no icon error.
            Assert.False(HasKey(V.ValidateFields(def, _ => true), "icon"));
            // No delegate → icon check skipped (Godot-free callers).
            Assert.False(HasKey(V.ValidateFields(def, null), "icon"));
        }

        [Fact]
        public void MultipleErrors_AllCollected()
        {
            var def = new ItemDefinition { Id = "", Charges = -1, CostOre = Fixed.FromInt(-1) };
            var r = V.ValidateFields(def);
            Assert.False(r.Ok);
            Assert.True(r.Errors.Count >= 3);
        }
    }
}
