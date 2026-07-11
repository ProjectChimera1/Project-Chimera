#nullable enable
using System;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 5.2 (AR-39/AR-12/FR-18 data) — one test per I/O Matrix row for <see cref="FactionValidator"/>, plus a
    /// regression proving <c>alpha_faction.json</c>/<c>beta_faction.json</c> still <c>LoadFromFile</c> successfully
    /// and pass <see cref="FactionValidator.ValidateComplete"/> (they are genuinely complete showcase factions).
    /// The mesh_path and required-role rows call <see cref="FactionValidator.ValidateComplete"/> directly — never
    /// through <see cref="FactionDefinition.LoadFromFile"/> (Review Loop 2: those two checks must never gate
    /// ordinary Save/load, only a caller that explicitly means "is this faction finished").
    /// </summary>
    public class FactionValidatorTests
    {
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

        /// <summary>A minimal, fully-valid faction — passes both <see cref="FactionValidator.Validate"/> and
        /// <see cref="FactionValidator.ValidateComplete"/>. Each test mutates exactly ONE axis away from this
        /// baseline.</summary>
        private static FactionDefinition ValidFaction()
        {
            var def = new FactionDefinition { Id = "test_faction", DisplayName = "Test Faction" };
            def.Units.Add(Worker());
            def.Units.Add(Melee());
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        [Fact]
        public void ValidFaction_Validate_IsOk()
        {
            Assert.True(FactionValidator.Validate(ValidFaction()).Ok);
        }

        [Fact]
        public void ValidFaction_ValidateComplete_IsOk()
        {
            Assert.True(FactionValidator.ValidateComplete(ValidFaction()).Ok);
        }

        // ── Dangling building prereq ──────────────────────────────────────────

        [Fact]
        public void DanglingBuildingPrereq_Validate_ReturnsLocatedError_NamingFieldAndDanglingId()
        {
            FactionDefinition def = ValidFaction();
            def.Buildings[0].Prerequisites = new[] { "nonexistent_building" };

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e =>
                e.Message.Contains("command_center") &&
                e.Message.Contains("nonexistent_building") &&
                e.Message.Contains("prerequisites"));
        }

        // ── Empty / unknown ai_preset ─────────────────────────────────────────

        [Fact]
        public void EmptyAiPreset_Validate_ReturnsLocatedError_IdentifyingAiPreset()
        {
            FactionDefinition def = ValidFaction();
            def.AiPreset = "";

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset");
        }

        [Fact]
        public void UnknownAiPreset_Validate_ReturnsLocatedError_IdentifyingAiPreset()
        {
            FactionDefinition def = ValidFaction();
            def.AiPreset = "nonsense";

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "ai_preset" && e.Message.Contains("nonsense"));
        }

        [Fact]
        public void KnownAiPreset_Balanced_Validate_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.AiPreset = "balanced";
            Assert.True(FactionValidator.Validate(def).Ok);
        }

        // ── Invalid color ─────────────────────────────────────────────────────

        [Fact]
        public void ColorTooShort_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Color = new float[] { 0.1f, 0.2f, 0.3f };

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "color");
        }

        [Fact]
        public void ColorTooLong_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Color = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "color");
        }

        [Fact]
        public void ColorComponentBelowZero_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Color = new float[] { -0.1f, 0.5f, 0.5f, 1.0f };

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "color");
        }

        [Fact]
        public void ColorComponentAboveOne_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Color = new float[] { 0.5f, 1.1f, 0.5f, 1.0f };

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "color");
        }

        [Fact]
        public void ColorComponentNaN_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Color = new float[] { float.NaN, 0.5f, 0.5f, 1.0f };

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "color");
        }

        [Fact]
        public void ValidColor_Validate_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.Color = new float[] { 0f, 1f, 0.5f, 1f };
            Assert.True(FactionValidator.Validate(def).Ok);
        }

        // ── Duplicate unit id ─────────────────────────────────────────────────

        [Fact]
        public void DuplicateUnitId_Validate_ReturnsLocatedError_NamingRepeatedId()
        {
            FactionDefinition def = ValidFaction();
            def.Units.Add(Melee("worker")); // reuses the Worker's id — the repeat

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e =>
                e.FieldPath == "units" && e.Message.Contains("worker") && e.Message.Contains("duplicate"));
        }

        [Fact]
        public void BlankUnitId_Validate_ReturnsLocatedError_NeverSilentlySkipped()
        {
            FactionDefinition def = ValidFaction();
            def.Units.Add(new UnitDefinition { Id = "", Category = "Melee", MeshPath = "res://assets/x.glb" });

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "units" && e.Message.Contains("missing an id"));
        }

        [Fact]
        public void CaseVariantAiPreset_Validate_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.AiPreset = "Balanced"; // case-insensitive closed-set match

            Assert.True(FactionValidator.Validate(def).Ok);
        }

        // ── Missing mesh_path (ValidateComplete-only) ────────────────────────

        [Fact]
        public void MissingUnitMeshPath_Validate_IsOk_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Units[0].MeshPath = null; // Worker with no mesh_path — a legitimate mid-edit state

            Assert.True(FactionValidator.Validate(def).Ok);

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e => e.FieldPath == "mesh_path" && e.Message.Contains("worker"));
        }

        [Fact]
        public void MissingBuildingMeshPath_Validate_IsOk_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Buildings[0].MeshPath = "";

            Assert.True(FactionValidator.Validate(def).Ok);

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e => e.FieldPath == "mesh_path" && e.Message.Contains("command_center"));
        }

        [Fact]
        public void WhitespaceOnlyMeshPath_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Units[0].MeshPath = "   "; // whitespace-only — not a usable path

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e => e.FieldPath == "mesh_path" && e.Message.Contains("worker"));
        }

        // ── Missing required role (ValidateComplete-only) ────────────────────

        [Fact]
        public void MissingWorker_Validate_IsOk_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Units.RemoveAll(u => string.Equals(u.Category, "Worker", StringComparison.OrdinalIgnoreCase));

            Assert.True(FactionValidator.Validate(def).Ok);

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e => e.FieldPath == "units" && e.Message.Contains("Worker"));
        }

        [Fact]
        public void MissingCombatUnit_Validate_IsOk_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Units.RemoveAll(u => !string.Equals(u.Category, "Worker", StringComparison.OrdinalIgnoreCase));

            Assert.True(FactionValidator.Validate(def).Ok);

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e => e.FieldPath == "units" && e.Message.Contains("combat"));
        }

        // ── Unchanged showcase load (regression) ─────────────────────────────

        [Fact]
        public void AlphaFaction_LoadFromFile_Succeeds_AiPresetBalanced_ValidateOk_ValidateCompleteOk()
        {
            string path = ResolveDataPath("alpha_faction.json");
            FactionDefinition alpha = FactionDefinition.LoadFromFile(path);

            Assert.Equal("balanced", alpha.AiPreset);
            Assert.True(FactionValidator.Validate(alpha).Ok);
            Assert.True(FactionValidator.ValidateComplete(alpha).Ok);
        }

        [Fact]
        public void BetaFaction_LoadFromFile_Succeeds_AiPresetBalanced_ValidateOk_ValidateCompleteOk()
        {
            string path = ResolveDataPath("beta_faction.json");
            FactionDefinition beta = FactionDefinition.LoadFromFile(path);

            Assert.Equal("balanced", beta.AiPreset);
            Assert.True(FactionValidator.Validate(beta).Ok);
            Assert.True(FactionValidator.ValidateComplete(beta).Ok);
        }

        // ── LoadFromFile wiring: the new Validate checks must throw THROUGH the loader ────────
        // These lock the load-time enforcement of the three new checks (ai_preset / color / duplicate-unit-id)
        // and the relocated dangling-prereq check, mirroring the throws-through-load coverage every sibling
        // validator already has (ResourceCostValidatorTests / TechTreeValidatorTests / ResearchValidatorTests /
        // BuildingDefinitionValidatorTests). Without them, LoadFromFile's `FactionValidator.Validate(def)` call
        // could be silently unwired and the whole suite would stay green — a malformed faction would then load
        // straight into a match, the exact regression this story exists to prevent.

        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_faction_validator_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>A minimal, fully-valid faction JSON (passes <see cref="FactionValidator.Validate"/>). Each
        /// LoadFromFile test mutates exactly ONE field away from this to prove that axis throws through the loader.
        /// ai_preset/color are omitted so they take their valid C# defaults ("balanced" / [.2,.5,1,1]).</summary>
        private static string ValidFactionJson(string unitsJson =
                """{ "id": "worker", "display_name": "Worker", "category": "Worker", "hp": 50 }""",
            string buildingsJson = "", string extraTopLevel = "") => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          {{extraTopLevel}}
          "units": [{{unitsJson}}],
          "buildings": [{{buildingsJson}}]
        }
        """;

        [Fact]
        public void LoadFromFile_ValidMinimalFaction_Succeeds()
        {
            string path = WriteTempFaction(ValidFactionJson());
            try { Assert.NotNull(FactionDefinition.LoadFromFile(path)); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_UnknownAiPreset_Throws_LocatedError()
        {
            string path = WriteTempFaction(ValidFactionJson(extraTopLevel: "\"ai_preset\": \"nonsense\","));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("ai_preset", ex.Message);
                Assert.Contains("nonsense", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_InvalidColor_Throws_LocatedError()
        {
            string path = WriteTempFaction(ValidFactionJson(extraTopLevel: "\"color\": [1.0, 1.0, 1.0],"));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("color", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_DuplicateUnitId_Throws_LocatedError()
        {
            string path = WriteTempFaction(ValidFactionJson(unitsJson:
                """{ "id": "worker", "display_name": "W", "category": "Worker", "hp": 50 },""" +
                """{ "id": "worker", "display_name": "W2", "category": "Melee", "hp": 50 }"""));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("duplicate unit id", ex.Message);
                Assert.Contains("worker", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_DanglingBuildingPrereq_Throws_LocatedError()
        {
            // Exercises the RELOCATED TechTreeValidator through the new FactionValidator gate — proves the
            // relocation kept LoadFromFile rejecting a dangling prerequisite (not just the three brand-new checks).
            string barracks =
                """{ "id": "barracks", "display_name": "Barracks", "category": "Structure", "hp": 100, "mesh_path": "res://b.glb", "construction_time": 10, "prerequisites": ["ghost_building"] }""";
            string path = WriteTempFaction(ValidFactionJson(buildingsJson: barracks));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("ghost_building", ex.Message);
                Assert.Contains("prerequisites", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── AR-12 schema fields: JsonPropertyName bindings must round-trip ───────────────────
        // The story's headline deliverable is six new schema fields; without these, a wrong [JsonPropertyName] on
        // any descriptor field would ship silently (the AiPreset=="balanced" assertions elsewhere pass only via the
        // C# default — the JSON omits ai_preset — so they prove nothing about JSON binding).

        [Fact]
        public void AlphaFaction_SignatureMechanic_DeserializesFromJson()
        {
            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));
            Assert.Equal("equal_exchange", alpha.SignatureMechanicId); // alpha_faction.json authors this key today
        }

        [Fact]
        public void NewSchemaFields_RoundTripFromJson()
        {
            string path = WriteTempFaction(ValidFactionJson(extraTopLevel:
                "\"ai_preset\": \"balanced\"," +
                "\"signature_mechanic\": \"sig_mech\"," +
                "\"signature_mechanic_display\": \"Sig Mech\"," +
                "\"signature_mechanic_effect_id\": \"eff_1\"," +
                "\"hero_unit_id\": \"hero_1\"," +
                "\"persistence_enabled\": true,"));
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.Equal("balanced", def.AiPreset);
                Assert.Equal("sig_mech", def.SignatureMechanicId);
                Assert.Equal("Sig Mech", def.SignatureMechanicDisplay);
                Assert.Equal("eff_1", def.SignatureMechanicEffectId);
                Assert.Equal("hero_1", def.HeroUnitId);
                Assert.True(def.PersistenceEnabled);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NewSchemaFields_Defaults_WhenJsonOmitsThem()
        {
            string path = WriteTempFaction(ValidFactionJson());
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.Equal("balanced", def.AiPreset);      // valid closed-set default, NOT ""
                Assert.Equal("", def.SignatureMechanicId);
                Assert.Null(def.SignatureMechanicDisplay);
                Assert.Null(def.SignatureMechanicEffectId);
                Assert.Null(def.HeroUnitId);
                Assert.False(def.PersistenceEnabled);
            }
            finally { File.Delete(path); }
        }

        /// <summary>Resolve a shipped faction JSON by walking up from the test-assembly directory to
        /// <c>resources/data/factions/</c> (mirrors <c>BuildingDefinitionValidatorTests.ResolveDataPath</c> /
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
