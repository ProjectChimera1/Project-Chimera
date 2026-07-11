#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 5.5 (FR-17, UX-DR40) — Godot-free xUnit coverage of <see cref="FactionDefinerWizardCore"/>: preset-pool
    /// scanning + deep-clone, the located-error → step mapping, and the <c>ValidateComplete</c>-gated atomic
    /// Finish/save (every I/O-matrix row the spec names, except "Color step render" — that row is a pure UI
    /// rendering concern of <c>FactionDefinerPanel</c> with no Godot-free core surface, verified instead by the
    /// spec's own in-editor manual check). Uses the REAL shipped <c>alpha_faction.json</c>/<c>beta_faction.json</c>
    /// as the scan source (mirrors <c>FactionValidatorTests</c>' <c>ResolveDataPath</c> precedent — both files are
    /// already known-valid, so no hand-rolled fixture is needed), and writes Finish output only under a per-test
    /// temp directory (never the real <c>resources/data/factions/</c> folder).
    /// </summary>
    public class FactionDefinerWizardTests
    {
        // ── ScanPresets ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ScanPresets_RealAlphaAndBeta_PopulatesPoolsTaggedWithSourceFactionId()
        {
            FactionPresetPool pool = FactionDefinerWizardCore.ScanPresets(new[]
            {
                ResolveDataPath("alpha_faction.json"),
                ResolveDataPath("beta_faction.json"),
            });

            Assert.Equal(16, pool.Units.Count);      // 8 alpha + 8 beta
            Assert.Equal(10, pool.Buildings.Count);  // 5 alpha + 5 beta
            Assert.Empty(pool.Research);             // both shipped factions author no research entries today

            FactionPresetOption<UnitDefinition> worker =
                Assert.Single(pool.Units, o => o.Def.Id == "worker");
            Assert.Equal("alpha", worker.SourceFactionId);

            FactionPresetOption<UnitDefinition> forgehand =
                Assert.Single(pool.Units, o => o.Def.Id == "forgehand");
            Assert.Equal("beta", forgehand.SourceFactionId);
        }

        [Fact]
        public void ScanPresets_UnreadablePath_SkippedDefensively_OtherPathsStillPopulate()
        {
            string missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");

            FactionPresetPool pool = FactionDefinerWizardCore.ScanPresets(new[]
            {
                missing,
                ResolveDataPath("alpha_faction.json"),
            });

            Assert.Equal(8, pool.Units.Count);
            Assert.Equal(5, pool.Buildings.Count);
        }

        [Fact]
        public void ScanPresets_NullInput_ReturnsEmptyPool_NoThrow()
        {
            FactionPresetPool pool = FactionDefinerWizardCore.ScanPresets(null!);
            Assert.Empty(pool.Units);
            Assert.Empty(pool.Buildings);
            Assert.Empty(pool.Research);
        }

        // ── DeepClone ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void DeepClone_Unit_ProducesIndependentInstance_WithEqualValues()
        {
            var original = new UnitDefinition { Id = "grunt", DisplayName = "Grunt", Category = "Melee", Hp = 123f };

            UnitDefinition clone = FactionDefinerWizardCore.DeepClone(original);

            Assert.NotSame(original, clone);
            Assert.Equal(original.Id, clone.Id);
            Assert.Equal(original.Hp, clone.Hp);

            clone.DisplayName = "Mutated";
            Assert.Equal("Grunt", original.DisplayName);   // mutating the clone never touches the source
        }

        [Fact]
        public void DeepClone_Building_PreservesDerivedBuildingOnlyFields()
        {
            var original = new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", Hp = 500f,
                ConstructionTime = 30f, SupplyBonus = 0, ProducesCategory = "Melee",
            };

            BuildingDefinition clone = FactionDefinerWizardCore.DeepClone(original);

            Assert.NotSame(original, clone);
            Assert.Equal(30f, clone.ConstructionTime);
            Assert.Equal(0, clone.SupplyBonus);
            Assert.Equal("Melee", clone.ProducesCategory);
        }

        // ── StepForError ─────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("color", "faction 'x'.color: must be an [r, g, b, a] array of length 4.", FactionDefinerStep.NameColor)]
        [InlineData("id", "a faction file already exists at '...' — choose a different id.", FactionDefinerStep.NameColor)]
        [InlineData("ai_preset", "faction 'x'.ai_preset: must be authored.", FactionDefinerStep.AiPreset)]
        [InlineData("units", "faction 'x'.units: roster is missing a required Worker unit.", FactionDefinerStep.Roster)]
        [InlineData("prerequisites", "unit 'archer'.prerequisites: references unknown building id 'range'.", FactionDefinerStep.Roster)]
        [InlineData("prerequisites", "building 'aviary'.prerequisites: references unknown building id 'siege_workshop'.", FactionDefinerStep.BuildingsTech)]
        [InlineData("cost", "unit 'grunt'.cost: references unknown resource id 'gold'.", FactionDefinerStep.Roster)]
        [InlineData("cost", "building 'barracks'.cost: references unknown resource id 'gold'.", FactionDefinerStep.BuildingsTech)]
        [InlineData("mesh_path", "unit 'grunt' is missing mesh_path (required for a complete/playable faction).", FactionDefinerStep.Roster)]
        [InlineData("mesh_path", "building 'barracks' is missing mesh_path (required for a complete/playable faction).", FactionDefinerStep.BuildingsTech)]
        [InlineData("hp", "building 'barracks'.hp: must be a positive value.", FactionDefinerStep.BuildingsTech)]
        public void StepForError_MapsFieldPathAndMessageKindLabel_ToExpectedStep(
            string fieldPath, string message, FactionDefinerStep expected)
        {
            Assert.Equal(expected, FactionDefinerWizardCore.StepForError(fieldPath, message));
        }

        // ── TryFinish ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TryFinish_FullValidSelection_WritesFile_AndReloadsValidateCompleteOk_NoParsedOrPrimaryUnitLeak()
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("crimson_order_5_5");
                def.StartingOre = 350f;       // distinct, non-default (default is 200) — proves the Finish write
                def.StartingCrystal = 75f;    // path carries the Starting Conditions step's values through, not just defaults
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.True(result.Ok);
                Assert.NotNull(result.WrittenPath);
                Assert.True(File.Exists(result.WrittenPath));

                FactionDefinition reloaded = FactionDefinition.LoadFromFile(result.WrittenPath!);
                Assert.Equal("crimson_order_5_5", reloaded.Id);
                Assert.Equal(2, reloaded.Units.Count);
                Assert.Single(reloaded.Buildings);
                Assert.Equal("balanced", reloaded.AiPreset);
                Assert.Equal(350f, reloaded.StartingOre);
                Assert.Equal(75f, reloaded.StartingCrystal);
                Assert.True(FactionValidator.ValidateComplete(reloaded).Ok);

                // Review Loop 1 (bad_spec fix) regression guard: the Finish write path must NEVER whole-object
                // JsonSerializer.Serialize a FactionDefinition/UnitDefinition/BuildingDefinition — that leaks the six
                // computed Parsed* getters (bogus PascalCase int fields) plus FactionDefinition.PrimaryUnit as a
                // duplicated nested object. Mirrors FactionWriteRoundTripTests'
                // Update_DoesNotEmitParsedGetters_NorBalloonDefaults guard shape, applied to the wizard's own output.
                string raw = File.ReadAllText(result.WrittenPath!);
                Assert.DoesNotContain("Parsed", raw);
                Assert.DoesNotContain("PrimaryUnit", raw);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinish_DanglingBuildingPrerequisite_BlocksAtBuildingsTechStep_NoFileWritten()
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("dangling_test");
                // aviary requires siege_workshop, which is deliberately NOT picked → dangling prerequisite.
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "aviary" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "prerequisites"
                    && e.Message.Contains("aviary") && e.Message.Contains("siege_workshop"));
                Assert.Equal(FactionDefinerStep.BuildingsTech, result.Step);
                Assert.False(File.Exists(Path.Combine(dir, "dangling_test_faction.json")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinish_MissingWorker_BlocksAtRosterStep()
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("no_worker_test");
                Pick(def, pool, unitIds: new[] { "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "units" && e.Message.Contains("Worker"));
                Assert.Equal(FactionDefinerStep.Roster, result.Step);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinish_EmptyRoster_BlockedBySameRequiredRoleCheck_RosterStep()
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("empty_roster_test");
                Pick(def, pool, unitIds: Array.Empty<string>(), buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "units" && e.Message.Contains("Worker"));
                Assert.Contains(result.Errors, e => e.FieldPath == "units" && e.Message.Contains("combat"));
                Assert.Equal(FactionDefinerStep.Roster, result.Step);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinish_TargetFactionFileAlreadyExists_BlocksNamingIdField_NeverOverwrites()
        {
            string dir = MakeTempDir();
            try
            {
                string existingPath = Path.Combine(dir, "dup_faction.json");
                File.WriteAllText(existingPath, "SENTINEL-DO-NOT-OVERWRITE");

                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("dup");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "id" && e.Message.Contains("already exists"));
                Assert.Equal(FactionDefinerStep.NameColor, result.Step);

                Assert.Equal("SENTINEL-DO-NOT-OVERWRITE", File.ReadAllText(existingPath));   // never overwritten
                Assert.False(File.Exists(existingPath + ".tmp"));                            // no stray tmp left behind
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Theory]
        [InlineData("../evil")]
        [InlineData("sub/dir")]
        [InlineData("sub\\dir")]
        public void TryFinish_MalformedId_BlocksNamingIdField_NeverEscapesTargetDir(string malformedId)
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft(malformedId);
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "id" && e.Message.Contains(malformedId));
                Assert.Equal(FactionDefinerStep.NameColor, result.Step);
                // No file was written anywhere reachable from dir (in it, or escaping to its parent via "..").
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "evil_faction.json")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── Test helpers ─────────────────────────────────────────────────────────────────────────────────────

        private static FactionPresetPool ScanRealAlphaBeta() => FactionDefinerWizardCore.ScanPresets(new[]
        {
            ResolveDataPath("alpha_faction.json"),
            ResolveDataPath("beta_faction.json"),
        });

        private static FactionDefinition NewDraft(string id) => new()
        {
            Id = id,
            DisplayName = "Test Faction",
            Color = new[] { 0.2f, 0.5f, 0.8f, 1f },
            AiPreset = "balanced",
        };

        private static void Pick(FactionDefinition def, FactionPresetPool pool, string[] unitIds, string[] buildingIds)
        {
            foreach (string id in unitIds)
                def.Units.Add(pool.Units.First(o => o.Def.Id == id).Def);
            foreach (string id in buildingIds)
                def.Buildings.Add(pool.Buildings.First(o => o.Def.Id == id).Def);
        }

        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"faction-definer-wizard-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Resolve a shipped faction JSON by walking up from the test-assembly directory to
        /// <c>resources/data/factions/</c> (mirrors <c>FactionValidatorTests.ResolveDataPath</c>).</summary>
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
