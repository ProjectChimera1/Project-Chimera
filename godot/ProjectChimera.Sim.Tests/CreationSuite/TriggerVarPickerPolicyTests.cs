#nullable enable
using ProjectChimera.CreationSuite;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.CreationSuite
{
    /// <summary>
    /// DW-345 — the Trigger Editor manual form's variable-picker eligibility policy
    /// (<see cref="TriggerVarPickerPolicy"/>, the Godot-free core behind <c>RefreshVarPickers</c>).
    ///
    /// The defect: the LEGACY (flat/literal) set_variable path filtered by type only, so a PerPlayer-scoped Int
    /// variable was offered — but the flat <c>TriggerAction.Faction</c> defaults to 0 and the form has no
    /// player-slot picker, so the authored trigger always wrote player slot 0 (silently the wrong player).
    /// Story 7.4's second-pass review fixed the WIDENED (expression) path only; DW-345 applies the same
    /// exclusion to the legacy path. The first test here FAILS against the pre-fix filter.
    /// </summary>
    public class TriggerVarPickerPolicyTests
    {
        // ── THE DW-345 regression: the legacy (non-widened) path must exclude PerPlayer targets ──

        [Fact]
        public void LegacyPath_ExcludesPerPlayerIntTargets()
        {
            // Pre-fix this was TRUE (type-only filter) — the picker offered a PerPlayer Int that the flat
            // shape then wrote at player slot 0, whatever player the author meant.
            Assert.False(TriggerVarPickerPolicy.SetVariableTargetEligible(
                DslValueType.Int, VarScope.PerPlayer, widened: false));
        }

        [Fact]
        public void WidenedPath_StillExcludesPerPlayerTargets_The74Pass2Contract()
        {
            // Pins the shipped 7.4 pass-2 exclusion so a refactor cannot regress it while touching the policy.
            Assert.False(TriggerVarPickerPolicy.SetVariableTargetEligible(
                DslValueType.Int, VarScope.PerPlayer, widened: true));
            Assert.False(TriggerVarPickerPolicy.SetVariableTargetEligible(
                DslValueType.Fixed, VarScope.PerPlayer, widened: true));
            Assert.False(TriggerVarPickerPolicy.SetVariableTargetEligible(
                DslValueType.Bool, VarScope.PerPlayer, widened: true));
        }

        // ── The surviving legacy-path shape: Int-only, Global + TriggerLocal (write-scratch) ──

        [Theory]
        [InlineData(VarScope.Global)]
        [InlineData(VarScope.TriggerLocal)]
        public void LegacyPath_OffersIntGlobalsAndTriggerLocals(VarScope scope)
        {
            Assert.True(TriggerVarPickerPolicy.SetVariableTargetEligible(DslValueType.Int, scope, widened: false));
        }

        [Theory]
        [InlineData(DslValueType.Fixed)]
        [InlineData(DslValueType.Bool)]
        [InlineData(DslValueType.Point)]
        [InlineData(DslValueType.Array)]
        [InlineData(DslValueType.EntityRef)]
        [InlineData(DslValueType.FactionRef)]
        [InlineData(DslValueType.TimerRef)]
        public void LegacyPath_StaysIntOnly(DslValueType type)
        {
            Assert.False(TriggerVarPickerPolicy.SetVariableTargetEligible(type, VarScope.Global, widened: false));
        }

        // ── The widened-path shape: Int/Fixed/Bool over Global + TriggerLocal; never Point/Array/refs ──

        [Theory]
        [InlineData(DslValueType.Int)]
        [InlineData(DslValueType.Fixed)]
        [InlineData(DslValueType.Bool)]
        public void WidenedPath_OffersTypedScalars(DslValueType type)
        {
            Assert.True(TriggerVarPickerPolicy.SetVariableTargetEligible(type, VarScope.Global, widened: true));
            Assert.True(TriggerVarPickerPolicy.SetVariableTargetEligible(type, VarScope.TriggerLocal, widened: true));
        }

        [Theory]
        [InlineData(DslValueType.Point)]
        [InlineData(DslValueType.Array)]
        [InlineData(DslValueType.EntityRef)]
        [InlineData(DslValueType.FactionRef)]
        [InlineData(DslValueType.TimerRef)]
        public void WidenedPath_ExcludesNonAssignableTypes(DslValueType type)
        {
            Assert.False(TriggerVarPickerPolicy.SetVariableTargetEligible(type, VarScope.Global, widened: true));
        }

        // ── The condition picker: Int-only, TriggerLocal excluded (a condition reads BEFORE scope entry) ──

        [Fact]
        public void ConditionPicker_OffersIntGlobals_AndExcludesTriggerLocal()
        {
            Assert.True(TriggerVarPickerPolicy.ConditionVariableEligible(DslValueType.Int, VarScope.Global));
            Assert.False(TriggerVarPickerPolicy.ConditionVariableEligible(DslValueType.Int, VarScope.TriggerLocal));
            Assert.False(TriggerVarPickerPolicy.ConditionVariableEligible(DslValueType.Fixed, VarScope.Global));
        }
    }
}
