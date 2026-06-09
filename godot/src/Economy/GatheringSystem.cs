using ProjectChimera.Core;

namespace ProjectChimera.Economy
{
    /// <summary>
    /// Worker/gatherer state machine. Runs each simulation tick.
    ///
    /// Workers (GatherState != Inactive) automatically cycle through:
    ///   Idle → MovingToResource → Gathering → MovingToBase → (deposit) → repeat
    ///
    /// CombatSystem skips any entity with GatherState != Inactive, so workers never
    /// auto-attack — even when their unit data carries attack damage.
    /// MovementSystem handles their physical movement via MoveTarget + Moving flag.
    /// This system only manages state transitions.
    /// </summary>
    public class GatheringSystem : ISimSystem
    {
        private static readonly Fixed ARRIVE_AT_NODE_SQR  = Fixed.FromFloat(1.8f) * Fixed.FromFloat(1.8f);
        private static readonly Fixed ARRIVE_AT_BASE_SQR  = Fixed.FromFloat(3.0f) * Fixed.FromFloat(3.0f);

        private readonly ResourceNodeStore _nodes;
        private readonly ResourceStore     _resources;
        private readonly MatchStats?        _stats;

        public GatheringSystem(ResourceNodeStore nodes, ResourceStore resources, MatchStats? stats = null)
        {
            _nodes     = nodes;
            _resources = resources;
            _stats     = stats;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if ((world.Flags[i] & EntityFlags.Alive) == 0) continue;
                if (world.GatherState[i] == GatherState.Inactive) continue;
                // Worker is currently walking to a build site — let MovementSystem
                // move them and BuildingSystem handle arrival; don't touch gather state.
                if (world.CommandState[i] == UnitCommand.Build) continue;

                switch (world.GatherState[i])
                {
                    case GatherState.Idle:
                        TickIdle(world, i);
                        break;
                    case GatherState.MovingToResource:
                        TickMovingToResource(world, i);
                        break;
                    case GatherState.Gathering:
                        TickGathering(world, i, dt);
                        break;
                    case GatherState.MovingToBase:
                        TickMovingToBase(world, i);
                        break;
                }
            }
        }

        // ── State handlers ────────────────────────────────────────────────────

        private void TickIdle(EntityWorld world, int id)
        {
            int node = FindBestNode(world.Position[id], world.FactionOf[id]);
            if (node < 0) return; // No nodes available — stay Idle

            AssignToNode(world, id, node);
        }

        private void TickMovingToResource(EntityWorld world, int id)
        {
            int node = world.GatherTarget[id];

            // Node was depleted by someone else while en route
            if (node < 0 || !_nodes.Active[node])
            {
                ReleaseNode(world, id);
                world.GatherState[id] = GatherState.Idle;
                return;
            }

            Fixed sqr = FixedVec3.SqrDistance(world.Position[id], _nodes.Position[node]);
            if (sqr > ARRIVE_AT_NODE_SQR) return; // Still travelling

            // Arrived at node
            world.Flags[id]      &= ~EntityFlags.Moving;
            world.Velocity[id]    = FixedVec3.Zero;
            world.GatherState[id] = GatherState.Gathering;
        }

        private void TickGathering(EntityWorld world, int id, Fixed dt)
        {
            int node = world.GatherTarget[id];

            if (node < 0 || !_nodes.Active[node])
            {
                // Node gone — return what we have, then seek another
                world.GatherState[id] = world.CarryAmount[id] > Fixed.Zero
                    ? GatherState.MovingToBase
                    : GatherState.Idle;
                world.GatherTarget[id] = -1;
                if (world.GatherState[id] == GatherState.MovingToBase)
                    SetMoveToBase(world, id);
                return;
            }

            // Gather from node this tick
            Fixed rate     = _nodes.GatherRate[node];
            Fixed canGather = Fixed.Min(rate * dt, _nodes.SupplyRemaining[node]);
            Fixed canCarry  = world.CarryCapacity[id] - world.CarryAmount[id];
            Fixed gathered  = Fixed.Min(canGather, canCarry);

            world.CarryAmount[id]        = world.CarryAmount[id] + gathered;
            _nodes.SupplyRemaining[node] = _nodes.SupplyRemaining[node] - gathered;

            // Deplete node
            if (_nodes.SupplyRemaining[node] <= Fixed.Zero)
            {
                _nodes.Active[node]           = false;
                _nodes.AssignedGatherers[node] = 0; // all workers will re-route next tick
            }

            // Return to base if carry full or node just depleted
            if (world.CarryAmount[id] >= world.CarryCapacity[id] || !_nodes.Active[node])
            {
                if (_nodes.Active[node])
                    _nodes.AssignedGatherers[node]--;
                world.GatherTarget[id] = -1;
                SetMoveToBase(world, id);
                world.GatherState[id] = GatherState.MovingToBase;
            }
        }

        private void TickMovingToBase(EntityWorld world, int id)
        {
            FixedVec3 basePos = _resources.FactionBase[(int)world.FactionOf[id]];
            Fixed sqr = FixedVec3.SqrDistance(world.Position[id], basePos);
            if (sqr > ARRIVE_AT_BASE_SQR) return; // Still travelling

            // Arrived — deposit ore
            _stats?.RecordOreMined(world.FactionOf[id], world.CarryAmount[id]);
            _resources.AddOre(world.FactionOf[id], world.CarryAmount[id]);
            world.CarryAmount[id]  = Fixed.Zero;
            world.Flags[id]       &= ~EntityFlags.Moving;
            world.Velocity[id]     = FixedVec3.Zero;

            // Immediately seek another node
            world.GatherState[id] = GatherState.Idle;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void AssignToNode(EntityWorld world, int workerId, int nodeIdx)
        {
            world.GatherTarget[workerId] = nodeIdx;
            _nodes.AssignedGatherers[nodeIdx]++;
            world.MoveTarget[workerId]   = _nodes.Position[nodeIdx];
            world.Flags[workerId]       |= EntityFlags.Moving;
            world.GatherState[workerId]  = GatherState.MovingToResource;
        }

        private void ReleaseNode(EntityWorld world, int workerId)
        {
            int node = world.GatherTarget[workerId];
            if (node >= 0 && _nodes.Active[node] && _nodes.AssignedGatherers[node] > 0)
                _nodes.AssignedGatherers[node]--;
            world.GatherTarget[workerId] = -1;
        }

        private void SetMoveToBase(EntityWorld world, int id)
        {
            world.MoveTarget[id]   = _resources.FactionBase[(int)world.FactionOf[id]];
            world.Flags[id]       |= EntityFlags.Moving;
        }

        /// <summary>
        /// Find the nearest active node that isn't over capacity.
        /// Returns -1 if no suitable node exists.
        /// </summary>
        private int FindBestNode(FixedVec3 pos, Faction faction)
        {
            int   bestNode    = -1;
            Fixed bestSqrDist = Fixed.MaxValue;

            for (int n = 0; n < _nodes.Count; n++)
            {
                if (!_nodes.Active[n]) continue;
                if (_nodes.AssignedGatherers[n] >= _nodes.MaxGatherers[n]) continue;

                Fixed sqr = FixedVec3.SqrDistance(pos, _nodes.Position[n]);
                if (sqr < bestSqrDist)
                {
                    bestSqrDist = sqr;
                    bestNode    = n;
                }
            }
            return bestNode;
        }
    }
}
