#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The CLOSED set of how an ability ACTIVATES (Story 2.6, FR-9). Authored as a string on
    /// <see cref="AbilityDefinition.Activation"/> (default <c>"active"</c>) and resolved to this enum via
    /// <see cref="AbilityDefinition.ParsedActivation"/>; an unknown string resolves to <c>null</c> and is rejected
    /// by <see cref="AbilityValidator"/> (never silently defaulted — same fail-closed discipline as
    /// <see cref="AbilityDefinition.ParsedTargeting"/>).
    ///
    /// <para>This is the "no scripting escape hatch" surface for passives: a passive is just an
    /// <see cref="AbilityDefinition"/> whose activation is one of the three passive modes, composed from the SAME
    /// closed effect vocabulary as active abilities. There is no free-text trigger logic — ever.</para>
    ///
    /// <list type="bullet">
    ///   <item><see cref="Active"/> — today's player-cast ability (the default; every pre-2.6 ability is Active).</item>
    ///   <item><see cref="Aura"/> — a while-alive aura: every tick the owner re-grants a short modifier to units
    ///         found via <c>SearchArea</c> (the driver-level per-tick pass; expiry-by-non-refresh).</item>
    ///   <item><see cref="OnHit"/> — a rider that runs when THIS unit's attack lands (melee-first, Story 2.6).</item>
    ///   <item><see cref="WhileAlive"/> — a permanent/periodic self-passive installed at spawn
    ///         (a permanent <c>ApplyModifier</c> or a <c>Persistent</c> DoT/HoT).</item>
    /// </list>
    /// </summary>
    public enum PassiveActivation : byte
    {
        /// <summary>Player-cast ability (default). Runs through the 2.4 cast spine; unchanged by 2.6.</summary>
        Active = 0,

        /// <summary>While-alive aura — re-applied each tick to <c>SearchArea</c> matches (Story 2.6 AC1).</summary>
        Aura = 1,

        /// <summary>On-hit rider — fires when this unit's attack lands, and not otherwise (Story 2.6 AC2).</summary>
        OnHit = 2,

        /// <summary>Permanent/periodic self-passive installed at spawn (Story 2.6 AC3).</summary>
        WhileAlive = 3,
    }
}
