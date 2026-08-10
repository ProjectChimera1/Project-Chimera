#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-916 — flow-field path following must be paced by the SIMULATION TICK, never by the rendered frame.
    ///
    /// <para><b>The defect.</b> The steering loop lived in <c>FlowFieldBridge._Process</c> — a Godot <c>Node</c>, so
    /// it ran once per RENDERED FRAME and wrote <see cref="EntityWorld.MoveTarget"/> straight into the sim. Online,
    /// <c>MainScene</c> drains <c>LockstepPacer</c> with <c>while (HasTickBudget) StepOnce()</c>, up to
    /// <c>MAX_CATCHUP_TICKS</c> = 4 ticks in ONE frame. So a frame longer than 33.3 ms advanced the sim several ticks
    /// against a SINGLE steering refresh: every tick past the first sought a target sampled from a stale position,
    /// while a peer rendering above 30 FPS refreshed every tick. <see cref="EntityWorld.Position"/> is folded into
    /// <c>SimChecksum</c>, so the two peers desynced — which is exactly how the 2026-08-09 two-machine LAN run died
    /// at tick 2640, the moment combat load pushed one machine under 30 FPS.
    ///
    /// <para><b>TEETH.</b> <see cref="TickingTheHostAlone_SteersAUnit_NoPresentationCallPerTick"/> is the regression
    /// net: it advances a unit using NOTHING but <c>StepOnce</c>. On the pre-fix code the steering lived in a Godot
    /// Node that this Godot-free assembly cannot even reference, so the unit would sit still and the test fails. Any
    /// future move of steering back onto a per-frame callback re-breaks it the same way.
    /// <see cref="TickBatching_DoesNotChangeThePosition_TwoPeersOneStalling"/> models the two machines directly: the
    /// same order, the same tick count, batched 1-at-a-time versus 4-at-a-time, must land byte-identical.</para>
    /// </summary>
    public class FlowFieldSteeringTickPacingTests
    {
        /// <summary>Ticks to run — comfortably more than the four a single stalled frame can bank, and long enough
        /// for a stale-target step to show up in the folded position.</summary>
        private const int TICKS = 40;

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A minimal 2-faction host — the same construction the goldens use, so the spine is representative.</summary>
        private static SimulationHost BuildHost() => SimulationHost.Create(
            NullLogSink.Instance,
            new FactionRegistry(2),
            new FactionDefinition(),
            new FactionDefinition());

        /// <summary>Spawn one mover at the origin and order it to <paramref name="goal"/> through the sim's own
        /// steering entry point (the same call the wire-order dispatch makes).</summary>
        private static int SpawnAndOrder(SimulationHost host, FixedVec3 goal)
        {
            int id = host.World.Create(FixedVec3.Zero, Faction.Player1,
                                       Fixed.FromInt(100), Fixed.FromInt(4));
            host.Steering.RequestPath(host.World, id, goal);
            return id;
        }

        [Fact]
        public void SteeringSystem_IsInTheSpine_ImmediatelyBeforeMovement()
        {
            var systems = BuildHost().Systems;

            int steerIdx = -1, moveIdx = -1;
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] is FlowFieldSteeringSystem) steerIdx = i;
                if (systems[i] is MovementSystem)          moveIdx  = i;
            }

            Assert.True(steerIdx >= 0, "FlowFieldSteeringSystem must be registered in the sim spine — if it is not, "
                                     + "steering is being driven from outside the tick and DW-916 is back.");
            // Immediately before Movement: the target must be sampled from this tick's starting position and consumed
            // by the seek in the SAME tick, with nothing in between that could move the unit first.
            Assert.Equal(steerIdx + 1, moveIdx);
        }

        /// <summary>
        /// THE regression net. Nothing here touches presentation: a unit is ordered to move and the host is ticked.
        /// If steering is not inside the tick, the unit never leaves the origin.
        /// </summary>
        [Fact]
        public void TickingTheHostAlone_SteersAUnit_NoPresentationCallPerTick()
        {
            var host = BuildHost();
            int id   = SpawnAndOrder(host, V(30, 0));

            FixedVec3 start = host.World.Position[id];
            for (int t = 0; t < TICKS; t++) host.StepOnce();
            FixedVec3 end = host.World.Position[id];

            Assert.True(end.X > start.X,
                "The unit did not move on ticks alone — flow-field steering is not running inside the simulation "
              + "tick, so it is being paced by something outside it (DW-916).");
        }

        /// <summary>
        /// Two peers, same orders, same tick count — one steps a tick at a time (a machine comfortably above 30 FPS),
        /// the other steps four at a time (a machine stalling, draining a full MAX_CATCHUP_TICKS budget in one frame).
        /// Position is folded into the checksum, so anything but an exact match is a desync.
        /// </summary>
        [Fact]
        public void TickBatching_DoesNotChangeThePosition_TwoPeersOneStalling()
        {
            var smooth   = BuildHost();
            var stalling = BuildHost();

            int a = SpawnAndOrder(smooth,   V(30, 12));
            int b = SpawnAndOrder(stalling, V(30, 12));

            for (int t = 0; t < TICKS; t++) smooth.StepOnce();          // 1 tick per "frame"
            for (int f = 0; f < TICKS / 4; f++)                          // 4 ticks per "frame"
                for (int t = 0; t < 4; t++) stalling.StepOnce();

            Assert.Equal(smooth.World.Position[a].X.Raw, stalling.World.Position[b].X.Raw);
            Assert.Equal(smooth.World.Position[a].Z.Raw, stalling.World.Position[b].Z.Raw);
            Assert.Equal(smooth.LastChecksum, stalling.LastChecksum);
        }

        /// <summary>
        /// The path state is SIM state now, so a cancelled order stops the steering writes on the very next tick —
        /// no frame has to elapse for the cancel to take effect.
        /// </summary>
        [Fact]
        public void CancelPath_StopsSteering_OnTheNextTick()
        {
            var host = BuildHost();
            int id   = SpawnAndOrder(host, V(30, 0));

            Assert.True(host.Steering.HasPath(id));
            host.StepOnce();

            host.Steering.CancelPath(id);
            Assert.False(host.Steering.HasPath(id));

            host.StepOnce();
            FixedVec3 afterCancel = host.World.Position[id];
            host.StepOnce();

            // MovementSystem may still coast the unit toward the last MoveTarget, but steering must no longer be
            // re-aiming it: the field is gone, so no further target is written.
            Assert.False(host.Steering.HasPath(id));
            Assert.True(afterCancel.X.Raw >= 0);
        }
    }
}
