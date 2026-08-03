#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Pure result of an <see cref="AbilityValidator"/> pass (Story 2.3, AR-39) — no logging, no throw. A parallel
    /// of <see cref="ValidationResult"/> (which is hardcoded to <c>Validated&lt;ScenarioData&gt;</c>); abilities get
    /// their own type rather than retyping/generalizing that one (Decision #2 = A — zero blast radius on the
    /// ScenarioData gate). When <see cref="Ok"/> is false, <see cref="Error"/> carries a single LOCATED message
    /// (<c>"ability '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c>) and <see cref="Value"/> is <c>default</c> — no
    /// runnable <c>Validated&lt;AbilityDefinition&gt;</c> escapes a failed validation (AC2).
    ///
    /// <para><b>DW-278 — the non-fatal WARNING channel.</b> Errors are the only thing that can stop content; but a
    /// whole class of ability is <i>authorable, valid, and inert</i> (a <c>duration_ticks: 0</c> modifier the author
    /// believes is instantaneous, a <c>period_effect</c> with no <c>period_ticks</c>, a phase-less Persistent, a
    /// stacked periodic whose pulse does not scale). Rejecting those would break existing content and overreach the
    /// gate's fail-closed remit, so they surface as located <see cref="Warnings"/> instead: <see cref="Ok"/> is
    /// UNAFFECTED and the token is still minted. Warnings are advisory diagnostics for an authoring surface — never a
    /// runtime input, never folded into any checksum.</para>
    /// </summary>
    public readonly struct AbilityValidationResult
    {
        /// <summary>True when the ability passed every check. Warnings do NOT affect this.</summary>
        public bool Ok { get; }

        /// <summary>Located error (id + field path + reason) when <see cref="Ok"/> is false; null when Ok.</summary>
        public string? Error { get; }

        /// <summary>
        /// The minted proof-of-validation value. Meaningful ONLY when <see cref="Ok"/> is true; on a failed result
        /// it is <c>default</c> (an unusable, empty <c>Validated&lt;AbilityDefinition&gt;</c>) so a rejected
        /// definition can never be cast.
        /// </summary>
        public Validated<AbilityDefinition> Value { get; }

        // Backing field rather than an auto-property so `default(AbilityValidationResult)` still exposes an EMPTY
        // list from `Warnings` instead of null (the struct's zero value has no allocated list).
        private readonly IReadOnlyList<(string FieldPath, string Message)>? _warnings;

        /// <summary>
        /// DW-278: located NON-FATAL diagnostics — <c>(FieldPath, Message)</c> pairs whose message carries the same
        /// <c>"ability '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c> shape as <see cref="Error"/>. Empty (never null) on
        /// every result that has none, including a failed one: a REJECTED ability reports its error only, because a
        /// graph that failed the gate was not walked to completion and its warnings would be arbitrary. Ordering is
        /// deterministic for a given graph (the validator's single iterative walk).
        /// </summary>
        public IReadOnlyList<(string FieldPath, string Message)> Warnings => _warnings ?? NoWarnings;

        private static readonly IReadOnlyList<(string FieldPath, string Message)> NoWarnings =
            System.Array.Empty<(string FieldPath, string Message)>();

        private AbilityValidationResult(bool ok, string? error, Validated<AbilityDefinition> value,
                                        IReadOnlyList<(string FieldPath, string Message)>? warnings)
        {
            Ok = ok;
            Error = error;
            Value = value;
            _warnings = warnings;
        }

        /// <summary>Successful validation carrying the minted proof-of-validation value and no warnings.</summary>
        public static AbilityValidationResult Pass(Validated<AbilityDefinition> value) => new(true, null, value, null);

        /// <summary>
        /// DW-278: successful validation carrying the minted value PLUS the non-fatal warnings the walk collected.
        /// <see cref="Ok"/> is true regardless of how many warnings there are.
        /// </summary>
        public static AbilityValidationResult Pass(Validated<AbilityDefinition> value,
                                                   IReadOnlyList<(string FieldPath, string Message)> warnings) =>
            new(true, null, value, warnings is { Count: > 0 } ? warnings : null);

        /// <summary>Failed validation carrying ONLY a located error (no usable token, no warnings).</summary>
        public static AbilityValidationResult Fail(string located) => new(false, located, default, null);
    }
}
