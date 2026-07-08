#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 4.3 (AC1/AC2) — <see cref="ResourceCostValidator"/>'s import-time gate over the authored sparse
    /// <see cref="UnitDefinition.Cost"/> map, and its wiring into <see cref="FactionDefinition.LoadFromFile"/>.
    /// One test per Cost-map I/O Matrix row in the spec: unknown id, negative amount, overflow amount, sparse-map-
    /// only-crystal loads clean, empty-map loads clean, legacy-only loads clean.
    /// </summary>
    public class ResourceCostValidatorTests
    {
        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_resourcecost_validator_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        private static string FactionJson(string unitsJson = "", string buildingsJson = "") => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "units": [{{unitsJson}}],
          "buildings": [{{buildingsJson}}]
        }
        """;

        // ── Validate() direct tests (no file I/O) ────────────────────────────────

        [Fact]
        public void UnauthoredCost_Null_IsSkipped_NoErrors()
        {
            var def = new FactionDefinition();
            def.Units.Add(new UnitDefinition { Id = "worker", CostOre = 50, CostCrystal = 0 });
            Assert.Empty(ResourceCostValidator.Validate(def));
        }

        [Fact]
        public void UnknownResourceIdInUnitCost_IsRejected_NamingUnitIdAndKey()
        {
            var def = new FactionDefinition();
            def.Units.Add(new UnitDefinition { Id = "archer", Cost = new Dictionary<string, int> { { "gems", 10 } } });

            var errors = ResourceCostValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("unit 'archer'") && e.Contains("gems")
                                       && e.Contains("no runtime resource registered for it yet"));
        }

        [Fact]
        public void UnknownResourceIdInBuildingCost_IsRejected_NamingBuildingIdAndKey()
        {
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Cost = new Dictionary<string, int> { { "gems", 10 } },
            });

            var errors = ResourceCostValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("building 'barracks'") && e.Contains("gems")
                                       && e.Contains("no runtime resource registered for it yet"));
        }

        [Fact]
        public void NegativeCostAmount_IsRejected()
        {
            var def = new FactionDefinition();
            def.Units.Add(new UnitDefinition { Id = "archer", Cost = new Dictionary<string, int> { { "ore", -5 } } });

            var errors = ResourceCostValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("archer") && e.Contains("ore") && e.Contains("-5")
                                       && e.Contains(">= 0"));
        }

        [Fact]
        public void OverflowCostAmount_IsRejected()
        {
            var def = new FactionDefinition();
            def.Units.Add(new UnitDefinition { Id = "archer", Cost = new Dictionary<string, int> { { "crystal", 32768 } } });

            var errors = ResourceCostValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("archer") && e.Contains("crystal") && e.Contains("32768")
                                       && e.Contains("exceeds the maximum resource cost"));
        }

        [Fact]
        public void SparseMapOnlyCrystal_LoadsClean_NoErrors()
        {
            var def = new FactionDefinition();
            def.Units.Add(new UnitDefinition { Id = "mage", Cost = new Dictionary<string, int> { { "crystal", 75 } } });
            Assert.Empty(ResourceCostValidator.Validate(def));
        }

        [Fact]
        public void EmptyCostMap_LoadsClean_NoErrors()
        {
            var def = new FactionDefinition();
            def.Units.Add(new UnitDefinition { Id = "free_unit", Cost = new Dictionary<string, int>() });
            Assert.Empty(ResourceCostValidator.Validate(def));
        }

        [Fact]
        public void LegacyOnlyCost_NoAuthoredCostKey_LoadsClean_NoErrors()
        {
            var def = new FactionDefinition();
            def.Units.Add(new UnitDefinition { Id = "worker", CostOre = 50, CostCrystal = 100 });
            def.Buildings.Add(new BuildingDefinition { Id = "barracks", CostOre = 150, CostCrystal = 0 });
            Assert.Empty(ResourceCostValidator.Validate(def));
        }

        [Fact]
        public void NullFactionDefinition_ReturnsEmpty_NoThrow()
        {
            var ex = Record.Exception(() => ResourceCostValidator.Validate(null!));
            Assert.Null(ex);
            Assert.Empty(ResourceCostValidator.Validate(null!));
        }

        // ── FactionDefinition.LoadFromFile wiring (throws with located error) ────

        [Fact]
        public void LoadFromFile_UnknownCostResourceId_Throws_LocatedError()
        {
            string json = FactionJson(unitsJson:
                """{ "id": "archer", "display_name": "Archer", "category": "Ranged", "hp": 50, "cost": { "gems": 10 } }""");
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archer", ex.Message);
                Assert.Contains("gems", ex.Message);
                Assert.Contains("no runtime resource registered for it yet", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_NegativeCostAmount_Throws_LocatedError()
        {
            string json = FactionJson(unitsJson:
                """{ "id": "archer", "display_name": "Archer", "category": "Ranged", "hp": 50, "cost": { "ore": -5 } }""");
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archer", ex.Message);
                Assert.Contains(">= 0", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_SparseMapOnlyCrystal_LoadsClean_ResolvedCostOmitsOre()
        {
            string json = FactionJson(unitsJson:
                """{ "id": "mage", "display_name": "Mage", "category": "Ranged", "hp": 50, "cost": { "crystal": 75 } }""");
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                var resolved = def.Units[0].ResolvedCost;
                Assert.Equal(75, resolved["crystal"]);
                Assert.False(resolved.ContainsKey("ore"));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_ExplicitEmptyCostMap_LoadsClean_ResolvedCostIsEmpty()
        {
            string json = FactionJson(unitsJson:
                """{ "id": "free_unit", "display_name": "Free", "category": "Melee", "hp": 50, "cost_ore": 999, "cost": {} }""");
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.Empty(def.Units[0].ResolvedCost); // authored {} wins verbatim — the legacy cost_ore:999 is ignored
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_ExistingAlphaFaction_LoadsCleanly_NoNewError()
        {
            string path = ResolveDataPath("alpha_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
        }

        [Fact]
        public void LoadFromFile_ExistingBetaFaction_LoadsCleanly_NoNewError()
        {
            string path = ResolveDataPath("beta_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
        }

        /// <summary>Resolve a shipped faction JSON by walking up from the test-assembly directory to
        /// <c>resources/data/factions/</c> (mirrors <c>TechTreeValidatorTests.ResolveDataPath</c>).</summary>
        private static string ResolveDataPath(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "factions");
                if (Directory.Exists(candidate)) return Path.Combine(candidate, fileName);
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/factions above {AppContext.BaseDirectory}");
        }
    }
}
