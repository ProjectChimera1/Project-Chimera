using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;
using ProjectChimera.Sim.Tests.Effects; // AbilityTestAbilities.SelfHeal (in-code test_heal with a crystal cost)

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9b (AC1.5 / AC4.2) — the WORKER-CAST + CRYSTAL-COST golden. A single Player1 WORKER (id 0), gathering
    /// from a node, casts a Self ability costing energy + ore + CRYSTAL at tick 1 via a CastAbility UnitOrder through
    /// the shared <see cref="OrderApplier"/> — the FIRST golden whose recorded <see cref="SimChecksum"/> sequence
    /// exercises (a) a WORKER (not a combat unit) reaching the cast pipeline, and (b) a crystal debit. Over the run the
    /// checksum captures the energy+ore+crystal debit at tick 1 AND the worker's ongoing gather-deposit cycle (Ore[P1]
    /// keeps climbing for the remaining ~290 ticks) — proving in ONE deterministic run that the cast never interrupted
    /// the gather loop (AC1.4).
    ///
    /// Player2 is left EMPTY so <see cref="ProjectChimera.AI.AiOpponentSystem"/> no-ops; every hashed field is
    /// integer/<see cref="Fixed"/> → CROSS-PLATFORM SAFE, compared on BOTH CI legs (not Windows-gated). The ability
    /// slot + energy pool are wired DIRECTLY (not via <c>ApplyUnitDefinition</c>) to keep the scenario free of any
    /// float-derived value, exactly like <see cref="AbilityCastScenario"/> and <see cref="GoldenScenario"/>.
    /// </summary>
    public static class WorkerCastCrystalCostScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval=1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        /// <summary>The worker is created FIRST → id 0 (the host creates zero entities). <see cref="Build"/> asserts it.</summary>
        public const int WorkerId = 0;

        private const int CastAtStep = 0; // issue BEFORE the first StepOnce → consumed in tick 1's checksum

        /// <summary>Construct a fresh, fully-wired sim with one gathering Player1 worker holding a crystal-costed Self ability.</summary>
        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[]
            {
                AbilityTestAbilities.SelfHeal(costEnergy: 15, costOre: 15, costCrystal: 10, cooldownSec: 20, heal: 20),
            });

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),           // P1 + P2 active
                new FactionDefinition(),
                new FactionDefinition(),
                registry: registry);
            host.ChecksumInterval = 1;            // checksum every tick

            int worker = PopulateScenario(host.World, host.Nodes, host.Resources, registry);
            if (worker != WorkerId)
                throw new System.InvalidOperationException(
                    $"WorkerCastCrystalCostScenario invariant broken: worker id was {worker}, expected {WorkerId}. " +
                    "The worker MUST be created first so the tick-1 cast order addresses the right entity.");

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, worker);
        }

        /// <summary>Populate the fixed scenario (all Fixed; no Fixed.FromFloat): a gathering worker that also holds a
        /// crystal-costed Self ability, one ore node, a deposit base, and starting ore + crystal.</summary>
        private static int PopulateScenario(EntityWorld world, ResourceNodeStore nodes,
            ResourceStore resources, AbilityRegistry registry)
        {
            // Worker (id 0): gathers ore (→ Ore[P1] evolves) AND holds the crystal-costed Self ability in slot 0.
            int worker = world.Create(V(-12, 0, 4), Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            world.GatherState[worker]   = GatherState.Idle;   // GatheringSystem picks up the node on tick 1
            world.CarryCapacity[worker] = Fixed.FromInt(20);
            world.AbilityId[worker * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("test_heal");
            world.AbilityCount[worker] = 1;
            world.MaxEnergy[worker] = Fixed.FromInt(15);
            world.Energy[worker]    = Fixed.FromInt(15);       // affords exactly one cast

            nodes.Create(V(-12, 0, 8), Fixed.FromInt(500), Fixed.FromInt(7), 3);
            resources.FactionBase[(int)Faction.Player1] = V(-14, 0, 0);
            resources.AddOre(Faction.Player1, Fixed.FromInt(200));
            resources.AddCrystal(Faction.Player1, Fixed.FromInt(50));
            return worker;
        }

        /// <summary>Issue the fixed cast schedule for loop index <paramref name="i"/> (run BEFORE <c>StepOnce</c>, so the
        /// cast at index 0 is consumed in tick 1's checksum). Self cast of slot 0: slot in TargetX, target -1 (Self) in
        /// TargetZ — through the SAME shared <see cref="OrderApplier"/> live/replay/offline use (the cast spine).</summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            if (i == CastAtStep)
                OrderApplier.Apply(host.World,
                    new UnitOrder(WorkerId, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)),
                    Faction.Player1);
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
