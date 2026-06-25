#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// The Equal-Exchange-shaped self-cost primitive (AC4): a FLAT, armor-INDEPENDENT change to a target's
    /// Health pool, clamped to <c>[0, MaxHealth]</c>. It deliberately does NOT route through
    /// <c>DamageResolver</c>/<c>DamageTable</c> — no damage matrix, no armor scaling — so a designer can author
    /// an exact HP cost or restore (e.g. "spend 10 HP to power an ability"). Use <see cref="DamageEffect"/> when
    /// armor-scaled combat damage and the death sequence are wanted.
    ///
    /// By design this leaf is a pure pool adjustment: reaching 0 HP clamps but does NOT fire the death sequence
    /// (UnitKilled event / RecordKill / Destroy) — that side-effecting path belongs to <see cref="DamageEffect"/>
    /// via <c>DamageResolver</c>. Keeping the self-cost primitive side-effect-free is intentional for 2.1.
    /// </summary>
    public sealed class DirectHpDeltaEffect : LeafEffect
    {
        /// <summary>Flat HP change (negative = cost, positive = restore). Armor-independent.</summary>
        public readonly Fixed Delta;

        /// <summary>Construct a flat HP-delta leaf.</summary>
        public DirectHpDeltaEffect(Fixed delta) => Delta = delta;

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            EntityWorld world = ctx.World;
            int t = ctx.PrimaryTargetId;
            if (!world.IsAlive(t)) return; // dead/recycled target — no-op (future callers hit these)

            // Flat, armor-independent, NEVER through the damage matrix. Clamp into the valid HP band.
            world.Health[t] = Fixed.Clamp(world.Health[t] + Delta, Fixed.Zero, world.EffectiveMaxHealth[t]);
        }
    }
}
