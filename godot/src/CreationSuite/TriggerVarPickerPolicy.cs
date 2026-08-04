#nullable enable
using ProjectChimera.Dsl; // DslValueType, VarScope

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// DW-345 — the Godot-free eligibility policy behind <c>TriggerEditorPanel.RefreshVarPickers</c> (the
    /// EditorHistory/StartSlotMath extraction pattern: pure decision core out of the Godot panel so it is
    /// Tier-1 testable). One function per picker so the panel cannot re-derive the filter inline and drift.
    ///
    /// The DW-345 defect this closes: the LEGACY (flat/literal) <c>set_variable</c> path filtered by TYPE only,
    /// so a PerPlayer-scoped Int variable was offered — but the flat <c>TriggerAction.Faction</c> defaults to 0
    /// and the manual form exposes no player-slot picker, so the authored trigger ALWAYS wrote player slot 0
    /// (silently the wrong player for anyone else). Story 7.4's second-pass review closed the same gap on the
    /// NEW (widened/expression) path only; this policy applies the same exclusion to both paths. PerPlayer
    /// targets are authored via Raw IR, which carries the slot — the same hatch the widened path documents.
    /// A future player-slot picker on the manual row (the 7.10 T3 editor direction) would relax this here.
    /// </summary>
    public static class TriggerVarPickerPolicy
    {
        /// <summary>
        /// True when a declared variable belongs in the manual form's <c>set_variable</c> TARGET picker.
        /// <paramref name="widened"/> = a value expression is present (the Story 7.4 typed path): Int/Fixed/Bool
        /// targets. Otherwise the 7.3 literal path: Int targets only. BOTH paths exclude
        /// <see cref="VarScope.PerPlayer"/> — the form has no player-slot picker and both persisted shapes write
        /// slot 0 (flat <c>TriggerAction.Faction</c> default / <c>PersistManualExpression</c>), so offering a
        /// PerPlayer variable would silently assign the wrong player (DW-345). TriggerLocal stays allowed:
        /// <c>set_variable</c> is the write-scratch producer.
        /// </summary>
        public static bool SetVariableTargetEligible(DslValueType type, VarScope scope, bool widened)
        {
            if (scope == VarScope.PerPlayer) return false; // no slot picker on the form — Raw IR carries the slot
            return widened
                ? type is DslValueType.Int or DslValueType.Fixed or DslValueType.Bool
                : type == DslValueType.Int;
        }

        /// <summary>
        /// True when a declared variable belongs in the manual form's <c>variable_comparison</c> picker:
        /// Int-typed (the 7.3 condition reads Int only) and NOT TriggerLocal — a condition reads BEFORE the
        /// trigger-local scope is entered (it would read 0), so TriggerLocal is write-scratch only.
        /// </summary>
        public static bool ConditionVariableEligible(DslValueType type, VarScope scope) =>
            type == DslValueType.Int && scope != VarScope.TriggerLocal;
    }
}
