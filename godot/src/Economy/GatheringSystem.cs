#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;      // StatusFlags (modifier-imposed per-entity status; a value enum, same sim layer)
using ProjectChimera.Navigation;   // CheckedStep / PathabilityGrid — the DW-532 walk-stall probe (pure Fixed, Godot-free)

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
    /// DW-532 closes the FIFTH way, which none of those four can reach: a worker CONFINED by blocked cells
    /// (<c>ScenarioApplier</c> warns-and-places an authored spawn inside a blocked region; <c>MovementSystem</c> then
    /// confines it) never leaves <see cref="GatherState.MovingToResource"/>, so it held its reservation for the whole
    /// match. <see cref="TickWalkStall"/> bounds that leg — after <see cref="WALK_STALL_GRACE_TICKS"/> consecutive ticks
    /// of no possible advance it yields the slot and keeps walking, re-claiming only if it ever arrives.
    ///
    /// DW-634 — an EXPLICIT ORDER BEATS THE AUTOMATIC SWEEP. A worker trained from a building carrying a rally point
    /// spawns with <see cref="EntityWorld.RallyMovePending"/> set; <see cref="TickIdle"/> stands down (no
    /// <see cref="AssignToNode"/>) until that worker reaches its rally <see cref="EntityWorld.CommandGoal"/>, then
    /// clears the one-shot flag and resumes the unchanged nearest-node logic — which, from the rally point, picks the
    /// node the player rallied to. Pre-fix the sweep overwrote the rally MoveTarget on the very next tick, so a rally
    /// could never direct new workers to a specific mine.
    ///
    /// DW-689 bounds that stand-down. Its "superseded" escape (any command state other than the rally's own Move)
    /// cannot fire in the SIMULATION layer, so a worker rallied to an UNREACHABLE point stood down forever and never
    /// gathered again. <see cref="EntityWorld.RallyStandDownTicks"/> counts consecutive ticks of no progress toward the
    /// goal against the best-approach mark in <see cref="EntityWorld.RallyGoalBestSqr"/>; after
    /// <see cref="RALLY_STANDDOWN_GRACE_TICKS"/> the leg is released and the worker rejoins the sweep.
    ///
    /// DW-619 / DW-834 — a <see cref="StatusFlags.Stunned"/>/<see cref="StatusFlags.Rooted"/> worker PRODUCES NOTHING
    /// AND TAKES NO GATHER ACTION AT ALL. The DW-266 status pass reached MovementSystem/CombatSystem/AbilityCastSystem
    /// but never the economy, so a stun anchored a worker standing at its node and then let it keep mining that node
    /// out at full rate — the stun only half-landed. DW-619 gated the <see cref="GatherState.Gathering"/> arm; DW-834
    /// found that gating ONE of <see cref="Tick"/>'s FOUR arms left the sentence above false in two more places. A held
    /// worker already standing inside the drop-off radius still ran <see cref="TickMovingToBase"/> and BANKED its whole
    /// carry (that arrival test is purely positional, so it needs no movement), and a held worker in
    /// <see cref="GatherState.Idle"/> still ran <see cref="FindBestNode"/> + <see cref="AssignToNode"/> and TOOK a node
    /// reservation away from a worker that could actually use it. The mask (see <see cref="GATHER_BLOCKING"/>) is
    /// therefore read ONCE, above the switch in <see cref="Tick"/>, so Idle / MovingToResource / Gathering /
    /// MovingToBase are all suspended together. PAUSE, never cancel: no state transition, no reservation change, no
    /// carry change, no supply drain — the worker resumes exactly where it stood the tick the modifier expires.
    ///
    /// CombatSystem skips any entity with GatherState != Inactive, so workers never
    /// auto-attack — even when their unit data carries attack damage.
    /// MovementSystem handles their physical movement via MoveTarget + Moving flag.
    /// This system only manages state transitions.
    /// </summary>
    public class GatheringSystem : ISimSystem
    {
        /// <summary>
        /// DW-619 / DW-834 — the statuses that SUSPEND a worker's whole gather tick (every arm of
        /// <see cref="Tick"/>'s switch, gated once above it), deliberately the same pair as
        /// <c>MovementSystem.MOVE_BLOCKING</c> (the mirror the recorded closure asks for).
        ///
        /// <para><see cref="StatusFlags.Stunned"/> is the headline case: fully incapacitated everywhere else in the
        /// codebase (no move, no attack, no cast), it must not keep mining either.
        /// <see cref="StatusFlags.Rooted"/> is included because the GATHER cycle is a MOVEMENT loop, not a standalone
        /// action: a rooted worker is already anchored by DW-266, so for a GATHER node the flag mostly costs it only a
        /// partial carry it banks the moment the root expires.</para>
        ///
        /// <para><b>DW-834 — the two places "held ⇒ produces nothing" was NOT true.</b> This doc used to name a
        /// Streaming node (credit IN PLACE, no delivery leg) as "the only way a held worker could still feed its
        /// faction every tick", reasoning that an anchored worker can never DELIVER a load. Both halves were false,
        /// and both are confirmed. (1) <see cref="TickMovingToBase"/>'s arrival test is PURELY POSITIONAL
        /// (<see cref="ARRIVE_AT_BASE_SQR"/>, 3.0 world units) — a worker stunned or rooted while already standing
        /// inside the drop-off radius needs no movement at all, so it banked its ENTIRE carry on the very next tick
        /// (one-shot per hold: the deposit re-idles it). Reachable in play by exactly the AoE stun or root a player
        /// lands over a drop-off. (2) <see cref="TickIdle"/> demands no movement either — a held worker beside a free
        /// node ran <see cref="AssignToNode"/> and consumed one of that node's
        /// <see cref="ResourceNodeStore.MaxGatherers"/> slots while anchored, taking capacity from a worker that could
        /// have used it. Streaming is the LOUDEST case, never the only one, which is why the mask is now read at the
        /// dispatch loop instead of inside one arm.</para>
        ///
        /// <para>Kept as ONE named mask so the Rooted half is a one-token revert if a later balance story decides root
        /// is "held in place, still able to act" (the reading <c>MovementSystem</c>'s own doc comment records) all the
        /// way down to harvesting.</para>
        /// </summary>
        private const StatusFlags GATHER_BLOCKING = StatusFlags.Stunned | StatusFlags.Rooted;

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

        /// <summary>
        /// DW-532 — how many CONSECUTIVE ticks a <see cref="GatherState.MovingToResource"/> worker may be completely
        /// unable to advance toward its reserved node before it HANDS THE GATHER SLOT BACK (while staying en route).
        /// Whole ticks, one second at the fixed sim rate, sourced from <see cref="SimulationLoop.TICKS_PER_SECOND"/>
        /// exactly like <see cref="STREAMING_GATE_GRACE_TICKS"/>.
        ///
        /// <para>The leak this bounds is the DW-148 × DW-207 interaction: <c>ScenarioApplier</c> deliberately only WARNS
        /// about an authored spawn inside a blocked cell and places the unit anyway, while <c>MovementSystem</c> now
        /// CONFINES such a unit — so a worker on an interior cell of a ≥3-cell-wide blocked region has its step and both
        /// wall-slide axes rejected every tick and can never close on the node whose
        /// <see cref="ResourceNodeStore.AssignedGatherers"/> slot <see cref="AssignToNode"/> already reserved. None of
        /// the four release paths enumerated above can fire for it (it never leaves MovingToResource, never dies, gets
        /// no Build order, and the DW-80 window lives past arrival), so enough stranded workers starve the node for
        /// EVERY faction — the precise starvation DW-207 was written to eliminate.</para>
        /// </summary>
        public const int WALK_STALL_GRACE_TICKS = SimulationLoop.TICKS_PER_SECOND;

        /// <summary>
        /// DW-532 — the <see cref="EntityWorld.GatherWalkStallTicks"/> sentinel meaning "this worker already gave its
        /// reservation back mid-walk". Negative so it can never be mistaken for a streak count, and so
        /// <see cref="ReleaseGatherSlot"/> can tell a holder from a yielder without a second array.
        /// </summary>
        public const int SLOT_YIELDED = -1;

        /// <summary>
        /// DW-689 — how many CONSECUTIVE ticks a worker standing down for its rally first leg (DW-634) may fail to get
        /// any CLOSER to its rally <see cref="EntityWorld.CommandGoal"/> before the leg is declared unwinnable, the
        /// one-shot <see cref="EntityWorld.RallyMovePending"/> is released and the worker rejoins the ordinary
        /// nearest-node sweep. Whole ticks, sourced from <see cref="SimulationLoop.TICKS_PER_SECOND"/> exactly like
        /// <see cref="STREAMING_GATE_GRACE_TICKS"/> and <see cref="WALK_STALL_GRACE_TICKS"/>.
        ///
        /// <para>The freeze this bounds: DW-634's stand-down releases only on ARRIVAL or when the rally's own
        /// <see cref="UnitCommand.Move"/> is superseded, and NOTHING in the simulation layer takes a rallied worker out
        /// of Move (CombatSystem's gatherer normalization rewrites every command except Move; OrderQueueSystem skips an
        /// empty queue; ClearWorkerBuild fires only from Build; the Move→Stop writers are presentation-side and gated
        /// tighter than the arrival radius). A worker rallied into a <c>PathabilityGrid</c>-blocked region is
        /// hard-stopped at the boundary by <c>MovementSystem</c>, so its arrival test can never pass and it stood in
        /// Idle for the whole match — a silent, permanent loss of its economic function.</para>
        ///
        /// <para>THREE seconds, not the one second DW-80/DW-532 use, and deliberately so: those two windows yield a
        /// node RESERVATION (cheap, self-healing, re-claimed on arrival), whereas a false positive here DISCARDS the
        /// player's explicit rally order — the very thing DW-634 exists to protect. Three seconds of zero NET progress
        /// toward the goal (the mark is the best distance reached, so cumulative ground counts and only a single raw
        /// tick of improvement anywhere in the window re-arms it) cannot be produced by a worker that is genuinely
        /// walking, including one being jostled through a crowd at the production building's door.</para>
        ///
        /// <para><b>DW-984 — that last claim is only true because the progress test reads an UNSATURATED distance.</b>
        /// It was false as first shipped: the test compared <c>FixedVec3.SqrDistance</c> values, which saturate at
        /// <see cref="Fixed.MaxValue"/> (32767.99 u² ⇒ ~181.02 units), so on any rally leg longer than 181 units EVERY
        /// tick produced the identical clamped value, <c>&lt;</c> was never true, and a worker walking at full speed
        /// across an ordinary 240–256-unit map burned the whole window and had its rally discarded — precisely the
        /// DW-634 defect this bound was written not to re-introduce. A worker covers only ~12 units in the 90 ticks, so
        /// it could not escape the clamp inside the window either. The test now runs on
        /// <c>FixedVec3.SqrDistanceRaw</c> / the <c>long</c> <see cref="EntityWorld.RallyGoalBestSqr"/> lane, which is
        /// bit-identical below the clamp and keeps counting above it. Any future rewrite of the progress test must
        /// keep that property: a "which is nearer" comparison may never be built on the saturating helper.</para>
        /// </summary>
        public const int RALLY_STANDDOWN_GRACE_TICKS = SimulationLoop.TICKS_PER_SECOND * 3;

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

                // DW-834 — STATUS GATE (stun / root), read ONCE for all four arms below. DW-619 placed this test
                // inside TickGathering only, which left the invariant it claimed ("a held worker PRODUCES NOTHING",
                // "a status can never cost it its GatherTarget") false for the other three: MovingToBase banks a whole
                // carry from inside the drop-off radius without moving, Idle claims a fresh node reservation without
                // moving, and MovingToResource accrues DW-532 walk-stall ticks against a worker that MovementSystem is
                // deliberately not integrating at all (its own DW-266 anchor), i.e. it counts a stall the grid never
                // caused. One gate above the switch is the recorded closure and is exactly equivalent to four
                // identical early returns.
                //
                // PAUSE, NOT CANCEL — the same contract DW-266 gives movement. No arm runs, so nothing transitions,
                // nothing is credited, no supply drains, no reservation is taken OR handed back, and no streak
                // advances; the worker resumes the tick the modifier expires, from the exact state it held. Deferring
                // a release (e.g. a node depleted under a held worker) is the correct half of that trade: the release
                // still fires the tick the hold ends, and death/Build-interrupt release paths are outside this loop
                // and unaffected.
                //
                // Read-only, one flag test, no new state. Every recorded golden leaves StatusFlagsOf at None for every
                // entity (the same premise MovementSystem's DW-266 anchor and DW-619's gate already rest on), so this
                // branch is never entered there and nothing folded into SimChecksum moves.
                if ((world.StatusFlagsOf[i] & GATHER_BLOCKING) != 0) continue;

                switch (world.GatherState[i])
                {
                    case GatherState.Idle:
                        TickIdle(world, i);
                        break;
                    case GatherState.MovingToResource:
                        TickMovingToResource(world, i, dt);
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
            // DW-634 — an outstanding RALLY first leg outranks the automatic sweep. A worker trained from a building
            // carrying a rally point spawns with CommandState=Move + MoveTarget/CommandGoal = the rally point and
            // EntityWorld.RallyMovePending set; until it gets there this sweep must NOT overwrite MoveTarget with the
            // nearest node, or the player's explicit rally is silently discarded on the very next tick (the defect).
            // The flag is a ONE-SHOT: cleared here the moment the leg is finished, after which the unchanged
            // nearest-node logic below runs normally — and, the worker now standing AT the rally, naturally picks the
            // node beside it (which is the mine the player rallied to). No new gather state, no rally-to-resource
            // targeting: the recorded MINIMAL shape.
            if (world.RallyMovePending[id])
            {
                // ARRIVAL is the PURE-SIM goal test OrderQueueSystem uses — SqrDistance(Position, CommandGoal) against
                // the shared ArrivalTuning radius — never EntityFlags.Moving or the presentation Move→Stop flip, both
                // of which are presentation-written and would diverge headless-golden vs live-client.
                //
                // DW-984 — read the UNSATURATED raw accumulator, not the clamped Fixed. FixedVec3.SqrMagnitude
                // saturates at Fixed.MaxValue = 32767.99 u², i.e. ~181.02 units of separation, so on a long rally leg
                // (map_bounds is 120–128 on every shipped scenario ⇒ 240–256-unit spans are ordinary) the Fixed value
                // is CONSTANT while the worker walks and the no-progress test below could never re-arm. The ARRIVAL
                // comparison is unaffected either way and stays byte-identical: the radius is 4 u² = 262144 raw, so a
                // clamped Fixed and an unclamped long both answer "outside" for every separation past 181 u, and below
                // the clamp the long IS the Fixed's .Raw, bit for bit.
                long goalSqr = FixedVec3.SqrDistanceRaw(world.Position[id], world.CommandGoal[id]);
                bool arrived  = goalSqr <= ArrivalTuning.GoalArriveRadiusSqr.Raw;
                // SUPERSEDED: any command state other than the rally's own Move means something else took the worker
                // (a Stop/Idle from CombatSystem's gatherer normalization, ClearWorkerBuild's Idle after a build, a
                // fresh player order). The rally leg is over, and gating on an arrival that may never come would park
                // the worker in Idle forever.
                if (world.CommandState[id] == UnitCommand.Move && !arrived)
                {
                    // DW-689 — THE STAND-DOWN IS BOUNDED. The escape above ("superseded") cannot fire in the SIMULATION
                    // layer: nothing there ever takes a rallied worker out of UnitCommand.Move (see
                    // RALLY_STANDDOWN_GRACE_TICKS for the full enumeration). So a rally point inside a
                    // PathabilityGrid-blocked region — where MovementSystem hard-stops the worker at the blocked-cell
                    // boundary and SqrDistance can never fall inside the arrival radius — returned here on every tick
                    // FOREVER: FindBestNode/AssignToNode unreachable, the worker never gathering again, where pre-DW-634
                    // it was re-targeted to the nearest node on the very next tick.
                    //
                    // The bound is a NO-PROGRESS test, not a timer: release only when the leg PROVABLY cannot complete.
                    // RallyGoalBestSqr holds the CLOSEST approach so far, so cumulative ground counts (a single raw tick
                    // of improvement anywhere in the window re-arms it) and separation jitter against a wall cannot keep
                    // resetting the budget — a jittering worker must beat its own best, which converges. Subsumes every
                    // stall cause at once (grid hard stop, zeroed move speed, a cleared Moving flag) with no grid probe
                    // and no dt.
                    //
                    // A counter of 0 means UNARMED — the fresh-Create/Clear state, and the state a resumed save comes
                    // back in (DW-690 persists the pending flag but not this budget) — so the window arms itself here
                    // from the worker's REAL current distance and needs no sentinel and no Array.Fill.
                    //
                    // DW-984 — this comparison is the reason goalSqr is read RAW above. It is the one place in the
                    // method that orders two SEPARATIONS against each other rather than testing one against a radius,
                    // and that is exactly what the Fixed clamp cannot do: past ~181 units every separation is
                    // Fixed.MaxValue, so `<` was false on every tick of a leg longer than that and the budget ran down
                    // against a worker that was walking normally. The mark is stored raw for the same reason.
                    if (world.RallyStandDownTicks[id] == 0 || goalSqr < world.RallyGoalBestSqr[id])
                    {
                        world.RallyGoalBestSqr[id]    = goalSqr; // new best (or the first mark) — restart the window
                        world.RallyStandDownTicks[id] = 1;
                        return;
                    }
                    if (++world.RallyStandDownTicks[id] < RALLY_STANDDOWN_GRACE_TICKS) return;
                    // Budget spent: fall THROUGH to the release below and on into the unchanged nearest-node logic, so
                    // this tick both ends the leg and re-employs the worker (no extra idle tick).
                }
                world.RallyMovePending[id] = false;
                // The leg is over however it ended (arrived / superseded / DW-689 release) — disarm the budget so a
                // later rally on this same entity starts a clean window.
                world.RallyStandDownTicks[id] = 0;
                world.RallyGoalBestSqr[id]    = 0L;
            }

            int node = FindBestNode(world.Position[id], world.FactionOf[id]);
            if (node < 0) return; // No nodes available — stay Idle

            AssignToNode(world, id, node);
        }

        private void TickMovingToResource(EntityWorld world, int id, Fixed dt)
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
            if (sqr > ARRIVE_AT_NODE_SQR)
            {
                TickWalkStall(world, id, node, dt); // DW-532 — bound the leg so a confined worker can't hold the slot forever
                return;                             // Still travelling
            }

            // Arrived at node. DW-532: a worker that yielded its reservation mid-walk (confined, then freed — the grid
            // is rebuilt on every scenario re-apply) is holding NOTHING, so it must CLAIM a slot now rather than gather
            // off the books; the node may have filled while it was stuck, in which case it re-seeks like any worker
            // whose node vanished. A never-stalled worker's counter is already 0 and this branch is skipped entirely.
            if (world.GatherWalkStallTicks[id] == SLOT_YIELDED)
            {
                if (_nodes.AssignedGatherers[node] >= _nodes.MaxGatherers[node])
                {
                    ReleaseNode(world, id);   // no capacity to re-take (a no-op on the counter — it yielded already)
                    world.GatherState[id] = GatherState.Idle;
                    return;
                }
                _nodes.AssignedGatherers[node]++;
            }
            world.GatherWalkStallTicks[id] = 0; // the walk is over either way — no streak survives into Gathering

            world.Flags[id]      &= ~EntityFlags.Moving;
            world.Velocity[id]    = FixedVec3.Zero;
            world.GatherState[id] = GatherState.Gathering;
        }

        /// <summary>
        /// DW-532 — one tick of the MovingToResource stall watch: count a tick on which this worker cannot advance
        /// toward its node AT ALL, and once the streak reaches <see cref="WALK_STALL_GRACE_TICKS"/> hand the reserved
        /// <see cref="ResourceNodeStore.AssignedGatherers"/> slot back while leaving the worker en route.
        ///
        /// <para><b>The stall test.</b> The probe is the SHARED <see cref="CheckedStep.Resolve"/> the movement
        /// integrator itself uses, applied to a FULL-SPEED, separation-free seek step toward the node: direction to the
        /// node × <see cref="EntityWorld.EffectiveMoveSpeed"/> × <paramref name="dt"/>. A result equal to the CURRENT
        /// position <b>on a step that has LENGTH</b> is the helper REFUSING it: neither the full step nor any surviving
        /// wall-slide axis displaced the mover at all (for an axis-aligned step the "surviving" perpendicular slide is
        /// itself zero-length, which is why the test is on the RESULT, not on which branch produced it).</para>
        ///
        /// <para><b>DW-805 — what this probe is NOT.</b> It is not the step <c>MovementSystem</c> integrates, and the
        /// two claims that used to be written here as a conservatism argument are both FALSE — recorded so a
        /// maintainer widening the probe does not reason from them:
        /// <list type="number">
        ///   <item>"A step hard-stopped at this length is hard-stopped at every length in that direction" runs the
        ///         WRONG WAY. <c>PathabilityGrid</c>'s sweep rejects at the FIRST foreign blocked cell, so a SHORTER
        ///         step is a strict PREFIX of this one and can resolve CLEAR exactly where the full-speed step hard
        ///         stops. Confirmed directly against <see cref="CheckedStep"/>: from a position 1.0 unit short of a
        ///         blocked band, a 1.5-unit +X step returns the origin while a 0.5-unit +X step resolves clear.</item>
        ///   <item>"The arrive-slowdown / separation terms this probe omits can only make the real step SHORTER" is
        ///         false for SEPARATION, which is an ADDED VECTOR (<c>MovementSystem</c>'s neighbour push) — it
        ///         changes the step's DIRECTION, not just its length, and it is the entire velocity whenever the seek
        ///         term is damped toward zero. Both terms are live here: <see cref="AssignToNode"/> sets MoveTarget to
        ///         the node, and this method only runs while <c>sqr &gt; ARRIVE_AT_NODE_SQR</c> (dist &gt; 1.8), so the
        ///         whole 1.8-to-4.0 band sits inside <c>MovementSystem.SLOW_RADIUS</c>'s damping.</item>
        /// </list>
        /// Also unmodelled, each of which makes a worker unable to advance for a reason that is NOT the grid:
        /// <c>MovementSystem</c>'s DW-266 status anchor and the <c>HoldPosition</c> anchor (both skip integration
        /// entirely), and DW-938's <c>Phased</c> builder. The status half no longer reaches this method — DW-834's gate
        /// above <see cref="Tick"/>'s switch skips the whole arm for a held worker — but Hold and Phased still do.</para>
        ///
        /// <para><b>Why the write is still safe.</b> The safety is NOT "the probe can only under-report"; it is that a
        /// single stall tick decides nothing. The streak must run <see cref="WALK_STALL_GRACE_TICKS"/> CONSECUTIVE
        /// ticks and ANY tick of progress resets it to 0, so a probe that reads a damped or deflected worker as stalled
        /// self-corrects the moment it closes on the true hard stop; two attempts to produce a false YIELD from the
        /// damping/separation terms alone both failed. And the consequence of a false positive is bounded by design:
        /// the worker keeps its <see cref="EntityWorld.GatherTarget"/>, keeps walking, and RE-CLAIMS a slot on arrival
        /// (<see cref="TickMovingToResource"/>) — it loses a reservation, never its assignment. A worker still shuffling
        /// inside its own blocked cell (permitted by DW-148's confinement) reads as making ground until it is pinned
        /// against the cell boundary.</para>
        ///
        /// <para><b>DW-803 — the length qualifier is load-bearing.</b> <see cref="CheckedStep.Resolve"/> returns the
        /// ORIGIN both when the mover may not move and when it never asked to, so a ZERO-LENGTH probe reads as a hard
        /// stop no matter what the grid holds (a degenerate segment short-circuits the sweep to "not blocked" and
        /// <c>Resolve</c> returns from its first branch). Anything that zeroes <see cref="EntityWorld.EffectiveMoveSpeed"/>
        /// — a snare item or granted modifier, floored at zero by <c>ModifierSystem.RecomputeEntity</c> — therefore made
        /// a worker on completely CLEAR ground yield its reservation. The explicit zero-step guard below SKIPS the probe
        /// on such a tick, leaving the streak untouched in both directions.</para>
        ///
        /// <para><b>Why the reservation is yielded rather than the worker re-idled.</b> Re-idling churns: nothing makes
        /// <see cref="FindBestNode"/> reachability-aware, so the very next <see cref="TickIdle"/> re-picks the same
        /// nearest node and re-claims the slot, leaving it occupied for all but one tick in every grace window — no fix
        /// at all. Yielding is terminal for the leg and self-heals: keeping <see cref="GatherState.MovingToResource"/>
        /// means the worker resumes and re-claims on arrival if the blocked region is ever rebuilt away.</para>
        ///
        /// <para><b>Determinism.</b> Gated on a grid with at least one blocked cell, so on a flat/legacy map (every
        /// recorded golden) this method touches nothing and the folded <c>AssignedGatherers</c> cannot move. All
        /// arithmetic is <see cref="Fixed"/>/integer through the same helper the integrator uses, so the tick it fires
        /// on is identical on every lockstep peer and every same-seed replay.</para>
        /// </summary>
        private void TickWalkStall(EntityWorld world, int id, int node, Fixed dt)
        {
            PathabilityGrid? grid = world.Pathability;
            if (grid == null || !grid.AnyBlocked) return;                    // flat/legacy map — an exact no-op
            if (world.GatherWalkStallTicks[id] == SLOT_YIELDED) return;      // already handed back — nothing left to give

            FixedVec3 pos     = world.Position[id];
            FixedVec3 desired = pos + (_nodes.Position[node] - pos).Normalized() * world.EffectiveMoveSpeed[id] * dt;

            // DW-803 — a ZERO-LENGTH step is NOT the grid's hard stop, and the probe below cannot tell the two apart.
            // CheckedStep.Resolve answers "you may not move" and "you did not ask to move" with the SAME value (the
            // origin): a degenerate segment gives PathabilityGrid.IsBlockedOnSegmentOutside col == colEnd and
            // row == rowEnd, its walk loop never runs, it returns false, and Resolve returns `desired` from its FIRST
            // not-blocked branch — the hard-stop return is never reached. Reading that as "every sweep was rejected"
            // made a worker on CLEAR ground surrender its folded AssignedGatherers reservation the moment anything
            // zeroed its speed (ModifierSystem.RecomputeEntity floors EffectiveMoveSpeed at zero, and a snare item
            // well inside ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA gets there), handing a 1-cap node to a rival
            // faction while this worker was still targeting it.
            //
            // The test is on the COMPUTED STEP, deliberately not on `speed == Fixed.Zero`: the same degenerate reading
            // fires for any speed small enough that direction × speed × dt truncates to Fixed zero.
            //
            // SKIP, don't reset. A zero-length step is NO EVIDENCE either way, so the streak is left exactly as it is:
            // advancing it is the defect, and clearing it would let a repeating snare reset a genuinely confined
            // worker's window forever and disarm DW-532 outright.
            if (desired == pos) return;

            if (CheckedStep.Resolve(grid, pos, desired) != pos)
            {
                world.GatherWalkStallTicks[id] = 0; // made ground — the streak is CONSECUTIVE, exactly like DW-80's
                return;
            }

            if (++world.GatherWalkStallTicks[id] < WALK_STALL_GRACE_TICKS) return;

            // Hand the slot back directly (NOT through ReleaseGatherSlot, which also clears GatherTarget — this worker
            // keeps its target and keeps walking). Floor-guarded like every other decrement of this counter.
            if (_nodes.Active[node] && _nodes.AssignedGatherers[node] > 0)
                _nodes.AssignedGatherers[node]--;
            world.GatherWalkStallTicks[id] = SLOT_YIELDED;
        }

        private void TickGathering(EntityWorld world, int id, Fixed dt)
        {
            // DW-619's stun/root gate used to sit HERE. DW-834 hoisted it to the Tick dispatch loop, where it covers
            // this arm and the three it was silently missing; this method is unreachable for a GATHER_BLOCKING worker.
            // No node supply drain, no CarryAmount accrual, no Streaming credit-in-place, no depletion, no DW-80
            // closed-gate streak and no state transition happen while held — same behaviour, four arms instead of one.
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
            world.GatherWalkStallTicks[workerId] = 0; // DW-532: a fresh leg starts a fresh stall streak, holding the slot just taken
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
        /// <para>DW-532 adds the one exception to "MovingToResource holds a reservation": a worker whose leg stalled out
        /// (<see cref="EntityWorld.GatherWalkStallTicks"/> == <see cref="SLOT_YIELDED"/>) is still walking but already
        /// handed its slot back, so releasing it again here would be the same double-decrement in a new disguise —
        /// a stranded worker that later dies would silently evict a live worker from the node it never reached.</para>
        ///
        /// <para>Mutates the folded <c>AssignedGatherers</c> counter, so it must stay integer-only and be reachable in
        /// the same order on every peer: <see cref="EntityWorld.Destroy"/> fires its hook synchronously, in the same
        /// deterministic sequence, before the id returns to the free list.</para>
        /// </summary>
        public static void ReleaseGatherSlot(EntityWorld world, ResourceNodeStore nodes, int workerId)
        {
            int node = world.GatherTarget[workerId];
            if (node >= 0 && HoldsGatherSlot(world.GatherState[workerId])
                && world.GatherWalkStallTicks[workerId] != SLOT_YIELDED // DW-532: a stranded worker already gave it back
                && nodes.Active[node] && nodes.AssignedGatherers[node] > 0)
                nodes.AssignedGatherers[node]--;
            world.GatherTarget[workerId]         = -1;
            world.GateClosedTicks[workerId]      = 0; // DW-80: no node, no streak
            world.GatherWalkStallTicks[workerId] = 0; // DW-532: no node, no stall streak and nothing outstanding
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
        ///
        /// <para><b>DW-984 — why the seed is a <c>long</c> and the compare reads <c>SqrDistanceRaw</c>.</b> This is a
        /// STRICT-NEAREST scan seeded at the maximum, i.e. the second shape in this file that ORDERS two separations
        /// rather than testing one against a radius, and it had the same saturation hole. <c>SqrDistance</c> clamps at
        /// <see cref="Fixed.MaxValue"/> (~181.02 units), and the seed WAS <c>Fixed.MaxValue</c> — so when every
        /// eligible node sat past 181 units, each candidate's <c>sqr</c> equalled the seed, the strict <c>&lt;</c> was
        /// false for ALL of them, and this returned -1 as though no node existed. <c>TickIdle</c> reads that as "no
        /// nodes available — stay Idle", so EVERY worker in a base whose near nodes have depleted or filled up
        /// (<c>Active</c>/<c>MaxGatherers</c> are exactly the filters that leave only distant candidates) stopped
        /// gathering permanently instead of walking to the far mine. Ordinary late-game state on a 240–256-unit map.
        /// The raw accumulator never clamps, and <c>long.MaxValue</c> is unreachable by any real separation (a
        /// full-diagonal 512-unit span is ~1.7e10), so the strict-nearest + ascending-id tie-break is unchanged and
        /// every already-working case is bit-identical.</para>
        /// </summary>
        private int FindBestNode(FixedVec3 pos, Faction faction)
        {
            int  bestNode    = -1;
            long bestSqrDist = long.MaxValue;

            for (int n = 0; n < _nodes.Count; n++)
            {
                if (!_nodes.Active[n]) continue;
                if (_nodes.CollectionModel[n] == ResourceCollectionModel.Income) continue; // never assign workers to Income nodes
                if (_nodes.AssignedGatherers[n] >= _nodes.MaxGatherers[n]) continue;
                if (!StructureGateOpen(n, faction)) continue;

                long sqr = FixedVec3.SqrDistanceRaw(pos, _nodes.Position[n]);
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
