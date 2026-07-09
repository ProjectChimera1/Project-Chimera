#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 4.1 (AC1) — proves a faction JSON building entry missing <c>construction_time</c>, <c>supply_bonus</c>,
    /// or <c>produces_category</c> is rejected at <see cref="FactionDefinition.LoadFromFile"/> with a located error
    /// naming the building id and the missing field, and that no <see cref="FactionDefinition"/> is returned (the
    /// throw happens before the caller ever observes a partially-loaded def). Also covers the direct
    /// <see cref="BuildingDefinitionValidator.Validate"/> multi-error (list-all) shape and the valid/happy path.
    /// </summary>
    public class BuildingDefinitionValidatorTests
    {
        private const string ValidBuildingFields =
            "\"construction_time\": 15, \"supply_bonus\": 10, \"produces_category\": \"Worker\"";

        private static string FactionJson(string buildingFieldsJson) => FactionJsonWithHp(500, buildingFieldsJson);

        private static string FactionJsonWithHp(float hp, string buildingFieldsJson) => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "units": [],
          "buildings": [
            { "id": "command_center", "display_name": "HQ", "category": "Structure", "hp": {{hp}}, {{buildingFieldsJson}} }
          ]
        }
        """;

        /// <summary>Writes <paramref name="json"/> to a fresh temp file and returns its absolute path (LoadFromFile
        /// only accepts a path, not a raw string).</summary>
        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_building_validator_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void MissingSupplyBonus_Throws_NamingBuildingIdAndField()
        {
            string path = WriteTempFaction(FactionJson("\"construction_time\": 15, \"produces_category\": \"Worker\""));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("command_center", ex.Message);
                Assert.Contains("supply_bonus", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void MissingConstructionTime_Throws_NamingBuildingIdAndField()
        {
            string path = WriteTempFaction(FactionJson("\"supply_bonus\": 10, \"produces_category\": \"Worker\""));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("command_center", ex.Message);
                Assert.Contains("construction_time", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void MissingProducesCategory_Throws_NamingBuildingIdAndField()
        {
            string path = WriteTempFaction(FactionJson("\"construction_time\": 15, \"supply_bonus\": 10"));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("command_center", ex.Message);
                Assert.Contains("produces_category", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void AllThreeMissing_Throws_ListingEveryMissingField()
        {
            string path = WriteTempFaction(FactionJson(""));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("construction_time", ex.Message);
                Assert.Contains("supply_bonus", ex.Message);
                Assert.Contains("produces_category", ex.Message);
            }
            finally { File.Delete(path); }
        }

        /// <summary>Review pass: <c>Hp</c> is no longer vestigial once a resolved def is threaded through
        /// <c>BuildingStore.Create</c> (<c>BuildingSystem.PlaceBuildingDirect</c>/<c>QueueWorkerBuild</c>), so a
        /// non-positive value is rejected the same as the other now-load-bearing fields (does not catch a fully
        /// omitted <c>hp</c> silently defaulting to 100 — see deferred-work.md).</summary>
        [Fact]
        public void ZeroHp_Throws_NamingBuildingIdAndField()
        {
            string path = WriteTempFaction(FactionJsonWithHp(0, ValidBuildingFields));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("command_center", ex.Message);
                Assert.Contains("hp", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ValidBuilding_LoadsWithoutThrowing()
        {
            string path = WriteTempFaction(FactionJson(ValidBuildingFields));
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.NotNull(def);
                Assert.Single(def.Buildings);
                BuildingDefinition b = def.Buildings[0];
                Assert.Equal(500f, b.Hp);
                Assert.Equal(15f, b.ConstructionTime);
                Assert.Equal(10, b.SupplyBonus);
                Assert.Equal("Worker", b.ProducesCategory);
                Assert.Equal("0.1", b.MinGameVersion);
            }
            finally { File.Delete(path); }
        }

        // ── Direct validator unit tests (no file I/O) ──────────────────────────────

        [Fact]
        public void Validate_AllFieldsPresent_IsOk()
        {
            var def = new BuildingDefinition { Id = "barracks", ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee" };
            Assert.True(BuildingDefinitionValidator.Validate(def).Ok);
        }

        [Fact]
        public void Validate_AllThreeMissing_ReturnsThreeLocatedErrors()
        {
            var def = new BuildingDefinition { Id = "barracks" };
            BuildingValidationResult result = BuildingDefinitionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Equal(3, result.Errors.Count);
            Assert.Contains(result.Errors, e => e.FieldPath == "construction_time");
            Assert.Contains(result.Errors, e => e.FieldPath == "supply_bonus");
            Assert.Contains(result.Errors, e => e.FieldPath == "produces_category");
            Assert.All(result.Errors, e => Assert.Contains("barracks", e.Message));
        }

        [Fact]
        public void Validate_ZeroOrNegativeHp_ReturnsLocatedError()
        {
            var zero = new BuildingDefinition { Id = "barracks", Hp = 0f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee" };
            Assert.Contains(BuildingDefinitionValidator.Validate(zero).Errors, e => e.FieldPath == "hp");

            var negative = new BuildingDefinition { Id = "barracks", Hp = -5f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee" };
            Assert.Contains(BuildingDefinitionValidator.Validate(negative).Errors, e => e.FieldPath == "hp");
        }

        // ── Story 4.5: the siblings-aware overload — reuses UnitDefinitionValidator's id/dup-id/enum/cost-range gate,
        //    kinded "building", over the same def (see BuildingDefinitionValidator.Validate(def, siblings)). ──

        private static BuildingDefinition ValidBuilding(string id = "barracks") => new()
        {
            Id = id, Category = "Structure", ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
        };

        [Fact]
        public void SiblingsOverload_BlankId_ReturnsLocatedError_KindedBuilding()
        {
            var def = ValidBuilding(id: "");
            BuildingValidationResult result = BuildingDefinitionValidator.Validate(def, siblings: null);
            Assert.Contains(result.Errors, e => e.FieldPath == "id");
            Assert.Contains(result.Errors, e => e.Message.StartsWith("building '"));
        }

        [Fact]
        public void SiblingsOverload_DuplicateId_ReturnsLocatedError()
        {
            var a = ValidBuilding(id: "barracks");
            var b = ValidBuilding(id: "barracks");
            var siblings = new List<BuildingDefinition> { a, b };

            BuildingValidationResult result = BuildingDefinitionValidator.Validate(a, siblings);
            Assert.Contains(result.Errors, e => e.FieldPath == "id" && e.Message.Contains("duplicate"));
        }

        [Fact]
        public void SiblingsOverload_UnknownCategory_ReturnsLocatedError()
        {
            var def = ValidBuilding();
            def.Category = "NotARealCategory";
            BuildingValidationResult result = BuildingDefinitionValidator.Validate(def, siblings: null);
            Assert.Contains(result.Errors, e => e.FieldPath == "category");
        }

        [Fact]
        public void SiblingsOverload_NegativeOreCostMapEntry_ReturnsLocatedError()
        {
            var def = ValidBuilding();
            def.Cost = new Dictionary<string, int> { ["ore"] = -10 };
            BuildingValidationResult result = BuildingDefinitionValidator.Validate(def, siblings: null);
            Assert.Contains(result.Errors, e => e.FieldPath == "cost" && e.Message.Contains("must be >= 0"));
        }

        [Fact]
        public void SiblingsOverload_CrystalCostMapEntryAtCeiling_ReturnsLocatedError()
        {
            var def = ValidBuilding();
            def.Cost = new Dictionary<string, int> { ["crystal"] = 32768 };
            BuildingValidationResult result = BuildingDefinitionValidator.Validate(def, siblings: null);
            Assert.Contains(result.Errors, e => e.FieldPath == "cost" && e.Message.Contains("exceeds the maximum resource cost"));
        }

        [Fact]
        public void SiblingsOverload_UnknownCostMapResourceId_ReturnsLocatedError()
        {
            var def = ValidBuilding();
            def.Cost = new Dictionary<string, int> { ["gas"] = 5 };
            BuildingValidationResult result = BuildingDefinitionValidator.Validate(def, siblings: null);
            Assert.Contains(result.Errors, e => e.FieldPath == "cost" && e.Message.Contains("unknown resource id"));
        }

        [Fact]
        public void SiblingsOverload_ExistingValidAlphaBuilding_StillPasses_WithNullSiblings()
        {
            // The LoadFromFile call path (BuildingDefinitionValidator.Validate(def)) is unaffected by this story:
            // it forwards to Validate(def, null), so an existing valid building keeps loading cleanly.
            string path = ResolveDataPath("alpha_faction.json");
            FactionDefinition alpha = FactionDefinition.LoadFromFile(path);
            Assert.NotEmpty(alpha.Buildings);
            foreach (BuildingDefinition b in alpha.Buildings)
                Assert.True(BuildingDefinitionValidator.Validate(b, siblings: null).Ok);
        }

        /// <summary>Resolve a shipped faction JSON by walking up from the test-assembly directory to
        /// <c>resources/data/factions/</c> (mirrors <c>ResourceCostValidatorTests.ResolveDataPath</c> /
        /// <c>TechTreeValidatorTests.ResolveDataPath</c>).</summary>
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
