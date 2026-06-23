using System;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Navigation;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Handles returned by <see cref="GoldenScenario.Build"/>: a freshly-constructed, fully-wired
    /// simulation ready to be stepped, with direct access to the stores for recording checksums and
    /// injecting the AC3 perturbation.
    /// </summary>
    public sealed class GoldenHarness
    {
        /// <summary>The live 9-system loop. Drive it with <see cref="SimulationLoop.StepOnce"/> (never Update).</summary>
        public SimulationLoop Loop { get; }

        /// <summary>Entity SoA world (positions/health/flags). Read for checksums; perturb for AC3.</summary>
        public EntityWorld World { get; }

        /// <summary>Building store (construction timers drive checksum evolution).</summary>
        public BuildingStore Buildings { get; }

        /// <summary>Per-faction resource balances (Ore[P1] evolves as the worker deposits).</summary>
        public ResourceStore Resources { get; }

        /// <summary>
        /// Entity id of the designated perturbation target (the worker). The worker is a gatherer
        /// (GatherState != Inactive), so CombatSystem never touches its health — a +1 raw injection
        /// therefore persists for the remainder of the run, giving AC3 a clean, permanently-divergent
        /// signal. Always equal to <see cref="GoldenScenario.PerturbTargetId"/>.
        /// </summary>
        public int PerturbTargetId { get; }

        public GoldenHarness(SimulationLoop loop, EntityWorld world,
            BuildingStore buildings, ResourceStore resources, int perturbTargetId)
        {
            Loop = loop;
            World = world;
            Buildings = buildings;
            Resources = resources;
            PerturbTargetId = perturbTargetId;
        }
    }

    /// <summary>
    /// Builds the fixed, deterministic in-code scenario that the golden-checksum harness pins.
    ///
    /// This replicates MainScene.cs:246-268 (the live 9-system tick loop) Godot-free, then populates a
    /// small synthetic world authored entirely with <see cref="Fixed"/> (no <c>Fixed.FromFloat</c>) so the
    /// recorded <see cref="SimChecksum"/> sequence is byte-identical on every run. The scenario is built
    /// IN CODE — not loaded from alpha_map_01.json — on purpose: the JSON apply path is Godot-coupled in
    /// MainScene, and a Godot-free ScenarioApplier does not exist until Story 1.8b. An in-code scenario has
    /// zero production dependencies, zero duplication of MainScene.ApplyScenario, and zero drift risk, and
    /// pins exactly what the strangler migration must preserve: the deterministic evolution of a fixed world
    /// state through the real 9 systems.
    ///
    /// TODO(1.8b): add an alpha_map_01-loaded golden + start-state hash once ScenarioApplier is Godot-free.
    /// </summary>
    public static class GoldenScenario
    {
        /// <summary>
        /// Default tick count for the golden run. 300 ticks = 10s at 30 tps; with ChecksumInterval = 1
        /// that yields 300 samples (ticks 1..300), satisfying the AC's "300+ ticks".
        /// </summary>
        public const int DefaultTicks = 300;

        /// <summary>
        /// Entity id of the worker — the AC3 perturbation target. The worker is created FIRST in
        /// <see cref="Build"/> so its id is deterministically 0 and never shifts. <see cref="Build"/>
        /// asserts this invariant so an accidental reordering fails loudly instead of silently
        /// perturbing the wrong entity.
        /// </summary>
        public const int PerturbTargetId = 0;

        /// <summary>
        /// Construct a fresh, fully-wired simulation. Allocates brand-new stores and systems on EVERY
        /// call — no static or shared mutable state — so two calls in one process are independent (AC1)
        /// and a fresh process reproduces the committed golden exactly (AC2).
        /// </summary>
        public static GoldenHarness Build()
        {
            // ── Stores (order as in MainScene.cs:246-251) ─────────────────────────
            var world        = new EntityWorld();
            var nodes        = new ResourceNodeStore();
            var resources    = new ResourceStore(Fixed.Zero); // P1 ore set below; P2 stays 0 (keeps the AI quiet)
            var buildings    = new BuildingStore();
            var projectiles  = new ProjectileStore();
            var combatEvents = new CombatEventQueue();
            var stats        = new MatchStats();
            var fog          = new FogOfWarSystem(Faction.Player1);
            var p1Def        = new FactionDefinition();
            var p2Def        = new FactionDefinition();
            var buildSys     = new BuildingSystem(buildings, resources, p1Def, p2Def, stats);
            var director     = new ScenarioDirector(buildings, resources);

            // ── Scenario population (all Fixed; no Fixed.FromFloat) ───────────────
            int workerId = PopulateScenario(world, nodes, buildings, resources);
            if (workerId != PerturbTargetId)
                throw new InvalidOperationException(
                    $"GoldenScenario invariant broken: worker id was {workerId}, expected " +
                    $"{PerturbTargetId}. The worker MUST be created first so AC3 perturbs the right entity.");

            // ── THE 9-system tick order (MainScene.cs:257-266, verbatim) ──────────
            var loop = new SimulationLoop(world,
                buildSys,                                                   // 1 BuildingSystem    (Economy)
                new GatheringSystem(nodes, resources, stats),              // 2 GatheringSystem   (Economy)
                new MovementSystem(),                                      // 3 MovementSystem    (Navigation)
                new CombatSystem(projectiles, combatEvents, stats),       // 4 CombatSystem      (Combat)
                new ProjectileSystem(projectiles, combatEvents, stats),   // 5 ProjectileSystem  (Combat)
                new SupplySystem(resources),                              // 6 SupplySystem      (Economy)
                fog,                                                       // 7 FogOfWarSystem    (Core)
                new AiOpponentSystem(buildings, resources, buildSys, AiDifficulty.Normal), // 8 AI (plays Player2)
                director);                                                 // 9 ScenarioDirector  (Core) — runs last

            loop.EnableChecksums(buildings, resources); // REQUIRED before stepping, or no checksum fires
            loop.ChecksumInterval = 1;                  // checksum EVERY tick so the located-tick is exact

            // Mirror MainScene's director lifecycle: initialize empty trigger state. ScenarioDirector.Tick
            // early-returns when there are no triggers, so this is a faithful no-op — included for fidelity
            // to the live construction (and to pin the lifecycle the 1.8 decomposition must preserve).
            director.LoadScenario(new ScenarioData());

            return new GoldenHarness(loop, world, buildings, resources, workerId);
        }

        /// <summary>
        /// Populate a fixed, deterministic scenario that exercises every system and makes the checksum
        /// evolve over time. Returns the worker's entity id (the AC3 perturbation target).
        /// </summary>
        private static int PopulateScenario(EntityWorld world, ResourceNodeStore nodes,
            BuildingStore buildings, ResourceStore resources)
        {
            // --- Worker (id 0): the AC3 perturbation target. Gathers ore -> Ore[P1] evolves; never fights. ---
            // Created FIRST so its id is a stable, documented constant (PerturbTargetId).
            int worker = world.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(4)),
                                      Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            world.GatherState[worker]   = GatherState.Idle;   // GatheringSystem picks up the node on tick 1
            world.CarryCapacity[worker] = Fixed.FromInt(20);

            // --- Player1 melee (id 1): closes on the P2 fodder and fights (Movement + Combat). ---
            int p1Melee = world.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                                       Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.AttackDamage[p1Melee] = Fixed.FromInt(10);
            world.AttackRange[p1Melee]  = Fixed.FromInt(2);   // <= 2.5 => melee
            world.AttackSpeed[p1Melee]  = Fixed.FromInt(1);
            world.DamageTypeOf[p1Melee] = DamageType.Normal;
            world.ArmorTypeOf[p1Melee]  = ArmorType.Light;
            world.MoveTarget[p1Melee]   = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);
            world.Flags[p1Melee]       |= EntityFlags.Moving; // REQUIRED for MovementSystem to move it

            // --- Player1 ranged (id 2): fires projectiles at range (Movement + Combat + Projectile). ---
            int p1Ranged = world.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.FromInt(3)),
                                        Faction.Player1, Fixed.FromInt(70), Fixed.FromInt(3));
            world.AttackDamage[p1Ranged] = Fixed.FromInt(8);
            world.AttackRange[p1Ranged]  = Fixed.FromInt(6);  // > 2.5 => ranged => spawns projectiles
            world.AttackSpeed[p1Ranged]  = Fixed.FromInt(1);
            world.DamageTypeOf[p1Ranged] = DamageType.Pierce;
            world.ArmorTypeOf[p1Ranged]  = ArmorType.Light;
            world.MoveTarget[p1Ranged]   = new FixedVec3(Fixed.FromInt(8), Fixed.Zero, Fixed.Zero);
            world.Flags[p1Ranged]       |= EntityFlags.Moving;

            // --- Player2 fodder (ids 3-5): 3 combat units. NO production building + ZERO ore keeps the AI
            //     quiet: 3 < the Normal attack threshold of 5 (so it never launches), and it can't afford to
            //     build/train. It runs (and is pinned) every tick but no-ops deterministically. ---
            CreateP2Fodder(world, new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero));
            CreateP2Fodder(world, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(3)));
            CreateP2Fodder(world, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(-3)));

            // --- Resource node for the worker (supply, gatherRate, maxGatherers). ---
            nodes.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(8)),
                         Fixed.FromInt(500), Fixed.FromInt(7), 3);

            // --- Buildings: one finished CommandCenter + one Barracks left under construction. ---
            int cc = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                                      Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[cc] = Fixed.Zero; // mark complete
            buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.FromInt(-6)),
                             Faction.Player1, BuildingType.Barracks); // stays under construction => timer ticks down

            // --- P1 deposit base so the worker completes gather trips (drives Ore[P1] => checksum moves). ---
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero);

            // --- P1 starting ore (P2 deliberately stays at 0). ---
            resources.AddOre(Faction.Player1, Fixed.FromInt(200));

            return worker;
        }

        /// <summary>Create one Player2 combat unit (fodder) at the given position.</summary>
        private static void CreateP2Fodder(EntityWorld world, FixedVec3 pos)
        {
            int u = world.Create(pos, Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
            world.AttackDamage[u] = Fixed.FromInt(6);
            world.AttackRange[u]  = Fixed.FromInt(2);
            world.AttackSpeed[u]  = Fixed.FromInt(1);
            world.DamageTypeOf[u] = DamageType.Normal;
            world.ArmorTypeOf[u]  = ArmorType.Medium;
        }
    }
}
