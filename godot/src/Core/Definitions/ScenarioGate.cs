#nullable enable
using System;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Pure (Godot-free) shadow / fail-closed policy for the scenario validation gate (Story 1.7, D7).
    ///
    /// Splits the DECISION from the side effects: this type reads the fail-closed toggle and decides whether an
    /// apply should proceed given a validation outcome. The actual located-error logging and the apply happen at
    /// the presentation call site (<c>MainScene.ValidateBeforeApply</c>), which is the only layer allowed to
    /// touch Godot. Keeping the decision here — in src/Core/Definitions, compiled into the Godot-free
    /// Tier-1 test assembly — makes the policy unit-testable (AC4); src/UI is NOT in the Tier-1 source globs, so
    /// a helper placed there could not be tested.
    ///
    /// Default = shadow mode: the validator only LOGS on master and the model is still applied, so master never
    /// breaks. The fail-closed flip (refuse to apply on any failure) is gated behind an env toggle, intended to
    /// be turned on only on a release branch after a corpus run proves every shipped scenario validates.
    /// </summary>
    public static class ScenarioGate
    {
        /// <summary>Environment toggle. Unset or any value other than "1" = shadow mode; "1" = fail-closed.</summary>
        public const string FailClosedEnvVar = "CHIMERA_VALIDATE_FAILCLOSED";

        /// <summary>
        /// Reads the fail-closed toggle from the environment (default off). Pure .NET (no Godot), so it is
        /// callable from the Godot-free layer and from tests. Mirrors the codebase's only branch-toggle idiom
        /// (CHIMERA_GOLDEN_RECORD).
        /// </summary>
        public static bool IsFailClosed() =>
            Environment.GetEnvironmentVariable(FailClosedEnvVar) == "1";

        /// <summary>
        /// The shadow / fail-closed decision. Shadow mode (<paramref name="failClosed"/> == false): ALWAYS
        /// proceed — the validator only logs, never halts. Fail-closed (<paramref name="failClosed"/> == true):
        /// proceed only if the model is valid. Pure function: no logging, no side effects.
        /// </summary>
        /// <param name="ok">Whether validation succeeded.</param>
        /// <param name="failClosed">Whether the fail-closed toggle is on.</param>
        /// <returns>True if the apply should proceed; false to halt (the fail-closed + invalid case only).</returns>
        public static bool ShouldProceed(bool ok, bool failClosed) => ok || !failClosed;
    }
}
