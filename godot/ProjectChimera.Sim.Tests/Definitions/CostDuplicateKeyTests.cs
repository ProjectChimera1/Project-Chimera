#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-537 — a duplicated resource key inside an authored <c>cost</c> block must FAIL CLOSED with a located
    /// diagnostic instead of silently binding last-wins.
    ///
    /// <para><b>The defect these tests pin.</b> <see cref="UnitDefinition.Cost"/> (inherited by
    /// <see cref="BuildingDefinition"/>) and <see cref="ResearchLevel.Cost"/> are
    /// <c>Dictionary&lt;string,int&gt;</c> DTO properties, and System.Text.Json binds a repeated JSON key
    /// LAST-WINS. <c>"cost": { "ore": 50, "ore": 5 }</c> therefore deserializes to a ONE-entry map holding 5, the
    /// creator's authored 50 is gone, and NO model-level check can see it — <see cref="ResourceCostValidator"/> and
    /// <see cref="ResearchValidator"/> both walk the already-collapsed dictionary. The first test below is the
    /// characterization proof of exactly that (it passes with or without the fix); every test after it drives the
    /// real loader and fails without <see cref="CostDuplicateKeyGuard"/>.</para>
    ///
    /// Same defect class DW-227 closed for <c>DamageTable.FromJson</c>'s nested multiplier dictionaries, minus its
    /// enum-key aliasing (see <c>CaseVariantResourceKeys_AreNotDuplicates</c> for why ordinal is correct here).
    /// Godot-free (Tier-1): writes real files to the OS temp directory, exactly like
    /// <c>FactionLoadParseFailureTests</c>/<c>ResearchValidatorTests</c>.
    /// </summary>
    public class CostDuplicateKeyTests
    {
        // ── Fixtures (shapes borrowed from ResearchValidatorTests / FactionLoadParseFailureTests) ──

        private const string RequiredBuildingFields =
            "\"construction_time\": 10, \"supply_bonus\": 0, \"produces_category\": \"Worker\"";

        private static string Unit(string id, string costJson) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Worker", "hp": 50, "cost": {{costJson}} }""";

        private static string Building(string id, string costJson) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredBuildingFields}}, "cost": {{costJson}} }""";

        private static string Research(string id, string costJson) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "levels": [ { "time_ticks": 10, "cost": {{costJson}} } ] }""";

        private static string FactionJson(string unitsJson = "", string buildingsJson = "", string researchJson = "") => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "units": [{{unitsJson}}],
          "buildings": [{{buildingsJson}}],
          "research": [{{researchJson}}]
        }
        """;

        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_cost_dupkey_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"chimera_cost_dupkey_dir_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ── The defect premise: STJ really is silently last-wins here ─────────────────────────

        [Fact]
        public void Deserialize_DuplicateCostKey_IsSilentlyLastWins_AndTheModelCannotSeeIt()
        {
            // Characterization, NOT a regression assert: it holds before and after the fix, and is the whole reason
            // the guard has to re-walk the RAW document. Note Count == 1 — every model-level check (cost-map size,
            // known-resource-id, range) sees a perfectly well-formed one-entry map.
            string json = $$"""{ "id": "worker", "cost": { "ore": 50, "ore": 5 } }""";

            UnitDefinition def = JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions)!;

            Assert.NotNull(def.Cost);
            Assert.Single(def.Cost!);            // ONE entry — the collision is invisible to every model-level check
            Assert.Equal(5, def.Cost!["ore"]);   // the authored 50 is gone, with no diagnostic anywhere
        }

        // ── The loader now fails closed (each of these throws only WITH the fix) ─────────────

        [Fact]
        public void LoadFromFile_DuplicateUnitCostKey_IsRejectedWithLocatedError()
        {
            string path = WriteTempFaction(FactionJson(unitsJson: Unit("worker", "{ \"ore\": 50, \"ore\": 5 }")));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains(path, ex.Message);                  // located: names the offending file
                Assert.Contains("unit 'worker'.cost", ex.Message);  // located: names the offending entry + field
                Assert.Contains("duplicate resource key 'ore'", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_DuplicateBuildingCostKey_IsRejectedWithLocatedError()
        {
            string path = WriteTempFaction(FactionJson(
                buildingsJson: Building("barracks", "{ \"crystal\": 20, \"ore\": 100, \"crystal\": 1 }")));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                // "building", not "unit" — BuildingDefinition inherits Cost, but the located kind must stay accurate.
                Assert.Contains("building 'barracks'.cost", ex.Message);
                Assert.Contains("duplicate resource key 'crystal'", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_DuplicateResearchLevelCostKey_IsRejectedWithLocatedError()
        {
            string path = WriteTempFaction(FactionJson(
                researchJson: Research("armor_up", "{ \"ore\": 50, \"ore\": 5 }")));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("research 'armor_up'.levels[0].cost", ex.Message);   // located down to the level index
                Assert.Contains("duplicate resource key 'ore'", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_DuplicateCostObject_IsRejected()
        {
            // The DW-227 'duplicate multipliers object' analogue: the whole earlier cost map is discarded, so the
            // bound model shows a valid single map and nothing downstream can tell.
            string unit =
                $$"""{ "id": "worker", "display_name": "w", "category": "Worker", "hp": 50, "cost": { "ore": 50 }, "cost": { "crystal": 5 } }""";
            string path = WriteTempFaction(FactionJson(unitsJson: unit));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("unit 'worker'", ex.Message);
                Assert.Contains("duplicate 'cost' object", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_ReportsEveryDuplicate_ListAll_NotJustTheFirst()
        {
            // List-all, matching the aggregate errors channel every other content check uses: a creator fixing one
            // duplicate must not have to re-run the load to discover the next.
            string path = WriteTempFaction(FactionJson(
                unitsJson:    Unit("worker", "{ \"ore\": 50, \"ore\": 5 }") + "," +
                              Unit("archer", "{ \"crystal\": 10, \"crystal\": 1 }"),
                researchJson: Research("armor_up", "{ \"ore\": 1, \"ore\": 2 }")));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("unit 'worker'.cost", ex.Message);
                Assert.Contains("unit 'archer'.cost", ex.Message);
                Assert.Contains("research 'armor_up'.levels[0].cost", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_IdLessEntry_IsLocatedByIndex()
        {
            // An entry with no authored id still has to be FINDABLE. (A blank id is separately a validator error,
            // so this asserts the duplicate line only.)
            string unit = $$"""{ "display_name": "w", "category": "Worker", "hp": 50, "cost": { "ore": 1, "ore": 2 } }""";
            string path = WriteTempFaction(FactionJson(unitsJson: unit));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("unit #0.cost", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── No over-trigger: valid content still loads byte-identically ──────────────────────

        [Fact]
        public void LoadFromFile_WellFormedCosts_StillLoadUnchanged()
        {
            string path = WriteTempFaction(FactionJson(
                unitsJson:     Unit("worker", "{ \"ore\": 50 }"),
                buildingsJson: Building("barracks", "{ \"ore\": 100, \"crystal\": 20 }"),
                researchJson:  Research("armor_up", "{ \"ore\": 75 }")));
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.Equal(50,  def.GetUnit("worker")!.Cost!["ore"]);
                Assert.Equal(100, def.GetBuilding("barracks")!.Cost!["ore"]);
                Assert.Equal(20,  def.GetBuilding("barracks")!.Cost!["crystal"]);
                Assert.Equal(75,  def.GetResearch("armor_up")!.Levels[0].Cost!["ore"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_CaseVariantResourceKeys_AreNotReportedAsDuplicates()
        {
            // The mirror image of DW-227's enum-key aliasing. A Dictionary<string,int> uses the default ORDINAL
            // comparer, so "ore" and "Ore" are two SURVIVING entries — no value is discarded, and calling that a
            // duplicate would be a false positive. The second key is still rejected, by the pre-existing
            // unknown-resource-id check, which is the correct diagnostic for it.
            string path = WriteTempFaction(FactionJson(unitsJson: Unit("worker", "{ \"ore\": 50, \"Ore\": 5 }")));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("unknown resource id 'Ore'", ex.Message);
                Assert.DoesNotContain("duplicate resource key", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_ContentRejection_StillThrowsTheValidatorMessage_NotADuplicateMessage()
        {
            // The new pass sits beside the validators, not in front of them: a well-formed file with BAD CONTENT
            // must still surface the validator's own located error (the DW-62 guard's equivalent assertion).
            string path = WriteTempFaction(FactionJson(unitsJson: Unit("worker", "{ \"unobtainium\": 5 }")));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("unobtainium", ex.Message);
                Assert.DoesNotContain("duplicate", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_MalformedJson_StillReportsOnlyTheParseError()
        {
            // The DW-537 pass must never run over an unparseable document and pile a second, confusing line on top
            // of DW-62's single parse line.
            string path = WriteTempFaction("{ \"id\": \"test_faction\", \"display_name\": \"Test");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Single(ex.Message.Split('\n'));
                Assert.Contains("malformed faction JSON", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Discovery agrees with the load gate (selectable == launchable) ───────────────────

        [Fact]
        public void LoadSelectableFromDirectory_DuplicateCostKey_IsExcludedWithItsReason()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "dup_faction.json"),
                    FactionJson(unitsJson: Unit("worker", "{ \"ore\": 50, \"ore\": 5 }")));

                var excluded = new List<(string File, string Reason)>();
                IReadOnlyList<FactionDefinition> defs =
                    FactionDefinition.LoadSelectableFromDirectory(dir, (f, r) => excluded.Add((f, r)));

                Assert.Empty(defs);   // NOT listed as selectable while LoadFromFile would reject it
                Assert.Contains(excluded, e => e.Reason.Contains("duplicate resource key 'ore'"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── The reusable guard, driven directly (bare single-definition documents) ───────────

        [Fact]
        public void Scan_BareUnitDocument_WithDuplicateCostKey_IsReported()
        {
            // The card panels / LLM draft parsers deserialize ONE definition from a root object, so the guard has to
            // understand that root shape too, not only a faction file.
            IReadOnlyList<string> errors =
                CostDuplicateKeyGuard.Scan($$"""{ "id": "archer", "cost": { "ore": 40, "ore": 4 } }""");

            Assert.Single(errors);
            Assert.Contains("definition 'archer'.cost", errors[0]);
            Assert.Contains("duplicate resource key 'ore'", errors[0]);
        }

        [Fact]
        public void Scan_BareResearchDocument_WithDuplicateLevelCostKey_IsReported()
        {
            IReadOnlyList<string> errors = CostDuplicateKeyGuard.Scan($$"""
            {
              "id": "armor_up",
              "levels": [
                { "time_ticks": 10, "cost": { "ore": 1 } },
                { "time_ticks": 20, "cost": { "crystal": 5, "crystal": 6 } }
              ]
            }
            """);

            Assert.Single(errors);
            Assert.Contains("research 'armor_up'.levels[1].cost", errors[0]);
            Assert.Contains("duplicate resource key 'crystal'", errors[0]);
        }

        [Fact]
        public void Scan_AcceptsTheSameDialectTheLenientLoaderDoes()
        {
            // Comments + trailing commas are legal in every content file (ContentJson.Base), so the raw pass MUST
            // accept them — otherwise it would report a bogus "could not be re-read" line for a valid faction file.
            IReadOnlyList<string> errors = CostDuplicateKeyGuard.Scan("""
            {
              // a creator's note
              "id": "archer",
              "cost": { "ore": 40, "ore": 4, },
            }
            """);

            Assert.Single(errors);
            Assert.Contains("duplicate resource key 'ore'", errors[0]);
        }

        [Theory]
        [InlineData((string?)null)]
        [InlineData("")]
        [InlineData("[]")]        // non-object root — binds to nothing here
        [InlineData("null")]
        [InlineData("{ \"id\": \"archer\" }")]                                  // no cost block at all
        [InlineData("{ \"id\": \"archer\", \"cost\": { \"ore\": 1 } }")]        // a clean cost block
        [InlineData("{ \"units\": [ { \"id\": \"w\", \"cost\": {} } ] }")]      // an authored-empty (free) cost map
        public void Scan_WithNothingWrong_ReturnsNoErrors(string? json)
        {
            Assert.Empty(CostDuplicateKeyGuard.Scan(json));
        }

        [Fact]
        public void Scan_UnparseableDocument_FailsClosedWithADiagnostic_NotSilence()
        {
            // Unreachable in production (callers scan only AFTER a successful deserialize); if the dialects ever
            // drift apart, the pass must say so rather than quietly certify a document it never read.
            IReadOnlyList<string> errors = CostDuplicateKeyGuard.Scan("{ \"id\": ");

            Assert.Single(errors);
            Assert.Contains("could not be re-read", errors[0]);
        }

        // ── Shipped content is clean (and stays clean) ───────────────────────────────────────

        [Fact]
        public void Scan_ShippedFactionFiles_CarryNoDuplicateCostKeys()
        {
            // Proves the fix moves no shipped content (every real faction parses exactly as before) AND acts as the
            // tripwire if a hand-edited faction file ever commits one.
            string dir = ResolveDataDir("factions");
            string[] files = Directory.GetFiles(dir, "*.json");
            Assert.NotEmpty(files);

            foreach (string file in files)
            {
                IReadOnlyList<string> errors = CostDuplicateKeyGuard.Scan(File.ReadAllText(file));
                Assert.True(errors.Count == 0,
                    $"{Path.GetFileName(file)}: {string.Join(" | ", errors)}");
            }
        }

        /// <summary>Walk up from the test binary to the repo's <c>resources/data/&lt;sub&gt;</c> (the
        /// <c>AsymmetryPlaytestValidationTests.ResolveDataDir</c> idiom).</summary>
        private static string ResolveDataDir(string sub)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }
    }
}
