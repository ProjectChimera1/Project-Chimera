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
        // Arrive: stop when squared distance to target is below this
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
                    Fixed speed = world.Speed[i];
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
                Fixed maxSpeed = world.Speed[i];
                Fixed velSqr = velocity.SqrMagnitude();
                if (velSqr > maxSpeed * maxSpeed)
                    velocity = velocity.Normalized() * maxSpeed;

                world.Velocity[i] = velocity;
                world.Position[i] = pos + velocity * dt;
            }
        }
    }
}
