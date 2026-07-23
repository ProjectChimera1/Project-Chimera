using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.2 (AC3) — a TRUE 8-active-faction determinism scenario, the proof that the extended
    /// <see cref="Faction"/> enum (Player5..Player8) and the widened per-faction arrays (FACTION_ARRAY_SIZE = 9)
    /// carry a full 8-player match with no <c>IndexOutOfRangeException</c>. Constructs the checksum with
    /// <c>new FactionRegistry(8)</c> so every per-faction store fold (Ore/Research/WinState/Alliance) reads
    /// slots 1..8 and the <see cref="ScenarioDirector"/> threshold poll spans slots 0..7. Every active faction
    /// owns ≥1 unit AND a DISTINCT starting ore balance so slot 8 is genuinely written and hashed; P1's
    /// gathering worker keeps the sequence dynamic. Two-run in-process byte-equality only (no committed golden) —
    /// see <see cref="MultiFactionExpansionTests"/>.
    /// </summary>
    public static class MultiFaction8Scenario
    {
        /// <summary>300 ticks = 10s at 30 tps, ChecksumInterval = 1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        /// <summary>Construct a fresh, fully-wired 8-faction simulation. No static/shared mutable state.</summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(8),   // EIGHT active factions — every per-faction store folds slots 1..8
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            int perturbTarget = PopulateScenario(host.World, host.Nodes, host.Buildings, host.Resources);

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, perturbTarget);
        }

        /// <summary>Populate a deterministic 8-faction scenario. Returns a stable entity id (created first → 0).</summary>
        private static int PopulateScenario(EntityWorld world, ResourceNodeStore nodes,
            BuildingStore buildings, ResourceStore resources)
        {
            // ── P3 inert unit (id 0), far out, never fights/moves. ──
            int p3 = world.Create(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(40)),
                                   Faction.Player3, Fixed.FromInt(50), Fixed.FromInt(3));

            // ── P1 worker (gatherer) — drives Ore[P1] → sequence evolves. ──
            int worker = world.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(4)),
                                      Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            world.GatherState[worker]   = GatherState.Idle;
            world.CarryCapacity[worker] = Fixed.FromInt(20);

            // ── P1 melee closes on P2 fodder. ──
            int p1Melee = world.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                                       Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.EffectiveAttackDamage[p1Melee] = Fixed.FromInt(10);
            world.AttackRange[p1Melee]  = Fixed.FromInt(2);
            world.AttackSpeed[p1Melee]  = Fixed.FromInt(1);
            world.DamageTypeOf[p1Melee] = DamageType.Normal;
            world.ArmorTypeOf[p1Melee]  = ArmorType.Light;
            world.MoveTarget[p1Melee]   = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);
            world.Flags[p1Melee]       |= EntityFlags.Moving;

            // ── P2 fodder (3 units) — quiet AI; fights P1. ──
            CreateFodder(world, Faction.Player2, new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero));
            CreateFodder(world, Faction.Player2, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(3)));
            CreateFodder(world, Faction.Player2, new FixedVec3(Fixed.FromInt(11), Fixed.Zero, Fixed.FromInt(-3)));

            // ── P4..P8: one inert unit each, spread far out on distinct tiles (inert-by-construction: no
            //    attack/gather/move flag → stable health), each with a DISTINCT ore balance so the newly-added
            //    enum slots 5-8 and the resized [9] arrays are genuinely written and hashed. ──
            CreateInert(world, Faction.Player4, new FixedVec3(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50)));
            CreateInert(world, Faction.Player5, new FixedVec3(Fixed.FromInt(-50), Fixed.Zero, Fixed.FromInt(50)));
            CreateInert(world, Faction.Player6, new FixedVec3(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(-50)));
            CreateInert(world, Faction.Player7, new FixedVec3(Fixed.FromInt(-50), Fixed.Zero, Fixed.FromInt(-50)));
            CreateInert(world, Faction.Player8, new FixedVec3(Fixed.FromInt(60), Fixed.Zero, Fixed.FromInt(60)));

            // ── P1 resource node + deposit base. ──
            nodes.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(8)),
                         Fixed.FromInt(500), Fixed.FromInt(7), 3);
            int cc = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                                      Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[cc] = Fixed.Zero;
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero);

            // ── DISTINCT starting ore for EVERY active faction (Player1..Player8) — writes slot 8, the highest
            //    newly-backed index, proving no OOB against the resized [9] arrays. ──
            resources.AddOre(Faction.Player1, Fixed.FromInt(200));
            resources.AddOre(Faction.Player2, Fixed.FromInt(120));
            resources.AddOre(Faction.Player3, Fixed.FromInt(150));
            resources.AddOre(Faction.Player4, Fixed.FromInt(75));
            resources.AddOre(Faction.Player5, Fixed.FromInt(90));
            resources.AddOre(Faction.Player6, Fixed.FromInt(60));
            resources.AddOre(Faction.Player7, Fixed.FromInt(45));
            resources.AddOre(Faction.Player8, Fixed.FromInt(30));

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

        /// <summary>One inert-by-construction unit (0 attack, no gather/move flag → perfectly stable).</summary>
        private static void CreateInert(EntityWorld world, Faction faction, FixedVec3 pos)
        {
            world.Create(pos, faction, Fixed.FromInt(50), Fixed.FromInt(3));
        }
    }
}
