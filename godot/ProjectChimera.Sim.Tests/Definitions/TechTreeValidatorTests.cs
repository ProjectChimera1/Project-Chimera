#nullable enable
using System;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 4.2 (AC1/AC2) — <see cref="TechTreeValidator"/>'s import-time referential + cycle lint, and its
    /// wiring into <see cref="FactionDefinition.LoadFromFile"/>. One test per I/O Matrix row in the spec:
    /// unknown building prereq, unknown unit prereq, 2-node cycle, self cycle, custom-building prereq target,
    /// null-prerequisites-no-throw, and the existing alpha/beta faction JSON loading clean.
    /// </summary>
    public class TechTreeValidatorTests
    {
        private const string RequiredFields =
            "\"construction_time\": 10, \"supply_bonus\": 0, \"produces_category\": \"Worker\"";

        private static string Building(string id, string prereqsJson = "[]") =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredFields}}, "prerequisites": {{prereqsJson}} }""";

        private static string BuildingNoPrereqField(string id) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredFields}} }""";

        private static string BuildingNullPrereq(string id) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredFields}}, "prerequisites": null }""";

        private static string Unit(string id, string category, string prereqsJson) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "{{category}}", "hp": 50, "prerequisites": {{prereqsJson}} }""";

        private static string FactionJson(string buildingsJson, string unitsJson = "") => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "units": [{{unitsJson}}],
          "buildings": [{{buildingsJson}}]
        }
        """;

        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_techtree_validator_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        // ── Unknown building prereq ──────────────────────────────────────────────

        [Fact]
        public void UnknownBuildingPrereq_Throws_NamingReferencingIdAndUnknownId()
        {
            string json = FactionJson(Building("archery_range", "[\"barrackz\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archery_range", ex.Message);
                Assert.Contains("barrackz", ex.Message);
                Assert.Contains("unknown building id", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Unknown unit prereq ──────────────────────────────────────────────────

        [Fact]
        public void UnknownUnitPrereq_Throws_NamingReferencingIdAndUnknownId()
        {
            string json = FactionJson(
                BuildingNoPrereqField("archery_range"),
                Unit("archer", "Ranged", "[\"barrackz\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archer", ex.Message);
                Assert.Contains("barrackz", ex.Message);
                Assert.Contains("unknown building id", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── 2-node cycle ──────────────────────────────────────────────────────────

        [Fact]
        public void TwoNodeCycle_Throws_NamingBothIdsInCycleOrder()
        {
            string json = FactionJson(Building("a", "[\"b\"]") + "," + Building("b", "[\"a\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("tech tree cycle", ex.Message);
                Assert.Contains("a", ex.Message);
                Assert.Contains("b", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Self cycle ────────────────────────────────────────────────────────────

        [Fact]
        public void SelfCycle_Throws_NamingTheId()
        {
            string json = FactionJson(Building("a", "[\"a\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("tech tree cycle", ex.Message);
                Assert.Contains("a -> a", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Duplicate building id (review-pass addition) ─────────────────────────

        [Fact]
        public void DuplicateBuildingId_Throws_NamingTheId()
        {
            // Review-pass fix (Story 4.2): without this check, the second building sharing an id would silently
            // collapse into the first for both the referential-lint id set and the cycle-detection graph, so a
            // cycle reachable only through the duplicate's own, distinct prerequisites could go undetected.
            string json = FactionJson(Building("archery_range") + "," + Building("archery_range"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("duplicate building id", ex.Message);
                Assert.Contains("archery_range", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DuplicateBuildingId_UnsoundCycleWouldOtherwiseHideBehindTheDuplicate_StillCaughtAsDuplicateError()
        {
            // The scenario the missing check would have hidden: "a" requires "b" (fine, first occurrence), and a
            // SECOND "b" requires "a" (a genuine cycle reachable only via the duplicate's own edges). Asserting on
            // the duplicate-id error alone is sufficient — the load fails either way, so the cycle can never
            // silently pass regardless of which "b" the DFS happens to walk.
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "a", Prerequisites = new[] { "b" } });
            def.Buildings.Add(new BuildingDefinition { Id = "b" });
            def.Buildings.Add(new BuildingDefinition { Id = "b", Prerequisites = new[] { "a" } });

            var errors = TechTreeValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("duplicate building id") && e.Contains("b"));
        }

        // ── 3+-node (indirect) cycle ──────────────────────────────────────────────

        [Fact]
        public void ThreeNodeIndirectCycle_Throws_NamingTheFullChain()
        {
            // The 2-node/self-cycle tests above don't exercise the DFS's path-slicing chain-extraction logic
            // (Visit's `path.IndexOf` + slice) beyond a trivial 1-2 element chain — this proves the general case.
            string json = FactionJson(
                Building("a", "[\"b\"]") + "," + Building("b", "[\"c\"]") + "," + Building("c", "[\"a\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("tech tree cycle", ex.Message);
                Assert.Contains("a -> b -> c -> a", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── List-all: multiple simultaneous violations surface together ──────────

        [Fact]
        public void TwoSimultaneousDanglingReferences_BothSurfaceInOneThrownMessage()
        {
            // Proves the "list-all, never first-fail" referential-lint claim (the class doc / FactionDefinition's
            // doc comment) rather than just asserting it from the implementation's `List<string>`/`AddRange` shape.
            string json = FactionJson(
                Building("archery_range", "[\"barrackz\"]") + "," + Building("siege_workshop", "[\"nope\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archery_range", ex.Message);
                Assert.Contains("barrackz", ex.Message);
                Assert.Contains("siege_workshop", ex.Message);
                Assert.Contains("nope", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Custom building as prereq target ─────────────────────────────────────

        [Fact]
        public void CustomBuildingPrereqTarget_NoValidationError()
        {
            // The validator only checks that the id exists among Buildings[].Id — it does not care whether the
            // referencing type is enum-backed or BuildingType.Custom (Story 4.1's Custom sentinel has no
            // TechTreeChecker mapping, but is a perfectly valid AUTHORED id here).
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "watchtower" });
            def.Buildings.Add(new BuildingDefinition { Id = "keep", Prerequisites = new[] { "watchtower" } });

            var errors = TechTreeValidator.Validate(def);
            Assert.Empty(errors);
        }

        // ── Null prerequisites array ─────────────────────────────────────────────

        [Fact]
        public void NullPrerequisitesArray_TreatedAsEmpty_NoThrowNoCrash()
        {
            string json = FactionJson(BuildingNullPrereq("command_center"));
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.Single(def.Buildings);
                Assert.Null(def.Buildings[0].Prerequisites);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Validate_NullPrerequisitesOnBuildingAndUnit_NoThrowNoErrors()
        {
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "command_center", Prerequisites = null! });
            var unit = new UnitDefinition { Id = "worker", Prerequisites = null! };
            def.Units.Add(unit);

            var ex = Record.Exception(() => TechTreeValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(TechTreeValidator.Validate(def));
        }

        // ── Valid acyclic chain (existing shipped content) ───────────────────────

        [Fact]
        public void AlphaFaction_LoadsCleanly_NoNewError()
        {
            string path = ResolveDataPath("alpha_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
        }

        [Fact]
        public void BetaFaction_LoadsCleanly_NoNewError()
        {
            string path = ResolveDataPath("beta_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
        }

        /// <summary>Resolve a shipped faction JSON by walking up from the test-assembly directory to
        /// <c>resources/data/factions/</c> (mirrors <c>CanonicalScenarioTests.DataFile</c> /
        /// <c>DataDrivenBuildingScenario.AlphaFactionPath</c>, Story 1.10a's established pattern).</summary>
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
