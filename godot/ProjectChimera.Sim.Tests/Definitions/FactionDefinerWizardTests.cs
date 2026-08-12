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
        // DW-505: these two rows used to pass a message NO producer emits (the un-prefixed "unit 'grunt' is missing
        // mesh_path…"), so they were green while the real ValidateComplete message — which carried a faction-located
        // prefix — mis-routed. They now carry the shape FactionValidator.ValidateComplete actually emits; the
        // MeshAssetLint sibling shape (identical, different reason) is covered in MeshPathResolutionTests, and
        // MeshPathBlankRoutesToStepOwningTheItem below re-derives both from the live validator so they cannot drift
        // apart again.
        [InlineData("mesh_path", "unit 'grunt'.mesh_path: must be authored (required for a complete/playable faction 'x').", FactionDefinerStep.Roster)]
        [InlineData("mesh_path", "building 'barracks'.mesh_path: must be authored (required for a complete/playable faction 'x').", FactionDefinerStep.BuildingsTech)]
        // DW-505: the pre-fix shape must ALSO route correctly now — StepForError strips a faction-located prefix
        // before sniffing the kind label, so no future faction-level producer can silently re-open the mis-route.
        [InlineData("mesh_path", "faction 'x'.mesh_path: unit 'grunt' is missing mesh_path (required for a complete/playable faction).", FactionDefinerStep.Roster)]
        [InlineData("mesh_path", "faction 'x'.mesh_path: building 'barracks' is missing mesh_path (required for a complete/playable faction).", FactionDefinerStep.BuildingsTech)]
        [InlineData("hp", "building 'barracks'.hp: must be a positive value.", FactionDefinerStep.BuildingsTech)]
        // DW-106 / DW-114: a hero is a roster unit → Roster; a signature effect id is a faction-config default → AI Preset.
        [InlineData("hero_unit_id", "faction 'x'.hero_unit_id: names unit 'ghost' which is not in this faction's roster.", FactionDefinerStep.Roster)]
        [InlineData("signature_mechanic_effect_id", "faction 'x'.signature_mechanic_effect_id: 'no_such_effect' does not resolve to any loaded ability.", FactionDefinerStep.AiPreset)]
        // DW-114: the two economy fields the Starting Conditions step renders (LIVE since DW-115's finite/non-negative
        // check) and the remaining signature_mechanic* descriptor paths; DW-116: the raw-JSON parse-failure path.
        // Without the explicit cases every one of these lands on the BuildingsTech sniff-default, which has no UI for
        // any of them.
        [InlineData("starting_ore", "faction 'x'.starting_ore: must be a finite value >= 0 (found -5).", FactionDefinerStep.StartingConditions)]
        [InlineData("starting_crystal", "faction 'x'.starting_crystal: must be a finite value >= 0 (found NaN).", FactionDefinerStep.StartingConditions)]
        [InlineData("signature_mechanic", "faction 'x'.signature_mechanic: must be authored.", FactionDefinerStep.AiPreset)]
        [InlineData("signature_mechanic_display", "faction 'x'.signature_mechanic_display: must be authored.", FactionDefinerStep.AiPreset)]
        [InlineData("raw_json", "could not parse JSON: '{' is an invalid start of a property name.", FactionDefinerStep.NameColor)]
        // DW-735/DW-776: the last field path still riding the sniff-default. "faction is null." names the whole draft,
        // not a control, so it lands with raw_json on Name & Color — never on Buildings & Tech, which has no UI for it.
        [InlineData("faction", "faction is null.", FactionDefinerStep.NameColor)]
        public void StepForError_MapsFieldPathAndMessageKindLabel_ToExpectedStep(
            string fieldPath, string message, FactionDefinerStep expected)
        {
            Assert.Equal(expected, FactionDefinerWizardCore.StepForError(fieldPath, message));
        }

        // ── DW-735/DW-776: the `faction` path, re-derived from BOTH live producers ────────────────────────────

        [Fact]
        public void NullDraft_RoutesToNameColor_FromEveryLiveProducerOfTheFactionPath()
        {
            // The regression: StepForError had no `faction` case, so a null-draft error fell through to the
            // Buildings & Tech sniff-default — a step with no control for "there is no faction at all". Both
            // producers are driven for real here (never a hand-typed message) so the assertion cannot go stale if
            // either guard's wording changes; the routing contract is what is pinned.

            // Producer 1 — FactionDefinerWizardCore.TryFinish's own null-def guard.
            FactionDefinerFinishResult finish = FactionDefinerWizardCore.TryFinish(null!, Path.GetTempPath());
            Assert.False(finish.Ok);
            (string FieldPath, string Message) finishError = Assert.Single(finish.Errors);
            Assert.Equal("faction", finishError.FieldPath);
            Assert.Equal(FactionDefinerStep.NameColor, finish.Step);
            Assert.Equal(FactionDefinerStep.NameColor,
                FactionDefinerWizardCore.StepForError(finishError.FieldPath, finishError.Message));

            // Producer 2 — FactionValidator.Validate's null-def guard (the LoadFromFile path).
            FactionValidationResult validation = FactionValidator.Validate(null!);
            Assert.False(validation.Ok);
            (string FieldPath, string Message) validatorError =
                Assert.Single(validation.Errors, e => e.FieldPath == "faction");
            Assert.Equal(FactionDefinerStep.NameColor,
                FactionDefinerWizardCore.StepForError(validatorError.FieldPath, validatorError.Message));
        }

        [Fact]
        public void StepForError_UnknownFieldPath_StillFallsBackToBuildingsTech()
        {
            // The DW-114/DW-116 additions must NOT turn the shared-path sniff-default into a per-path whitelist: a
            // faction-level structural message (or a research entry) with no "unit '" prefix still lands on
            // Buildings & Tech, which is where research lives.
            Assert.Equal(FactionDefinerStep.BuildingsTech,
                FactionDefinerWizardCore.StepForError("research", "faction 'x'.research: duplicate research id 'armor'."));
        }

        [Fact]
        public void StepForError_FactionLevelStructuralMessages_StillFallBackToBuildingsTech_AfterPrefixStripping()
        {
            // DW-505 guard: stripping the "faction '<id>'.<field>: " prefix before the kind-label sniff must not turn
            // FACTION-level reasons into roster hits. None of these names a unit or building as its subject, so every
            // one still lands on the Buildings & Tech fallback exactly as before the strip existed.
            Assert.Equal(FactionDefinerStep.BuildingsTech,
                FactionDefinerWizardCore.StepForError("buildings", "faction 'x'.buildings: buildings list is null."));
            Assert.Equal(FactionDefinerStep.BuildingsTech,
                FactionDefinerWizardCore.StepForError("buildings",
                    "faction 'x'.buildings: duplicate building id 'barracks' (another building already uses this id)."));
            Assert.Equal(FactionDefinerStep.BuildingsTech,
                FactionDefinerWizardCore.StepForError("research",
                    "faction 'x'.research: research id 'armor' collides with a building id (ids must be unique across buildings and research)."));

            // Degenerate/adversarial inputs never throw — a malformed or truncated prefix simply fails to strip.
            Assert.Equal(FactionDefinerStep.BuildingsTech, FactionDefinerWizardCore.StepForError("mesh_path", null!));
            Assert.Equal(FactionDefinerStep.BuildingsTech, FactionDefinerWizardCore.StepForError("mesh_path", ""));
            Assert.Equal(FactionDefinerStep.BuildingsTech, FactionDefinerWizardCore.StepForError("mesh_path", "faction 'x"));
            Assert.Equal(FactionDefinerStep.BuildingsTech, FactionDefinerWizardCore.StepForError("mesh_path", "faction 'x'.mesh_path"));
        }

        // ── DW-505: the mesh_path kind label re-derived from the LIVE validator, never a hand-typed string ─────

        [Theory]
        [InlineData(true, FactionDefinerStep.Roster)]
        [InlineData(false, FactionDefinerStep.BuildingsTech)]
        public void ValidateCompleteMeshPathError_RoutesToTheStepOwningTheItem(bool blankTheUnit, FactionDefinerStep expected)
        {
            // The regression this pins: the theory rows above once asserted a message shape NO producer emitted, so
            // they stayed green while the message FactionValidator really emits ("faction 'x'.mesh_path: unit '…' is
            // missing mesh_path…") pushed the kind label off the front and mis-routed every missing UNIT mesh_path to
            // Buildings & Tech. Taking the message straight off ValidateComplete makes the assertion impossible to
            // satisfy with a message the validator does not actually produce.
            FactionPresetPool pool = ScanRealAlphaBeta();
            FactionDefinition def = NewDraft("dw505_mesh_path_routing");
            Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
            if (blankTheUnit) def.Units[1].MeshPath = null;   // infantry: a roster item -> the Roster step
            else def.Buildings[0].MeshPath = "";              // command_center: a build item -> Buildings & Tech

            FactionValidationResult result = FactionValidator.ValidateComplete(def);

            (string FieldPath, string Message) error =
                Assert.Single(result.Errors, e => e.FieldPath == "mesh_path");
            Assert.Equal(expected, FactionDefinerWizardCore.StepForError(error.FieldPath, error.Message));
        }

        [Fact]
        public void ValidateCompleteMeshPathError_UsesTheSameKindLabelShapeAsMeshAssetLint()
        {
            // DW-505: the two producers on the mesh_path axis (this blank-value check and MeshAssetLint's
            // dangling-file check) must speak one shape, or the wizard routes correctly for one and not the other.
            // Both are "{kind} '{id}'.mesh_path: {reason}" — the leading-kind-label convention StepForError sniffs.
            FactionPresetPool pool = ScanRealAlphaBeta();
            FactionDefinition def = NewDraft("dw505_shape_parity");
            Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
            def.Units[0].MeshPath = null;
            def.Buildings[0].MeshPath = "   ";   // whitespace-only counts as blank

            IReadOnlyList<(string FieldPath, string Message)> blanks = FactionValidator.ValidateComplete(def).Errors;
            Assert.Contains(blanks, e => e.FieldPath == "mesh_path"
                && e.Message.StartsWith("unit 'worker'.mesh_path:", StringComparison.Ordinal));
            Assert.Contains(blanks, e => e.FieldPath == "mesh_path"
                && e.Message.StartsWith("building 'command_center'.mesh_path:", StringComparison.Ordinal));

            // The faction id survives the reshaping — the message still says which faction is incomplete.
            Assert.All(blanks.Where(e => e.FieldPath == "mesh_path"),
                e => Assert.Contains("dw505_shape_parity", e.Message));

            // Same leading shape as the sibling lint on the same field path.
            FactionDefinition dangling = NewDraft("dw505_shape_parity");
            Pick(dangling, ScanRealAlphaBeta(), unitIds: new[] { "worker" }, buildingIds: new[] { "command_center" });
            dangling.Units[0].MeshPath = "res://missing.glb";
            dangling.Buildings[0].MeshPath = "res://missing_too.glb";
            IReadOnlyList<(string FieldPath, string Message)> lint =
                MeshAssetLint.FindMissingMeshFiles(dangling, _ => false);
            Assert.Contains(lint, e => e.Message.StartsWith("unit 'worker'.mesh_path:", StringComparison.Ordinal));
            Assert.Contains(lint, e => e.Message.StartsWith("building 'command_center'.mesh_path:", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(true, FactionDefinerStep.Roster)]
        [InlineData(false, FactionDefinerStep.BuildingsTech)]
        public void TryFinish_BlankMeshPath_BlocksAndRoutesToTheStepOwningTheItem_NoFileWritten(
            bool blankTheUnit, FactionDefinerStep expected)
        {
            // DW-505 end-to-end: the live UX defect was only observable through the real Finish path, where
            // FactionDefinerFinishResult.Failure feeds errors[0] into StepForError and the panel jumps there. Before
            // the fix a creator who forgot a UNIT mesh_path was dropped on Buildings & Tech, which renders no roster
            // control — the remedy is the Roster step's mesh field.
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("dw505_finish_routing");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                if (blankTheUnit) def.Units[1].MeshPath = "";
                else def.Buildings[0].MeshPath = null;

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Single(result.Errors);   // exactly the mesh_path error — nothing else about this draft is invalid
                Assert.Equal("mesh_path", result.Errors[0].FieldPath);
                Assert.Equal(expected, result.Step);
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── DW-114 / DW-116: the routing observed end-to-end through a real Finish attempt ─────────────────────

        [Theory]
        [InlineData(-5f, 0f, "starting_ore")]
        [InlineData(200f, -1f, "starting_crystal")]
        [InlineData(float.NaN, 0f, "starting_ore")]
        [InlineData(200f, float.PositiveInfinity, "starting_crystal")]
        public void TryFinish_InvalidStartingResource_BlocksAndRoutesToStartingConditionsStep_NoFileWritten(
            float ore, float crystal, string expectedFieldPath)
        {
            // DW-114 is LIVE, not latent: FactionValidator.Validate emits located starting_ore/starting_crystal errors
            // (DW-115), and before the StepForError cases existed the wizard jumped the creator to Buildings & Tech —
            // a step with no ore/crystal control at all. The remedy is the Starting Conditions step's two NumInputs.
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("dw114_starting_resource_test");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                def.StartingOre = ore;
                def.StartingCrystal = crystal;

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Single(result.Errors);   // exactly the economy error — nothing else about this draft is invalid
                Assert.Equal(expectedFieldPath, result.Errors[0].FieldPath);
                Assert.Equal(FactionDefinerStep.StartingConditions, result.Step);
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_MalformedJson_RoutesToNameColorStep_NotBuildingsTech()
        {
            // DW-116: the parse-failure error's ("raw_json", …) field path must map to a defensible step for any
            // consumer of result.Step. FactionDefinerPanel skips the jump in Advanced mode today, so this is the only
            // surface that can observe the mapping — Buildings & Tech would be actively misleading.
            string dir = MakeTempDir();
            try
            {
                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson("{ not valid json !!", dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "raw_json");
                Assert.Equal(FactionDefinerStep.NameColor, result.Step);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_LiteralNullJson_RoutesToNameColorStep_NotBuildingsTech()
        {
            // DW-116, second raw_json producer: the "JSON parsed to no faction object" branch (the literal `null`)
            // shares the raw_json field path and must route identically.
            string dir = MakeTempDir();
            try
            {
                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson("null", dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "raw_json");
                Assert.Equal(FactionDefinerStep.NameColor, result.Step);
            }
            finally { Directory.Delete(dir, recursive: true); }
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

        // ── DW-112: the File.Exists pre-check → File.Move(overwrite:false) TOCTOU window ───────────────────────
        //
        // The pre-check above and the atomic move are two separate observations of the same fact. A destination the
        // pre-check did not see — created between the two by a second wizard session or an external tool, or simply
        // invisible to File.Exists because it is a DIRECTORY — used to fall through to the generic
        // "save failed: {ex.Message}" branch, handing the creator the raw OS string "Cannot create a file when that
        // file already exists." for exactly the situation the pre-check words helpfully. The move now classifies its
        // own failure and reuses the pre-check's located `id` error.

        [Fact]
        public void TryFinish_TargetNameTakenByDirectory_ReportsFriendlyIdError_NotRawSaveFailed()
        {
            // RED without the fix (measured on this machine, Win11 26200): File.Exists returns FALSE for a directory,
            // so the pre-check waves it through, the .tmp is written and self-checks fine, and only
            // File.Move(overwrite:false) fails — with an IOException the old generic catch surfaced verbatim as
            // "save failed: Cannot create a file when that file already exists." on the id field.
            //
            // This is the one arm of the DW-112 window a single-threaded test can stage end-to-end, so it is what
            // pins the wiring from TryFinish's catch into TryClassifyTargetCollision. The concurrent-FILE arm needs a
            // target that materialises strictly between the two observations, which no in-process test can order —
            // it is pinned directly against the classifier below.
            string dir = MakeTempDir();
            try
            {
                string targetAbs = Path.Combine(dir, "blocked_faction.json");
                Directory.CreateDirectory(targetAbs);   // the destination NAME is taken, by a folder

                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("blocked");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                (string FieldPath, string Message) err = Assert.Single(result.Errors, e => e.FieldPath == "id");
                Assert.Contains("already exists", err.Message, StringComparison.Ordinal);
                Assert.Contains("choose a different id", err.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("save failed", err.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(FactionDefinerStep.NameColor, result.Step);

                Assert.True(Directory.Exists(targetAbs));                  // the blocking folder is left alone
                Assert.False(File.Exists(targetAbs + ".tmp"));             // the failed write leaves no stray .tmp
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryClassifyTargetCollision_TargetFileAppearedAfterThePreCheck_ReusesTheExactPreCheckMessage()
        {
            // The concurrent-FILE arm — the literal TOCTOU DW-112 names. Driven at the classifier because the window
            // it describes (target absent at the pre-check, present at the move) cannot be staged from inside the
            // single call it spans. Asserts message IDENTITY, not just "contains 'already exists'": the whole point
            // of the entry is that a creator sees the SAME friendly sentence whichever observation catches the
            // collision, so the two wordings must be produced by one builder and are pinned here against the real
            // pre-check output for the same path.
            string dir = MakeTempDir();
            try
            {
                string targetAbs = Path.Combine(dir, "raced_faction.json");
                File.WriteAllText(targetAbs, "SENTINEL-DO-NOT-OVERWRITE");

                // The message the PRE-CHECK arm produces for this exact path, read off a real TryFinish run.
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("raced");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                FactionDefinerFinishResult preCheck = FactionDefinerWizardCore.TryFinish(def, dir);
                Assert.False(preCheck.Ok);
                string preCheckMessage = Assert.Single(preCheck.Errors, e => e.FieldPath == "id").Message;

                // The message the POST-MOVE arm produces for the same path, given the IOException an
                // overwrite:false move raises when the destination is occupied.
                FactionDefinerFinishResult? raced = FactionDefinerWizardCore.TryClassifyTargetCollision(
                    new IOException("Cannot create a file when that file already exists."), targetAbs);

                Assert.NotNull(raced);
                Assert.False(raced!.Value.Ok);
                Assert.Equal(FactionDefinerStep.NameColor, raced.Value.Step);
                Assert.Equal(preCheckMessage,
                    Assert.Single(raced.Value.Errors, e => e.FieldPath == "id").Message);

                // A move can also fail with UnauthorizedAccessException; same classification.
                Assert.NotNull(FactionDefinerWizardCore.TryClassifyTargetCollision(
                    new UnauthorizedAccessException("denied"), targetAbs));

                Assert.Equal("SENTINEL-DO-NOT-OVERWRITE", File.ReadAllText(targetAbs));   // classification reads only
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryClassifyTargetCollision_FreeTargetOrUnrelatedFailure_ReturnsNull_SoSaveFailedStands()
        {
            // The classifier must not dress every write failure up as a collision — a full disk or a locked .tmp is
            // not "choose a different id", and telling the creator it is would be a worse lie than the raw OS string
            // DW-112 replaces.
            string dir = MakeTempDir();
            try
            {
                string freeTarget = Path.Combine(dir, "nothing_here_faction.json");

                // Right exception family, but nothing occupies the destination -> not a collision.
                Assert.Null(FactionDefinerWizardCore.TryClassifyTargetCollision(
                    new IOException("There is not enough space on the disk."), freeTarget));

                // Destination occupied, but the failure is not one a filesystem move raises -> not a collision.
                File.WriteAllText(freeTarget, "x");
                Assert.Null(FactionDefinerWizardCore.TryClassifyTargetCollision(
                    new InvalidOperationException("something else entirely"), freeTarget));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinish_WriteFailsBeforeTheMove_StillReportsSaveFailed_NotACollision()
        {
            // Companion guard to the two above: the DW-112 re-read is scoped to the MOVE. A serialize/write/self-check
            // failure keeps reporting its own reason, because that reason is the truthful account of what went wrong.
            // Staged by taking the .tmp NAME with a directory, which makes File.WriteAllText throw
            // UnauthorizedAccessException while the real target is still free.
            string dir = MakeTempDir();
            try
            {
                string targetAbs = Path.Combine(dir, "tmp_blocked_faction.json");
                Directory.CreateDirectory(targetAbs + ".tmp");

                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("tmp_blocked");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                (string FieldPath, string Message) err = Assert.Single(result.Errors, e => e.FieldPath == "id");
                Assert.Contains("save failed", err.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("choose a different id", err.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(targetAbs));   // and nothing was written to the target
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

        // ── DW-528: reserved Windows device basename on the free-text filename path ───────────────────────────

        [Theory]
        [InlineData("con.x")]     // -> "con.x_faction.json" -> Win32 matches the segment before the FIRST dot => CON
        [InlineData("nul.")]      // -> "nul._faction.json"                                                   => NUL
        [InlineData("com1.a")]    // -> "com1.a_faction.json"                                                 => COM1
        [InlineData("LPT9.x")]    // case-insensitive                                                         => LPT9
        public void TryFinish_ReservedDeviceBasenameId_BlocksNamingIdField_WritesNothing(string reservedId)
        {
            // RED without the fix (measured): a '.' is a legal filename char and there is no "..", so the
            // separator/traversal guard passes the id straight through, and Finish reports SUCCESS having written a
            // faction file whose leading segment is a Win32 device name (Win32 matches everything before the FIRST
            // dot — "NUL.tar.gz is equivalent to NUL"). Whether the local filesystem refuses that name depends on the
            // Windows build (this dev machine's Win11 26200 does not), which is exactly why the guard has to be a
            // validation rule rather than a caught IO error: the file saves here and is unopenable elsewhere.
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft(reservedId);
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                (string FieldPath, string Message) err =
                    Assert.Single(result.Errors, e => e.FieldPath == "id");
                Assert.Contains("reserved", err.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("save failed", err.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(FactionDefinerStep.NameColor, result.Step);

                // Blocked BEFORE any write — no faction file and no stray .tmp anywhere under the temp dir.
                Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Theory]
        [InlineData("con")]       // -> "con_faction.json": an ORDINARY file — the suffix is what makes it safe
        [InlineData("nul")]
        [InlineData("com1")]
        public void TryFinish_BareReservedWordId_StillSaves_BecauseTheSuffixMakesTheBasenameSafe(string bareId)
        {
            // The other half of the guard's contract, and the reason it inspects the ASSEMBLED name rather than the
            // bare id: a blunt id-level check (DW-454's whole-id helper) would refuse these for no filesystem reason.
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft(bareId);
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.True(result.Ok, string.Join(" | ", result.Errors.Select(e => e.Message)));
                Assert.Equal(Path.Combine(dir, bareId + "_faction.json"), result.WrittenPath);
                Assert.True(File.Exists(result.WrittenPath));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_ReservedDeviceBasenameId_IsBlockedByTheSameGate()
        {
            // The Advanced raw-JSON pane delegates to TryFinish, so it must inherit the guard — a creator who pastes
            // the id instead of typing it in the Name & Color step gets the same located reject, not "save failed:".
            string dir = MakeTempDir();
            try
            {
                string json = BuildValidRawFactionJson("aux.raw");

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(json, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "id"
                                                    && e.Message.Contains("reserved", StringComparison.OrdinalIgnoreCase));
                Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void FactionFileSuffix_MatchesTheDiscoveryGlobAWrittenFileIsFoundBy()
        {
            // The suffix is now a named constant so the reserved-device guard re-derives itself if it is ever changed
            // (DW-528). Tie it to the "*_faction.json" discovery convention: a wizard-written file under a name the
            // loader's glob misses would save successfully and then be invisible in every faction picker.
            Assert.Equal("_faction.json", FactionDefinerWizardCore.FactionFileSuffix);

            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("discoverable");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.True(result.Ok);
                Assert.Contains(Directory.GetFiles(dir, "*_faction.json"), f => f == result.WrittenPath);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── ClearStaleHeroReference (Story 5.6, Spec Change Log review pass 1) ─────────────────────────────────

        [Fact]
        public void ClearStaleHeroReference_HeroIdMatchesNoUnit_ClearsAndReturnsTrue()
        {
            var def = new FactionDefinition
            {
                HeroUnitId = "ghost_unit",
                Units = new List<UnitDefinition> { new UnitDefinition { Id = "worker", IsHero = false } },
            };

            bool cleared = FactionDefinerWizardCore.ClearStaleHeroReference(def);

            Assert.True(cleared);
            Assert.Null(def.HeroUnitId);
        }

        [Fact]
        public void ClearStaleHeroReference_HeroIdMatchesLiveUnit_LeavesItAlone_ReturnsFalse()
        {
            var heroUnit = new UnitDefinition { Id = "champion", IsHero = true };
            var def = new FactionDefinition
            {
                HeroUnitId = "champion",
                Units = new List<UnitDefinition> { heroUnit },
            };

            bool cleared = FactionDefinerWizardCore.ClearStaleHeroReference(def);

            Assert.False(cleared);
            Assert.Equal("champion", def.HeroUnitId);
        }

        [Fact]
        public void ClearStaleHeroReference_NoHeroIdAuthored_NoOp_ReturnsFalse()
        {
            var def = new FactionDefinition { HeroUnitId = null, Units = new List<UnitDefinition>() };

            Assert.False(FactionDefinerWizardCore.ClearStaleHeroReference(def));
            Assert.Null(def.HeroUnitId);
        }

        [Fact]
        public void ClearStaleHeroReference_NullUnitsList_NullGuarded_ClearsStaleId_NoThrow()
        {
            // A raw-JSON-deserialized FactionDefinition can carry a null Units list (Edge Case Hunter, review
            // pass 1) — must not NRE, must still clear the dangling id.
            var def = new FactionDefinition { HeroUnitId = "ghost", Units = null! };

            bool cleared = FactionDefinerWizardCore.ClearStaleHeroReference(def);

            Assert.True(cleared);
            Assert.Null(def.HeroUnitId);
        }

        [Fact]
        public void ClearStaleHeroReference_NullDraft_NoThrow_ReturnsFalse()
        {
            Assert.False(FactionDefinerWizardCore.ClearStaleHeroReference(null));
        }

        // ── TryFinish: Hero / Persistence + empty ai_preset (Story 5.6, FR-18/AR-12) ───────────────────────────

        [Fact]
        public void TryFinish_HeroAndPersistenceSet_WritesBothToFile_HeroUnitIsExplicitlyHeroFlagged()
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("hero_persist_test");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                // Neither shipped alpha/beta unit is hero-flagged (confirmed by review, Spec Change Log) — hand-add
                // an explicitly IsHero=true unit so this test proves the hero-round-trip behavior against a REAL
                // hero, not merely that an arbitrary unit id string round-trips (the defect the prior pass's
                // fixture baked in).
                var heroUnit = new UnitDefinition
                {
                    Id = "champion_of_the_order", DisplayName = "Champion", Category = "Melee", IsHero = true,
                    MeshPath = "res://resources/models/placeholder.glb",   // ValidateComplete requires mesh_path
                };
                def.Units.Add(heroUnit);
                def.HeroUnitId = heroUnit.Id;
                def.PersistenceEnabled = true;

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.True(result.Ok);
                FactionDefinition reloaded = FactionDefinition.LoadFromFile(result.WrittenPath!);
                Assert.Equal("champion_of_the_order", reloaded.HeroUnitId);
                Assert.True(reloaded.PersistenceEnabled);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinish_EmptyAiPreset_BlocksAtAiPresetStep_LocatedError_NoFileWritten()
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("empty_preset_test");
                def.AiPreset = "";   // Story 5.6: "no preset selected" is now reachable through the real picker
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
                Assert.Equal(FactionDefinerStep.AiPreset, result.Step);
                Assert.False(File.Exists(Path.Combine(dir, "empty_preset_test_faction.json")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinish_DanglingHeroUnitId_ClearedInsideTryFinish_IndependentOfAnyPanelStepRender()
        {
            // Pins the "never a dangling hero reference reaches a written file" guarantee to TryFinish ITSELF
            // (Spec Change Log, review pass 1): the def is constructed directly here with NO Panel/step-render
            // code involved at all, so a passing assertion proves the guarantee does not rest on a UI rebuild hook.
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("dangling_hero_test");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                def.HeroUnitId = "no_such_unit_in_this_roster";   // dangling — never added to def.Units

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(def, dir);

                Assert.True(result.Ok);   // TryFinish clears the dangling reference rather than blocking on it
                FactionDefinition reloaded = FactionDefinition.LoadFromFile(result.WrittenPath!);
                Assert.Null(reloaded.HeroUnitId);   // the written file carries NO dangling hero_unit_id
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── TryFinishFromRawJson (Story 5.6, Advanced mode) ─────────────────────────────────────────────────────

        [Fact]
        public void TryFinishFromRawJson_ValidJson_WritesFile_SameGateAsSimplePath()
        {
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("raw_json_valid_test");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                string json = FactionDefinerWizardCore.SerializeDraftClean(def);

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(json, dir);

                Assert.True(result.Ok);
                Assert.True(File.Exists(result.WrittenPath));
                FactionDefinition reloaded = FactionDefinition.LoadFromFile(result.WrittenPath!);
                Assert.Equal("raw_json_valid_test", reloaded.Id);
                Assert.Equal("balanced", reloaded.AiPreset);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_MalformedJson_BlocksWithLocatedRawJsonError_NoFileWritten()
        {
            string dir = MakeTempDir();
            try
            {
                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson("{ not valid json !!", dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "raw_json");
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_LiteralNullJson_BlocksWithLocatedRawJsonError_NoThrow()
        {
            string dir = MakeTempDir();
            try
            {
                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson("null", dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "raw_json");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_ValidJsonMissingAiPreset_BlockedBySameAiPresetValidatorError_NoFileWritten()
        {
            // NOTE (DW-117): this covers the PRESENT-but-empty ai_preset case, NOT key absence — SerializeDraftClean
            // always writes the key (root["ai_preset"] = def.AiPreset ?? ""). Key-absence is covered by
            // TryFinishFromRawJson_AiPresetKeyAbsent_BlockedSameAsSimpleMode_NoFileWritten below.
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("raw_json_missing_preset_test");
                def.AiPreset = "";
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                string json = FactionDefinerWizardCore.SerializeDraftClean(def);

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(json, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
                Assert.False(File.Exists(Path.Combine(dir, "raw_json_missing_preset_test_faction.json")));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_AiPresetKeyAbsent_BlockedSameAsSimpleMode_NoFileWritten()
        {
            // DW-117: a syntactically valid faction doc that OMITS the ai_preset key entirely must be blocked by the
            // same "must be authored" validator error Simple mode produces (its forced "" AiPreset) — an omitted key
            // must NOT silently inherit the C# "balanced" default and pass. Also assert the located error routes the
            // wizard to the AI Preset step (the user-visible consequence of the located ai_preset error).
            string dir = MakeTempDir();
            try
            {
                string json = RewriteAiPresetLine(BuildValidRawFactionJson("raw_json_absent_preset_test"), null);
                Assert.DoesNotContain("ai_preset", json);

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(json, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
                Assert.Equal(FactionDefinerStep.AiPreset, result.Step);
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_AiPresetKeyAbsentWithDuplicateOtherKey_StillBlocked_NoFileWritten()
        {
            // DW-117 (Edge Case Hunter, review pass 1): the re-inspection must tolerate duplicate property names
            // exactly as the JsonSerializer deserialize does (last-wins). A doc that omits ai_preset AND duplicates
            // another top-level key deserializes fine (AiPreset stays "balanced"); the key-presence re-parse must
            // still fire and force "" so it is blocked. The earlier JsonNode.Parse approach THREW on the duplicate
            // key, hit the best-effort catch, and silently reopened the bypass — this test locks that regression out.
            string dir = MakeTempDir();
            try
            {
                string absent = RewriteAiPresetLine(BuildValidRawFactionJson("raw_json_dupkey_preset_test"), null);
                string dupJson = DuplicateTopLevelIdLine(absent);

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(dupJson, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_AiPresetKeyOffCase_TreatedAsAbsent_Blocked_NoFileWritten()
        {
            // DW-117: the fix's case-sensitivity is load-bearing. An off-case key ("Ai_Preset") is ignored by the
            // case-sensitive deserialize (AiPreset stays at the "balanced" default) AND is absent to the case-
            // sensitive JsonDocument key check, so it must be forced to "" and blocked — never silently accepted.
            string dir = MakeTempDir();
            try
            {
                string json = RewriteAiPresetLine(BuildValidRawFactionJson("raw_json_offcase_preset_test"),
                    "\"Ai_Preset\": \"balanced\",");

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(json, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_AiPresetKeyPresentButNull_Blocked_NormalizationDoesNotFire()
        {
            // DW-117 (I/O matrix): a PRESENT ai_preset key with JSON null keeps its existing outcome — blocked by the
            // "must be authored" validator error (deserialize sets AiPreset=null, coalesced to "" by the validator).
            // The key IS present, so the DW-117 key-absent normalization does not fire; the block is the validator's.
            string dir = MakeTempDir();
            try
            {
                string json = RewriteAiPresetLine(BuildValidRawFactionJson("raw_json_null_preset_test"),
                    "\"ai_preset\": null,");

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(json, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_AiPresetKeyPresentButUnknownValue_BlockedNotRecognized_NoFileWritten()
        {
            // DW-117 (I/O matrix): a PRESENT ai_preset key with an unknown value keeps its existing outcome —
            // blocked by the closed-set "not a recognized ai_preset" validator error, unaffected by this change.
            string dir = MakeTempDir();
            try
            {
                string json = RewriteAiPresetLine(BuildValidRawFactionJson("raw_json_unknown_preset_test"),
                    "\"ai_preset\": \"aggressive\",");

                FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinishFromRawJson(json, dir);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── TryFinishFromRawJson: signature_mechanic_effect_id resolution (Story 14.3, DW-106) ──────────────────

        [Fact]
        public void TryFinishFromRawJson_DanglingSignatureEffectId_WithRegistry_BlocksAtAiPresetStep_NoFileWritten()
        {
            // DW-106: the Advanced raw-JSON pane authors a signature_mechanic_effect_id that resolves to no ability
            // in the threaded registry → the Finish gate must block with a located signature_mechanic_effect_id
            // error routed to the AI Preset step, and write no file.
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("dangling_sig_test");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                def.SignatureMechanicEffectId = "no_such_effect";   // dangling — absent from the registry below
                string json = FactionDefinerWizardCore.SerializeDraftClean(def);
                Assert.Contains("signature_mechanic_effect_id", json);   // SerializeDraftClean wrote the key

                var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "some_other_ability" } });

                FactionDefinerFinishResult result =
                    FactionDefinerWizardCore.TryFinishFromRawJson(json, dir, registry);

                Assert.False(result.Ok);
                Assert.Contains(result.Errors, e =>
                    e.FieldPath == "signature_mechanic_effect_id" && e.Message.Contains("no_such_effect"));
                Assert.Equal(FactionDefinerStep.AiPreset, result.Step);
                Assert.Empty(Directory.GetFiles(dir, "*_faction.json*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryFinishFromRawJson_ResolvingSignatureEffectId_WithRegistry_WritesFile()
        {
            // Counterpart to the dangling case: a signature_mechanic_effect_id that DOES resolve against the threaded
            // registry passes the gate and writes the file (proves the check is a real resolution, not a blanket block).
            string dir = MakeTempDir();
            try
            {
                FactionPresetPool pool = ScanRealAlphaBeta();
                FactionDefinition def = NewDraft("resolving_sig_test");
                Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
                def.SignatureMechanicEffectId = "real_effect";
                string json = FactionDefinerWizardCore.SerializeDraftClean(def);

                var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "real_effect" } });

                FactionDefinerFinishResult result =
                    FactionDefinerWizardCore.TryFinishFromRawJson(json, dir, registry);

                Assert.True(result.Ok);
                Assert.True(File.Exists(result.WrittenPath));

                // Round-trip: the resolving signature id must actually survive the write (SerializeDraftClean emits
                // signature_mechanic_effect_id only when non-empty, so a regression that dropped it on write would
                // otherwise still pass a bare file-exists assertion).
                FactionDefinition reloaded = FactionDefinition.LoadFromFile(result.WrittenPath!);
                Assert.Equal("real_effect", reloaded.SignatureMechanicEffectId);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── Test helpers ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A complete, valid faction as clean raw JSON (the Advanced-pane input shape): picks the real
        /// alpha/beta worker+infantry+command_center and serializes via the sanctioned SerializeDraftClean.</summary>
        private static string BuildValidRawFactionJson(string id)
        {
            FactionPresetPool pool = ScanRealAlphaBeta();
            FactionDefinition def = NewDraft(id);
            Pick(def, pool, unitIds: new[] { "worker", "infantry" }, buildingIds: new[] { "command_center" });
            return FactionDefinerWizardCore.SerializeDraftClean(def);
        }

        /// <summary>Rewrite the single top-level <c>ai_preset</c> line of SerializeDraftClean output: replace it with
        /// <paramref name="replacementLine"/> (original indentation preserved), or remove it entirely when null.
        /// Asserts the key appears on exactly one line so this fails loudly if the serializer format ever drifts, and
        /// targets only that line — decoupled from any incidental <c>"balanced"</c> substring elsewhere in the doc.</summary>
        private static string RewriteAiPresetLine(string json, string? replacementLine)
        {
            string[] lines = json.Split('\n');
            int idx = -1, count = 0;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith("\"ai_preset\"", StringComparison.Ordinal)) { count++; if (idx < 0) idx = i; }
            Assert.Equal(1, count);
            if (replacementLine == null)
                return string.Join('\n', lines.Where((_, i) => i != idx));
            string indent = lines[idx].Substring(0, lines[idx].Length - lines[idx].TrimStart().Length);
            lines[idx] = indent + replacementLine;
            return string.Join('\n', lines);
        }

        /// <summary>Duplicate the single top-level <c>id</c> line (indentation and trailing comma preserved) to build
        /// a document with a duplicate property name — tolerated by JsonSerializer/JsonDocument (last-wins) — used to
        /// exercise the DW-117 re-inspection's duplicate-key tolerance.</summary>
        private static string DuplicateTopLevelIdLine(string json)
        {
            string[] lines = json.Split('\n');
            int idx = -1;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith("\"id\"", StringComparison.Ordinal)) { idx = i; break; }
            Assert.InRange(idx, 0, lines.Length - 1);
            var withDup = lines.ToList();
            withDup.Insert(idx + 1, lines[idx]);   // exact duplicate line keeps its trailing comma -> valid JSON
            return string.Join('\n', withDup);
        }

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
