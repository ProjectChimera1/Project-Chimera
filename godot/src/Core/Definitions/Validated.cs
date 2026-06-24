#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Proof that a value passed <see cref="ScenarioValidator"/> — the single pre-tick validation gate
    /// (Story 1.7, AR-39). A <see cref="Validated{T}"/> requires a <see cref="ScenarioValidator.Proof"/> token
    /// to construct, and Proof has an <c>internal</c> constructor — so nothing OUTSIDE the sim assembly can mint
    /// a Validated. Within the assembly, a source scan (ValidatedSoleMinterTest) fails the build if any
    /// <c>new Validated&lt;</c> appears outside <c>ScenarioValidator.cs</c>, so the validator is the sole minter
    /// in practice. (D1 originally proposed a PRIVATE Proof ctor relying on "an enclosing type can call its
    /// nested type's private ctor" — that is false in C#: it raises CS0122. The internal-ctor + source-scan
    /// combination is the equivalent guarantee; see ScenarioValidator.Proof.)
    ///
    /// The type is generic so the same machinery carries forward: Story 1.8b makes the scenario applier consume
    /// only <c>Validated&lt;ScenarioData&gt;</c>, and Epic 2 (Story 2.3) reuses it for
    /// <c>Validated&lt;AbilityDefinition&gt;</c>. 1.7 builds the gate + the type; it does NOT yet type-gate the
    /// applier (that is 1.8b — see D8).
    /// </summary>
    public readonly struct Validated<T>
    {
        /// <summary>The validated value. It is trustworthy precisely because a Proof was required to wrap it.</summary>
        public T Value { get; }

        /// <summary>
        /// Mint a validated value. Callable only from <see cref="ScenarioValidator"/> within the sim assembly,
        /// because it requires a <see cref="ScenarioValidator.Proof"/> whose constructor is <c>internal</c> (a
        /// private nested ctor would be a C# CS0122 error — see the class summary above; <c>internal</c> + the
        /// ValidatedSoleMinterTest source scan is the equivalent guarantee).
        /// </summary>
        public Validated(T value, ScenarioValidator.Proof proof) { Value = value; }
    }

    /// <summary>
    /// Pure result of a validation pass — no logging, no throw (Story 1.7, D3). The caller (presentation)
    /// decides shadow vs fail-closed policy. When <see cref="Ok"/> is false, <see cref="Error"/> carries a
    /// single LOCATED message (field path + offending value); <see cref="Value"/> is meaningful only when
    /// <see cref="Ok"/> is true.
    /// </summary>
    public readonly struct ValidationResult
    {
        /// <summary>True when the model passed every check.</summary>
        public bool Ok { get; }

        /// <summary>Located error, e.g. "scenario.units[3].slot=5 references no declared player_slot". Null when Ok.</summary>
        public string? Error { get; }

        /// <summary>The minted proof-of-validation value. Meaningful only when <see cref="Ok"/> is true.</summary>
        public Validated<ScenarioData> Value { get; }

        private ValidationResult(bool ok, string? error, Validated<ScenarioData> value)
        {
            Ok = ok;
            Error = error;
            Value = value;
        }

        /// <summary>Successful validation carrying the minted proof-of-validation value.</summary>
        public static ValidationResult Pass(Validated<ScenarioData> value) => new(true, null, value);

        /// <summary>Failed validation carrying a single located error message (field path + offending value).</summary>
        public static ValidationResult Fail(string located) => new(false, located, default);
    }
}
