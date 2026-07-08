#nullable enable
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

        private ItemValidationResult(bool ok, string? error, Validated<ItemDefinition> value)
        {
            Ok = ok;
            Error = error;
            Value = value;
        }

        /// <summary>Successful validation carrying the minted proof-of-validation value.</summary>
        public static ItemValidationResult Pass(Validated<ItemDefinition> value) => new(true, null, value);

        /// <summary>Failed validation carrying ONLY a located error (no usable token).</summary>
        public static ItemValidationResult Fail(string located) => new(false, located, default);
    }
}
