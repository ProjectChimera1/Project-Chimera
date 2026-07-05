namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 2.13 (AC2, Decision D-1) — the SHARED goal-arrival radius for the state-transition thresholds that
    /// decide when a unit has "reached its goal": <c>CombatSystem.AMOVE_ARRIVE_SQR</c> (AttackMove→Idle and Patrol
    /// waypoint advance) and <c>OrderQueueSystem.ORDER_ARRIVE_SQR</c> (a queued Move/AttackMove completes). A single
    /// source so those two CANNOT DRIFT apart.
    ///
    /// <para><b>Why widen (deferred-work #7 — the arrival-hover deadlock).</b> A crowd sharing one CommandGoal
    /// settles on a ~1.0u separation-vs-seek EQUILIBRIUM RING: MovementSystem seek scales to zero near the goal while
    /// separation stays strong, so the wave never reaches the old 0.5u radius (0.25 squared, INSIDE the ring),
    /// <c>ResumeAttackMove</c> never flips it to Idle, and the units hover in AttackMove forever. Widening the radius
    /// to 2u (Decision D-1, "keep it simple") clears the ring for realistic wave sizes.</para>
    ///
    /// <para><b>Deliberately NOT wired to <c>MovementSystem.ARRIVE_THRESHOLD_SQR</c> (Story 2.13 deviation from
    /// D-1's "all three").</b> That constant is the LOW-LEVEL physical stop (it clears the Moving flag / zeroes
    /// velocity for ANY moving unit, including a combat chaser). Every melee unit in content has <c>attack_range
    /// 1.5</c> (&lt; 2u); widening the physical stop to 2u makes MovementSystem halt a melee chaser at 2u — OUTSIDE
    /// its 1.5u attack range — so it can never close to strike (an infinite stop-at-2u / re-chase oscillation). The
    /// wave/queue deadlock is fully fixed by the two GOAL thresholds here (they gate on distance to the
    /// CommandGoal, independent of the physical stop), so the physical stop stays at 0.5u to preserve melee combat.
    /// Guarded by <c>MeleeClosesWithinArriveRadiusTests</c>. See the Dev Record for the full analysis.</para>
    ///
    /// <para><b>Determinism.</b> Built from <see cref="Fixed.FromInt"/> (exact 16.16), NEVER
    /// <see cref="Fixed.FromFloat"/>, so no float quantization enters these per-tick sim paths (CHM0005-clean). The
    /// value is authored-immutable (NOT a SimChecksum input): it changes only WHEN units flip to Idle, altering
    /// already-folded Position/CommandState transitively → no fold, AlgoVersion stays 9.</para>
    /// </summary>
    public static class ArrivalTuning
    {
        /// <summary>Goal-arrival radius in world units (2u): a unit within this of its CommandGoal has "arrived".</summary>
        public static readonly Fixed GoalArriveRadius = Fixed.FromInt(2);

        /// <summary>Squared goal-arrival radius (4u²) — the value the sim compares SqrDistance-to-goal against.</summary>
        public static readonly Fixed GoalArriveRadiusSqr = GoalArriveRadius * GoalArriveRadius;
    }
}
