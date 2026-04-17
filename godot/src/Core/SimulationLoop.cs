using System;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Interface for systems that run each simulation tick.
    /// </summary>
    public interface ISimSystem
    {
        /// <summary>Process one simulation tick. dt is the fixed timestep in Fixed.</summary>
        void Tick(EntityWorld world, Fixed dt);
    }

    /// <summary>
    /// Fixed-timestep simulation loop running at 30 ticks/sec.
    /// Accumulates real time and steps the simulation in fixed increments.
    /// Pure C# — no Godot dependency.
    /// </summary>
    public class SimulationLoop
    {
        public const int TICKS_PER_SECOND = 30;

        /// <summary>Fixed timestep as a Fixed value: 1/30 ≈ 0.03333</summary>
        public static readonly Fixed FixedDt = Fixed.FromRaw(Fixed.ONE / TICKS_PER_SECOND); // ~2184 raw

        /// <summary>Fixed timestep in seconds (float, for accumulator math).</summary>
        public const float DT_SECONDS = 1.0f / TICKS_PER_SECOND;

        /// <summary>
        /// How often to compute a checksum, in ticks (0 = disabled).
        /// Default: 60 ticks (every 2 seconds at 30 tps).
        /// </summary>
        public int ChecksumInterval { get; set; } = 60;

        /// <summary>The most recently computed world-state checksum (0 if never computed).</summary>
        public uint LastChecksum { get; private set; }

        /// <summary>
        /// Fired after each checksum computation: (tick, checksum).
        /// Wire this in MainScene to log or compare with the remote peer.
        /// </summary>
        public Action<uint, uint>? OnChecksum;

        public EntityWorld World { get; }

        /// <summary>Current simulation tick number.</summary>
        public uint CurrentTick { get; private set; }

        /// <summary>
        /// Interpolation alpha: how far between prev tick and current tick we are.
        /// Range [0, 1). Used by the presentation layer for smooth rendering.
        /// </summary>
        public float InterpolationAlpha { get; private set; }

        private readonly ISimSystem[] _systems;
        private float _accumulator;

        // Optional stores for checksum — set via EnableChecksums()
        private BuildingStore?  _checksumBuildings;
        private ResourceStore?  _checksumResources;

        public SimulationLoop(EntityWorld world, params ISimSystem[] systems)
        {
            World = world;
            _systems = systems;
            _accumulator = 0f;
            CurrentTick = 0;
        }

        /// <summary>
        /// Provide building and resource stores so the sim loop can include them in checksums.
        /// Call once after construction, before the first Update().
        /// </summary>
        public void EnableChecksums(BuildingStore buildings, ResourceStore resources)
        {
            _checksumBuildings = buildings;
            _checksumResources = resources;
        }

        /// <summary>
        /// Advance exactly one simulation tick, bypassing the accumulator.
        /// Used by LockstepManager in online mode — tick advancement is
        /// gated on both peers' commands arriving, not wall-clock time.
        /// </summary>
        public void StepOnce()
        {
            World.SnapshotPositions();

            for (int i = 0; i < _systems.Length; i++)
                _systems[i].Tick(World, FixedDt);

            CurrentTick++;
            InterpolationAlpha = 0f;

            if (ChecksumInterval > 0 && CurrentTick % (uint)ChecksumInterval == 0
                && _checksumBuildings != null && _checksumResources != null)
            {
                LastChecksum = SimChecksum.Compute(World, _checksumBuildings, _checksumResources);
                OnChecksum?.Invoke(CurrentTick, LastChecksum);
            }
        }

        /// <summary>
        /// Advance the simulation by the given real-time delta (in seconds).
        /// May step 0, 1, or multiple ticks depending on accumulated time.
        /// Returns the number of ticks that were processed.
        /// </summary>
        public int Update(float realDelta)
        {
            // Clamp to prevent spiral of death (e.g., after breakpoint or tab-out)
            if (realDelta > 0.25f) realDelta = 0.25f;

            _accumulator += realDelta;
            int ticksProcessed = 0;

            while (_accumulator >= DT_SECONDS)
            {
                // Snapshot positions for interpolation before simulating
                World.SnapshotPositions();

                // Run all systems in order
                for (int i = 0; i < _systems.Length; i++)
                {
                    _systems[i].Tick(World, FixedDt);
                }

                _accumulator -= DT_SECONDS;
                CurrentTick++;
                ticksProcessed++;

                // Checksum every N ticks for desync detection (P2.4)
                if (ChecksumInterval > 0 && CurrentTick % (uint)ChecksumInterval == 0
                    && _checksumBuildings != null && _checksumResources != null)
                {
                    LastChecksum = SimChecksum.Compute(World, _checksumBuildings, _checksumResources);
                    OnChecksum?.Invoke(CurrentTick, LastChecksum);
                }
            }

            InterpolationAlpha = _accumulator / DT_SECONDS;
            return ticksProcessed;
        }
    }
}
