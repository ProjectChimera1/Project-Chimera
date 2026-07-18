#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 7.14 — the pure, Godot-free map from a win condition/preset to the single default-objective TITLE text
    /// used when a scenario authors no objectives of its own. Deterministic-from-preset (the preset params already
    /// fold into <see cref="CanonicalModelHash"/> via <c>MixWinConditionSpec</c>), so the sim (default-objective
    /// resolution) and presentation (briefing/quest-log text) agree by construction. Home is
    /// <c>src/Core/Definitions/</c>, NOT <c>WinConditionPresets.cs</c> (that file is the DSL round-trip witness, not a
    /// runtime dependency).
    ///
    /// <para>This is presentation TEXT only — it never enters the tick, never folds into any checksum, and returns a
    /// string only. A preset (when present) takes precedence over the bare built-in enum; an unrecognized value falls
    /// back to a generic "Achieve victory" so the resolver never yields a blank/zero objective.</para>
    /// </summary>
    public static class WinObjectiveText
    {
        /// <summary>The generic last-resort default when neither the preset nor the built-in enum is recognized.</summary>
        public const string GenericVictory = "Achieve victory";

        /// <summary>
        /// The default-objective title for a scenario's win condition. A non-<see cref="WinPresetKind.None"/> preset
        /// wins over the built-in <paramref name="win"/> enum; otherwise the built-in enum maps to its line; an
        /// unknown value returns <see cref="GenericVictory"/>.
        /// </summary>
        public static string For(WinCondition win, WinConditionSpec? spec)
        {
            if (spec != null && spec.Preset != WinPresetKind.None)
            {
                return spec.Preset switch
                {
                    WinPresetKind.KingOfTheHill       => "Hold the contested region",
                    WinPresetKind.TimedSurvival       => "Survive until the timer expires",
                    WinPresetKind.Assassination       => "Eliminate the enemy leader",
                    WinPresetKind.LandmarkDestruction => "Destroy the enemy landmark",
                    _                                 => GenericVictory,
                };
            }

            return win switch
            {
                WinCondition.DestroyAllBuildings => "Destroy all enemy buildings",
                WinCondition.EliminateAllUnits   => "Eliminate all enemy units",
                _                                => GenericVictory,
            };
        }
    }
}
