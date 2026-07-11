#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 5.7 (FR-19/UX-DR80) — one test per I/O Matrix row for
    /// <see cref="FactionDefinition.LoadSelectableFromDirectory"/>: the directory-scan discovery method that
    /// closes DW-97's discovery half (gates every <c>*_faction.json</c> through
    /// <see cref="FactionValidator.ValidateComplete"/>, never the lenient <see cref="FactionValidator.Validate"/>
    /// that <see cref="FactionDefinition.LoadFromFile"/> uses). All Godot-free (Tier-1) — writes real files to a
    /// temp directory, mirroring <see cref="HeroProfilePersistenceTests"/>'s own <c>TempDir</c> pattern.
    /// </summary>
    public class FactionDiscoveryTests
    {
        // ── Faction builders (mirrors FactionValidatorTests' baseline helpers) ─────────────

        private static UnitDefinition Worker(string id = "worker") => new()
        {
            Id = id,
            DisplayName = id,
            Category = "Worker",
            MeshPath = "res://assets/worker.glb",
            Hp = 50f,
        };

        private static UnitDefinition Melee(string id = "melee") => new()
        {
            Id = id,
            DisplayName = id,
            Category = "Melee",
            MeshPath = "res://assets/melee.glb",
            Hp = 50f,
        };

        private static BuildingDefinition ValidBuilding(string id = "command_center") => new()
        {
            Id = id,
            DisplayName = id,
            Category = "Structure",
            MeshPath = "res://assets/command_center.glb",
            Hp = 100f,
            ConstructionTime = 10f,
            SupplyBonus = 0,
            ProducesCategory = "Worker",
        };

        /// <summary>A minimal, fully-valid (ValidateComplete-passing) faction, mirroring alpha/beta's own shape.</summary>
        private static FactionDefinition ValidFaction(string id)
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(Worker());
            def.Units.Add(Melee());
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        /// <summary>A faction that passes the lenient <see cref="FactionValidator.Validate"/> (so it would
        /// LoadFromFile fine) but fails <see cref="FactionValidator.ValidateComplete"/> — no Worker-category unit.</summary>
        private static FactionDefinition IncompleteFaction_MissingWorker(string id)
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(Melee());
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        private static void WriteFaction(string dir, string fileName, FactionDefinition def)
        {
            File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(def, FactionDefinition.JsonOptions));
        }

        // ── Row: valid wizard-authored faction is included ─────────────────────────────────

        [Fact]
        public void ValidFaction_IsIncluded_NoExclusionReported()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "newfaction_faction.json", ValidFaction("newfaction"));

            var excluded = new List<(string, string)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("newfaction", result[0].Id);
            Assert.Empty(excluded);
        }

        // ── Row: showcase factions (alpha/beta-style pair) both appear alongside each other ─

        [Fact]
        public void ShowcaseStylePair_BothIncluded_AlongsideEachOther()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "alpha_faction.json", ValidFaction("alpha"));
            WriteFaction(dir.Path, "beta_faction.json", ValidFaction("beta"));

            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(dir.Path);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, f => f.Id == "alpha");
            Assert.Contains(result, f => f.Id == "beta");
        }

        // ── Row: non-faction sample files are never even scanned ───────────────────────────

        [Fact]
        public void NonFactionSampleFiles_AreIgnoredEntirely_NoSkipReport()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "alpha_faction.json", ValidFaction("alpha"));
            // Deliberately unparseable-as-faction garbage — if these were ever scanned they'd trigger onExcluded.
            File.WriteAllText(Path.Combine(dir.Path, "_buildingcard_sample.json"), "{ not json faction content ][");
            File.WriteAllText(Path.Combine(dir.Path, "_unitcard_sample.json"), "{ not json faction content ][");

            var excluded = new List<(string, string)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("alpha", result[0].Id);
            Assert.Empty(excluded); // filename filter excludes the sample files before any parse is attempted
        }

        // ── Row: faction failing ValidateComplete is excluded with a located reason ────────

        [Fact]
        public void ValidateCompleteFailing_Excluded_WithLocatedReason()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "broken_faction.json", IncompleteFaction_MissingWorker("broken"));

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Empty(result);
            Assert.Single(excluded);
            Assert.Equal("broken_faction.json", excluded[0].Name);
            Assert.False(string.IsNullOrWhiteSpace(excluded[0].Reason));
        }

        // ── Row: malformed JSON is excluded via onExcluded, scan continues ─────────────────

        [Fact]
        public void MalformedJson_Excluded_ScanContinuesToNextFile()
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "malformed_faction.json"), "{ this is not valid json ][");
            WriteFaction(dir.Path, "valid_faction.json", ValidFaction("valid"));

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("valid", result[0].Id);
            Assert.Single(excluded);
            Assert.Equal("malformed_faction.json", excluded[0].Name);
            Assert.False(string.IsNullOrWhiteSpace(excluded[0].Reason));
        }

        // ── Row: missing directory returns empty, never throws ─────────────────────────────

        [Fact]
        public void MissingDirectory_ReturnsEmpty_NoThrow()
        {
            string absent = Path.Combine(Path.GetTempPath(), "chimera_faction_discovery_absent_" + System.Guid.NewGuid().ToString("N"));
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(absent);
            Assert.Empty(result);
        }

        // ── Row: an EXISTING directory with zero matching files (distinct code path from "missing") ─

        [Fact]
        public void ExistingEmptyDirectory_ReturnsEmpty_NoThrow()
        {
            using var dir = new TempDir();
            // Directory exists (Directory.Exists passes) but has no *_faction.json files at all —
            // a distinct branch from MissingDirectory_ReturnsEmpty_NoThrow's Directory.Exists-false path.
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(dir.Path);
            Assert.Empty(result);
        }

        // ── Row: duplicate faction Id across two files — first file wins, second reported ──

        [Fact]
        public void DuplicateId_FirstFileWins_SecondReportedAndExcluded()
        {
            using var dir = new TempDir();
            // Ordinal filename order: a_dup < b_dup, so "a_dup_faction.json" is the first-seen occurrence.
            WriteFaction(dir.Path, "a_dup_faction.json", ValidFaction("shared"));
            WriteFaction(dir.Path, "b_dup_faction.json", ValidFaction("shared"));

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("shared", result[0].Id);
            Assert.Single(excluded);
            Assert.Equal("b_dup_faction.json", excluded[0].Name);
            Assert.Contains("duplicate", excluded[0].Reason, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NullOrEmptyDirectory_ReturnsEmpty_NoThrow()
        {
            Assert.Empty(FactionDefinition.LoadSelectableFromDirectory(""));
            Assert.Empty(FactionDefinition.LoadSelectableFromDirectory(null!));
        }

        // ── Row: deterministic ordinal-by-Id ordering, independent of on-disk filename order ─

        [Fact]
        public void ThreeOrMoreFiles_OrderedOrdinalById_IndependentOfFilenameOrder()
        {
            using var dir = new TempDir();
            // Filename order (ordinal): a_file < b_file < c_file — deliberately NOT matching Id order, so this
            // proves the final sort is by def.Id, not by the walk order.
            WriteFaction(dir.Path, "a_file_faction.json", ValidFaction("zzz"));
            WriteFaction(dir.Path, "b_file_faction.json", ValidFaction("mmm"));
            WriteFaction(dir.Path, "c_file_faction.json", ValidFaction("aaa"));

            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(dir.Path);

            Assert.Equal(3, result.Count);
            Assert.Equal(new[] { "aaa", "mmm", "zzz" }, new[] { result[0].Id, result[1].Id, result[2].Id });
        }

        // ── Test-local temp directory helper (mirrors HeroProfilePersistenceTests' own TempDir) ────

        private sealed class TempDir : System.IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_faction_discovery_" + System.Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
