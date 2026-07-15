#nullable enable
using System;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 6.4 — the Godot-free resolved-region store: parallel <see cref="Ids"/> (string) + <see cref="Rects"/>
    /// (<see cref="FixedRect"/>) built ONCE at scenario-apply from <c>ScenarioData.Regions</c> (each float corner
    /// resolved through <see cref="Fixed.FromFloat"/> at that single conversion boundary — never here, never
    /// in-tick). Region bounds are STATIC authored data, never mutated mid-match, so this store is deliberately
    /// NOT folded into <c>SimChecksum</c> (same category as the trigger system it serves).
    ///
    /// <para>The only consumer is <c>ScenarioDirector.EvalCondition</c>'s <c>unit_in_region</c> case, which
    /// resolves a <c>region_id</c> to an index via <see cref="TryGetIndex"/> then point-in-rects live unit
    /// positions via <see cref="Contains"/>. Degenerate/empty stores are safe: <see cref="Empty"/> reports
    /// <c>Count == 0</c> and every lookup fails cleanly.</para>
    /// </summary>
    public sealed class RegionStore
    {
        /// <summary>Region ids in authored order, parallel to <see cref="Rects"/>. A region is referenced by this
        /// string id, mirroring the existing timer/variable string-key convention.</summary>
        public readonly string[] Ids;

        /// <summary>Resolved <see cref="FixedRect"/> bounds, parallel to <see cref="Ids"/>.</summary>
        public readonly FixedRect[] Rects;

        /// <summary>The shared empty store (no regions authored). Reused so the common case allocates nothing.</summary>
        public static readonly RegionStore Empty = new RegionStore(Array.Empty<string>(), Array.Empty<FixedRect>());

        /// <summary>Number of resolved regions.</summary>
        public int Count => Ids.Length;

        /// <summary>Construct over pre-resolved parallel arrays. The two arrays MUST be the same length (asserted).</summary>
        public RegionStore(string[] ids, FixedRect[] rects)
        {
            if (ids is null) throw new ArgumentNullException(nameof(ids));
            if (rects is null) throw new ArgumentNullException(nameof(rects));
            if (ids.Length != rects.Length)
                throw new ArgumentException($"RegionStore ids ({ids.Length}) and rects ({rects.Length}) must be the same length.");
            Ids = ids;
            Rects = rects;
        }

        /// <summary>
        /// Resolve a region <paramref name="id"/> to its index (ascending scan, ordinal string compare — no
        /// Dictionary enumeration on any sim path). Returns false (idx = -1) for a null/empty/unknown id. First
        /// match wins; the validator guarantees unique ids upstream.
        /// </summary>
        public bool TryGetIndex(string? id, out int idx)
        {
            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < Ids.Length; i++)
                {
                    if (string.Equals(Ids[i], id, StringComparison.Ordinal))
                    {
                        idx = i;
                        return true;
                    }
                }
            }
            idx = -1;
            return false;
        }

        /// <summary>
        /// True when <paramref name="pos"/> is inside the region at <paramref name="idx"/> (inclusive edges). An
        /// out-of-range index returns false rather than throwing — a defensive guard; callers resolve idx via
        /// <see cref="TryGetIndex"/> first.
        /// </summary>
        public bool Contains(int idx, FixedVec3 pos)
        {
            if (idx < 0 || idx >= Rects.Length) return false;
            return Rects[idx].Contains(pos);
        }
    }
}
