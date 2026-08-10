#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Navigation;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presentation-side adapter for flow-field pathfinding: converts a Godot <see cref="Vector3"/> destination into
    /// the sim's <see cref="FixedVec3"/> and hands the order to <see cref="FlowFieldSteeringSystem"/>. It holds NO
    /// path state and writes NO simulation state of its own.
    ///
    /// <para><b>DW-916 — why this class is now three delegating methods.</b> It used to own the per-entity flow
    /// fields and do the steering itself in <c>_Process</c>, i.e. once per RENDERED FRAME, writing
    /// <c>MoveTarget</c>/<c>Flags</c>/<c>CommandState</c> straight into <see cref="EntityWorld"/>. The online sim
    /// steps off <c>LockstepPacer</c>, which may drain up to <c>MAX_CATCHUP_TICKS</c> = 4 ticks inside ONE frame, so
    /// every frame longer than 33.3 ms advanced the sim several ticks against a single steering refresh — the ticks
    /// past the first steered toward a target sampled at a stale position. A peer rendering faster refreshed every
    /// tick, took a different step, and the folded <c>Position</c> diverged: the GLOBAL DESYNC that ended the
    /// 2026-08-09 LAN run at tick 2640, the moment combat load pushed one machine under 30 FPS. The steering loop now
    /// lives in the sim spine at index 3 (immediately before <c>MovementSystem</c>) where it runs exactly once per
    /// tick on every peer and in replay.</para>
    ///
    /// <para>The public API is unchanged, so the <c>LockstepManager</c>/<c>ReplayPlayer</c>/<c>SelectionSystem</c>
    /// wiring that calls <see cref="RequestPath"/>/<see cref="RequestAttackMove"/>/<see cref="CancelPath"/> is
    /// untouched. This stays a <see cref="Node"/> only because the bootstrap parents it into the scene; it no longer
    /// implements <c>_Process</c>, and nothing about a frame can reach the simulation through it.</para>
    /// </summary>
    public partial class FlowFieldBridge : Node
    {
        private EntityWorld?              _world;
        private FlowFieldSteeringSystem?  _steering;

        /// <summary>
        /// Bind the adapter to the entity world and the sim's steering system. Called by the FlowFieldInit phase
        /// once the scenario's buildings are placed (the phase also seeds the obstacle map and the steering
        /// system's building baseline).
        /// </summary>
        public void Initialize(EntityWorld world, FlowFieldSteeringSystem steering)
        {
            _world    = world;
            _steering = steering;
        }

        /// <summary>
        /// Issue a Move command to <paramref name="entityId"/> toward <paramref name="destination"/>.
        /// The unit ignores enemies en route.
        /// </summary>
        public void RequestPath(int entityId, Vector3 destination)
        {
            if (_world == null || _steering == null) return;
            _steering.RequestPath(_world, entityId, ToSim(destination));
        }

        /// <summary>
        /// Issue an AttackMove command to <paramref name="entityId"/> toward <paramref name="destination"/>.
        /// The unit attacks enemies encountered in range and resumes toward the goal after each kill.
        /// </summary>
        public void RequestAttackMove(int entityId, Vector3 destination)
        {
            if (_world == null || _steering == null) return;
            _steering.RequestAttackMove(_world, entityId, ToSim(destination));
        }

        /// <summary>
        /// Cancel the active path for <paramref name="entityId"/>.
        /// Does not change CommandState — the caller owns that transition.
        /// </summary>
        public void CancelPath(int entityId) => _steering?.CancelPath(entityId);

        /// <summary>
        /// The float→<see cref="Fixed"/> conversion at the presentation boundary — the ONLY float in the path, and
        /// it happens once per issued order (never per tick), exactly as it did pre-DW-916. Y is discarded: the flow
        /// field is a 2-D grid over X/Z.
        /// </summary>
        private static FixedVec3 ToSim(Vector3 destination) =>
            new FixedVec3(Fixed.FromFloat(destination.X), Fixed.Zero, Fixed.FromFloat(destination.Z));
    }
}
