using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Steering-based movement system. Applies seek + arrive to moving units and
    /// separation to ALL alive units (so units spread apart even while stationary or attacking).
    ///
    /// Seek:       Steer toward MoveTarget at full speed (Moving units only).
    /// Arrive:     Scale speed down linearly within SLOW_RADIUS of target; stop at ARRIVE_THRESHOLD.
    /// Separation: Push away from contacting neighbours — per-pair contact = summed per-unit radii, biased so a
    ///             moving unit yields less than an idle one, and a Push unit is not displaced by a Yield neighbour
    ///             (Story 1.13, DG-2 / FR-54). Every alive unit separates (so units spread even while attacking).
    /// </summary>
    public class MovementSystem : ISimSystem
    {
        // Arrive: stop when squared distance to target is below this. Story 2.13 (AC2) DELIBERATELY keeps this at
        // 0.5u — the LOW-LEVEL physical stop for any moving unit, including a combat chaser. Widening it to the 2u
        // goal-arrive radius (ArrivalTuning) would halt every melee unit (attack_range 1.5 < 2) OUTSIDE its strike
        // range → it could never close (MeleeUnitBelowArriveRadius_StillClosesAndStrikes guards this). The wave /
        // queued-Move deadlock is fixed by the two GOAL thresholds (CombatSystem.AMOVE_ARRIVE_SQR / OrderQueueSystem.
        // ORDER_ARRIVE_SQR), which gate on distance-to-CommandGoal and are independent of this physical stop.
        private static readonly Fixed ARRIVE_THRESHOLD_SQR =
            Fixed.FromFloat(0.5f) * Fixed.FromFloat(0.5f);

        // Arrive: begin slowing down within this distance of target
        private static readonly Fixed SLOW_RADIUS = Fixed.FromFloat(4.0f);

        // Separation: how WIDE to scan for neighbours (the flat query bound). NOT the contact distance — that is
        // now the per-pair summed radii (Story 1.13). Kept at 2.0 so the neighbour scan + 32-slot buffer are
        // unchanged, and so 2 * EntityWorld.MAX_COLLISION_RADIUS (= 2.0) never exceeds it (no contact silently
        // missed). A future story wanting bigger units widens BOTH this and MAX_COLLISION_RADIUS together.
        private static readonly Fixed SEPARATION_QUERY_RADIUS = Fixed.FromFloat(2.0f);

        // Separation: multiplier on the summed separation vector
        private static readonly Fixed SEPARATION_STRENGTH = Fixed.FromFloat(2.5f);

        // Separation: the fraction by which a MOVING unit's separation displacement (and thus the perturbation to
        // its path-following) is damped, so an idle neighbour yields more in a mixed contact while same-state pairs
        // (both moving / both idle) stay symmetric (Story 1.13, AC1). Fixed.Half = 0.5 (named, not a bare literal).
        private static readonly Fixed MOVING_SEPARATION_BIAS = Fixed.Half;

        private readonly SpatialHash _spatialHash = new SpatialHash();

        // Pre-allocated neighbor buffer — 32 slots is enough for a 2-unit separation radius
        private readonly int[] _neighborBuffer = new int[32];

        public void Tick(EntityWorld world, Fixed dt)
        {
            // Rebuild spatial hash from current positions once per tick
            _spatialHash.Rebuild(world);

            int count = world.HighWaterMark;
            for (int i = 0; i < count; i++)
            {
                if ((world.Flags[i] & EntityFlags.Alive) == 0) continue;

                // Story 1.12 (AC5b) — Hold Position anchor: a HoldPosition unit is NEVER displaced from its
                // tile by separation/collision steering. This is the REAL distinction from Stop (which still
                // gets pushed). Zero its velocity and skip seek+separation entirely; the unit stays in the
                // spatial hash, so neighbours still see it and steer AROUND it — only its OWN position is
                // anchored. (DG-1: Hold no longer aliases Stop.) Stop is deliberately NOT exempted.
                if (world.CommandState[i] == UnitCommand.HoldPosition)
                {
                    world.Velocity[i] = FixedVec3.Zero;
                    continue;
                }

                FixedVec3 pos = world.Position[i];
                bool isMoving = (world.Flags[i] & EntityFlags.Moving) != 0;

                // --- Seek with arrive (moving units only) ---
                FixedVec3 velocity = FixedVec3.Zero;

                if (isMoving)
                {
                    FixedVec3 toTarget = world.MoveTarget[i] - pos;
                    Fixed sqrDist = toTarget.SqrMagnitude();

                    if (sqrDist <= ARRIVE_THRESHOLD_SQR)
                    {
                        world.Velocity[i] = FixedVec3.Zero;
                        world.Flags[i] &= ~EntityFlags.Moving;
                        // NOTE: CommandState is NOT reset here. PathRequestSystem owns the
                        // Move→Idle transition for nav-path moves; direct-steer units are
                        // handled by PathRequestSystem's cleanup pass each frame.
                        continue; // Arrived — skip separation this tick (next tick it applies)
                    }

                    Fixed dist = toTarget.Magnitude();
                    Fixed speed = world.EffectiveMoveSpeed[i];
                    if (dist < SLOW_RADIUS)
                        speed = speed * dist / SLOW_RADIUS;

                    velocity = toTarget.Normalized() * speed;
                }

                // --- Separation from nearby units (all alive units) ---
                // The query is a flat bound (how wide to scan); the actual CONTACT is the per-pair summed radii.
                int neighborCount = _spatialHash.QueryRadius(world, pos, SEPARATION_QUERY_RADIUS, i, _neighborBuffer);
                if (neighborCount > 0)
                {
                    FixedVec3 separation = FixedVec3.Zero;
                    for (int n = 0; n < neighborCount; n++)
                    {
                        int j = _neighborBuffer[n];
                        FixedVec3 away = pos - world.Position[j];
                        Fixed neighborDist = away.Magnitude();
                        if (neighborDist <= Fixed.Zero) continue; // exactly overlapping — skip (unchanged)

                        // AC2b: per-pair contact = summed radii (replaces the flat SEPARATION_RADIUS in the weight).
                        Fixed contact = world.CollisionRadius[i] + world.CollisionRadius[j];
                        if (neighborDist >= contact) continue; // not in contact

                        // AC2c: a Push unit is never displaced by a Yield neighbour it contacts — it skips that
                        // neighbour's contribution to its OWN separation. (The yield unit is still pushed by the
                        // push unit when the yield unit computes ITS separation, where this guard is false.)
                        if (world.SeparationPriorityOf[i] == SeparationPriority.Push &&
                            world.SeparationPriorityOf[j] == SeparationPriority.Yield) continue;

                        // Linear falloff normalized by the summed radii: full push at dist=0, zero push at contact.
                        Fixed weight = (contact - neighborDist) / contact;
                        separation = separation + away.Normalized() * weight;
                    }

                    // AC1: moving-vs-idle bias — damp a MOVING unit's total separation by MOVING_SEPARATION_BIAS so
                    // its path-following is perturbed by at most that fraction and an idle neighbour yields more in a
                    // mixed contact; same-state pairs (both moving / both idle) stay symmetric (equal magnitude).
                    Fixed bias = isMoving ? (Fixed.One - MOVING_SEPARATION_BIAS) : Fixed.One;
                    velocity = velocity + separation * SEPARATION_STRENGTH * bias;
                }

                // No net force — skip update
                if (velocity == FixedVec3.Zero) continue;

                // --- Clamp to max speed ---
                Fixed maxSpeed = world.EffectiveMoveSpeed[i];
                Fixed velSqr = velocity.SqrMagnitude();
                if (velSqr > maxSpeed * maxSpeed)
                    velocity = velocity.Normalized() * maxSpeed;

                world.Velocity[i] = velocity;
                world.Position[i] = pos + velocity * dt;

                // Story 6.5 — deterministic blocked-cell rejection (the sim TEETH; the flow-field OR-in is only the
                // live-game "route around" nicety). A live unit may not INTEGRATE its position INTO — or THROUGH
                // (DW-147) — a blocked cell it was not already in. Null / all-clear grid ⇒ an exact no-op (Position untouched ⇒ byte-identical to
                // pre-feature, so flat maps never move a per-tick checksum). Pure Fixed, integer cell lookup, so it is
                // byte-identical across same-seed replays and both lockstep peers.
                var pathability = world.Pathability;
                if (pathability != null && pathability.AnyBlocked)
                {
                    FixedVec3 np = world.Position[i];
                    // DW-148: reject any step that ENTERS a blocked cell the unit is not already standing in. The
                    // reference cell is the unit's PRE-step cell, so:
                    //   • a unit in a CLEAR cell behaves exactly as before (a blocked destination is necessarily a
                    //     different cell) — byte-identical for every validated map, so no golden moves;
                    //   • a unit that somehow starts INSIDE a blocked cell (a slope-derived spawn the Godot-free gate
                    //     cannot see — see ScenarioValidator.CheckSpawnsNotBlocked) is CONFINED: it may shuffle within
                    //     its own cell and walk out into a CLEAR neighbour, but it can no longer traverse ONWARD
                    //     through the blocked region. Pre-fix such a unit was exempt from blocking entirely and walked
                    //     through walls freely.
                    // DW-147: the test is SWEPT, not endpoint-only. The whole segment pos→np is walked cell by cell
                    // (PathabilityGrid.IsBlockedOnSegmentOutside) and rejected on the FIRST foreign blocked cell it
                    // enters. Endpoint-only sampling let a unit whose per-tick displacement reached the 2-unit cell
                    // size (move speed ≳ 60 u/s) TUNNEL a one-cell-thick wall — both endpoints clear, the wall never
                    // sampled — and let a diagonal step clip a blocked cell's corner. For an AXIS-ALIGNED sub-cell step
                    // the swept walk visits exactly the two endpoint cells, so it is byte-identical to the pre-fix
                    // check; a DIAGONAL sub-cell step additionally visits the one shared-edge cell it really passes
                    // through, which is the deliberate tightening (no corner-cutting through an obstacle).
                    int fromCell = PathabilityGrid.CellOf(pos.X, pos.Z);
                    if (pathability.IsBlockedOnSegmentOutside(fromCell, pos.X, pos.Z, np.X, np.Z))
                    {
                        // Wall-slide: keep whichever single-axis move stays out of a foreign blocked cell; otherwise
                        // fully retain the pre-step position. Each candidate slide is itself SWEPT from the pre-step
                        // position, so a fast axis-aligned slide cannot tunnel the wall the full step was rejected for.
                        // Ascending id + Fixed-only ⇒ deterministic.
                        if (!pathability.IsBlockedOnSegmentOutside(fromCell, pos.X, pos.Z, np.X, pos.Z))
                            world.Position[i] = new FixedVec3(np.X, np.Y, pos.Z); // slide along X
                        else if (!pathability.IsBlockedOnSegmentOutside(fromCell, pos.X, pos.Z, pos.X, np.Z))
                            world.Position[i] = new FixedVec3(pos.X, np.Y, np.Z); // slide along Z
                        else
                            world.Position[i] = pos;                              // hard stop at the boundary
                    }
                }
            }
        }
    }
}
