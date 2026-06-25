using System;
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Deterministic, Godot-free role-based formation planner (Story 1.13, DG-2 / FR-54). Turns a multi-unit
    /// Move / AttackMove order into one destination per unit: front-line archetypes (Melee, Siege) lead toward
    /// the target and back-line archetypes (Ranged, Air, Worker, Structure) trail, each rank a single row laid
    /// out perpendicular to the move direction.
    ///
    /// <para>Extracted from the flat <c>ceil(sqrt(N))</c> grid that SelectionSystem's <c>IssueMoveCommand</c> and
    /// <c>IssueAttackMoveCommand</c> previously DUPLICATED — both now call this one helper, so Move and AttackMove
    /// can never diverge. It is <see cref="Fixed"/>/<see cref="FixedVec3"/> only (NO <c>using Godot;</c>) so it
    /// lives in the sim source set and is Tier-1-testable without Godot (cf. 1.11 <c>DelayMath</c>, 1.12
    /// <c>OrderApplier</c>).</para>
    ///
    /// <para>It is NOT a tick system and is NOT folded into <c>SimChecksum</c>: the formation is computed ONCE on
    /// the issuing machine and each per-unit destination is transmitted as a <c>Fixed</c> <c>MoveTarget</c> over
    /// the lockstep wire, so its OUTPUT folds into the checksum via <c>Position</c> while the planner itself stays
    /// presentation-time logic.</para>
    /// </summary>
    public static class FormationPlanner
    {
        /// <summary>
        /// Canonical facing used when the requested facing is ~zero (target == group centroid), so the layout
        /// stays deterministic instead of normalizing a zero-length direction. +Z is an arbitrary fixed axis.
        /// </summary>
        private static readonly FixedVec3 CanonicalFacing = new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.One);

        /// <summary>Front-line archetypes lead the formation; everything else trails (per AC4b + the role-mapping
        /// decision: Ranged/Air/Worker/Structure → back).</summary>
        private static bool IsFrontLine(UnitCategory c) => c == UnitCategory.Melee || c == UnitCategory.Siege;

        /// <summary>
        /// Plan per-unit destinations for a group move. <paramref name="categoriesAscending"/> is parallel to
        /// <paramref name="idsAscending"/> (both in ASCENDING entity-id order — the deterministic slot-assignment
        /// contract); only the count and per-index category are read. <paramref name="facing"/> is the move
        /// direction (typically <c>target − selectionCentroid</c>); it is normalized here, with a canonical
        /// fallback when degenerate. Returns one destination per input id, in the same order. Pure <see cref="Fixed"/>:
        /// identical inputs → identical output (AC4c). No float, no Math.*, no RNG, no wall-clock.
        /// </summary>
        public static FixedVec3[] Plan(ReadOnlySpan<int> idsAscending, ReadOnlySpan<UnitCategory> categoriesAscending,
            FixedVec3 target, FixedVec3 facing, Fixed spacing)
        {
            int n = idsAscending.Length;
            var dest = new FixedVec3[n];
            if (n == 0) return dest;
            // AC5: a single-unit move degrades to the centered single destination (the target point itself).
            if (n == 1) { dest[0] = target; return dest; }

            // Normalize facing; fall back to a canonical axis when degenerate. This covers BOTH a zero facing
            // (target == centroid) AND a purely VERTICAL facing (f.X == f.Z == 0), which would otherwise collapse
            // `right` (below) to zero and stack a whole rank on one point — violating AC5 distinctness. Today every
            // spawn forces Y == 0 so facing is always planar and only the zero case can fire, but the planner is a
            // general reusable helper, so the vertical degeneracy is guarded too.
            FixedVec3 f = facing.Normalized();
            if (f.X == Fixed.Zero && f.Z == Fixed.Zero) f = CanonicalFacing;
            // Row direction: a 90° rotation of f in the XZ plane (unit-length when f is planar-unit, which it is
            // for an RTS move on the ground plane). Units in a rank spread along this axis.
            FixedVec3 right = new FixedVec3(f.Z, Fixed.Zero, -f.X);

            // Count roles. A rank's depth along facing is +spacing (front) / −spacing (back) ONLY when BOTH ranks
            // exist; a one-archetype group sits on the centerline (depth 0) so there is no empty opposite rank (AC5).
            int frontCount = 0;
            for (int k = 0; k < n; k++) if (IsFrontLine(categoriesAscending[k])) frontCount++;
            int backCount = n - frontCount;

            Fixed frontDepth = backCount  > 0 ? spacing  : Fixed.Zero;
            Fixed backDepth  = frontCount > 0 ? -spacing : Fixed.Zero;

            // Lay each rank as a single centered row along `right`, slotting ascending ids left→right. Every front
            // destination ends up forward of every back destination relative to facing (proj = target·f ± spacing),
            // and no two destinations coincide (distinct lateral within a rank; distinct depth across ranks).
            int frontSlot = 0, backSlot = 0;
            for (int k = 0; k < n; k++)
            {
                bool front     = IsFrontLine(categoriesAscending[k]);
                int  groupCount = front ? frontCount : backCount;
                int  slot       = front ? frontSlot++ : backSlot++;
                Fixed depth     = front ? frontDepth  : backDepth;

                // Centered lateral offset (slot − (groupCount−1)/2) * spacing, kept exact in Fixed via
                // ((2*slot − (groupCount−1)) * spacing) / 2 — no float, no fractional-int truncation.
                Fixed lateral = Fixed.FromInt(2 * slot - (groupCount - 1)) * spacing / Fixed.FromInt(2);
                dest[k] = target + f * depth + right * lateral;
            }
            return dest;
        }
    }
}
