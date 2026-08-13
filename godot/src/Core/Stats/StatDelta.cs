#nullable enable

namespace ProjectChimera.Core.Stats
{
    /// <summary>
    /// Story 15-24a — one sparse stat contribution: a <see cref="StatId"/> plus a <see cref="Fixed"/> delta.
    /// The element type of <c>Modifier.StatDeltas</c> (and of every other sparse stat vector in the pipeline).
    ///
    /// <para>Public readonly FIELDS, deliberately — the vector folds into the canonical content hash
    /// (<c>CanonicalFold.MixModifier</c>) and <c>EffectFoldCompletenessTests</c> pins the fold-shaped
    /// vocabulary to field shape so no state can hide behind a property.</para>
    ///
    /// <para>The MEANING of <see cref="Delta"/> depends on the stat's <see cref="StatAggregation"/>: stat
    /// units for a Flat stat, a fraction for a Percent stat (0.15 = +15%). Canonical vectors (see
    /// <see cref="StatVocabulary.Canonicalize"/>) are sorted ascending <see cref="Stat"/> with no zero
    /// entries and no duplicate ids — the one representation saves, folds, and equality all agree on.</para>
    /// </summary>
    public readonly struct StatDelta
    {
        /// <summary>Which stat this contribution targets.</summary>
        public readonly StatId Stat;

        /// <summary>The signed contribution (stat units for Flat, a fraction for Percent aggregation).</summary>
        public readonly Fixed Delta;

        public StatDelta(StatId stat, Fixed delta)
        {
            Stat = stat;
            Delta = delta;
        }
    }
}
