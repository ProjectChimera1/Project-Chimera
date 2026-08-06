#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// DW-648 — THE shared CHECKED-STEP helper: the single, Godot-free place where a MOVEMENT write of a sim
    /// entity's position is resolved against the <see cref="PathabilityGrid"/>.
    ///
    /// <para><b>Why this exists.</b> Story 6.5 put the deterministic blocked-cell rejection INLINE in
    /// <c>MovementSystem</c>, and DW-147 made that check SWEPT there. The rejection therefore protected exactly ONE
    /// writer: any other code that assigned <c>EntityWorld.Position</c> and meant "this entity moved" — a future
    /// teleport/blink effect, a snap-to-waypoint, a knockback, a flow-field/path consumer that repositions directly —
    /// would have had to HAND-COPY the four-branch sweep-and-slide sequence, and a hand-copy that drifts (or is simply
    /// omitted) deposits a unit inside or across a wall with nothing in the suite able to see it. Extracting the
    /// sequence here gives every such writer ONE call to make, and lets
    /// <c>PositionWriterGuardTests</c> assert that the sweep is invoked from this file and nowhere else.</para>
    ///
    /// <para><b>What it is NOT for.</b> AUTHORED / SCENARIO PLACEMENT — <see cref="EntityWorld.Create"/> spawns,
    /// editor placement, save-restore — must NOT route through here. Placement has no "previous position" to sweep
    /// FROM, and silently relocating authored content would be worse than shipping it: that half is owned by the
    /// load-time diagnostic <c>ScenarioValidator.CheckSpawnsNotBlocked</c> (DW-148). Airborne
    /// <c>ProjectileStore</c> positions are likewise out of scope — pathability is a GROUND constraint and a shell
    /// flies over walls.</para>
    ///
    /// <para><b>Determinism.</b> Pure <see cref="Fixed"/> / integer cell arithmetic delegated to
    /// <see cref="PathabilityGrid.IsBlockedOnSegmentOutside"/> — no floating point, no allocation, no ordering
    /// dependence. A null or all-clear grid returns <paramref name="desired"/> UNCHANGED (reference-identical value),
    /// so flat/legacy maps are a byte-for-byte no-op and no per-tick checksum or golden can move.</para>
    /// </summary>
    public static class CheckedStep
    {
        /// <summary>
        /// Resolve a movement step from <paramref name="from"/> toward <paramref name="desired"/> against
        /// <paramref name="pathability"/>, returning the position the mover is actually allowed to occupy.
        ///
        /// <para>The resolution is exactly the one Story 6.5 / DW-147 / DW-148 established for
        /// <c>MovementSystem</c>, moved here verbatim:</para>
        /// <list type="number">
        ///   <item>No grid, or a grid with no blocked cell ⇒ <paramref name="desired"/> (exact no-op).</item>
        ///   <item>The SWEPT segment <paramref name="from"/>→<paramref name="desired"/> enters no blocked cell other
        ///         than the mover's own start cell ⇒ <paramref name="desired"/>. The start cell is deliberately never
        ///         tested (DW-148's confinement contract: a unit that somehow starts inside a blocked cell may shuffle
        ///         within it and walk OUT into a clear neighbour, but may not traverse onward).</item>
        ///   <item>Otherwise WALL-SLIDE: keep the X-only move if ITS swept segment is clear, else the Z-only move if
        ///         ITS swept segment is clear. Each candidate is swept from the PRE-step position, so a fast
        ///         axis-aligned slide cannot tunnel the wall the full step was rejected for. The surviving axis keeps
        ///         <paramref name="desired"/>'s Y (a slide is still a step; only the blocked axis is dropped).</item>
        ///   <item>Neither axis survives ⇒ HARD STOP at <paramref name="from"/>, Y included — the mover did not move
        ///         at all this tick, so it keeps its whole pre-step position.</item>
        /// </list>
        ///
        /// <para>X is tried before Z unconditionally; that fixed order (not "the longer axis", not "the axis with more
        /// clearance") is what makes the outcome independent of the caller and identical on every lockstep peer and
        /// same-seed replay.</para>
        ///
        /// <para><b>DW-803 — the RETURN VALUE IS NOT A BLOCKED-PROBE.</b> This method answers "where may the mover
        /// stand", not "was the mover blocked", and those two questions have the same answer for different reasons: a
        /// return of <paramref name="from"/> means hard stop (case 4) when the step had LENGTH, but means "nothing was
        /// asked" when <paramref name="desired"/> equals <paramref name="from"/> — a degenerate segment has
        /// <c>col == colEnd</c> and <c>row == rowEnd</c>, so <see cref="PathabilityGrid.IsBlockedOnSegmentOutside"/>'s
        /// walk loop never runs, it reports NOT blocked, and case 2 returns immediately without the grid being
        /// consulted at all (this holds even when <paramref name="from"/> is itself inside a wall). Any caller that
        /// infers "blocked" from <c>Resolve(...) == from</c> MUST therefore exclude the zero-length step first —
        /// <c>GatheringSystem.TickWalkStall</c> did not, and surrendered a folded gather reservation every time a snare
        /// zeroed a worker's move speed on clear ground.</para>
        /// </summary>
        /// <param name="pathability">The resolved blocked-cell grid, or <c>null</c> on a flat/legacy map.</param>
        /// <param name="from">The mover's position BEFORE the step (the sweep origin and the hard-stop fallback).</param>
        /// <param name="desired">The position the caller's integration produced for this step.</param>
        /// <returns>The accepted position: <paramref name="desired"/>, a single-axis slide, or <paramref name="from"/>.</returns>
        public static FixedVec3 Resolve(PathabilityGrid? pathability, FixedVec3 from, FixedVec3 desired)
        {
            // Flat/legacy fast path — byte-identical to having no pathability feature at all.
            if (pathability == null || !pathability.AnyBlocked) return desired;

            // The mover's OWN cell, named once and reused for all three sweeps so every candidate shares the same
            // DW-148 confinement reference (sweeping a slide from a DIFFERENT origin cell would let a confined unit
            // re-enter the region it was just rejected from).
            int fromCell = PathabilityGrid.CellOf(from.X, from.Z);

            if (!pathability.IsBlockedOnSegmentOutside(fromCell, from.X, from.Z, desired.X, desired.Z))
                return desired;

            if (!pathability.IsBlockedOnSegmentOutside(fromCell, from.X, from.Z, desired.X, from.Z))
                return new FixedVec3(desired.X, desired.Y, from.Z);   // slide along X

            if (!pathability.IsBlockedOnSegmentOutside(fromCell, from.X, from.Z, from.X, desired.Z))
                return new FixedVec3(from.X, desired.Y, desired.Z);   // slide along Z

            return from;                                              // hard stop at the boundary
        }
    }
}
