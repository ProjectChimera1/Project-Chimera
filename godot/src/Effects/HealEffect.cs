#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Restores a flat <see cref="Fixed"/> amount of Health to the target, clamped into <c>[0, MaxHealth]</c>
    /// (no overheal). Armor-independent (healing is not combat damage). Like every leaf, it guards
    /// <see cref="EntityWorld.IsAlive"/> at entry. <see cref="Amount"/> is expected to be non-negative; a
    /// (mis-authored) negative amount is floored at 0 — it never underflows to negative HP — but the intended
    /// primitive for an HP cost is <see cref="DirectHpDeltaEffect"/>.
    /// </summary>
    public sealed class HealEffect : LeafEffect
    {
        /// <summary>Flat heal amount (expected &gt;= 0).</summary>
        public readonly Fixed Amount;

        /// <summary>Construct a heal leaf. <paramref name="requireTag"/> (Story 2.11, default None) gates the apply on
        /// the target's tag — e.g. "heal only Organic" is <c>heal{require_tag:Organic}</c>; omit for byte-identical 2.1 behaviour.</summary>
        public HealEffect(Fixed amount, UnitTag requireTag = UnitTag.None) : base(requireTag) => Amount = amount;

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            EntityWorld world = ctx.World;
            int t = ctx.PrimaryTargetId;
            if (!world.IsAlive(t)) return;

            // Clamp into the valid HP band [0, MaxHealth] — no overheal, and a (mis-authored) negative amount
            // floors at 0 instead of underflowing to negative HP (matches DirectHpDeltaEffect's both-ends clamp).
            world.Health[t] = Fixed.Clamp(world.Health[t] + Amount, Fixed.Zero, world.EffectiveMaxHealth[t]);
        }
    }
}
