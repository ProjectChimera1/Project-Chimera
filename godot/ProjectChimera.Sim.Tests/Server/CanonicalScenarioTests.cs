#nullable enable
using System;
using System.IO;
using System.Linq;
using ProjectChimera.Core;              // Faction
using ProjectChimera.Core.Definitions;  // ScenarioData, ScenarioSerializer, ScenarioValidator, FactionDefinition
using ProjectChimera.Core.Sim;          // ServerBootstrap, SimulationHost, NullLogSink
using ProjectChimera.Sim.Tests.Golden;  // GoldenChecksumReplay, GoldenHarness
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 1.9b (AC1/AC4) — the canonical "P2.4" two-machine LAN-determinism scenario(s) must be VALID + shippable
    /// (so a real match builds a validated sim spine, not the relay-only fallback) and DETERMINISTIC through the real
    /// <see cref="ServerBootstrap"/> path (so two machines running it agree). Guards the on-disk 2-player maps the
    /// runbook pins: <c>map_02_iron_crossing</c> (canonical P2.4, D4) + <c>alpha_map_01</c> (the
    /// <c>MainScene.ScenarioPath</c> export default — the zero-config fallback). These are the actual shipped JSON
    /// files, not an in-code mirror, so a broken/invalid canonical scenario fails here before the two-machine run.
    /// </summary>
    public class CanonicalScenarioTests
    {
        private const string P2_4_Scenario   = "map_02_iron_crossing.json"; // pinned canonical (Story 1.9b D4)
        private const string DefaultScenario = "alpha_map_01.json";         // MainScene.ScenarioPath export default

        [Theory]
        [InlineData(P2_4_Scenario)]
        [InlineData(DefaultScenario)]
        public void CanonicalScenario_LoadsAndValidates(string fileName)
        {
            string path = DataFile("scenarios", fileName);
            Assert.True(File.Exists(path), $"scenario file missing: {path}");

            ScenarioData? model = ScenarioSerializer.LoadFromFile(path);
            Assert.NotNull(model);   // loads + parses, else the server falls back to relay + quorum only

            ValidationResult result = new ScenarioValidator().Validate(model!);
            Assert.True(result.Ok, $"{fileName} failed validation: {result.Error}");
        }

        [Fact]
        public void P2_4_Scenario_IsDeterministic_AcrossTwoServerBootstrapBuilds()
        {
            var a = GoldenChecksumReplay.RunAndRecord(300, build: BuildP2_4ViaServerBootstrap);
            var b = GoldenChecksumReplay.RunAndRecord(300, build: BuildP2_4ViaServerBootstrap);

            Assert.True(a.Count >= 300, $"expected >= 300 checksum samples, got {a.Count}");
            Assert.True(a.SequenceEqual(b),
                "Two ServerBootstrap runs of the canonical P2.4 scenario diverged — a determinism leak.");
        }

        /// <summary>Build a sim host from the on-disk canonical P2.4 scenario + its alpha/beta factions, via the real ServerBootstrap.</summary>
        private static GoldenHarness BuildP2_4ViaServerBootstrap()
        {
            ScenarioData? model = ScenarioSerializer.LoadFromFile(DataFile("scenarios", P2_4_Scenario));
            Assert.NotNull(model);

            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = FactionDefinition.LoadFromFile(DataFile("factions", "alpha_faction.json"));
            slotDefs[(int)Faction.Player2] = FactionDefinition.LoadFromFile(DataFile("factions", "beta_faction.json"));

            SimulationHost? host = ServerBootstrap.Build(model!, slotDefs, damageTable: null,
                NullLogSink.Instance, activeFactionCount: 2);
            Assert.NotNull(host);   // map_02 is valid → fail-closed must NOT trip
            host!.ChecksumInterval = 1;
            return new GoldenHarness(host, 0);
        }

        /// <summary>
        /// Resolve a file under <c>godot/resources/data/&lt;sub&gt;/</c> by walking up from the test-assembly
        /// directory (robust to the bin/Debug/net8.0 depth and to the future CI working dir — Story 1.10a).
        /// </summary>
        private static string DataFile(string sub, string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return Path.Combine(candidate, fileName);
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }
    }
}
