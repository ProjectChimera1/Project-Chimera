#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Pure result of an <see cref="AbilityValidator"/> pass (Story 2.3, AR-39) — no logging, no throw. A parallel
    /// of <see cref="ValidationResult"/> (which is hardcoded to <c>Validated&lt;ScenarioData&gt;</c>); abilities get
    /// their own type rather than retyping/generalizing that one (Decision #2 = A — zero blast radius on the
    /// ScenarioData gate). When <see cref="Ok"/> is false, <see cref="Error"/> carries a single LOCATED message
    /// (<c>"ability '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c>) and <see cref="Value"/> is <c>default</c> — no
    /// runnable <c>Validated&lt;AbilityDefinition&gt;</c> escapes a failed validation (AC2).
    /// </summary>
    public readonly struct AbilityValidationResult
    {
        /// <summary>True when the ability passed every check.</summary>
        public bool Ok { get; }

        /// <summary>Located error (id + field path + reason) when <see cref="Ok"/> is false; null when Ok.</summary>
        public string? Error { get; }

        /// <summary>
        /// The minted proof-of-validation value. Meaningful ONLY when <see cref="Ok"/> is true; on a failed result
        /// it is <c>default</c> (an unusable, empty <c>Validated&lt;AbilityDefinition&gt;</c>) so a rejected
        /// definition can never be cast.
        /// </summary>
        public Validated<AbilityDefinition> Value { get; }

        private AbilityValidationResult(bool ok, string? error, Validated<AbilityDefinition> value)
        {
            Ok = ok;
            Error = error;
            Value = value;
        }

        /// <summary>Successful validation carrying the minted proof-of-validation value.</summary>
        public static AbilityValidationResult Pass(Validated<AbilityDefinition> value) => new(true, null, value);

        /// <summary>Failed validation carrying ONLY a located error (no usable token).</summary>
        public static AbilityValidationResult Fail(string located) => new(false, located, default);
    }
}
