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
    /// Builds the 4-active-faction golden scenario for Story 1.3a — the span-path counterpart to the
    /// 2-faction <see cref="GoldenScenario"/>. It runs the SAME Godot-free 9-system loop, but constructs the
    /// loop's checksum with <c>new FactionRegistry(4)</c> so the per-tick <see cref="SimChecksum"/> hashes
    /// Ore for Player1..Player4 (ascending, via the registry) — proving the registry's active-faction loop
    /// genuinely spans more than two factions and stays byte-deterministic.
    ///
    /// Why 4 factions: it spans the full current <see cref="Faction"/> enum (Player1..Player4), which already
    /// fits the size-5 store arrays with zero resizing (resizing to 8 is Story 9.2, NOT this story).
    ///
    /// Faction roles:
    ///   • P1 — a gathering worker (drives Ore[P1]) + a melee unit + a CommandCenter + a Barracks left under
    ///     construction. This is what makes the golden DYNAMIC (ore, entity health/position, and the building
    ///     construction timer all evolve every tick).
    ///   • P2 — three fodder, 0 ore, no production building. Identical recipe to 1.2: the AI plays Player2 and
    ///     stays quiet (3 units &lt; its attack threshold, and it can afford nothing).
    ///   • P3 / P4 — one inert unit each, far out, with a DISTINCT constant ore balance. Their ore never moves
    ///     but IS hashed (the point of the span proof); their health is stable so the AC3 perturbation lands
    ///     cleanly (see the inert-unit note on <see cref="PopulateScenario"/>).
    /// </summary>
    public static class MultiFactionScenario
    {
        /// <summary>
        /// Default tick count for the multi-faction golden run. 300 ticks = 10s at 30 tps; with
        /// ChecksumInterval = 1 that yields 300 samples (ticks 1..300), matching the AC's "300+ ticks".
        /// </summary>
        public const int DefaultTicks = 300;

        /// <summary>
        /// Entity id of the AC3 perturbation target — the Player3 inert unit. It is created FIRST in
        /// <see cref="Build"/> so its id is deterministically 0 and never shifts; <see cref="Build"/> asserts
        /// this invariant so an accidental reordering fails loudly instead of perturbing the wrong entity.
        /// (Mirrors <see cref="GoldenScenario.PerturbTargetId"/>, but here the target is a faction-3 entity,
        /// as AC3 requires the drift guard to be proven on the multi-faction scenario.)
        /// </summary>
        public const int PerturbTargetId = 0;

        /// <summary>
        /// Construct a fresh, fully-wired 4-faction simulation. Allocates brand-new stores and systems on
        /// EVERY call — no static or shared mutable state — so two calls in one process are independent and a
        /// fresh process reproduces the committed golden exactly.
        /// </summary>
        public static GoldenHarness Build()
        {
            // ── Stores + systems (same construction as GoldenScenario / MainScene) ─────────────
            var world        = new EntityWorld();
            var nodes        = new ResourceNodeStore();
            var resources    = new ResourceStore(Fixed.Zero); // per-faction ore set below; P2 stays 0
            var buildings    = new BuildingStore();
            var projectiles  = new ProjectileStore();
            var combatEvents = new CombatEventQueue();
            var stats        = new MatchStats();
            var fog          = new FogOfWarSystem(Faction.Player1);
            var p1Def        = new FactionDefinition();
            var p2Def        = new FactionDefinition();
            var buildSys     = new BuildingSystem(buildings, resources, p1Def, p2Def, stats);
            var director     = new ScenarioDirector(buildings, resources);

            // ── Scenario population (all Fixed; no Fixed.FromFloat) ───────────────────────────
            int p3Target = PopulateScenario(world, nodes, buildings, resources);
            if (p3Target != PerturbTargetId)
                throw new InvalidOperationException(
                    $"MultiFactionScenario invariant broken: the P3 perturb target id was {p3Target}, " +
                    $"expected {PerturbTargetId}. It MUST be created first so AC3 perturbs the right " +
                    $"faction-3 entity.");

            // ── THE 9-system tick order (verbatim from GoldenScenario / MainScene) ────────────
            var loop = new SimulationLoop(world,
                buildSys,                                                   // 1 BuildingSystem    (Economy)
                new GatheringSystem(nodes, resources, stats),              // 2 GatheringSystem   (Economy)
                new MovementSystem(),                                      // 3 MovementSystem    (Navigation)
                new CombatSystem(projectiles, combatEvents, stats),       // 4 CombatSystem      (Combat)
                new ProjectileSystem(projectiles, combatEvents, stats),   // 5 ProjectileSystem  (Combat)
                new SupplySystem(resources),                              // 6 SupplySystem      (Economy)
                fog,                                                       // 7 FogOfWarSystem    (Core)
                new AiOpponentSystem(buildings, resources, buildSys, AiDifficulty.Normal), // 8 AI (plays Player2)
                director);                                                 // 9 ScenarioDirector  (Core) — last

            // FOUR active factions — the whole point: the checksum's faction loop now spans Ore[P1..P4].
            loop.EnableChecksums(buildings, resources, new FactionRegistry(4));
            loop.ChecksumInterval = 1; // checksum EVERY tick so the located-tick is exact

            // Mirror MainScene's director lifecycle (no-op with empty triggers; pins the lifecycle for 1.8).
            director.LoadScenario(new ScenarioData());

            return new GoldenHarness(loop, world, buildings, resources, p3Target);
        }

        /// <summary>
        /// Populate a fixed, deterministic 4-faction scenario. Returns the Player3 inert unit's entity id
        /// (the AC3 perturbation target), which — being created FIRST — is the stable constant
        /// <see cref="PerturbTargetId"/> (0).
        /// </summary>
        private static int PopulateScenario(EntityWorld world, ResourceNodeStore nodes,
            BuildingStore buildings, ResourceStore resources)
        {
            // ── P3 inert perturb target (id 0): created FIRST so its id is a stable, asserted constant. ──
            // It is INERT BY CONSTRUCTION, so its health is perfectly stable until the AC3 +1 injection:
            //   • AttackDamage stays 0  → CombatSystem skips it at the non-combatant guard (never attacks,
            //     and the gather/command branches never run for it).
            //   • GatherState stays Inactive → GatheringSystem skips it (no node-seeking, no wandering — the
            //     reason it is NOT a GatherState.Idle gatherer: a gatherer would path to P1's shared node and
            //     drift across the map; this is the story's "truly inert" option, robust by construction).
            //   • No Moving flag, and no other unit within MovementSystem's 2.0 separation radius → zero
            //     steering force → it never moves.
            //   • Placed far out (x=40, z=40 ≈ 49u from the P1↔P2 fight): even a post-fight global chase
            //     cannot cross the gap within the run (speed 3 u/s × 10 s ≈ 30u max travel).
            // Result: a faction-3 entity whose Fixed health is stable, so the AC3 +1 persists and is located
            // at exactly tick K+1. Its position+health ARE hashed by the (faction-agnostic) entity loop.
            int p3Target = world.Create(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(40)),
                                        Faction.Player3, Fixed.FromInt(50), Fixed.FromInt(3));

            // ── P1 worker (gatherer): gathers ore → Ore[P1] evolves → checksum is dynamic. Never fights. ──
            int worker = world.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(4)),
                                      Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            world.GatherState[worker]   = GatherState.Idle;   // GatheringSystem picks up the node on tick 1
            world.CarryCapacity[worker] = Fixed.FromInt(20);

            // ── P1 melee: closes on the P2 fodder and fights (Movement + Combat evolve the entity hash). ──
            int p1Melee = world.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                                       Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.AttackDamage[p1Melee] = Fixed.FromInt(10);
            world.AttackRange[p1Melee]  = Fixed.FromInt(2);   // <= 2.5 => melee
            world.AttackSpeed[p1Melee]  = Fixed.FromInt(1);
            world.DamageTypeOf[p1Melee] = DamageType.Normal;
            world.ArmorTypeOf[p1Melee]  = ArmorType.Light;
            world.MoveTarget[p1Melee]   = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);
            world.Flags[p1Melee]       |= EntityFlags.Moving; // REQUIRED for MovementSystem to move it

            // ── P2 fodder (3 units): 0 ore + NO production building keeps the AI quiet (same recipe as 1.2:
            //    3 < the Normal attack threshold of 5, and it can afford nothing). They fight P1 every tick. ──
            CreateP2Fodder(world, new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero));
            CreateP2Fodder(world, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(3)));
            CreateP2Fodder(world, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(-3)));

            // ── P4 inert unit: same inert-by-construction recipe as the P3 target, even further out. Its ore
            //    is constant but IS hashed under FactionRegistry(4) (the span proof). NOT the perturb target. ──
            world.Create(new FixedVec3(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50)),
                         Faction.Player4, Fixed.FromInt(50), Fixed.FromInt(3));

            // ── Resource node for the P1 worker (supply, gatherRate, maxGatherers). ──
            nodes.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(8)),
                         Fixed.FromInt(500), Fixed.FromInt(7), 3);

            // ── P1 buildings: one finished CommandCenter + one Barracks left under construction (its timer
            //    ticks down every tick → the building loop evolves too). ──
            int cc = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                                      Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[cc] = Fixed.Zero; // mark complete
            buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.FromInt(-6)),
                             Faction.Player1, BuildingType.Barracks); // stays under construction => timer ticks

            // ── P1 deposit base so the worker completes gather trips (drives Ore[P1] => checksum moves). ──
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero);

            // ── Per-faction starting ore — DISTINCT values so the active-faction span is visible in the hash.
            //    P1 evolves (worker deposits); P3/P4 stay constant (no node/base) but are hashed; P2 stays 0. ──
            resources.AddOre(Faction.Player1, Fixed.FromInt(200));
            resources.AddOre(Faction.Player3, Fixed.FromInt(150));
            resources.AddOre(Faction.Player4, Fixed.FromInt(75));

            return p3Target;
        }

        /// <summary>Create one Player2 combat unit (fodder) at the given position. Identical to 1.2's recipe.</summary>
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
