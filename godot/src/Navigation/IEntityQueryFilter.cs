#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Per-candidate predicate seam for a FILTERED <see cref="SpatialHash"/> query.
    ///
    /// Implemented by a caller-side <c>readonly struct</c> and consumed through a <c>where T : struct</c>
    /// generic constraint, so every test compiles to a devirtualized direct call: no delegate, no boxing, no
    /// heap allocation. That is what lets a caller's filter move INSIDE the query — and therefore in FRONT of
    /// the result-buffer truncation — without breaking the effect executor's zero-allocation contract.
    ///
    /// The predicate MUST be pure and visit-order-independent: the query walks candidates in spatial cell-scan
    /// order, so a predicate that depended on the order it was called in would make selection undeterministic.
    /// </summary>
    public interface IEntityQueryFilter
    {
        /// <summary>
        /// True when <paramref name="entityId"/> — an entity from the current spatial-hash snapshot that lies
        /// inside the query radius — should be collected into the result buffer.
        /// </summary>
        bool Accepts(EntityWorld world, int entityId);
    }
}
