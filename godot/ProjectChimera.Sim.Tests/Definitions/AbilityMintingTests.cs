#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.3 AC2 — the minting contract: a PASSED validation carries a usable
    /// <see cref="Validated{T}"/> wrapping the exact model; a FAILED validation carries an unusable
    /// <c>default</c> value, so no rejected definition can ever be cast. (The source-scan that the only
    /// <c>new Validated&lt;</c> minters are {ScenarioValidator.cs, AbilityValidator.cs} lives in
    /// <c>ValidatedMintingTests.NewValidated_AppearsOnlyInValidatorAllowList</c>, extended in this story.)
    /// </summary>
    public class AbilityMintingTests
    {
        private static readonly AbilityValidator V = new();

        [Fact]
        public void PassedValidation_MintsAValidated_CarryingTheModel()
        {
            var def = new AbilityDefinition { Id = "ok", Targeting = "Self", EffectGraph = new HealEffect(Fixed.FromInt(5)) };
            AbilityValidationResult r = V.Validate(def);
            Assert.True(r.Ok, r.Error);
            Assert.Same(def, r.Value.Value); // the validated value IS the model that passed the gate
        }

        [Fact]
        public void FailedValidation_CarriesNoUsableValue()
        {
            // Missing effect → fails; the result must carry a default (null) Validated value — nothing runnable escapes.
            var def = new AbilityDefinition { Id = "bad", Targeting = "Self", EffectGraph = null };
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Null(r.Value.Value); // default(Validated<AbilityDefinition>).Value == null
        }
    }
}
