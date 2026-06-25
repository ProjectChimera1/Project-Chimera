#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Restores a flat <see cref="Fixed"/> amount of Health to the target, clamped at <c>MaxHealth</c> (no
    /// overheal). Armor-independent (healing is not combat damage). Like every leaf, it guards
    /// <see cref="EntityWorld.IsAlive"/> at entry. <see cref="Amount"/> is expected to be non-negative; a
    /// negative amount is still safe (it lowers Health, never below the current value's natural floor of 0 only
    /// if authored that way) but the intended primitive for costs is <see cref="DirectHpDeltaEffect"/>.
    /// </summary>
    public sealed class HealEffect : LeafEffect
    {
        /// <summary>Flat heal amount (expected &gt;= 0).</summary>
        public readonly Fixed Amount;

        /// <summary>Construct a heal leaf.</summary>
        public HealEffect(Fixed amount) => Amount = amount;

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            EntityWorld world = ctx.World;
            int t = ctx.PrimaryTargetId;
            if (!world.IsAlive(t)) return;

            // Clamp at MaxHealth — no overheal.
            world.Health[t] = Fixed.Min(world.Health[t] + Amount, world.MaxHealth[t]);
        }
    }
}
