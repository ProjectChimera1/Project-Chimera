#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Skirmish;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-696 — the <c>*_faction.json</c> naming convention used to be spelled in THREE unlinked places: DW-528 named
    /// it for the WRITE side (<see cref="FactionDefinerWizardCore.FactionFileSuffix"/>) while both discovery scans —
    /// <see cref="FactionDefinition.LoadSelectableFromDirectory"/> and
    /// <see cref="SkirmishCatalog.ScanFactions"/> — carried hand-copied <c>"*_faction.json"</c> literals. Nothing tied
    /// them together, so changing the suffix in one place would let the wizard save a faction file that every picker
    /// then fails to discover: a save-and-vanish with no error anywhere, the worst class of silent failure for
    /// authored content.
    ///
    /// <para>All three now derive from <see cref="FactionFiles"/>. The source pin below is the test that actually
    /// enforces it — a runtime equality alone would still pass if a scan site re-inlined the literal, because the
    /// literal and the constant are equal TODAY; only the source scan can tell "derived" from "coincidentally
    /// identical". It is the <c>CommandApplyParityTests</c> (DW-86/DW-626) shape: comments stripped so prose can
    /// neither satisfy nor hide the assertion, and the tree located portably via
    /// <see cref="CallerFilePathAttribute"/>.</para>
    ///
    /// <para>The behavioural arm then proves the convention end to end: a file written under the WIZARD's suffix is
    /// discovered by BOTH scanners, and one written under any other suffix by neither.</para>
    /// </summary>
    public class FactionFileConventionTests
    {
        // ── The constants themselves ─────────────────────────────────────────────────────────

        [Fact]
        public void TheWizardSuffix_AndBothScanGlobs_AreOneConstant()
        {
            Assert.Equal(FactionFiles.Suffix, FactionDefinerWizardCore.FactionFileSuffix);
            Assert.Equal("*" + FactionFiles.Suffix, FactionFiles.DiscoveryGlob);

            // The shape itself is still what the shipped content and every doc reference claim, so this guard cannot
            // be satisfied by redefining the convention out from under them.
            Assert.Equal("_faction.json", FactionFiles.Suffix);
            Assert.Equal("*_faction.json", FactionFiles.DiscoveryGlob);
        }

        // ── The pin: neither scan site may re-inline the literal ─────────────────────────────

        [Theory]
        [InlineData("FactionDefinition.cs")]
        [InlineData("SkirmishCatalog.cs")]
        public void BothDiscoverySites_DeriveTheirGlob_FromTheSharedConstant(string fileName)
        {
            string path = fileName == "FactionDefinition.cs"
                ? SrcFile("Core", "Definitions", "FactionDefinition.cs")
                : SrcFile("Core", "Skirmish", "SkirmishCatalog.cs");
            Assert.True(File.Exists(path), $"source file not found at '{path}' (via [CallerFilePath]).");

            string blob = StripCommentsAndNormalize(File.ReadAllText(path));

            // Vacuous-pass guard: the site must still be a Directory.GetFiles scan at all. If the enumeration is ever
            // migrated (e.g. the DW-457 PCK-aware DirAccess sweep), this guard must be re-pointed rather than quietly
            // passing against a file that no longer globs anything.
            var scans = Regex.Matches(blob, @"\bDirectory\.GetFiles\(");
            Assert.True(scans.Count > 0,
                $"{fileName} no longer calls Directory.GetFiles — the faction discovery scan moved. Re-point this " +
                "DW-696 pin at the new enumeration site instead of deleting it.");

            // The load-bearing half: the glob must be the SHARED constant, never a re-inlined literal.
            Assert.False(Regex.IsMatch(blob, "\"\\*_faction\\.json\""),
                $"{fileName} re-inlines the \"*_faction.json\" discovery glob. DW-696: derive it from " +
                "FactionFiles.DiscoveryGlob so a suffix change moves the wizard's write side and BOTH scans together — " +
                "a file saved under a suffix the scans miss vanishes with no error anywhere.");

            Assert.Matches(@"\bFactionFiles\.DiscoveryGlob\b", blob);
        }

        [Fact]
        public void TheWizard_WritesThroughTheSharedConstant_NotALiteral()
        {
            string path = SrcFile("Core", "Definitions", "FactionDefinerWizardCore.cs");
            string blob = StripCommentsAndNormalize(File.ReadAllText(path));

            // The write side keeps its own discoverable name, but that name must BE the shared constant.
            Assert.Matches(@"const string FactionFileSuffix = FactionFiles\.Suffix\b", blob);
        }

        // ── The behavioural arm: written-under-the-suffix ⇒ found by BOTH scans ──────────────

        [Fact]
        public void AFileWrittenUnderTheWizardSuffix_IsFoundByBothDiscoveryScans()
        {
            using var dir = new TempDir();
            // Named exactly the way FactionDefinerWizardCore.TryFinish names its output.
            WriteFaction(dir.Path, "alpha" + FactionDefinerWizardCore.FactionFileSuffix, CompleteFaction("alpha"));

            IReadOnlyList<FactionDefinition> selectable =
                FactionDefinition.LoadSelectableFromDirectory(dir.Path);
            IReadOnlyList<FactionEntry> catalog = SkirmishCatalog.ScanFactions(dir.Path, "res://factions");

            Assert.Equal("alpha", Assert.Single(selectable).Id);
            Assert.Equal("alpha", Assert.Single(catalog).Id);
        }

        [Fact]
        public void AFileWrittenUnderAnyOtherSuffix_IsFoundByNeitherScan()
        {
            // The glob is deliberately not a bare *.json: the faction directory also holds sample content
            // (_unitcard_sample.json / _buildingcard_sample.json) that must never be mistaken for a faction.
            using var dir = new TempDir();
            WriteFaction(dir.Path, "alpha_clan.json", CompleteFaction("alpha"));
            WriteFaction(dir.Path, "_unitcard_sample.json", CompleteFaction("sample"));

            Assert.Empty(FactionDefinition.LoadSelectableFromDirectory(dir.Path));
            Assert.Empty(SkirmishCatalog.ScanFactions(dir.Path, "res://factions"));
        }

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────

        /// <summary>A roster-complete faction (Worker + a combat unit + a producing structure, every mesh_path
        /// filled) — i.e. one both scans' <c>ValidateComplete</c> gate accepts, so a scan returning nothing can only
        /// mean the file was not FOUND.</summary>
        private static FactionDefinition CompleteFaction(string id)
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(new UnitDefinition
            { Id = "worker", DisplayName = "worker", Category = "Worker", MeshPath = "res://assets/worker.glb", Hp = 50f });
            def.Units.Add(new UnitDefinition
            { Id = "melee", DisplayName = "melee", Category = "Melee", MeshPath = "res://assets/melee.glb", Hp = 50f });
            def.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "command_center", Category = "Structure",
                MeshPath = "res://assets/command_center.glb", Hp = 100f, ConstructionTime = 10f,
                SupplyBonus = 0, ProducesCategory = "Worker",
            });
            return def;
        }

        private static void WriteFaction(string dir, string fileName, FactionDefinition def)
            => File.WriteAllText(Path.Combine(dir, fileName),
                                 JsonSerializer.Serialize(def, FactionDefinition.JsonOptions));

        // ── Source-scan plumbing (mirrors CommandApplyParityTests) ───────────────────────────

        /// <summary>Strip block/line comments then collapse whitespace, so comment prose can never satisfy (or hide)
        /// the pins above.</summary>
        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            return Regex.Replace(text, @"\s+", " ");
        }

        /// <summary>This file lives in godot/ProjectChimera.Sim.Tests/Definitions/ → ../../src/&lt;parts&gt;.</summary>
        private static string SrcFile(params string[] parts) => SrcFileCore(parts);

        private static string SrcFileCore(string[] parts, [CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            var segments = new List<string> { dir, "..", "..", "src" };
            segments.AddRange(parts);
            return Path.GetFullPath(Path.Combine(segments.ToArray()));
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_factionfiles_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
            }
        }
    }
}
