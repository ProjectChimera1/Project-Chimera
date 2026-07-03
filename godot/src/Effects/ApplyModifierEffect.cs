#nullable enable
using System;
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Installs a first-class <see cref="Modifier"/> onto the target. The TYPE is defined in Story 2.1 so the
    /// closed vocabulary is complete (AC1's first-class Modifier); its EXECUTION — resolving against the
    /// <see cref="ModifierStore"/> (register / stack / refresh / ignore / schedule the period / recompute effective
    /// stats / expire) — lands in Story 2.2b. <see cref="Apply"/> installs the modifier through
    /// <c>ctx.ModifierStore</c>; reaching it with a NULL store fails CLOSED (a modifier graph needs a store in
    /// context). The 2.3 validator keeps modifier graphs out of any run that has no store wired.
    /// </summary>
    public sealed class ApplyModifierEffect : LeafEffect
    {
        /// <summary>The modifier descriptor to install into the store.</summary>
        public readonly Modifier Modifier;

        /// <summary>Construct an apply-modifier leaf. <paramref name="requireTag"/> (Story 2.11, default None) gates the
        /// single-target install on the target's tag; omit for byte-identical 2.2b behaviour.</summary>
        public ApplyModifierEffect(Modifier modifier, UnitTag requireTag = UnitTag.None) : base(requireTag) => Modifier = modifier;

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            // Same path the executor's explicit ApplyModifierEffect case routes to (kept for dispatch clarity).
            if (ctx.ModifierStore is null)
                throw new NotSupportedException(
                    "ApplyModifierEffect requires a ModifierStore in the EffectContext (Story 2.2b).");
            ctx.ModifierStore.Apply(ctx.PrimaryTargetId, Modifier, ctx.CasterId, ctx.CasterFaction);
        }
    }
}
