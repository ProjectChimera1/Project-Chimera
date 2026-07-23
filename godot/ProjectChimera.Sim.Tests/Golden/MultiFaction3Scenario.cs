using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.2 (AC3) — a 3-active-faction determinism scenario. Constructs the checksum with
    /// <c>new FactionRegistry(3)</c> so the per-tick <see cref="SimChecksum"/> hashes Ore for Player1..Player3
    /// (ascending, via the registry) and the <see cref="ScenarioDirector"/> threshold poll spans slots 0..2.
    /// Every active faction owns ≥1 unit and a DISTINCT starting ore balance so every slot is exercised; P1's
    /// gathering worker keeps the sequence dynamic (Ore[P1] evolves every tick). Used only for the two-run
    /// in-process byte-equality assertion (no committed golden) — see <see cref="MultiFactionExpansionTests"/>.
    /// </summary>
    public static class MultiFaction3Scenario
    {
        /// <summary>300 ticks = 10s at 30 tps, ChecksumInterval = 1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        /// <summary>Construct a fresh, fully-wired 3-faction simulation. No static/shared mutable state.</summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(3),   // THREE active factions — Ore[P1..P3] hashed, threshold poll spans 0..2
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            int perturbTarget = PopulateScenario(host.World, host.Nodes, host.Buildings, host.Resources);

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, perturbTarget);
        }

        /// <summary>Populate a deterministic 3-faction scenario. Returns a stable entity id (created first → 0).</summary>
        private static int PopulateScenario(EntityWorld world, ResourceNodeStore nodes,
            BuildingStore buildings, ResourceStore resources)
        {
            // ── P3 inert unit (id 0), far out, never fights/moves (see MultiFactionScenario for the inert recipe). ──
            int p3 = world.Create(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(40)),
                                   Faction.Player3, Fixed.FromInt(50), Fixed.FromInt(3));

            // ── P1 worker (gatherer): drives Ore[P1] → the sequence evolves. ──
            int worker = world.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(4)),
                                      Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            world.GatherState[worker]   = GatherState.Idle;
            world.CarryCapacity[worker] = Fixed.FromInt(20);

            // ── P1 melee closes on P2 fodder (Movement + Combat evolve the entity hash). ──
            int p1Melee = world.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                                       Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.EffectiveAttackDamage[p1Melee] = Fixed.FromInt(10);
            world.AttackRange[p1Melee]  = Fixed.FromInt(2);
            world.AttackSpeed[p1Melee]  = Fixed.FromInt(1);
            world.DamageTypeOf[p1Melee] = DamageType.Normal;
            world.ArmorTypeOf[p1Melee]  = ArmorType.Light;
            world.MoveTarget[p1Melee]   = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);
            world.Flags[p1Melee]       |= EntityFlags.Moving;

            // ── P2 fodder (3 units) — quiet AI (< attack threshold, affords nothing); fights P1. ──
            CreateFodder(world, Faction.Player2, new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero));
            CreateFodder(world, Faction.Player2, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(3)));
            CreateFodder(world, Faction.Player2, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(-3)));

            // ── P1 resource node + deposit base → the worker completes trips (Ore[P1] moves). ──
            nodes.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(8)),
                         Fixed.FromInt(500), Fixed.FromInt(7), 3);
            int cc = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                                      Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[cc] = Fixed.Zero;
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero);

            // ── DISTINCT starting ore per active faction so every hashed slot is visible. ──
            resources.AddOre(Faction.Player1, Fixed.FromInt(200));
            resources.AddOre(Faction.Player2, Fixed.FromInt(120));
            resources.AddOre(Faction.Player3, Fixed.FromInt(150));

            return p3;
        }

        private static void CreateFodder(EntityWorld world, Faction faction, FixedVec3 pos)
        {
            int u = world.Create(pos, faction, Fixed.FromInt(80), Fixed.FromInt(3));
            world.EffectiveAttackDamage[u] = Fixed.FromInt(6);
            world.AttackRange[u]  = Fixed.FromInt(2);
            world.AttackSpeed[u]  = Fixed.FromInt(1);
            world.DamageTypeOf[u] = DamageType.Normal;
            world.ArmorTypeOf[u]  = ArmorType.Medium;
        }
    }
}
