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
        // DW-106 / DW-114: a hero is a roster unit → Roster; a signature effect id is a faction-config default → AI Preset.
        [InlineData("hero_unit_id", "faction 'x'.hero_unit_id: names unit 'ghost' which is not in this faction's roster.", FactionDefinerStep.Roster)]
        [InlineData("signature_mechanic_effect_id", "faction 'x'.signature_mechanic_effect_id: 'no_such_effect' does not resolve to any loaded ability.", FactionDefinerStep.AiPreset)]
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
