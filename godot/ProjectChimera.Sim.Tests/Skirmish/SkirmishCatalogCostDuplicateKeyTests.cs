#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Skirmish;
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-780 — <see cref="SkirmishCatalog.ScanFactions"/> is the THIRD faction-file reader, and it deserialized
    /// straight through <c>JsonSerializer</c> without the DW-537 raw duplicate-cost-key pass that both
    /// <see cref="FactionDefinition"/> entry points run.
    ///
    /// <para><b>Why that matters.</b> System.Text.Json binds a repeated JSON key LAST-WINS on a
    /// <c>Dictionary&lt;string,int&gt;</c>, so <c>"cost": { "ore": 50, "ore": 5 }</c> collapses to one entry holding
    /// 5 and NO model-level check can see the collision. With the guard missing here, a faction whose cost block
    /// silently last-wins would appear in the SKIRMISH ROSTER as a selectable faction while
    /// <see cref="FactionDefinition.LoadFromFile"/> hard-rejects it and
    /// <see cref="FactionDefinition.LoadSelectableFromDirectory"/> excludes it — the selectable-but-not-launchable
    /// split DW-327/DW-537 exist to close. The three-way parity test below is the one that states the real contract:
    /// every faction-file reader must agree.</para>
    ///
    /// <para>Godot-free / Tier-1: writes real files to the OS temp directory, exactly like
    /// <c>CostDuplicateKeyTests</c> and <c>SkirmishSetupTests</c>' catalog scans.</para>
    /// </summary>
    public class SkirmishCatalogCostDuplicateKeyTests
    {
        // ── Fixtures: a roster-COMPLETE faction (so ValidateComplete cannot be what drops it) ───────────────

        /// <summary>A faction that passes <see cref="FactionValidator.ValidateComplete"/> — Worker + combat unit +
        /// producing structure, every <c>mesh_path</c> filled — with the worker's <c>cost</c> block supplied raw so a
        /// duplicate key can actually be authored (a serializer cannot emit one).</summary>
        private static string CompleteFactionJson(string id, string workerCostJson) => $$"""
        {
          "id": "{{id}}",
          "display_name": "{{id}}",
          "units": [
            { "id": "worker", "display_name": "worker", "category": "Worker", "mesh_path": "res://assets/worker.glb",
              "hp": 50, "cost": {{workerCostJson}} },
            { "id": "melee", "display_name": "melee", "category": "Melee", "mesh_path": "res://assets/melee.glb",
              "hp": 50 }
          ],
          "buildings": [
            { "id": "command_center", "display_name": "command_center", "category": "Structure",
              "mesh_path": "res://assets/command_center.glb", "hp": 100, "construction_time": 10,
              "supply_bonus": 0, "produces_category": "Worker" }
          ]
        }
        """;

        private const string CleanCost = """{ "ore": 50 }""";
        private const string DuplicateCost = """{ "ore": 50, "ore": 5 }""";

        // ── The premise: the duplicate really is invisible in the deserialized model ─────────────────────────

        [Fact]
        public void TheCollapsedModel_CannotSeeTheDuplicate()
        {
            // Characterization (passes with or without the fix): this is WHY a raw-text pass is the only way to catch
            // it, and it makes the drops below non-vacuous — nothing about the parsed def is malformed.
            FactionDefinition def = System.Text.Json.JsonSerializer.Deserialize<FactionDefinition>(
                CompleteFactionJson("dup", DuplicateCost), FactionDefinition.JsonOptions)!;

            Assert.True(FactionValidator.ValidateComplete(def).Ok);   // roster-complete: the ONLY other drop reason
            Assert.Equal(5, def.GetUnit("worker")!.Cost!["ore"]);     // the authored 50 is already gone, silently
        }

        // ── The fix ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ScanFactions_DuplicateCostKey_IsNotOfferedAsASkirmishFaction()
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "dup_faction.json"), CompleteFactionJson("dup", DuplicateCost));

            Assert.Empty(SkirmishCatalog.ScanFactions(dir.Path, "res://factions"));
        }

        [Fact]
        public void ScanFactions_DropsOnlyTheOffendingFile_TheScanContinues()
        {
            // Drop-on-fail, never throw-out-of-the-scan: one bad file must not blank the whole setup-screen roster.
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "dup_faction.json"),  CompleteFactionJson("dup", DuplicateCost));
            File.WriteAllText(Path.Combine(dir.Path, "good_faction.json"), CompleteFactionJson("good", CleanCost));

            FactionEntry only = Assert.Single(SkirmishCatalog.ScanFactions(dir.Path, "res://factions"));
            Assert.Equal("good", only.Id);
        }

        [Fact]
        public void AllThreeFactionFileReaders_AgreeOnADuplicateCostKey()
        {
            // The contract this entry is really about: LoadFromFile (throws), LoadSelectableFromDirectory
            // (excludes-with-reason) and ScanFactions (drops) must reach the SAME verdict on the SAME bytes.
            using var dir = new TempDir();
            string file = Path.Combine(dir.Path, "dup_faction.json");
            File.WriteAllText(file, CompleteFactionJson("dup", DuplicateCost));

            var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(file));
            Assert.Contains("duplicate resource key 'ore'", ex.Message);

            var excluded = new List<(string File, string Reason)>();
            Assert.Empty(FactionDefinition.LoadSelectableFromDirectory(dir.Path, (f, r) => excluded.Add((f, r))));
            Assert.Contains(excluded, e => e.Reason.Contains("duplicate resource key 'ore'"));

            Assert.Empty(SkirmishCatalog.ScanFactions(dir.Path, "res://factions"));
        }

        [Fact]
        public void AllThreeFactionFileReaders_AgreeOnACleanFile()
        {
            // Non-regression: the guard must not start dropping well-formed content — a multi-resource cost block is
            // not a duplicate, and the roster stays selectable through all three readers.
            using var dir = new TempDir();
            string file = Path.Combine(dir.Path, "good_faction.json");
            File.WriteAllText(file, CompleteFactionJson("good", """{ "ore": 50, "crystal": 5 }"""));

            Assert.Equal("good", FactionDefinition.LoadFromFile(file).Id);
            Assert.Single(FactionDefinition.LoadSelectableFromDirectory(dir.Path));
            Assert.Equal("good", Assert.Single(SkirmishCatalog.ScanFactions(dir.Path, "res://factions")).Id);
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_catalog_dupcost_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
            }
        }
    }
}
