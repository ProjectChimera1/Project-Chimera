#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Composition node: finds every entity within <see cref="Radius"/> of the context's primary target that
    /// passes <see cref="Filter"/>, and fans its single <see cref="Child"/> out to each match — in ASCENDING
    /// entity-id order (AC3). The executor reverse-pushes the matches so they pop ascending, capping fan-out at
    /// <c>EffectCaps.MaxSearchTargets</c>. The center is the primary target's position (an "impact point"); the
    /// caster is excluded only if <see cref="Filter"/> excludes it (e.g. an Enemy filter).
    /// </summary>
    public sealed class SearchAreaEffect : CompositionEffect
    {
        /// <summary>Search radius around the context's primary target position.</summary>
        public readonly Fixed Radius;

        /// <summary>The allegiance/alive predicate each candidate must satisfy.</summary>
        public readonly TargetFilter Filter;

        /// <summary>The single effect fanned out to each matched entity.</summary>
        public readonly EffectNode Child;

        /// <summary>
        /// Story 2.11 (AC3): OPTIONAL tag predicate. Default <see cref="UnitTag.None"/> = no constraint (every pre-2.11
        /// SearchArea is byte-identical). When non-None, a candidate is collected ONLY if its <c>EntityWorld.TagsOf</c>
        /// INTERSECTS these bits (match if ANY required bit is set) — an AND-constraint beside <see cref="Filter"/>,
        /// evaluated in <see cref="TargetMatcher"/> via the shared <see cref="TagGate"/>. A distinct axis from
        /// <see cref="Filter"/> (<c>TargetFilter : byte</c> is full — all 8 bits used), so it is a separate field.
        /// </summary>
        public readonly UnitTag RequireTag;

        /// <summary>Construct an area-search node. <paramref name="requireTag"/> (Story 2.11, default None) is a
        /// trailing-optional tag AND-constraint; omit it and every pre-2.11 SearchArea behaves byte-identically.</summary>
        public SearchAreaEffect(Fixed radius, TargetFilter filter, EffectNode child, UnitTag requireTag = UnitTag.None)
        {
            Radius = radius;
            Filter = filter;
            Child = child;
            RequireTag = requireTag;
        }

        /// <summary>
        /// Fill <paramref name="hitBuffer"/> (length <c>EffectCaps.MaxHitsPerSearch</c>) with the matched entity
        /// ids in ASCENDING-id order and return the count (&lt;= <c>EffectCaps.MaxSearchTargets</c>). Returns 0 if
        /// there is no spatial hash, the center is not alive, or nothing matches. Allocation-free: the
        /// allegiance/domain/tag predicate is pushed INTO the spatial query as a
        /// <see cref="TargetMatcher.QueryFilter"/> struct, which fills the caller's buffer already sorted
        /// ascending — so there is no separate sort and no post-hoc compaction pass.
        ///
        /// <para>SELECTION CONTRACT. Only MATCHING entities may occupy a buffer slot, and when more matches than
        /// <c>MaxHitsPerSearch</c> are in radius the kept set is the GLOBALLY LOWEST ids in the whole radius —
        /// not the first buffer-full in cell-scan order. Both halves are load-bearing, and both used to be wrong:
        /// the buffer used to fill with UNFILTERED candidates and get compacted afterwards, so a crowd of
        /// non-matching entities starved the fan-out (an Enemy nuke cast amid 64 of the caster's own allies
        /// selected ZERO enemies), and over-cap selection followed grid geometry rather than the documented
        /// ascending-id contract. Authored radii keep real counts small; the contract is what a peer can rely on.</para>
        /// </summary>
        internal int FindTargets(in EffectContext ctx, int[] hitBuffer)
        {
            EntityWorld world = ctx.World;
            int center = ctx.PrimaryTargetId;
            if (ctx.Spatial is null || !world.IsAlive(center))
                return 0;

            FixedVec3 pos = world.Position[center];
            var filter = new TargetMatcher.QueryFilter(
                Filter, ctx.CasterId, ctx.CasterFaction, RequireTag, ctx.Alliances);
            // excludeId = -1: no POSITIONAL exclusion — Filter (Self/Ally/Enemy/Neutral) decides allegiance, so
            // e.g. an Enemy filter already rejects the caster (and now rejects it before it can eat a slot).
            int count = ctx.Spatial.QueryRadiusLowestIds(world, pos, Radius, -1, hitBuffer, filter);

            // Clamp fan-out to the structural cap (named constant — CHM0004 clean). Defensive while
            // MaxHitsPerSearch == MaxSearchTargets; load-bearing the moment the buffer is sized larger.
            if (count > EffectCaps.MaxSearchTargets)
                count = EffectCaps.MaxSearchTargets;
            return count;
        }
    }
}
