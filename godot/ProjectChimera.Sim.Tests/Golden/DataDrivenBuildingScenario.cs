#nullable enable
using System;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 4.1 (AC3) — the DATA-DRIVEN BUILDING scenario. Loads the REAL, on-disk <c>alpha_faction.json</c> (the
    /// file this story corrected to carry <c>construction_time</c>/<c>supply_bonus</c>/<c>produces_category</c> and
    /// baked-matching <c>hp</c>) and places its 4 showcase buildings — command_center, barracks, archery_range,
    /// siege_workshop — for Player1 via <see cref="BuildingSystem.PlaceBuildingDirect"/>, the SAME production
    /// path a real match places buildings through. Unlike every other golden scenario (built in-code, in-Fixed, with
    /// a committed golden file), this one deliberately exercises the DATA-DRIVEN <c>BuildingStore.Create()</c> path —
    /// something no existing golden touches (they all stay on the switch fallback, per this story's "Always" boundary)
    /// — so <see cref="DataDrivenBuildingGoldenTests"/> can prove ITS determinism specifically.
    /// </summary>
    public static class DataDrivenBuildingScenario
    {
        public const int DefaultTicks = 120;

        /// <summary>
        /// Build a fresh, fully-wired simulation with the 4 showcase buildings placed via the data-driven
        /// <see cref="BuildingSystem.PlaceBuildingDirect"/> path. Allocates brand-new stores/host on EVERY call (no
        /// shared/static state), matching the other golden scenarios' Build() contract.
        /// </summary>
        public static (GoldenHarness Harness, int[] BuildingIds) Build()
        {
            FactionDefinition alpha = FactionDefinition.LoadFromFile(AlphaFactionPath());

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                alpha,
                new FactionDefinition());
            host.ChecksumInterval = 1; // checksum EVERY tick so a divergence is located exactly

            var ids = new int[4];
            // command_center pre-built (operational immediately — its SupplyBonus feeds SupplySystem every tick);
            // the other three left under construction so ConstructionTimer keeps evolving the checksum over the run.
            ids[0] = host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1,
                new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero), preBuilt: true);
            ids[1] = host.BuildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1,
                new FixedVec3(Fixed.FromInt(-5), Fixed.Zero, Fixed.Zero), preBuilt: false);
            ids[2] = host.BuildSys.PlaceBuildingDirect(BuildingType.ArcheryRange, Faction.Player1,
                new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), preBuilt: false);
            ids[3] = host.BuildSys.PlaceBuildingDirect(BuildingType.SiegeWorkshop, Faction.Player1,
                new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero), preBuilt: false);

            foreach (int id in ids)
                if (id < 0) throw new InvalidOperationException("DataDrivenBuildingScenario: a showcase building failed to place.");

            host.ScenarioDirector.LoadScenario(new ScenarioData());

            return (new GoldenHarness(host, ids[0]), ids);
        }

        /// <summary>
        /// Resolve the real, on-disk <c>alpha_faction.json</c> by walking up from the test-assembly directory
        /// (mirrors <c>CanonicalScenarioTests.DataFile</c>, Story 1.10a) — this scenario deliberately exercises the
        /// SAME shipped data this story corrected, not an in-code synthetic def, so a future edit to the shipped
        /// showcase buildings is caught here too.
        /// </summary>
        private static string AlphaFactionPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "factions");
                if (Directory.Exists(candidate)) return Path.Combine(candidate, "alpha_faction.json");
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/factions above {AppContext.BaseDirectory}");
        }
    }
}
