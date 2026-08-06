#nullable enable
using System;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
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

        // ── DW-111: duplicate building id (FactionValidator's own self-contained check) ────────

        private static ResearchDefinition ValidResearch(string id) =>
            new() { Id = id, Levels = { new ResearchLevel { TimeTicks = 10 } } };

        [Fact]
        public void DuplicateBuildingId_Validate_ReturnsLocatedError_NamingRepeatedId()
        {
            FactionDefinition def = ValidFaction();
            def.Buildings.Add(ValidBuilding("command_center")); // reuses the first building's id — the repeat

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e =>
                e.FieldPath == "buildings" && e.Message.Contains("command_center") && e.Message.Contains("duplicate"));
        }

        [Fact]
        public void DistinctBuildingIds_Validate_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.Buildings.Add(ValidBuilding("barracks")); // a second, distinct building — no duplicate
            Assert.True(FactionValidator.Validate(def).Ok);
        }

        // ── DW-111: duplicate research id ────────────────────────────────────

        [Fact]
        public void DuplicateResearchId_Validate_ReturnsLocatedError_NamingRepeatedId()
        {
            FactionDefinition def = ValidFaction();
            def.Research.Add(ValidResearch("armor_up"));
            def.Research.Add(ValidResearch("armor_up")); // the repeat

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e =>
                e.FieldPath == "research" && e.Message.Contains("armor_up") && e.Message.Contains("duplicate"));
        }

        [Fact]
        public void DistinctResearchIds_Validate_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.Research.Add(ValidResearch("armor_up"));
            def.Research.Add(ValidResearch("speed_up"));
            Assert.True(FactionValidator.Validate(def).Ok);
        }

        // ── DW-89: cross-namespace collision — a research id equal to a building id ────────────

        [Fact]
        public void ResearchIdCollidesWithBuildingId_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Research.Add(ValidResearch("command_center")); // same id as the building — cross-namespace collision

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e =>
                e.FieldPath == "research" && e.Message.Contains("command_center") && e.Message.Contains("collides"));
        }

        // ── DW-115: negative / NaN starting resources ────────────────────────

        [Fact]
        public void NegativeStartingOre_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.StartingOre = -1f;

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "starting_ore");
        }

        [Fact]
        public void NaNStartingOre_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.StartingOre = float.NaN;

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "starting_ore");
        }

        [Fact]
        public void NegativeStartingCrystal_Validate_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.StartingCrystal = -50f;

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "starting_crystal");
        }

        [Fact]
        public void NaNStartingCrystal_Validate_ReturnsLocatedError()
        {
            // Mirrors NaNStartingOre — the crystal branch has its own !float.IsFinite guard (a separate code line from
            // ore), so NaN crystal must be covered independently: NaN < 0f is false, so the negative-only tests miss it.
            FactionDefinition def = ValidFaction();
            def.StartingCrystal = float.NaN;

            FactionValidationResult result = FactionValidator.Validate(def);
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.FieldPath == "starting_crystal");
        }

        [Fact]
        public void NonNegativeStartingResources_Validate_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.StartingOre = 0f;      // zero is allowed
            def.StartingCrystal = 500f;
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
            // DW-505: the kind label must LEAD ("unit '<id>'.mesh_path: …"), not sit behind a faction-level
            // "faction '<id>'.mesh_path: " prefix — FactionDefinerWizardCore.StepForError routes on that leading
            // label, and the prefixed shape sent an author with a missing UNIT mesh_path to the Buildings & Tech
            // step. The faction id stays in the message, just no longer in front of the label.
            Assert.Contains(complete.Errors, e => e.FieldPath == "mesh_path"
                && e.Message.StartsWith("unit 'worker'.mesh_path:", StringComparison.Ordinal)
                && e.Message.Contains("'test_faction'"));
        }

        [Fact]
        public void MissingBuildingMeshPath_Validate_IsOk_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Buildings[0].MeshPath = "";

            Assert.True(FactionValidator.Validate(def).Ok);

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            // DW-505: same leading-kind-label convention on the building half of the axis.
            Assert.Contains(complete.Errors, e => e.FieldPath == "mesh_path"
                && e.Message.StartsWith("building 'command_center'.mesh_path:", StringComparison.Ordinal)
                && e.Message.Contains("'test_faction'"));
        }

        [Fact]
        public void WhitespaceOnlyMeshPath_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.Units[0].MeshPath = "   "; // whitespace-only — not a usable path

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e => e.FieldPath == "mesh_path"
                && e.Message.StartsWith("unit 'worker'.mesh_path:", StringComparison.Ordinal));
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

        // ── DW-106: hero_unit_id resolution (ValidateComplete-only) ──────────

        [Fact]
        public void HeroUnitIdResolves_ValidateComplete_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.HeroUnitId = "melee"; // names the Melee unit already in the roster

            Assert.True(FactionValidator.ValidateComplete(def).Ok);
        }

        [Fact]
        public void HeroUnitIdDangling_ValidateComplete_ReturnsLocatedError_NamingFactionAndDanglingId()
        {
            FactionDefinition def = ValidFaction();
            def.HeroUnitId = "ghost"; // no such unit id in the roster

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e =>
                e.FieldPath == "hero_unit_id"
                && e.Message.Contains("test_faction")   // names the faction
                && e.Message.Contains("ghost"));         // names the dangling id
        }

        [Fact]
        public void HeroUnitIdDangling_WithRegistry_StillReportsHeroError()
        {
            // The hero check is registry-independent — supplying a registry must not suppress it.
            FactionDefinition def = ValidFaction();
            def.HeroUnitId = "ghost";
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "some_ability" } });

            FactionValidationResult complete = FactionValidator.ValidateComplete(def, registry);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e => e.FieldPath == "hero_unit_id" && e.Message.Contains("ghost"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void HeroUnitIdUnauthored_ValidateComplete_IsOk(string? heroUnitId)
        {
            FactionDefinition def = ValidFaction();
            def.HeroUnitId = heroUnitId; // optional/unauthored — must pass

            Assert.True(FactionValidator.ValidateComplete(def).Ok);
        }

        // ── DW-106: signature_mechanic_effect_id resolution (ValidateComplete-only, registry-gated) ──

        [Fact]
        public void SignatureEffectIdResolves_WithRegistry_ValidateComplete_IsOk()
        {
            FactionDefinition def = ValidFaction();
            def.SignatureMechanicEffectId = "known_effect";
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });

            Assert.True(FactionValidator.ValidateComplete(def, registry).Ok);
        }

        [Fact]
        public void SignatureEffectIdDangling_WithRegistry_ValidateComplete_ReturnsLocatedError()
        {
            FactionDefinition def = ValidFaction();
            def.SignatureMechanicEffectId = "no_such_effect";
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });

            FactionValidationResult complete = FactionValidator.ValidateComplete(def, registry);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e =>
                e.FieldPath == "signature_mechanic_effect_id"
                && e.Message.Contains("test_faction")
                && e.Message.Contains("no_such_effect"));
        }

        [Fact]
        public void SignatureEffectIdDangling_NoRegistry_SignatureCheckSkipped_ValidateComplete_IsOk()
        {
            // Without a registry the signature check cannot resolve and must be skipped — the prior outcome is
            // unchanged (Ok here, since every other axis of ValidFaction is clean).
            FactionDefinition def = ValidFaction();
            def.SignatureMechanicEffectId = "no_such_effect";

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.True(complete.Ok);
            Assert.DoesNotContain(complete.Errors, e => e.FieldPath == "signature_mechanic_effect_id");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void SignatureEffectIdUnauthored_WithRegistry_ValidateComplete_IsOk(string? effectId)
        {
            FactionDefinition def = ValidFaction();
            def.SignatureMechanicEffectId = effectId; // optional/unauthored — must pass even with a registry
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });

            Assert.True(FactionValidator.ValidateComplete(def, registry).Ok);
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

        [Fact]
        public void AlphaAndBetaFactions_NoRegistry_ValidateComplete_StillOk_PostDw106()
        {
            // DW-106 no-regression: the shipped factions author no hero_unit_id (hero check passes) and the
            // signature check is skipped without a registry — both must still pass ValidateComplete(def) exactly
            // as before this story. Alpha/beta DO author a non-empty signature_mechanic_effect_id, so this row also
            // pins that a no-registry pass never trips the new signature check.
            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));
            FactionDefinition beta = FactionDefinition.LoadFromFile(ResolveDataPath("beta_faction.json"));

            Assert.True(string.IsNullOrWhiteSpace(alpha.HeroUnitId));
            Assert.True(string.IsNullOrWhiteSpace(beta.HeroUnitId));
            Assert.True(FactionValidator.ValidateComplete(alpha).Ok);
            Assert.True(FactionValidator.ValidateComplete(beta).Ok);
        }

        [Fact]
        public void AlphaAndBeta_SignatureEffectIds_ResolveAgainstRealRegistry_ValidateComplete_IsOk()
        {
            // DW-106 regression guard against the EXACT defect that spawned it: a SHIPPED faction's
            // signature_mechanic_effect_id drifting to a value matching no real ability. Unlike the no-registry row
            // above, this loads the REAL abilities directory and passes it into ValidateComplete, so alpha's
            // 'spike_transmutation' / beta's 'furnace_trickle' must actually resolve — a future re-drift of shipped
            // content, or a moved/empty abilities dir, fails HERE (the wizard-gate registry-wiring's real-content
            // assumption is pinned by the same real directory this asserts is non-empty).
            AbilityRegistry registry = AbilityRegistry.LoadFromDirectory(ResolveAbilitiesDir());
            Assert.True(registry.Count > 0, "real abilities directory resolved to an empty registry — path drift?");

            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));
            FactionDefinition beta = FactionDefinition.LoadFromFile(ResolveDataPath("beta_faction.json"));

            Assert.False(string.IsNullOrWhiteSpace(alpha.SignatureMechanicEffectId));
            Assert.False(string.IsNullOrWhiteSpace(beta.SignatureMechanicEffectId));
            Assert.True(FactionValidator.ValidateComplete(alpha, registry).Ok);
            Assert.True(FactionValidator.ValidateComplete(beta, registry).Ok);

            // Belt-and-suspenders: the real registry actually enforces — a dangling id on the real faction is blocked.
            alpha.SignatureMechanicEffectId = "no_such_effect_in_real_registry";
            FactionValidationResult dangling = FactionValidator.ValidateComplete(alpha, registry);
            Assert.False(dangling.Ok);
            Assert.Contains(dangling.Errors, e => e.FieldPath == "signature_mechanic_effect_id");
        }

        // ── Story 5.3 (FR-20): FMA showcase content hardening regression ─────────────────────
        // Proves the new signature-mechanic descriptor fields landed on BOTH shipped factions, that alpha's
        // roster regression (griffin replaced by junk "fatso"/"fatso_copy" placeholders) is reverted, and that
        // both factions pass ValidateComplete's SCHEMA-level completeness gate (non-blank mesh_path + required
        // roles) post-edit. ValidateComplete does not, and never has, verified that a mesh_path resolves to an
        // on-disk file (review pass 1 finding) — that on-disk check for the story's 12 in-scope entries per
        // faction was done manually (see the spec's Verification section); the two `aviary` buildings' known
        // dangling mesh_path values are tracked separately as DW-102, not asserted here.

        [Fact]
        public void AlphaFaction_LoadFromFile_HasSignatureMechanicDescriptorFields()
        {
            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));

            Assert.Equal("balanced", alpha.AiPreset);
            Assert.False(string.IsNullOrEmpty(alpha.SignatureMechanicDisplay));
            Assert.False(string.IsNullOrEmpty(alpha.SignatureMechanicEffectId));
        }

        [Fact]
        public void BetaFaction_LoadFromFile_HasSignatureMechanicDescriptorFields()
        {
            FactionDefinition beta = FactionDefinition.LoadFromFile(ResolveDataPath("beta_faction.json"));

            Assert.Equal("balanced", beta.AiPreset);
            Assert.False(string.IsNullOrEmpty(beta.SignatureMechanicDisplay));
            Assert.False(string.IsNullOrEmpty(beta.SignatureMechanicEffectId));
        }

        [Fact]
        public void AlphaFaction_LoadFromFile_GriffinRestored_AirCategory_NoJunkPlaceholders()
        {
            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));

            // Full stat block, not just Category/DisplayName — locks the fma-faction-design.md-sourced
            // pre-regression values (recovered via git history) against a future accidental edit.
            UnitDefinition? griffin = alpha.GetUnit("griffin");
            Assert.NotNull(griffin);
            Assert.Equal("Air", griffin!.Category);
            Assert.Equal("Greycrest, the Bonded", griffin.DisplayName);
            Assert.Equal("res://assets/models/factions/alpha/greycrest_bonded.glb", griffin.MeshPath);
            Assert.Equal(190f, griffin.Hp);
            Assert.Equal(6.5f, griffin.Speed);
            Assert.Equal(35f, griffin.AttackDamage);
            Assert.Equal(2.0f, griffin.AttackRange);
            Assert.Equal(1.1f, griffin.AttackSpeed);
            Assert.Equal("Pierce", griffin.DamageType);
            Assert.Equal("Light", griffin.ArmorType);
            Assert.Equal(200, griffin.CostOre);
            Assert.Equal(0, griffin.CostCrystal);
            Assert.Equal(2, griffin.Supply);
            Assert.Equal(1.4f, griffin.MeshScale);
            Assert.Equal(18.0f, griffin.TrainTime);
            Assert.Equal(15.0f, griffin.VisionRange);

            Assert.Null(alpha.GetUnit("fatso"));
            Assert.Null(alpha.GetUnit("fatso_copy"));
        }

        [Fact]
        public void AlphaFaction_RealJson_AviaryProducesGriffin_ThroughBuildingSystem()
        {
            // Verification-gap fix (review pass 1): the JSON-shape test above proves griffin's data is correct,
            // but never exercised the actual production-resolution path this restoration was meant to unblock —
            // Player1's real alpha_faction.json wired into a BuildingSystem, resolving its Aviary to griffin via
            // the same BuildingType.Aviary -> "Air" -> GetUnitByCategory route MainScene uses in the live game.
            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            var sys       = new BuildingSystem(buildings, resources, alpha);

            UnitDefinition? produced = sys.GetProductionUnit(BuildingType.Aviary, Faction.Player1);

            Assert.NotNull(produced);
            Assert.Equal("griffin", produced!.Id);
        }

        [Fact]
        public void AlphaAndBetaFactions_ValidateComplete_AreOk_PostFmaHardening()
        {
            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));
            FactionDefinition beta = FactionDefinition.LoadFromFile(ResolveDataPath("beta_faction.json"));

            Assert.True(FactionValidator.ValidateComplete(alpha).Ok);
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
        public void LoadFromFile_NegativeStartingOre_Throws_LocatedError()
        {
            // DW-115: proves the new starting-resource check throws THROUGH the loader (not just in the direct
            // Validate call), with a located starting_ore error.
            string path = WriteTempFaction(ValidFactionJson(extraTopLevel: "\"starting_ore\": -1,"));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("starting_ore", ex.Message);
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

        /// <summary>Resolve the shipped <c>resources/data/abilities/</c> directory (sibling of the factions dir),
        /// mirroring <c>SignatureMechanicRealContentTests</c>'s real-content resolution — used to prove shipped
        /// signature ids resolve against the genuinely-loaded registry, not a hand-built stand-in.</summary>
        private static string ResolveAbilitiesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "abilities");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/abilities above {AppContext.BaseDirectory}");
        }
    }
}
