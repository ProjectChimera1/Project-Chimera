#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Net-new, Godot-free composition root for the simulation (Story 1.8a / AR-6). It owns the SoA stores,
    /// the canonical 10-system tick order (with <c>ModifierSystem</c> at index 3 — immediately before
    /// <see cref="CombatSystem"/> — filled in Story 2.2a / AR-9), the <see cref="SimulationLoop"/> it
    /// wraps, and the single checksum sink. Because it has zero Godot dependency it compiles into the
    /// Godot-free Tier-1 test project and (Story 1.9a) the headless ServerBootstrap reuses it verbatim.
    ///
    /// This is a behavior-preserving extraction: the construction performed here is byte-for-byte equivalent
    /// to the former inline construction in MainScene, pinned by the byte-identical golden-checksum suite.
    /// The host <em>composes</em> the existing <see cref="SimulationLoop"/> — that file is NOT modified.
    /// </summary>
    public sealed class SimulationHost
    {
        private readonly SimulationLoop _loop;
        private readonly ILogSink _log;
        // Held so SystemOrderTest can assert the order WITHOUT reaching into the untouched SimulationLoop.
        private readonly ISimSystem[] _systems;

        // ── Stores / field-held systems, exposed so callers read host truth (no parallel copies). ──
        public EntityWorld World { get; }
        public ResourceNodeStore Nodes { get; }
        public ResourceStore Resources { get; }
        public BuildingStore Buildings { get; }
        public ProjectileStore Projectiles { get; }
        public CombatEventQueue CombatEvents { get; }
        public MatchStats MatchStats { get; }
        public BuildingSystem BuildSys { get; }
        public ScenarioDirector ScenarioDirector { get; }
        public FogOfWarSystem Fog { get; }

        // ── Loop pass-throughs (SimulationLoop is untouched; the host wraps it). ──
        public uint CurrentTick => _loop.CurrentTick;
        public uint LastChecksum => _loop.LastChecksum;
        public float InterpolationAlpha => _loop.InterpolationAlpha;
        public int ChecksumInterval { get => _loop.ChecksumInterval; set => _loop.ChecksumInterval = value; }

        /// <summary>
        /// Construct a fully-wired sim. The injected <paramref name="log"/> is the host's ONLY logging path
        /// (never GD.Print/Console). <paramref name="checksumFactions"/> is supplied by the caller — the
        /// 2-faction callers pass <c>new FactionRegistry(2)</c>, the 4-faction golden passes its own — so the
        /// host never hard-codes the faction count. <paramref name="damageTable"/> defaults to null, which the
        /// combat ctors resolve to <c>DamageTable.Default</c>; this is exactly what keeps the goldens' 3-arg
        /// CombatSystem/ProjectileSystem construction byte-identical to the host's 4-arg-with-null call.
        /// </summary>
        public static SimulationHost Create(
            ILogSink log,
            FactionRegistry checksumFactions,
            FactionDefinition? factionDef1 = null,
            FactionDefinition? factionDef2 = null,
            DamageTable? damageTable = null,
            AiDifficulty aiLevel = AiDifficulty.Normal)
            => new SimulationHost(log, checksumFactions, factionDef1, factionDef2, damageTable, aiLevel);

        private SimulationHost(ILogSink log, FactionRegistry checksumFactions,
            FactionDefinition? factionDef1, FactionDefinition? factionDef2,
            DamageTable? damageTable, AiDifficulty aiLevel)
        {
            _log = log;

            // Stores — constructed exactly as the former inline block. EntityWorld is default-seeded
            // (DEFAULT_RNG_SEED inside its ctor); NO match-seed plumbing here — that is forward-looking and
            // would move the golden. (D3/D4)
            World            = new EntityWorld();
            Nodes            = new ResourceNodeStore();
            Resources        = new ResourceStore(Fixed.Zero);
            Buildings        = new BuildingStore();
            Projectiles      = new ProjectileStore();
            CombatEvents     = new CombatEventQueue();
            MatchStats       = new MatchStats();
            Fog              = new FogOfWarSystem(Faction.Player1);
            BuildSys         = new BuildingSystem(Buildings, Resources, factionDef1, factionDef2, MatchStats);
            ScenarioDirector = new ScenarioDirector(Buildings, Resources);

            // ── The canonical 10-system tick order. The registration order IS the determinism contract;
            //    SystemOrderTest FAILS on any reorder/add/remove. ──
            _systems = new ISimSystem[]
            {
                BuildSys,                                                                 // [0] BuildingSystem   (Economy)
                new GatheringSystem(Nodes, Resources, MatchStats),                        // [1] GatheringSystem  (Economy)
                new MovementSystem(),                                                     // [2] MovementSystem   (Navigation)
                // ── AR-9 effective-stat recompute (Story 2.2a). At index 3, immediately before CombatSystem, so
                //    combat & projectile-spawn damage read freshly-recomputed Effective* stats the SAME tick a
                //    modifier changes them. A no-op until the ModifierStore (Story 2.2b) drives it. ──
                new ModifierSystem(),                                                     // [3] ModifierSystem   (Effects, AR-9)
                new CombatSystem(Projectiles, CombatEvents, MatchStats, damageTable),     // [4] (null table → DamageTable.Default)
                new ProjectileSystem(Projectiles, CombatEvents, MatchStats, damageTable), // [5] ProjectileSystem (Combat)
                new SupplySystem(Resources),                                              // [6] SupplySystem     (Economy)
                Fog,                                                                      // [7] FogOfWarSystem   (Core)
                new AiOpponentSystem(Buildings, Resources, BuildSys, aiLevel),            // [8] AI opponent (plays Player2)
                ScenarioDirector,                                                         // [9] ScenarioDirector — runs LAST
            };

            _loop = new SimulationLoop(World, _systems);
            _loop.EnableChecksums(Buildings, Resources, checksumFactions);

            // The sim spine's only host-side log in 1.8a: a one-shot construction diagnostic through the
            // injected seam. NullLogSink no-ops it (tests/server → zero effect on the golden); GodotLogSink
            // prints it for MainScene. NEVER a per-tick log (D6).
            _log.Info("[SimulationHost] Sim spine constructed (10 systems; ModifierSystem at index 3).");
        }

        /// <summary>Advance exactly one tick (lockstep / replay / golden path). Wraps SimulationLoop.StepOnce.</summary>
        public void StepOnce() => _loop.StepOnce();

        /// <summary>Advance by a real-time delta (offline free-run path). Returns the number of ticks processed.</summary>
        public int Update(float realDelta) => _loop.Update(realDelta);

        /// <summary>
        /// The SINGLE checksum-sink owner (D5). Replaces the former scattered
        /// <c>SimulationLoop.OnChecksum</c> assignments: each caller now sets the sink exactly once here.
        /// </summary>
        public void SetChecksumSink(Action<uint, uint> sink) => _loop.OnChecksum = sink;

        /// <summary>
        /// The ordered systems, for <c>SystemOrderTest</c> only. Internal: the sim source is compiled INTO
        /// the Tier-1 test assembly (and the game assembly), so the test sees this without InternalsVisibleTo.
        /// </summary>
        internal IReadOnlyList<ISimSystem> Systems => _systems;
    }
}
