#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Evaluates a <see cref="TargetFilter"/> against a candidate entity, relative to the caster. Pure,
    /// allocation-free, deterministic — called inside the executor's zero-alloc <c>Run</c> path. Evaluates the
    /// allegiance predicates (Self/Ally/Enemy/Neutral), the Alive AND-constraint, and (Story 2.9a) the
    /// Air/Ground/Structure domain AND-constraint via the shared <see cref="DomainClassifier"/>.
    /// </summary>
    internal static class TargetMatcher
    {
        // Story 2.9a (AC6): the TargetFilter bit for a candidate's domain — the ability-side counterpart to
        // AttackDomain, mapping the SHARED DomainClassifier.Of() result onto this enum's Air/Ground/Structure bits.
        private static TargetFilter DomainBit(Domain d) => d switch
        {
            Domain.Air       => TargetFilter.Air,
            Domain.Structure => TargetFilter.Structure,
            _                => TargetFilter.Ground,
        };
        /// <summary>
        /// True when <paramref name="candidateId"/> satisfies <paramref name="filter"/> for a caster of
        /// <paramref name="casterFaction"/> / id <paramref name="casterId"/>. Allegiance bits are OR-ed; an
        /// empty allegiance set means "any allegiance." <see cref="TargetFilter.Alive"/> is an AND-constraint.
        /// Assumes the candidate id is in-bounds (the spatial-hash snapshot only yields valid alive ids); still
        /// guards <see cref="EntityWorld.IsAlive"/> when the Alive bit is set.
        /// </summary>
        internal static bool Matches(TargetFilter filter, EntityWorld world, int casterId,
                                     Faction casterFaction, int candidateId,
                                     UnitTag requireTag = UnitTag.None)
        {
            // AND-constraint: explicit alive check when requested. (Dead ids never enter the spatial-hash
            // snapshot, but a leaf chained after a lethal sibling could re-reference a now-dead id.)
            if ((filter & TargetFilter.Alive) != 0 && !world.IsAlive(candidateId))
                return false;

            // Story 2.9a (AC6): domain AND-constraint, placed BEFORE the allegiance OR-group (which returns true
            // early). If the filter sets ANY of Air/Ground/Structure, the candidate's domain — from the SAME
            // CategoryOf→Domain classifier the combat AC1 filter uses, so the two can never disagree on "a flyer" —
            // must be among them. If it sets NONE (every existing SearchArea, domain=0), the check is a no-op, so
            // every pre-2.9a filter behaves byte-identically (AC6.1).
            const TargetFilter domains = TargetFilter.Air | TargetFilter.Ground | TargetFilter.Structure;
            TargetFilter wantedDomains = filter & domains;
            if (wantedDomains != TargetFilter.None
                && (wantedDomains & DomainBit(DomainClassifier.Of(world.CategoryOf[candidateId]))) == TargetFilter.None)
                return false;

            // Story 2.11 (AC3.2): tag AND-constraint, beside the 2.9a domain block. If a require_tag is set, the
            // candidate's TagsOf must INTERSECT it (match if ANY required bit is set); None (every pre-2.11 SearchArea)
            // is a no-op → byte-identical. Single-sourced with the single-target leaf gate via TagGate.Intersects, so
            // the two consumption sites can never disagree. Pure integer bit-AND — no Fixed/float/RNG. (candidateId is a
            // live spatial-hash snapshot id, so the TagsOf read is in-bounds, like the CategoryOf read above.)
            if (!TagGate.Intersects(world.TagsOf[candidateId], requireTag))
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
