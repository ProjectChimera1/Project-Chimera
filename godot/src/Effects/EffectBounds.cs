#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Effects
{
    /// <summary>The outcome of <see cref="EffectBounds.Validate"/>: valid, or invalid with a LOCATED reason.</summary>
    public readonly struct EffectBoundsResult
    {
        /// <summary>True when the graph is within all structural caps.</summary>
        public readonly bool IsValid;

        /// <summary>A located error (which node type, which limit) when <see cref="IsValid"/> is false; else null.</summary>
        public readonly string? Error;

        private EffectBoundsResult(bool isValid, string? error)
        {
            IsValid = isValid;
            Error = error;
        }

        /// <summary>A valid result.</summary>
        public static readonly EffectBoundsResult Valid = new(true, null);

        /// <summary>An invalid result carrying a located <paramref name="error"/>.</summary>
        public static EffectBoundsResult Invalid(string error) => new(false, error);
    }

    /// <summary>
    /// Load-time bound check for an effect graph (AC2). Rejects a graph whose composition nesting exceeds
    /// <c>EffectCaps.MaxEffectDepth</c> or whose any <c>SequenceEffect</c> holds more than
    /// <c>EffectCaps.MaxSequenceChildren</c> children, with a LOCATED error (offending node type + the limit).
    ///
    /// Depth semantics (PINNED BY TEST — do not infer from the constant): "depth" is the count of composition
    /// nodes on a root→leaf path. The root frame is depth 0; a composition node at frame-depth d is the (d+1)th
    /// composition on its path. A graph of N nested composition nodes is a "depth-N" graph — depth 8 is accepted,
    /// depth 9 is rejected. Leaves carry no depth contribution.
    ///
    /// Iterative (explicit stack), so validating a maliciously deep graph cannot itself stack-overflow — it
    /// rejects at the first over-cap node and never descends past it.
    /// </summary>
    public static class EffectBounds
    {
        /// <summary>
        /// Validate <paramref name="root"/>. A null graph is trivially valid (the executor no-ops on null).
        /// </summary>
        public static EffectBoundsResult Validate(EffectNode? root)
        {
            if (root is null)
                return EffectBoundsResult.Valid;

            // (node, frameDepth) — frameDepth = number of composition ancestors (root = 0).
            var stack = new Stack<(EffectNode Node, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                (EffectNode node, int depth) = stack.Pop();

                switch (node)
                {
                    case SequenceEffect seq:
                        if (depth >= EffectCaps.MaxEffectDepth)
                            return EffectBoundsResult.Invalid(
                                $"SequenceEffect at composition depth {depth} exceeds MaxEffectDepth={EffectCaps.MaxEffectDepth}.");
                        if (seq.Children.Length > EffectCaps.MaxSequenceChildren)
                            return EffectBoundsResult.Invalid(
                                $"SequenceEffect has {seq.Children.Length} children, exceeds MaxSequenceChildren={EffectCaps.MaxSequenceChildren}.");
                        for (int k = 0; k < seq.Children.Length; k++)
                            if (seq.Children[k] is not null)
                                stack.Push((seq.Children[k], depth + 1));
                        break;

                    case SearchAreaEffect search:
                        if (depth >= EffectCaps.MaxEffectDepth)
                            return EffectBoundsResult.Invalid(
                                $"SearchAreaEffect at composition depth {depth} exceeds MaxEffectDepth={EffectCaps.MaxEffectDepth}.");
                        if (search.Child is not null)
                            stack.Push((search.Child, depth + 1));
                        break;

                    case PersistentEffect persistent:
                        if (depth >= EffectCaps.MaxEffectDepth)
                            return EffectBoundsResult.Invalid(
                                $"PersistentEffect at composition depth {depth} exceeds MaxEffectDepth={EffectCaps.MaxEffectDepth}.");
                        if (persistent.InitialEffect is not null) stack.Push((persistent.InitialEffect, depth + 1));
                        if (persistent.PeriodEffect is not null) stack.Push((persistent.PeriodEffect, depth + 1));
                        if (persistent.ExpireEffect is not null) stack.Push((persistent.ExpireEffect, depth + 1));
                        break;

                    // Leaves contribute no depth and have no children.
                    default:
                        break;
                }
            }

            return EffectBoundsResult.Valid;
        }
    }
}
