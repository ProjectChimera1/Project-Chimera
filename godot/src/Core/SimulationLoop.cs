#nullable enable
using System;
using ProjectChimera.Dsl;     // DslVarTable (Story 7.3 checksum dep)
using ProjectChimera.Effects; // ModifierStore (Story 2.2b checksum dep)

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
        private FactionRegistry? _checksumFactions;
        private ModifierStore?   _checksumModifiers; // Story 2.2b (Option B): folds active modifier state (null ≡ empty)
        private HeroStore?       _checksumHeroes;    // Story 3.13: folds mutable HeroStore state (Level/Xp/growth) (null ≡ empty)
        private ItemStore?       _checksumItems;     // Story 3.15: folds mutable ItemStore + per-hero inventory (null ≡ empty)
        private ResourceNodeStore? _checksumNodes;   // Story 4.7: folds mutable ResourceNodeStore state (first-ever fold) (null ≡ empty)
        private ResearchStore?   _checksumResearch;  // Story 4.10: folds mutable ResearchStore state (first-ever fold) (null ≡ empty)
        private DslVarTable?     _checksumVars;      // Story 7.3: folds live DSL variables + timers (first-ever fold) (null ≡ empty)
        private DslEventQueue?   _checksumDslEvents; // Story 7.5: folds pending next-tick custom events (first-ever fold) (null ≡ empty)

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
        public void EnableChecksums(BuildingStore buildings, ResourceStore resources, FactionRegistry factions,
                                    ModifierStore? modifiers = null, HeroStore? heroes = null, ItemStore? items = null,
                                    ResourceNodeStore? nodes = null, ResearchStore? research = null,
                                    DslVarTable? vars = null, DslEventQueue? dslEvents = null)
        {
            _checksumBuildings = buildings;
            _checksumResources = resources;
            _checksumFactions  = factions;
            _checksumModifiers = modifiers; // Story 2.2b — folds the live store's active modifier state (null ≡ empty)
            _checksumHeroes    = heroes;    // Story 3.13 — folds the live store's mutable hero state (null ≡ empty)
            _checksumItems     = items;     // Story 3.15 — folds the live ItemStore + per-hero inventory (null ≡ empty)
            _checksumNodes     = nodes;     // Story 4.7 — folds the live ResourceNodeStore mutable state (null ≡ empty)
            _checksumResearch  = research;  // Story 4.10 — folds the live ResearchStore mutable state (null ≡ empty)
            _checksumVars      = vars;      // Story 7.3 — folds the live DslVarTable (variables + timers) (null ≡ empty)
            _checksumDslEvents = dslEvents; // Story 7.5 — folds the pending next-tick custom-event queue (null ≡ empty)
        }

        /// <summary>
        /// Story 3.10 (UX-DR62): reset the tick counter, last checksum, accumulator, and interpolation alpha to their
        /// post-construction values for the in-place Edit↔Play reset — WITHOUT disturbing the <see cref="EnableChecksums"/>
        /// store wiring (those refs are host-lifetime and stay valid across a reset). After this a re-applied world
        /// steps from tick 0 with a fresh checksum, reproducing a from-boot run byte-for-byte.
        /// </summary>
        public void ResetTick()
        {
            CurrentTick        = 0;
            LastChecksum       = 0;
            _accumulator       = 0f;
            InterpolationAlpha = 0f;
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
                && _checksumBuildings != null && _checksumResources != null && _checksumFactions != null)
            {
                LastChecksum = SimChecksum.Compute(World, _checksumBuildings, _checksumResources, _checksumFactions, _checksumModifiers, _checksumHeroes, _checksumItems, _checksumNodes, _checksumResearch, _checksumVars, _checksumDslEvents);
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
                    && _checksumBuildings != null && _checksumResources != null && _checksumFactions != null)
                {
                    LastChecksum = SimChecksum.Compute(World, _checksumBuildings, _checksumResources, _checksumFactions, _checksumModifiers, _checksumHeroes, _checksumItems, _checksumNodes, _checksumResearch, _checksumVars, _checksumDslEvents);
                    OnChecksum?.Invoke(CurrentTick, LastChecksum);
                }
            }

            InterpolationAlpha = _accumulator / DT_SECONDS;
            return ticksProcessed;
        }
    }
}
