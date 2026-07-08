#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Pure result of an <see cref="ItemDefinitionValidator"/> pass (Story 3.15, AR-39) — no logging, no throw. A
    /// parallel of <see cref="AbilityValidationResult"/> (Decision #2 = A — items get their own result type rather
    /// than retyping the ScenarioData/ability gates, for zero blast radius). When <see cref="Ok"/> is false,
    /// <see cref="Error"/> carries a single LOCATED message (<c>"item '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c>)
    /// and <see cref="Value"/> is <c>default</c> — no runnable <c>Validated&lt;ItemDefinition&gt;</c> escapes a failed
    /// validation.
    /// </summary>
    public readonly struct ItemValidationResult
    {
        /// <summary>True when the item passed every check.</summary>
        public bool Ok { get; }

        /// <summary>Located error (id + field path + reason) when <see cref="Ok"/> is false; null when Ok.</summary>
        public string? Error { get; }

        /// <summary>The minted proof-of-validation value. Meaningful ONLY when <see cref="Ok"/> is true; on a failed
        /// result it is <c>default</c> so a rejected definition can never be placed or used.</summary>
        public Validated<ItemDefinition> Value { get; }

        /// <summary>Story 3.16 EDITOR surface: EVERY located field error keyed by JSON <c>FieldPath</c> (mirrors
        /// <c>UnitValidationResult.Errors</c>), for the per-field badges. Empty on the sim <see cref="Pass"/>/<see cref="Fail"/>
        /// path (which mints/first-fails via <see cref="Error"/>); populated only by <see cref="Fields"/>.</summary>
        public IReadOnlyList<(string FieldPath, string Message)> Errors { get; }

        private static readonly IReadOnlyList<(string, string)> _noErrors = System.Array.Empty<(string, string)>();

        private ItemValidationResult(bool ok, string? error, Validated<ItemDefinition> value,
                                     IReadOnlyList<(string, string)> errors)
        {
            Ok = ok;
            Error = error;
            Value = value;
            Errors = errors;
        }

        /// <summary>Successful validation carrying the minted proof-of-validation value (sim gate).</summary>
        public static ItemValidationResult Pass(Validated<ItemDefinition> value) => new(true, null, value, _noErrors);

        /// <summary>Failed validation carrying ONLY a located error (no usable token) — the sim first-fail gate.</summary>
        public static ItemValidationResult Fail(string located) => new(false, located, default, _noErrors);

        /// <summary>Story 3.16 EDITOR result: carries EVERY located field error (keyed). <see cref="Ok"/> is true iff the
        /// list is empty; it mints NO token (the editor gate never places content — the sim <see cref="Pass"/> does).</summary>
        public static ItemValidationResult Fields(IReadOnlyList<(string FieldPath, string Message)> errors) =>
            new(errors.Count == 0, errors.Count == 0 ? null : errors[0].Message, default, errors);
    }
}
