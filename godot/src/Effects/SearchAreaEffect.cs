#nullable enable
using System;
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

        /// <summary>Construct an area-search node.</summary>
        public SearchAreaEffect(Fixed radius, TargetFilter filter, EffectNode child)
        {
            Radius = radius;
            Filter = filter;
            Child = child;
        }

        /// <summary>
        /// Fill <paramref name="hitBuffer"/> (length <c>EffectCaps.MaxHitsPerSearch</c>) with the matched entity
        /// ids in ASCENDING-id order and return the count (&lt;= <c>EffectCaps.MaxSearchTargets</c>). Allocation-free:
        /// queries the rebuilt spatial hash into the caller's buffer, sorts it ascending (QueryRadius is
        /// UNORDERED), then compacts out non-matching ids in place. Returns 0 if there is no spatial hash, the
        /// center is not alive, or nothing matches.
        ///
        /// Note: when more than <c>MaxHitsPerSearch</c> entities are in radius, QueryRadius keeps the first
        /// buffer-full it encounters (deterministic cell-scan order) and those are then sorted — selection stays
        /// deterministic, though it is not a global "lowest ids first" pick. Authored radii keep counts small.
        /// </summary>
        internal int FindTargets(in EffectContext ctx, int[] hitBuffer)
        {
            EntityWorld world = ctx.World;
            int center = ctx.PrimaryTargetId;
            if (ctx.Spatial is null || !world.IsAlive(center))
                return 0;

            FixedVec3 pos = world.Position[center];
            // excludeId = -1: include everything in radius; Filter (Self/Ally/Enemy/Neutral) decides allegiance.
            int count = ctx.Spatial.QueryRadius(world, pos, Radius, -1, hitBuffer);

            // QueryRadius returns UNORDERED — sort ascending-id so execution order is the deterministic contract
            // (AC3). Ids in the buffer are unique, so Array.Sort over ints is a total order (the CHM0003 advisory
            // is a false positive here: there are no equal keys to reorder).
            Array.Sort(hitBuffer, 0, count);

            // Compact out non-matching ids, preserving ascending order.
            int w = 0;
            for (int r = 0; r < count; r++)
            {
                int id = hitBuffer[r];
                if (TargetMatcher.Matches(Filter, world, ctx.CasterId, ctx.CasterFaction, id))
                    hitBuffer[w++] = id;
            }
            count = w;

            // Clamp fan-out to the structural cap (named constant — CHM0004 clean).
            if (count > EffectCaps.MaxSearchTargets)
                count = EffectCaps.MaxSearchTargets;
            return count;
        }
    }
}
