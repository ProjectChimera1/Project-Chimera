#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 15.11 (DW-286) — the OPTIONAL allegiance hint for a <see cref="AbilityTargeting.TargetUnit"/> ability,
    /// authored as a string on <see cref="AbilityDefinition.TargetAffinity"/> and resolved via
    /// <see cref="AbilityDefinition.ParsedTargetAffinity"/>. It steers the CLICK-PICKER (which unit the next click
    /// selects), NOT a new targeting mode — a heal-other is still a <c>TargetUnit</c> ability, it just picks a
    /// friendly rather than the historical enemy-only default.
    ///
    /// <para>It applies to <c>TargetUnit</c> ONLY. On <c>GroundPoint</c> the pick is a ground location (no unit is
    /// selected) and allegiance is governed by the effect's <c>SearchArea</c> Filter, so an affinity there is IGNORED;
    /// on <c>Self</c>/<c>None</c> no target is picked at all. <see cref="AbilityValidator"/> warns (non-fatal) when the
    /// hint is set on any non-<c>TargetUnit</c> ability.</para>
    ///
    /// <para>ABSENT (null) preserves today exactly: the picker stays enemy-only, so every shipped ability is
    /// unchanged and its <c>ContentHash</c>/<c>CanonicalModelHash</c> does not move. An UNKNOWN string resolves to
    /// null and is rejected by <see cref="AbilityValidator"/> (never silently defaulted — the
    /// <see cref="AbilityDefinition.ParsedTargeting"/> fail-closed posture).</para>
    /// </summary>
    public enum TargetAffinity : byte
    {
        /// <summary>The click-picker selects an ENEMY unit (the historical default, and what an absent hint means).</summary>
        Enemy = 0,

        /// <summary>The click-picker selects an ALLY: the caster's own faction, EXCLUDING the caster itself.</summary>
        Ally = 1,

        /// <summary>The click-picker selects ANY unit under the cursor (own, ally, enemy or neutral).</summary>
        Any = 2,
    }
}
