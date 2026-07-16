#nullable enable
using System;
using System.IO;
using System.Linq;
using ProjectChimera.Core;              // Faction
using ProjectChimera.Core.Definitions;  // ScenarioData, ScenarioSerializer, ScenarioValidator, FactionDefinition
using ProjectChimera.Core.Sim;          // ServerBootstrap, SimulationHost, NullLogSink
using ProjectChimera.Dsl;               // TriggerGraph (Story 7.2 lossless flat↔graph lowering)
using ProjectChimera.Sim.Tests.Dsl;     // TriggerFieldAssert (shared field-level trigger deep-compare)
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

        /// <summary>
        /// Story 7.2 (Block-If tripwire) — the live <c>ScenarioDirector.LoadScenario</c> lowering routes triggers
        /// through <c>TriggerGraph.FromFlat(...).ToFlat()</c>. Prove that migration is LOSSLESS on every shipped
        /// on-disk scenario: the lowered triggers must deep-equal (FIELD-for-field, not just length) the original flat
        /// array. (All shipped scenarios currently carry empty trigger sets, so today this pins the empty-set identity
        /// that keeps every golden byte-identical; when authored trigger content lands, the deep-compare below guards
        /// its round-trip too — and the non-empty field-level path is exercised now by
        /// <c>TriggerGraphLiveLoweringTests.LiveLoadScenario_RichTriggers_LowersLosslessly</c>, which drives the same
        /// wired lowering through the real LoadScenario with a rich trigger set.)
        /// </summary>
        [Theory]
        [InlineData(P2_4_Scenario)]
        [InlineData(DefaultScenario)]
        public void OnDiskScenario_FlatToGraphLowering_IsLossless(string fileName)
        {
            ScenarioData? model = ScenarioSerializer.LoadFromFile(DataFile("scenarios", fileName));
            Assert.NotNull(model);

            TriggerDefinition[] original = model!.Triggers;
            TriggerDefinition[] lowered  = TriggerGraph.FromFlat(original).ToFlat();

            TriggerFieldAssert.AssertEqual(original, lowered);
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
