#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 1.7 (AC4) — the shadow / fail-closed policy. The decision is a pure Godot-free function
    /// (<see cref="ScenarioGate.ShouldProceed"/>), so it is Tier-1 testable; the GD.PrintErr log and the actual
    /// apply live at the MainScene call site (presentation), verified by the Task 7 in-engine smoke. Also pins
    /// that the validator is pure (never throws) and that <see cref="ValidationResult.Fail"/> carries the located
    /// message the call site logs.
    /// </summary>
    public class ShadowModeTests
    {
        [Theory]
        [InlineData(true,  false, true)]  // valid   + shadow       → proceed
        [InlineData(false, false, true)]  // invalid + shadow       → proceed (log-only; master never breaks)
        [InlineData(true,  true,  true)]  // valid   + fail-closed  → proceed
        [InlineData(false, true,  false)] // invalid + fail-closed  → HALT (the only halt case)
        public void ShouldProceed_ImplementsShadowAndFailClosed(bool ok, bool failClosed, bool expected)
        {
            Assert.Equal(expected, ScenarioGate.ShouldProceed(ok, failClosed));
        }

        [Fact]
        public void FailClosedEnvVar_IsTheDocumentedToggleName()
        {
            Assert.Equal("CHIMERA_VALIDATE_FAILCLOSED", ScenarioGate.FailClosedEnvVar);
        }

        [Fact]
        public void IsFailClosed_DefaultsOff_WhenEnvUnset()
        {
            // Only assert the default when the var is actually absent, so this never fails in a CI that sets it.
            if (System.Environment.GetEnvironmentVariable(ScenarioGate.FailClosedEnvVar) == null)
                Assert.False(ScenarioGate.IsFailClosed());
        }

        [Fact]
        public void ValidationResult_Fail_CarriesLocatedMessage()
        {
            ValidationResult r = ValidationResult.Fail("scenario.units[3].slot=5 references no declared player_slot");
            Assert.False(r.Ok);
            Assert.Equal("scenario.units[3].slot=5 references no declared player_slot", r.Error);
        }

        [Fact]
        public void Validator_IsPure_NeverThrows_OnInvalidInput()
        {
            // The validator returns located errors; it must NOT throw (the call site decides shadow vs fail-closed).
            var invalid = new ScenarioData
            {
                MapBounds = -1f,
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 99 } },
            };
            var ex = Record.Exception(() => new ScenarioValidator().Validate(invalid));
            Assert.Null(ex);
            Assert.False(new ScenarioValidator().Validate(invalid).Ok);
        }
    }
}
