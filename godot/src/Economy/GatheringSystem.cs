#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Economy
{
    /// <summary>
    /// Worker/gatherer state machine. Runs each simulation tick.
    ///
    /// Workers (GatherState != Inactive) automatically cycle through:
    ///   Idle → MovingToResource → Gathering → MovingToBase → (deposit) → repeat
    ///
    /// Story 4.7 adds two more per-node collection models on top of the GATHER cycle above (see
    /// <see cref="ResourceCollectionModel"/>): Income (a periodic flat credit with zero assigned workers — driven
    /// by <see cref="TickIncomeNodes"/>, never through the worker state machine) and Streaming (workers still
    /// cycle Idle → MovingToResource → Gathering exactly like GATHER, but <see cref="TickGathering"/> credits the
    /// gathering worker's faction directly at the node each tick instead of carrying to base — no
    /// <c>MovingToBase</c> leg, no <c>CarryAmount</c>). A per-node <c>requires_structure</c> gate (mirroring
    /// <c>AiOpponentSystem.FindNearestEnemyBuilding</c>'s scan shape against <see cref="BuildingStore"/>) can make
    /// any node conditionally eligible. All GATHER-node behavior when <c>collection_model</c> is omitted/"Gather"
    /// is byte-identical to pre-4.7 (the new branches are dead code on that path).
    ///
    /// A node's <c>AssignedGatherers</c> counter is a real capacity reservation (FindBestNode skips a saturated node),
    /// so every way a worker can STOP occupying a node has to give the slot back. Three of the four used to leak
    /// (DW-207): the worker dying at the node, and a Build command interrupting it, both silently burned capacity
    /// forever. All four now route through the one static <see cref="ReleaseGatherSlot"/> path — the tick loop, the
    /// <see cref="EntityWorld.OnDestroy"/> subscription this system installs, and <c>BuildingSystem.QueueWorkerBuild</c>.
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

        /// <summary>
        /// DW-80 — how many CONSECUTIVE ticks a Streaming worker tolerates a closed <c>requires_structure</c> gate
        /// before it hands its gather slot back and re-idles to seek a different eligible node. Whole ticks (never
        /// dt-accumulated), one second at the fixed sim rate, sourced from <see cref="SimulationLoop.TICKS_PER_SECOND"/>
        /// so it can never drift from the real tick rate.
        ///
        /// <para>The grace window exists to PRESERVE Story 4.7's AC4 reading — a gate that closes and reopens mid-gather
        /// withholds then resumes credit for the SAME worker at the SAME node — while still honouring the recorded
        /// 2026-07-30 decision that a PERMANENTLY closed gate must not park a worker at zero production forever
        /// (matching GATHER's node-vanishes-mid-cycle re-seek). Re-idling is cheap even when no alternative node exists:
        /// <see cref="FindBestNode"/> itself checks the gate, so the worker simply stays Idle where it stands and
        /// re-acquires this very node the tick the gate reopens.</para>
        /// </summary>
        public const int STREAMING_GATE_GRACE_TICKS = SimulationLoop.TICKS_PER_SECOND;

        private readonly ResourceNodeStore _nodes;
        private readonly ResourceStore     _resources;
        private readonly BuildingStore     _buildings;
        private readonly MatchStats?        _stats;

        /// <param name="world">
        /// DW-207 — OPTIONAL death seam. When supplied, this system subscribes <see cref="EntityWorld.OnDestroy"/> so a
        /// worker that dies (or is editor-deleted) while holding a node's gather slot RELEASES it, instead of leaking
        /// <see cref="ResourceNodeStore.AssignedGatherers"/> capacity that no living worker can ever hand back. The main
        /// <see cref="Tick"/> loop cannot do this: it skips dead entities, and a recycled slot has already been reset by
        /// <see cref="EntityWorld.Create"/> by the time anything could notice. Nullable so the isolated-store test
        /// callers that never destroy an entity keep compiling unchanged; <c>SimulationHost</c> always passes the world.
        /// Subscribes AFTER <c>ModifierStore.ClearEntity</c> / <c>ItemSystem</c>'s death-drop (construction order), which
        /// is irrelevant to correctness here — the three subscribers touch disjoint state — but is fixed and therefore
        /// deterministic on every peer.
        /// </param>
        public GatheringSystem(ResourceNodeStore nodes, ResourceStore resources, BuildingStore buildings, MatchStats? stats = null,
                               EntityWorld? world = null)
        {
            _nodes     = nodes;
            _resources = resources;
            _buildings = buildings;
            _stats     = stats;

            if (world != null)
                world.OnDestroy += id => ReleaseGatherSlot(world, nodes, id);
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

            // Story 4.7 — Income nodes have zero assigned workers; they credit on their own periodic cadence,
            // ascending node id (deterministic, mirrors every other count-driven fold/tick in this codebase).
            TickIncomeNodes();
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
                // Node gone. Streaming never carries anything to return (Never rule: no MovingToBase leg for
                // Streaming) — go straight to Idle. GATHER returns what it has (byte-identical to pre-4.7): if
                // it's carrying something, head to base. The carried resource kind rides on the worker
                // (CarryResourceType, snapshotted at gather time), so the deposit routes correctly on arrival
                // regardless of GatherTarget (which is unfolded, not in SimChecksum — this changes no golden).
                bool wasStreaming = node >= 0 && _nodes.CollectionModel[node] == ResourceCollectionModel.Streaming;
                if (!wasStreaming && world.CarryAmount[id] > Fixed.Zero)
                {
                    world.GatherState[id] = GatherState.MovingToBase;
                    SetMoveToBase(world, id);
                }
                else
                {
                    world.GatherState[id]  = GatherState.Idle;
                    world.GatherTarget[id] = -1;
                }
                return;
            }

            bool streaming = _nodes.CollectionModel[node] == ResourceCollectionModel.Streaming;

            // requires_structure can close mid-cycle (e.g. the gating structure is destroyed) — withhold this
            // tick's Streaming credit entirely (no gather, no supply drain, no credit) rather than reassigning;
            // the worker stays put and resumes the instant the gate reopens. GATHER is unaffected (checked only
            // once, at FindBestNode assignment time) — never gated live, matching the Always/Never contract.
            //
            // DW-80: the "stays put" half is BOUNDED. A gate that closes PERMANENTLY (the gating structure destroyed and
            // never rebuilt) used to park the worker in Gathering at zero production forever. After
            // STREAMING_GATE_GRACE_TICKS consecutive closed ticks the worker gives its slot back (so another faction's
            // worker can claim it) and re-idles to seek a different eligible node — GATHER's node-vanishes re-seek
            // behaviour, per the recorded decision. Anything shorter than the grace window is still a pure withhold.
            if (streaming && !StructureGateOpen(node, world.FactionOf[id]))
            {
                world.GateClosedTicks[id]++;
                if (world.GateClosedTicks[id] >= STREAMING_GATE_GRACE_TICKS)
                {
                    ReleaseNode(world, id);                // hands the reserved slot back, clears GatherTarget + the streak
                    world.GatherState[id] = GatherState.Idle;
                }
                return;
            }
            // Gate open (or a GATHER node, which is never live-gated) — the streak must be CONSECUTIVE, so a reopen
            // resets it. Without this, N separate one-tick closures would eventually evict a perfectly productive worker.
            world.GateClosedTicks[id] = 0;

            // Gather from node this tick
            Fixed rate      = _nodes.GatherRate[node];
            Fixed canGather = Fixed.Min(rate * dt, _nodes.SupplyRemaining[node]);
            Fixed canCarry  = streaming ? canGather : world.CarryCapacity[id] - world.CarryAmount[id];
            Fixed gathered  = Fixed.Min(canGather, canCarry);

            _nodes.SupplyRemaining[node] = _nodes.SupplyRemaining[node] - gathered;

            if (streaming)
                CreditNode(node, world.FactionOf[id], gathered); // credit-in-place — no carry, ever
            else
            {
                world.CarryAmount[id]       = world.CarryAmount[id] + gathered;
                world.CarryResourceType[id] = _nodes.ResourceType[node]; // remember the carried kind so the deposit routes correctly even if GatherTarget is later cleared (e.g. a Build command)
            }

            // Deplete node
            if (_nodes.SupplyRemaining[node] <= Fixed.Zero)
            {
                _nodes.Active[node]           = false;
                _nodes.AssignedGatherers[node] = 0; // all workers will re-route next tick
            }

            if (streaming)
            {
                // Streaming never routes through MovingToBase. A depleted node sends the worker back to Idle to
                // seek another; otherwise it stays put and keeps crediting next tick.
                if (!_nodes.Active[node])
                {
                    world.GatherTarget[id] = -1;
                    world.GatherState[id]  = GatherState.Idle;
                }
                return;
            }

            // GATHER: return to base if carry full or node just depleted. The deposit resolves the resource kind
            // from CarryResourceType (snapshotted above), not GatherTarget, so it's safe to leave GatherTarget as-is
            // here — it's unfolded (not in SimChecksum), so this is behavior-identical to pre-4.7.
            if (world.CarryAmount[id] >= world.CarryCapacity[id] || !_nodes.Active[node])
            {
                if (_nodes.Active[node])
                    _nodes.AssignedGatherers[node]--;
                SetMoveToBase(world, id);
                world.GatherState[id] = GatherState.MovingToBase;
            }
        }

        private void TickMovingToBase(EntityWorld world, int id)
        {
            FixedVec3 basePos = _resources.FactionBase[(int)world.FactionOf[id]];
            Fixed sqr = FixedVec3.SqrDistance(world.Position[id], basePos);
            if (sqr > ARRIVE_AT_BASE_SQR) return; // Still travelling

            // Arrived — deposit by the CARRIED resource kind (Story 4.7). Resolving the kind from GatherTarget was
            // fragile: BuildingSystem clears GatherTarget when a Build command interrupts a returning worker, which
            // mis-credited a Crystal load as Ore via the old node<0 fallback. CarryResourceType is snapshotted at
            // gather time and rides on the worker, so the deposit always routes to the right balance (and a fresh
            // slot's default Ore reproduces pre-4.7 always-Ore behavior for any zero/edge carry).
            CreditKind(world.CarryResourceType[id], world.FactionOf[id], world.CarryAmount[id]);
            world.CarryAmount[id]       = Fixed.Zero;
            world.CarryResourceType[id] = ResourceKind.Ore; // reset the carry marker for the next trip
            world.GatherTarget[id]      = -1;
            world.Flags[id]       &= ~EntityFlags.Moving;
            world.Velocity[id]     = FixedVec3.Zero;

            // Immediately seek another node
            world.GatherState[id] = GatherState.Idle;
        }

        /// <summary>
        /// Story 4.7 — the Income tick pass: periodic flat credit, zero assigned workers, ascending node id.
        /// <see cref="ResourceNodeStore.IncomeTicksElapsed"/> is a whole-tick counter (never dt-accumulated, never
        /// wall-clock); a requires_structure gate closed this tick withholds credit WITHOUT advancing the counter
        /// (steady-state — no error), so the node doesn't burst-credit a backlog the instant the gate reopens.
        /// </summary>
        private void TickIncomeNodes()
        {
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (!_nodes.Active[n]) continue;
                if (_nodes.CollectionModel[n] != ResourceCollectionModel.Income) continue;

                Faction owner = _nodes.OwnerFaction[n];
                // Follow-up review patch: defend the Income pass against state the ScenarioValidator normally
                // forbids but a direct/internal ResourceNodeStore.Create could produce. An owner degraded to
                // Neutral (out-of-range owner_slot) would otherwise credit faction index 0 a phantom balance; a
                // non-positive period (the Create default is 0) would make IncomeTicksElapsed's `< period` test
                // false every tick and credit every tick instead of periodically. Validated content hits neither
                // (owner_slot declared + income_period_ticks>0 are required whenever collection_model=Income).
                if (owner == Faction.Neutral) continue;
                if (_nodes.IncomePeriodTicks[n] <= 0) continue;

                if (!StructureGateOpen(n, owner)) continue; // gate closed — withhold credit, no error

                _nodes.IncomeTicksElapsed[n]++;
                if (_nodes.IncomeTicksElapsed[n] < _nodes.IncomePeriodTicks[n]) continue;
                _nodes.IncomeTicksElapsed[n] = 0;

                Fixed credit = Fixed.Min(_nodes.GatherRate[n], _nodes.SupplyRemaining[n]); // Rate reused as "amount per period"
                _nodes.SupplyRemaining[n] = _nodes.SupplyRemaining[n] - credit;
                CreditNode(n, owner, credit);

                if (_nodes.SupplyRemaining[n] <= Fixed.Zero)
                    _nodes.Active[n] = false; // matches GATHER's existing depletion behavior
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Story 4.7 — dispatch a node's credit by its <see cref="ResourceNodeStore.ResourceType"/>
        /// (Streaming/Income credit-in-place at the node). Thin wrapper over <see cref="CreditKind"/>.</summary>
        private void CreditNode(int node, Faction faction, Fixed amount)
            => CreditKind(_nodes.ResourceType[node], faction, amount);

        /// <summary>Story 4.7 — dispatch a credit to the correct per-faction balance by resource kind, closing the
        /// Crystal-production dead path. Used both for credit-in-place (Streaming/Income, by node <see cref="ResourceKind"/>)
        /// and for a GATHER worker's base deposit (by the CARRIED kind — see <c>TickMovingToBase</c>). Ore credits feed
        /// <see cref="MatchStats.RecordOreMined"/> and (Story 11.2) Crystal credits feed
        /// <see cref="MatchStats.RecordCrystalMined"/> — the observational score-screen counters (unfolded).</summary>
        private void CreditKind(ResourceKind kind, Faction faction, Fixed amount)
        {
            if (kind == ResourceKind.Crystal)
            {
                _resources.AddCrystal(faction, amount);
                _stats?.RecordCrystalMined(faction, amount); // Story 11.2 — the observational Crystal twin of the Ore credit below
            }
            else
            {
                _resources.AddOre(faction, amount);
                _stats?.RecordOreMined(faction, amount);
            }
        }

        /// <summary>Story 4.7 — true when <paramref name="node"/> has no requires_structure gate, or
        /// <paramref name="faction"/> owns a qualifying structure within range.</summary>
        private bool StructureGateOpen(int node, Faction faction)
        {
            string requiredId = _nodes.RequiresStructureId[node];
            if (string.IsNullOrEmpty(requiredId)) return true;
            return FactionHasStructureNear(faction, requiredId, _nodes.Position[node], _nodes.RequiresStructureRadius[node]);
        }

        /// <summary>
        /// Story 4.7 — true when <paramref name="faction"/> owns an ALIVE building whose
        /// <see cref="BuildingStore.DefinitionId"/> equals <paramref name="buildingId"/> within
        /// <paramref name="radius"/> of <paramref name="from"/>. Reuses
        /// <c>AiOpponentSystem.FindNearestEnemyBuilding</c>'s scan shape (ascending id, <see cref="Fixed"/> squared
        /// distance) against the SAME <see cref="BuildingStore"/> dependency, but existence-only (no nearest-pick
        /// needed) and faction-OWNED rather than faction-EXCLUDED — never shared/ally visibility (owned-only, per
        /// the Never rule).
        /// </summary>
        private bool FactionHasStructureNear(Faction faction, string buildingId, FixedVec3 from, Fixed radius)
        {
            Fixed radiusSqr = radius * radius;
            for (int b = 0; b < _buildings.Count; b++)
            {
                if (!_buildings.Alive[b]) continue;
                // Review patch: a structure still under construction is "not functional yet" — the same rule
                // TechTreeChecker/BuildingSystem already enforce for every other structure-presence gate in this
                // codebase (TechTreeChecker.cs:76, BuildingSystem.cs:135/159/330/454/524). A requires_structure gate
                // must not open the instant a qualifying building is PLACED; it must wait until construction completes.
                if (_buildings.IsUnderConstruction(b)) continue;
                if (_buildings.FactionOf[b] != faction) continue;
                if (_buildings.DefinitionId[b] != buildingId) continue;
                Fixed sqrDist = FixedVec3.SqrDistance(from, _buildings.Position[b]);
                if (sqrDist <= radiusSqr) return true;
            }
            return false;
        }

        private void AssignToNode(EntityWorld world, int workerId, int nodeIdx)
        {
            world.GatherTarget[workerId] = nodeIdx;
            _nodes.AssignedGatherers[nodeIdx]++;
            world.GateClosedTicks[workerId] = 0; // DW-80: a fresh assignment starts a fresh closed-gate streak
            world.MoveTarget[workerId]   = _nodes.Position[nodeIdx];
            world.Flags[workerId]       |= EntityFlags.Moving;
            world.GatherState[workerId]  = GatherState.MovingToResource;
        }

        private void ReleaseNode(EntityWorld world, int workerId) => ReleaseGatherSlot(world, _nodes, workerId);

        /// <summary>
        /// DW-207 — the SINGLE gather-slot release path: hand <paramref name="workerId"/>'s reserved slot back to its
        /// <see cref="ResourceNodeStore.AssignedGatherers"/> counter (when it actually holds one) and clear its
        /// <see cref="EntityWorld.GatherTarget"/>. Static + public because the release must also happen from outside the
        /// tick loop — on death (<see cref="EntityWorld.OnDestroy"/>) and on a Build-command interrupt
        /// (<c>BuildingSystem.QueueWorkerBuild</c>) — and a second implementation at either site is exactly how the
        /// counter leaked in the first place: a node whose gatherers died at it permanently lost that much capacity, so
        /// <see cref="FindBestNode"/> skipped it as saturated forever.
        ///
        /// <para>ONLY <see cref="GatherState.MovingToResource"/> and <see cref="GatherState.Gathering"/> hold a
        /// reservation. <see cref="GatherState.MovingToBase"/> deliberately does NOT: <see cref="TickGathering"/> already
        /// decremented at that transition (a worker walking a load home is not occupying the node) even though it leaves
        /// <see cref="EntityWorld.GatherTarget"/> pointing at the node it just left. Releasing on that state too would
        /// DOUBLE-decrement and steal a live worker's slot — the mirror-image defect. <see cref="GatherState.Idle"/>
        /// always carries GatherTarget = −1, and the <c>node &gt;= 0</c> test makes a second call idempotent.</para>
        ///
        /// <para>Mutates the folded <c>AssignedGatherers</c> counter, so it must stay integer-only and be reachable in
        /// the same order on every peer: <see cref="EntityWorld.Destroy"/> fires its hook synchronously, in the same
        /// deterministic sequence, before the id returns to the free list.</para>
        /// </summary>
        public static void ReleaseGatherSlot(EntityWorld world, ResourceNodeStore nodes, int workerId)
        {
            int node = world.GatherTarget[workerId];
            if (node >= 0 && HoldsGatherSlot(world.GatherState[workerId])
                && nodes.Active[node] && nodes.AssignedGatherers[node] > 0)
                nodes.AssignedGatherers[node]--;
            world.GatherTarget[workerId]    = -1;
            world.GateClosedTicks[workerId] = 0; // DW-80: no node, no streak
        }

        /// <summary>DW-207 — the two <see cref="GatherState"/>s that occupy one of a node's
        /// <see cref="ResourceNodeStore.MaxGatherers"/> slots. See <see cref="ReleaseGatherSlot"/> for why
        /// <see cref="GatherState.MovingToBase"/> is excluded.</summary>
        private static bool HoldsGatherSlot(GatherState state) =>
            state == GatherState.MovingToResource || state == GatherState.Gathering;

        private void SetMoveToBase(EntityWorld world, int id)
        {
            world.MoveTarget[id]   = _resources.FactionBase[(int)world.FactionOf[id]];
            world.Flags[id]       |= EntityFlags.Moving;
        }

        /// <summary>
        /// Find the nearest active, non-Income node that isn't over capacity and (Story 4.7) whose
        /// requires_structure gate — if any — is open for <paramref name="faction"/>.
        /// Returns -1 if no suitable node exists.
        /// </summary>
        private int FindBestNode(FixedVec3 pos, Faction faction)
        {
            int   bestNode    = -1;
            Fixed bestSqrDist = Fixed.MaxValue;

            for (int n = 0; n < _nodes.Count; n++)
            {
                if (!_nodes.Active[n]) continue;
                if (_nodes.CollectionModel[n] == ResourceCollectionModel.Income) continue; // never assign workers to Income nodes
                if (_nodes.AssignedGatherers[n] >= _nodes.MaxGatherers[n]) continue;
                if (!StructureGateOpen(n, faction)) continue;

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
