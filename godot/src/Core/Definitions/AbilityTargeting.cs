#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The CLOSED set of how an ability acquires its target (Story 2.3, FR-12). Authored as a string on
    /// <see cref="AbilityDefinition.Targeting"/> and resolved to this enum via
    /// <see cref="AbilityDefinition.ParsedTargeting"/>; an unknown string resolves to <c>null</c> and is rejected
    /// by <see cref="AbilityValidator"/> (never silently defaulted). Story 2.4 drives the runtime cast flow
    /// (instant-cast vs. target-select vs. ground-cast) off this enum.
    /// </summary>
    public enum AbilityTargeting : byte
    {
        /// <summary>No target — a passive/aura-style trigger that needs no acquisition (reserved for 2.6 passives).</summary>
        None = 0,

        /// <summary>Self-cast: the caster is the primary target (the default for most heals/buffs).</summary>
        Self = 1,

        /// <summary>Single-target: the player selects one unit as the primary target.</summary>
        TargetUnit = 2,

        /// <summary>Ground-target: the player picks a point; the primary target is the impact location.</summary>
        GroundPoint = 3,
    }
}
