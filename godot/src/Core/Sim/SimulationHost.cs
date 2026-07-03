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
    /// the canonical 12-system tick order (with <c>OrderQueueSystem</c> at index 3 [Story 2.12 / FR-74], then
    /// <c>AbilityCastSystem</c> at index 4 and <c>ModifierSystem</c> at index 5 — both immediately before
    /// <see cref="CombatSystem"/>; the ability-cast spine landed in Story 2.4a / FR-11, the AR-9 effective-stat
    /// recompute in Story 2.2a), the <see cref="SimulationLoop"/> it wraps, and the single checksum sink. Because it has zero Godot dependency it compiles into the Godot-free Tier-1 test
    /// project and (Story 1.9a) the headless ServerBootstrap reuses it verbatim.
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
        /// <summary>The Story 2.2b AR-9 modifier store (driven by the index-3 ModifierSystem; folded into the checksum). Exposed like <see cref="Projectiles"/> for the 2.4 ability-cast path.</summary>
        public ModifierStore Modifiers { get; }
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
            AiDifficulty aiLevel = AiDifficulty.Normal,
            AbilityRegistry? registry = null)
            => new SimulationHost(log, checksumFactions, factionDef1, factionDef2, damageTable, aiLevel, registry);

        private SimulationHost(ILogSink log, FactionRegistry checksumFactions,
            FactionDefinition? factionDef1, FactionDefinition? factionDef2,
            DamageTable? damageTable, AiDifficulty aiLevel, AbilityRegistry? registry)
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

            // AR-9 effective-stat recompute (Story 2.2a), the Story 2.2b ModifierStore it drives, and the Story 2.4a
            // ability-cast system. Construct the systems + store FIRST — the store ctor takes modSys, and
            // AbilityCastSystem takes the store — then AttachStore closes the system↔store cycle, THEN build the
            // ordered array (so abilitySys can be slotted at index 3). The store needs the same damage table / event +
            // stats sinks combat uses (null → DamageTable.Default); it subscribes World.OnDestroy += ClearEntity in its
            // ctor (recycle safety). A null registry → the Empty registry, so existing callers stay scenario-identical.
            var modSys = new ModifierSystem();
            Modifiers = new ModifierStore(World, modSys, damageTable, CombatEvents, MatchStats);
            var abilitySys = new AbilityCastSystem(registry ?? AbilityRegistry.Empty, Resources, Modifiers,
                                                   damageTable, CombatEvents, MatchStats);
            modSys.AttachStore(Modifiers);

            // Story 2.6 — wire the WHILE-ALIVE self-passive installer to the spawn seam. EntityWorld fires
            // OnUnitDefinitionApplied once per def-based spawn (after the SoA is written); the cast system installs the
            // unit's self-passive (a Persistent HoT or a permanent stat modifier) through its executor + store. One
            // closure alloc at construction (never per-tick); symmetric with the OnDestroy → ClearEntity wire. A unit
            // with no self-passive (SelfPassiveAbilityIndex = -1) is a no-op, so existing scenarios stay identical.
            World.OnUnitDefinitionApplied += id => abilitySys.InstallSelfPassive(World, id);

            // ── The canonical 12-system tick order (Story 2.12 inserted OrderQueueSystem at index 3). The
            //    registration order IS the determinism contract; SystemOrderTest FAILS on any reorder/add/remove. ──
            _systems = new ISimSystem[]
            {
                BuildSys,                                                                 // [0] BuildingSystem    (Economy)
                new GatheringSystem(Nodes, Resources, MatchStats),                        // [1] GatheringSystem   (Economy)
                new MovementSystem(),                                                     // [2] MovementSystem    (Navigation)
                // ── Story 2.12 shift-queue advance. At index 3, immediately AFTER MovementSystem so a queued
                //    movement order's arrival is detected fresh THIS tick, and BEFORE AbilityCastSystem so a popped
                //    CastAbility order fires the same tick. Pops the head of each unit's completed order and dispatches
                //    it through the shared OrderApplier.ApplyActiveOrder (no second command→state path — FR-74/AC1). ──
                new OrderQueueSystem(),                                                    // [3] OrderQueueSystem  (Core, FR-74)
                // ── Story 2.4a ability-cast spine. At index 4, immediately BEFORE ModifierSystem, so a cast that
                //    installs a buff is recomputed by ModifierSystem (index 5) and read by CombatSystem (index 6) the
                //    SAME tick. Ticks per-slot cooldowns down, consumes the pending-cast intent, runs the effect graph. ──
                abilitySys,                                                               // [4] AbilityCastSystem  (Effects, FR-11)
                // ── AR-9 effective-stat recompute. Immediately before CombatSystem, so combat & projectile-spawn
                //    damage read freshly-recomputed Effective* stats the SAME tick a modifier changes them. Drives the
                //    ModifierStore (Story 2.2b) each tick (periods/expiry) then recomputes. ──
                modSys,                                                                   // [5] ModifierSystem    (Effects, AR-9)
                // Story 2.6: the on-hit rider needs the ability registry (index→graph) + the ModifierStore (apply leaf).
                new CombatSystem(Projectiles, CombatEvents, MatchStats, damageTable,
                                 registry ?? AbilityRegistry.Empty, Modifiers, Buildings), // [6] Buildings (Story 2.9a): anti-building combat
                new ProjectileSystem(Projectiles, CombatEvents, MatchStats, damageTable,
                                     Buildings),                                          // [7] Buildings (Story 2.9a): ranged-vs-building shells
                new SupplySystem(Resources),                                              // [8] SupplySystem      (Economy)
                Fog,                                                                      // [9] FogOfWarSystem    (Core)
                new AiOpponentSystem(Buildings, Resources, BuildSys, aiLevel),            // [10] AI opponent (plays Player2)
                ScenarioDirector,                                                         // [11] ScenarioDirector — runs LAST
            };

            _loop = new SimulationLoop(World, _systems);
            _loop.EnableChecksums(Buildings, Resources, checksumFactions, Modifiers); // fold the live modifier state (v6) + ability cooldowns (v7)

            // The sim spine's only host-side log in 1.8a: a one-shot construction diagnostic through the
            // injected seam. NullLogSink no-ops it (tests/server → zero effect on the golden); GodotLogSink
            // prints it for MainScene. NEVER a per-tick log (D6).
            _log.Info("[SimulationHost] Sim spine constructed (12 systems; OrderQueueSystem at index 3, AbilityCastSystem at index 4, ModifierSystem at index 5).");
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
