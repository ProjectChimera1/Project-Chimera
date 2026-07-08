#nullable enable
namespace ProjectChimera.Combat
{
    /// <summary>
    /// How a unit's attack lands (Story 3.12, FR — authorable attack delivery). An EXPLICIT, authorable per-unit
    /// property decoupled from range: <see cref="Hitscan"/> deals instant damage with no projectile regardless of
    /// AttackRange; <see cref="Projectile"/> always spawns a tracking projectile regardless of AttackRange. This
    /// replaces the old implicit inference (<c>AttackRange &gt; MELEE_THRESHOLD (2.5)</c>) so a creator can author a
    /// long-range instant sniper or a slow short-range lobber.
    ///
    /// Lives in the sim <see cref="ProjectChimera.Combat"/> namespace (no <c>using Godot;</c>), mirroring
    /// <see cref="DamageType"/>/<see cref="ArmorType"/>. Named <c>AttackDelivery</c> (not <c>Delivery</c>) to avoid
    /// clashing with the <c>UnitDefinition.Delivery</c> string property — the same property/enum disambiguation the
    /// codebase already uses for <see cref="DamageType"/>. Folded into <c>SimChecksum</c> as <c>(int)</c> (v10).
    /// </summary>
    public enum AttackDelivery
    {
        /// <summary>Instant damage, no projectile — regardless of AttackRange.</summary>
        Hitscan = 0,
        /// <summary>A tracking projectile is spawned that resolves damage on impact — regardless of AttackRange.</summary>
        Projectile = 1,
    }
}
