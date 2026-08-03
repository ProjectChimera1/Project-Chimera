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
    /// Godot-free coverage of the <c>mesh_path</c> resolution + validation bundle (DW-102 / DW-104 / DW-427):
    ///
    /// <list type="bullet">
    ///   <item><b>DW-427</b> — <see cref="MeshPathId"/>: the two-form convention test (<c>res://</c> project path vs
    ///     package logical id), the registry-key normalization that stops a case/slash/whitespace near-miss from
    ///     silently falling back to the box placeholder, and the diagnostic text that finally tells an author WHY a
    ///     custom mesh did not resolve (available ids + "did you mean").</item>
    ///   <item><b>DW-104</b> — <see cref="MeshAssetLint"/>: the disk-existence lint that
    ///     <see cref="FactionValidator.ValidateComplete"/> never had, kept out of the sim validator per the recorded
    ///     decision (probe-injected, so this suite stays Godot-free) and made load-bearing at the wizard Save edge
    ///     (<see cref="FactionDefinerWizardCore.TryFinish"/>).</item>
    ///   <item><b>DW-102</b> — the shipped alpha/beta factions: EVERY authored <c>res://</c> mesh_path resolves on
    ///     disk. This is the regression net for the two <c>aviary</c> buildings, whose paths pointed at GLBs
    ///     (<c>bonded_aerie.glb</c>/<c>wraithwing_brood.glb</c>) that were never generated; per the recorded decision
    ///     they now point at an on-disk placeholder (the same faction's Ranged-producer building mesh) until the real
    ///     art lands. Left un-netted, that test fails.</item>
    /// </list>
    ///
    /// Determinism: authoring-time string/filesystem logic only. <c>MeshPath</c> is explicitly excluded from
    /// <see cref="ContentHash"/> as presentation data, so nothing here folds into a checksum or moves a golden.
    /// </summary>
    public class MeshPathResolutionTests
    {
        // ── DW-427: the two-form convention ──────────────────────────────────────────────────────

        [Theory]
        [InlineData("res://assets/models/x.glb", true)]
        [InlineData("  res://assets/models/x.glb  ", true)]   // whitespace is authoring noise, not a different form
        [InlineData("assets/heavy_tank.glb", false)]
        [InlineData("heavy_tank.glb", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void IsProjectResourcePath_OnlyTrueForResPaths(string? meshPath, bool expected)
            => Assert.Equal(expected, MeshPathId.IsProjectResourcePath(meshPath));

        [Theory]
        [InlineData("assets/heavy_tank.glb", true)]
        [InlineData("heavy_tank.glb", true)]
        [InlineData("res://assets/models/x.glb", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void IsPackageAssetId_IsExactlyTheRegistryRoutingTest(string? meshPath, bool expected)
            => Assert.Equal(expected, MeshPathId.IsPackageAssetId(meshPath));

        // ── DW-427: key normalization (the case-insensitive-FS vs case-sensitive-registry mismatch) ──

        [Theory]
        [InlineData("assets/heavy_tank.glb", "assets/heavy_tank.glb")]
        [InlineData("Assets/Heavy_Tank.GLB", "assets/heavy_tank.glb")]   // the DW-427 case near-miss
        [InlineData("  assets/heavy_tank.glb  ", "assets/heavy_tank.glb")]
        [InlineData("assets\\heavy_tank.glb", "assets/heavy_tank.glb")]  // a Windows-authored separator
        [InlineData("./assets/heavy_tank.glb", "assets/heavy_tank.glb")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData(null, "")]
        public void NormalizeKey_FoldsCaseSlashAndWhitespaceDrift(string? raw, string expected)
            => Assert.Equal(expected, MeshPathId.NormalizeKey(raw));

        [Fact]
        public void NormalizeKey_IsIdempotent()
        {
            string once = MeshPathId.NormalizeKey("  ./Assets\\Heavy_Tank.GLB ");
            Assert.Equal(once, MeshPathId.NormalizeKey(once));
            Assert.Equal("assets/heavy_tank.glb", once);
        }

        [Theory]
        [InlineData("assets/heavy_tank.glb", "heavy_tank.glb")]
        [InlineData("Heavy_Tank.GLB", "heavy_tank.glb")]
        [InlineData("res://a/b/c.glb", "c.glb")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void FileNameOf_ReturnsTheNormalizedLastSegment(string? path, string expected)
            => Assert.Equal(expected, MeshPathId.FileNameOf(path));

        // ── DW-427: the miss diagnostic (the whole point — an author had NOTHING to debug) ────────

        [Fact]
        public void DescribeRegistryMiss_NamesAuthoredValue_NormalizedKey_AndEveryRegisteredId()
        {
            string msg = MeshPathId.DescribeRegistryMiss(
                "Assets/Ghost.GLB", new[] { "assets/heavy_tank.glb", "assets/scout.glb" });

            Assert.Contains("Assets/Ghost.GLB", msg);            // what the author typed
            Assert.Contains("assets/ghost.glb", msg);            // the key actually looked up
            Assert.Contains("assets/heavy_tank.glb", msg);       // what IS available
            Assert.Contains("assets/scout.glb", msg);
            Assert.Contains("asset_files", msg);                 // the documented convention
        }

        [Fact]
        public void DescribeRegistryMiss_EmptyRegistry_SaysNothingIsRegisteredAtAll()
        {
            string msg = MeshPathId.DescribeRegistryMiss("assets/heavy_tank.glb", Array.Empty<string>());

            Assert.Contains("No custom assets are registered", msg);
            Assert.DoesNotContain("Did you mean", msg);
            Assert.Equal(msg, MeshPathId.DescribeRegistryMiss("assets/heavy_tank.glb", null));   // null == empty
        }

        [Fact]
        public void DescribeRegistryMiss_BareFilenameNearMiss_SuggestsTheFullLogicalId()
        {
            string msg = MeshPathId.DescribeRegistryMiss("heavy_tank.glb", new[] { "assets/heavy_tank.glb" });

            Assert.Contains("Did you mean 'assets/heavy_tank.glb'?", msg);
        }

        [Fact]
        public void DescribeRegistryMiss_UnrelatedId_OffersNoMisleadingSuggestion()
        {
            string msg = MeshPathId.DescribeRegistryMiss("assets/ghost.glb", new[] { "assets/heavy_tank.glb" });

            Assert.DoesNotContain("Did you mean", msg);
        }

        [Fact]
        public void DescribeRegistryMiss_LongRegistry_IsBoundedAndOrderIndependent()
        {
            List<string> ids = Enumerable.Range(0, MeshPathId.MaxListedIds + 5)
                                         .Select(i => $"assets/a{i:D2}.glb").ToList();

            string forward  = MeshPathId.DescribeRegistryMiss("assets/ghost.glb", ids);
            string reversed = MeshPathId.DescribeRegistryMiss("assets/ghost.glb", Enumerable.Reverse(ids));

            Assert.Equal(forward, reversed);                       // ordinal-sorted -> one stable line per miss
            Assert.Contains("(+5 more)", forward);                  // bounded listing
            Assert.Contains("assets/a00.glb", forward);
            Assert.DoesNotContain($"assets/a{MeshPathId.MaxListedIds + 4:D2}.glb", forward);
        }

        // ── DW-104: the lint itself ──────────────────────────────────────────────────────────────

        [Fact]
        public void FindMissingMeshFiles_DanglingResPath_ReportsLocatedMeshPathErrorPerEntry()
        {
            FactionDefinition def = MinimalFaction();
            def.Units[0].MeshPath = "res://assets/models/does_not_exist.glb";
            def.Units[1].MeshPath = "res://assets/models/also_gone.glb";
            def.Buildings[0].MeshPath = "res://assets/models/also_missing.glb";

            IReadOnlyList<(string FieldPath, string Message)> errors =
                MeshAssetLint.FindMissingMeshFiles(def, _ => false);

            // One error per offending entry (list-all, the FactionValidator/UnitDefinitionValidator convention) —
            // never first-fail, so every bad field can badge at once.
            Assert.Equal(3, errors.Count);
            Assert.All(errors, e => Assert.Equal("mesh_path", e.FieldPath));
            Assert.Contains(errors, e => e.Message.StartsWith("unit 'worker'.mesh_path:", StringComparison.Ordinal)
                                         && e.Message.Contains("does_not_exist.glb"));
            Assert.Contains(errors, e => e.Message.StartsWith("unit 'infantry'.mesh_path:", StringComparison.Ordinal)
                                         && e.Message.Contains("also_gone.glb"));
            Assert.Contains(errors, e => e.Message.StartsWith("building 'command_center'.mesh_path:", StringComparison.Ordinal)
                                         && e.Message.Contains("also_missing.glb"));
        }

        [Fact]
        public void FindMissingMeshFiles_LocatedShape_RoutesUnitsToRoster_AndBuildingsToBuildingsTech()
        {
            // The wizard jumps to the step named by StepForError, which keys on the "unit '"/"building '" prefix —
            // so the lint's message shape is load-bearing, not cosmetic.
            FactionDefinition def = MinimalFaction();
            def.Units[0].MeshPath = "res://missing_unit.glb";
            def.Buildings[0].MeshPath = "res://missing_building.glb";

            var errors = MeshAssetLint.FindMissingMeshFiles(def, _ => false);

            (string FieldPath, string Message) unitError =
                errors.First(e => e.Message.StartsWith("unit '", StringComparison.Ordinal));
            (string FieldPath, string Message) buildingError =
                errors.First(e => e.Message.StartsWith("building '", StringComparison.Ordinal));

            Assert.Equal(FactionDefinerStep.Roster,
                FactionDefinerWizardCore.StepForError(unitError.FieldPath, unitError.Message));
            Assert.Equal(FactionDefinerStep.BuildingsTech,
                FactionDefinerWizardCore.StepForError(buildingError.FieldPath, buildingError.Message));
        }

        [Fact]
        public void FindMissingMeshFiles_ExistingPaths_ReportNothing()
            => Assert.Empty(MeshAssetLint.FindMissingMeshFiles(MinimalFaction(), _ => true));

        [Fact]
        public void FindMissingMeshFiles_BlankMeshPath_IsNotThisLintsAxis()
        {
            // A blank mesh_path is the documented box-placeholder mid-edit state, and ValidateComplete already reports
            // it — reporting it here too would badge the same field twice with two different reasons.
            FactionDefinition def = MinimalFaction();
            def.Units[0].MeshPath = null;
            def.Units[1].MeshPath = "";
            def.Buildings[0].MeshPath = "   ";

            Assert.Empty(MeshAssetLint.FindMissingMeshFiles(def, _ => false));
            Assert.Contains(FactionValidator.ValidateComplete(def).Errors, e => e.FieldPath == "mesh_path");
        }

        [Fact]
        public void FindMissingMeshFiles_PackageAssetId_IsSkipped_ItResolvesThroughTheRegistryNotTheDisk()
        {
            FactionDefinition def = MinimalFaction();
            // Downloaded-package logical ids — they never live under res://, so a disk probe must not judge them.
            def.Units[0].MeshPath = "assets/heavy_tank.glb";
            def.Units[1].MeshPath = "assets/scout.glb";
            def.Buildings[0].MeshPath = "assets/depot.glb";

            Assert.Empty(MeshAssetLint.FindMissingMeshFiles(def, _ => false));
        }

        [Fact]
        public void FindMissingMeshFiles_NullDefOrNullProbeOrThrowingProbe_NeverThrows()
        {
            Assert.Empty(MeshAssetLint.FindMissingMeshFiles(null, _ => false));
            Assert.Empty(MeshAssetLint.FindMissingMeshFiles(MinimalFaction(), null));

            // A throwing probe reads as "missing" rather than propagating out of an authoring gate.
            var errors = MeshAssetLint.FindMissingMeshFiles(MinimalFaction(),
                                                           _ => throw new InvalidOperationException("probe blew up"));
            Assert.NotEmpty(errors);
        }

        [Fact]
        public void FindMissingMeshFiles_NullListsAndNullElements_AreTolerated()
        {
            var def = new FactionDefinition { Id = "sparse", Units = null!, Buildings = null! };
            Assert.Empty(MeshAssetLint.FindMissingMeshFiles(def, _ => false));

            def.Units = new List<UnitDefinition> { null!, new UnitDefinition { Id = "u", MeshPath = "res://u.glb" } };
            def.Buildings = new List<BuildingDefinition> { null! };
            Assert.Single(MeshAssetLint.FindMissingMeshFiles(def, _ => false));
        }

        // ── DW-104: the Godot-free res:// probe + project-root walk-up ────────────────────────────

        [Fact]
        public void ResExistsProbe_MapsResPathsUnderTheProjectRoot_AndFailsClosedOnAnythingElse()
        {
            string root = MakeFakeProject(out string _);
            try
            {
                File.WriteAllText(Path.Combine(root, "assets", "there.glb"), "glb");
                Func<string, bool> probe = MeshAssetLint.MakeResExistsProbe(root);

                Assert.True(probe("res://assets/there.glb"));
                Assert.True(probe("  res://assets/there.glb  "));       // trimmed like every other path read
                Assert.False(probe("res://assets/missing.glb"));
                Assert.False(probe("assets/there.glb"));                 // a package id is not disk-checkable
                Assert.False(probe("res://"));                           // nothing named
                Assert.False(probe("res://../assets/there.glb"));         // never probes outside the project
                Assert.False(probe(""));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void TryResolveProjectRoot_FindsTheProjectGodotAncestor_ElseNull()
        {
            string root = MakeFakeProject(out string nested);
            try
            {
                Assert.Equal(root, MeshAssetLint.TryResolveProjectRoot(root));
                Assert.Equal(root, MeshAssetLint.TryResolveProjectRoot(nested));
                Assert.Equal(root, MeshAssetLint.TryResolveProjectRoot(Path.Combine(root, "project.godot")));
                Assert.NotNull(MeshAssetLint.TryMakeResExistsProbe(nested));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void TryResolveProjectRoot_NoMarkerAnywhere_IsNull_SoTheLintFailsOpen()
        {
            string bare = Path.Combine(Path.GetTempPath(), $"mesh-lint-bare-{Guid.NewGuid():N}");
            Directory.CreateDirectory(bare);
            try
            {
                Assert.Null(MeshAssetLint.TryResolveProjectRoot(bare));
                Assert.Null(MeshAssetLint.TryMakeResExistsProbe(bare));
                Assert.Null(MeshAssetLint.TryResolveProjectRoot(null));
                Assert.Null(MeshAssetLint.TryResolveProjectRoot("   "));
            }
            finally { Directory.Delete(bare, recursive: true); }
        }

        // ── DW-104: the lint is LOAD-BEARING at the wizard Save edge ──────────────────────────────

        [Fact]
        public void TryFinish_DanglingMeshPath_IsBlocked_NoFileWritten()
        {
            string root = MakeFakeProject(out string factionsDir);
            try
            {
                File.WriteAllText(Path.Combine(root, "assets", "worker.glb"), "glb");
                File.WriteAllText(Path.Combine(root, "assets", "melee.glb"), "glb");
                FactionDefinition def = MinimalFaction();
                def.Id = "lint_blocked";
                // Both units resolve (fixture defaults, now on disk); only the building dangles — the DW-102 shape.
                def.Buildings[0].MeshPath = "res://assets/never_generated.glb";

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, factionsDir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "mesh_path"
                                                    && e.Message.Contains("never_generated.glb"));
                Assert.Equal(FactionDefinerStep.BuildingsTech, result.Step);
                Assert.Empty(Directory.GetFiles(factionsDir));                     // nothing, not even a stray .tmp
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void TryFinish_EveryMeshPathResolves_Writes()
        {
            string root = MakeFakeProject(out string factionsDir);
            try
            {
                File.WriteAllText(Path.Combine(root, "assets", "worker.glb"), "glb");
                File.WriteAllText(Path.Combine(root, "assets", "melee.glb"), "glb");
                File.WriteAllText(Path.Combine(root, "assets", "command_center.glb"), "glb");
                FactionDefinition def = MinimalFaction();
                def.Id = "lint_passes";

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, factionsDir);

                Assert.True(result.Ok, result.Errors.Count > 0 ? result.Errors[0].Message : "");
                Assert.True(File.Exists(result.WrittenPath));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void TryFinish_InjectedProbeOverridesTheDerivedOne()
        {
            // The presentation caller can hand in Godot's ResourceLoader.Exists (which understands import remaps);
            // an injected probe must win over the filesystem walk-up, in both directions.
            string root = MakeFakeProject(out string factionsDir);
            try
            {
                FactionDefinition def = MinimalFaction();
                def.Id = "probe_override";
                def.Units[0].MeshPath = "res://assets/not_on_disk.glb";
                def.Buildings[0].MeshPath = "res://assets/also_not_on_disk.glb";

                Assert.True(FactionDefinerWizardCore.TryFinish(def, factionsDir, null, _ => true).Ok);

                def.Id = "probe_override_2";
                FactionDefinerFinishResult blocked =
                    FactionDefinerWizardCore.TryFinish(def, factionsDir, null, _ => false);
                Assert.False(blocked.Ok);
                Assert.Contains(blocked.Errors, e => e.FieldPath == "mesh_path");
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_ThreadsTheSameLint()
        {
            string root = MakeFakeProject(out string factionsDir);
            try
            {
                FactionDefinition def = MinimalFaction();
                def.Id = "raw_json_lint";
                def.Units[0].MeshPath = "res://assets/never_generated.glb";
                string json = FactionDefinerWizardCore.SerializeDraftClean(def);

                FactionDefinerFinishResult result =
                    FactionDefinerWizardCore.TryFinishFromRawJson(json, factionsDir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "mesh_path"
                                                    && e.Message.StartsWith("unit '", StringComparison.Ordinal));
                Assert.Empty(Directory.GetFiles(factionsDir));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void TryFinish_NoProjectTreeOnDisk_SkipsTheLint_ExistingBehaviorUnchanged()
        {
            // A bare temp dir (every pre-existing wizard test) has no project.godot ancestor: the lint must fail OPEN
            // there rather than reject every path it cannot resolve.
            string dir = Path.Combine(Path.GetTempPath(), $"mesh-lint-noproject-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                FactionDefinition def = MinimalFaction();
                def.Id = "no_project_root";
                def.Units[0].MeshPath = "res://assets/definitely_missing.glb";

                Assert.True(FactionDefinerWizardCore.TryFinish(def, dir).Ok);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── DW-102: the shipped content actually resolves ─────────────────────────────────────────

        [Theory]
        [InlineData("alpha_faction.json")]
        [InlineData("beta_faction.json")]
        public void ShippedFaction_EveryAuthoredResMeshPath_ExistsOnDisk(string fileName)
        {
            string factionsDir = ResolveFactionsDir();
            Func<string, bool>? probe = MeshAssetLint.TryMakeResExistsProbe(factionsDir);
            Assert.NotNull(probe);   // the real repo tree HAS a project.godot — if this ever fails, the walk-up drifted

            FactionDefinition def = FactionDefinition.LoadFromFile(Path.Combine(factionsDir, fileName));

            IReadOnlyList<(string FieldPath, string Message)> missing =
                MeshAssetLint.FindMissingMeshFiles(def, probe);

            Assert.True(missing.Count == 0,
                $"{fileName} references mesh files that are not on disk:\n  " +
                string.Join("\n  ", missing.Select(m => m.Message)));
        }

        [Fact]
        public void ShippedFactions_AviaryBuildings_StillAuthorAMeshPath_SoTheBuildMenuButtonSurvives()
        {
            // DW-102's decision repointed the two aviary buildings at an on-disk placeholder rather than deleting the
            // entries (removing a cost/prereq-gated build-menu button would regress it to a phantom free-build
            // fallback). Both halves matter: the entry exists, and its path resolves (asserted above).
            string factionsDir = ResolveFactionsDir();
            foreach (string file in new[] { "alpha_faction.json", "beta_faction.json" })
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(Path.Combine(factionsDir, file));
                BuildingDefinition? aviary = def.Buildings.FirstOrDefault(b => b?.Id == "aviary");

                Assert.NotNull(aviary);
                Assert.Equal("Air", aviary!.ProducesCategory);
                Assert.True(MeshPathId.IsProjectResourcePath(aviary.MeshPath), $"{file}: aviary lost its mesh_path");
            }
        }

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

        /// <summary>A minimal ValidateComplete-passing faction (Worker + combat unit + one building) whose mesh paths
        /// each test repoints as it needs. Mirrors <c>FactionValidatorTests</c>' fixture shape.</summary>
        private static FactionDefinition MinimalFaction() => new()
        {
            Id = "lint_fixture",
            DisplayName = "Lint Fixture",
            Color = new[] { 0.2f, 0.5f, 0.8f, 1f },
            AiPreset = "balanced",
            Units = new List<UnitDefinition>
            {
                new() { Id = "worker",   DisplayName = "Worker",   Category = "Worker", MeshPath = "res://assets/worker.glb", Hp = 50f },
                new() { Id = "infantry", DisplayName = "Infantry", Category = "Melee",  MeshPath = "res://assets/melee.glb",  Hp = 80f },
            },
            Buildings = new List<BuildingDefinition>
            {
                new()
                {
                    Id = "command_center", DisplayName = "Command Center", Category = "Structure",
                    MeshPath = "res://assets/command_center.glb", Hp = 500f,
                    ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker",
                },
            },
        };

        /// <summary>A throwaway directory tree that looks like a Godot project: a <c>project.godot</c> marker, an
        /// <c>assets/</c> folder for fake GLBs, and a <c>resources/data/factions</c> folder (returned via
        /// <paramref name="factionsDir"/>) for the wizard to write into. Returns the project root.</summary>
        private static string MakeFakeProject(out string factionsDir)
        {
            string root = Path.Combine(Path.GetTempPath(), $"mesh-lint-project-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            factionsDir = Path.Combine(root, "resources", "data", "factions");
            Directory.CreateDirectory(factionsDir);
            File.WriteAllText(Path.Combine(root, MeshAssetLint.ProjectMarkerFile), "; fake project marker\n");
            return root;
        }

        /// <summary>The real shipped <c>resources/data/factions</c> directory (walk-up from the test assembly —
        /// mirrors <c>FactionValidatorTests.ResolveDataPath</c>).</summary>
        private static string ResolveFactionsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "factions");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/factions above {AppContext.BaseDirectory}");
        }
    }
}
