using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Steering-based movement system. Applies seek + arrive to moving units and
    /// separation to ALL alive units (so units spread apart even while stationary or attacking).
    ///
    /// Seek:       Steer toward MoveTarget at full speed (Moving units only).
    /// Arrive:     Scale speed down linearly within SLOW_RADIUS of target; stop at ARRIVE_THRESHOLD.
    /// Separation: Push away from all alive neighbors within SEPARATION_RADIUS (every alive unit).
    /// </summary>
    public class MovementSystem : ISimSystem
    {
        // Arrive: stop when squared distance to target is below this
        private static readonly Fixed ARRIVE_THRESHOLD_SQR =
            Fixed.FromFloat(0.5f) * Fixed.FromFloat(0.5f);

        // Arrive: begin slowing down within this distance of target
        private static readonly Fixed SLOW_RADIUS = Fixed.FromFloat(4.0f);

        // Separation: query neighbors within this radius
        private static readonly Fixed SEPARATION_RADIUS = Fixed.FromFloat(2.0f);

        // Separation: multiplier on the summed separation vector
        private static readonly Fixed SEPARATION_STRENGTH = Fixed.FromFloat(2.5f);

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
                int neighborCount = _spatialHash.QueryRadius(world, pos, SEPARATION_RADIUS, i, _neighborBuffer);
                if (neighborCount > 0)
                {
                    FixedVec3 separation = FixedVec3.Zero;
                    for (int n = 0; n < neighborCount; n++)
                    {
                        int j = _neighborBuffer[n];
                        FixedVec3 away = pos - world.Position[j];
                        Fixed neighborDist = away.Magnitude();
                        if (neighborDist <= Fixed.Zero) continue; // exactly overlapping — skip

                        // Linear falloff: full push at dist=0, zero push at dist=SEPARATION_RADIUS
                        Fixed weight = (SEPARATION_RADIUS - neighborDist) / SEPARATION_RADIUS;
                        separation = separation + away.Normalized() * weight;
                    }
                    velocity = velocity + separation * SEPARATION_STRENGTH;
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
