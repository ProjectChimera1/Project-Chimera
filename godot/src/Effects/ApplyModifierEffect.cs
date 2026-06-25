#nullable enable
using System;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Installs a first-class <see cref="Modifier"/> onto the target. The TYPE is defined in Story 2.1 so the
    /// closed vocabulary is complete (AC1's first-class Modifier), but its EXECUTION — resolving against the
    /// ModifierStore (register / stack / tick the period / recompute effective stats / expire) — lands in Story
    /// 2.2b. Until then the executor must not mutate a (nonexistent) store: reaching it during a run is a loud,
    /// fail-closed <see cref="NotSupportedException"/> (a premature wire-up is caught, not silently no-op'd).
    /// Story 2.3's validator keeps modifier graphs off the executor until 2.2b ships.
    /// </summary>
    public sealed class ApplyModifierEffect : LeafEffect
    {
        /// <summary>The modifier descriptor to install (resolution deferred to 2.2b).</summary>
        public readonly Modifier Modifier;

        /// <summary>Construct an apply-modifier leaf.</summary>
        public ApplyModifierEffect(Modifier modifier) => Modifier = modifier;

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx) =>
            throw new NotSupportedException(
                "ApplyModifier execution lands in Story 2.2b (ModifierStore not yet built). " +
                "The 2.3 validator keeps modifier graphs off the executor until then.");
    }
}
