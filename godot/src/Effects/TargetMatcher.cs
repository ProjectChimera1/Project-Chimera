#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Evaluates a <see cref="TargetFilter"/> against a candidate entity, relative to the caster. Pure,
    /// allocation-free, deterministic — called inside the executor's zero-alloc <c>Run</c> path. Only the 2.1
    /// predicates (Self/Ally/Enemy/Neutral/Alive) are evaluated; the reserved Air/Ground/Structure bits are
    /// ignored here and wired in Story 2.9a.
    /// </summary>
    internal static class TargetMatcher
    {
        /// <summary>
        /// True when <paramref name="candidateId"/> satisfies <paramref name="filter"/> for a caster of
        /// <paramref name="casterFaction"/> / id <paramref name="casterId"/>. Allegiance bits are OR-ed; an
        /// empty allegiance set means "any allegiance." <see cref="TargetFilter.Alive"/> is an AND-constraint.
        /// Assumes the candidate id is in-bounds (the spatial-hash snapshot only yields valid alive ids); still
        /// guards <see cref="EntityWorld.IsAlive"/> when the Alive bit is set.
        /// </summary>
        internal static bool Matches(TargetFilter filter, EntityWorld world, int casterId,
                                     Faction casterFaction, int candidateId)
        {
            // AND-constraint: explicit alive check when requested. (Dead ids never enter the spatial-hash
            // snapshot, but a leaf chained after a lethal sibling could re-reference a now-dead id.)
            if ((filter & TargetFilter.Alive) != 0 && !world.IsAlive(candidateId))
                return false;

            const TargetFilter allegiance = TargetFilter.Self | TargetFilter.Ally
                                          | TargetFilter.Enemy | TargetFilter.Neutral;
            TargetFilter wanted = filter & allegiance;
            if (wanted == TargetFilter.None)
                return true; // no allegiance constraint → any faction is eligible

            Faction ef = world.FactionOf[candidateId];
            if ((wanted & TargetFilter.Self) != 0 && candidateId == casterId)
                return true;
            if ((wanted & TargetFilter.Ally) != 0 && ef == casterFaction && candidateId != casterId)
                return true;
            if ((wanted & TargetFilter.Enemy) != 0 && ef != casterFaction && ef != Faction.Neutral)
                return true;
            if ((wanted & TargetFilter.Neutral) != 0 && ef == Faction.Neutral)
                return true;

            return false;
        }
    }
}
