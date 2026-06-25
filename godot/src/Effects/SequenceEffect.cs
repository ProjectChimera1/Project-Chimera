#nullable enable
namespace ProjectChimera.Effects
{
    /// <summary>
    /// Composition node: runs an ordered list of children in authored order. The executor reverse-pushes the
    /// children onto its LIFO work-stack so they pop (and thus apply) in <see cref="Children"/> order. Bounded
    /// by <c>EffectCaps.MaxSequenceChildren</c> (enforced at load by <c>EffectBounds.Validate</c>).
    /// </summary>
    public sealed class SequenceEffect : CompositionEffect
    {
        /// <summary>The ordered children. Length must be &lt;= <c>EffectCaps.MaxSequenceChildren</c>.</summary>
        public readonly EffectNode[] Children;

        /// <summary>Construct a sequence over <paramref name="children"/> (executed in order).</summary>
        public SequenceEffect(params EffectNode[] children) => Children = children ?? System.Array.Empty<EffectNode>();
    }
}
