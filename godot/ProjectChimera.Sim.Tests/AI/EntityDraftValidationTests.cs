#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;
using Xunit;
using static ProjectChimera.Sim.Tests.AI.EntityDraftTestData;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.4 — proves each public-static <c>Validate{Unit,Ability,Hero,Faction}Draft</c> router routes through the
    /// SAME per-kind gate hand-authored data uses: a valid draft returns a def; an out-of-Fixed-range float or an
    /// unknown enum/archetype/ability-ref returns <c>(null, located error)</c> naming the path + offending value; a hero
    /// draft missing <c>is_hero:true</c>/a <c>hero</c> block is rejected; and a faction whose one unit fails
    /// <see cref="UnitDefinitionValidator"/> is rejected per-unit (closing the bare-faction-load deep-validation gap).
    /// </summary>
    public class EntityDraftValidationTests
    {
        // ── Unit ───────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateUnitDraft_Valid_ReturnsDef()
        {
            var (def, err) = LLMService.ValidateUnitDraft(ValidUnitJson, UnitCtx());
            Assert.Null(err);
            Assert.NotNull(def);
            Assert.Equal("grunt", def!.Id);
        }

        [Fact]
        public void ValidateUnitDraft_FloatOutOfFixedRange_LocatedReject()
        {
            string json = "{\"id\":\"grunt\",\"category\":\"Melee\",\"attack_damage\":40000}";
            var (def, err) = LLMService.ValidateUnitDraft(json, UnitCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("attack_damage", err);   // the path
            Assert.Contains("40000", err);           // the offending value
        }

        [Fact]
        public void ValidateUnitDraft_UnknownArchetype_LocatedReject()
        {
            string json = "{\"id\":\"mage\",\"category\":\"Wizard\"}";
            var (def, err) = LLMService.ValidateUnitDraft(json, UnitCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("category", err);
            Assert.Contains("Wizard", err);
        }

        [Fact]
        public void ValidateUnitDraft_UnknownAbilityRef_LocatedReject()
        {
            // A real (empty) registry rejects any ability ref fail-closed (per the validator's null-vs-empty contract).
            var ctx = new UnitDraftContext { AbilityRegistry = AbilityRegistry.Empty };
            string json = "{\"id\":\"grunt\",\"category\":\"Melee\",\"abilities\":[\"nope_missing\"]}";
            var (def, err) = LLMService.ValidateUnitDraft(json, ctx);
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("abilities", err);
            Assert.Contains("nope_missing", err);
        }

        // ── Ability ──────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateAbilityDraft_Valid_ReturnsDef_NumbersAreFixed()
        {
            var (def, err) = LLMService.ValidateAbilityDraft(ValidAbilityJson, AbilityCtx());
            Assert.Null(err);
            Assert.NotNull(def);
            Assert.Equal(Fixed.FromFloat(3f).Raw, def!.Cooldown.Raw);
        }

        [Fact]
        public void ValidateAbilityDraft_OutOfRangeNumber_LocatedReject()
        {
            string json = "{\"id\":\"nova\",\"targeting\":\"Self\",\"cooldown\":99999.0," +
                          "\"effect\":{\"kind\":\"heal\",\"amount\":40}}";
            var (def, err) = LLMService.ValidateAbilityDraft(json, AbilityCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("range", err);   // FixedJsonConverter's out-of-16.16-range reject
        }

        [Fact]
        public void ValidateAbilityDraft_UnknownTargeting_LocatedReject()
        {
            string json = "{\"id\":\"nova\",\"targeting\":\"Sideways\"," +
                          "\"effect\":{\"kind\":\"heal\",\"amount\":40}}";
            var (def, err) = LLMService.ValidateAbilityDraft(json, AbilityCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("targeting", err);
            Assert.Contains("Sideways", err);
        }

        // ── Hero ───────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateHeroDraft_Valid_ReturnsHeroUnit()
        {
            var (def, err) = LLMService.ValidateHeroDraft(ValidHeroJson, UnitCtx());
            Assert.Null(err);
            Assert.NotNull(def);
            Assert.True(def!.IsHero);
            Assert.NotNull(def.Hero);
        }

        [Fact]
        public void ValidateHeroDraft_NotAHero_LocatedReject()
        {
            // A perfectly-valid PLAIN unit (is_hero:false, no hero block) must be rejected by the HERO router.
            var (def, err) = LLMService.ValidateHeroDraft(ValidUnitJson, UnitCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("is_hero", err);
        }

        [Fact]
        public void ValidateHeroDraft_HeroFlagButNoBlock_LocatedReject()
        {
            string json = "{\"id\":\"champ\",\"category\":\"Melee\",\"is_hero\":true}";
            var (def, err) = LLMService.ValidateHeroDraft(json, UnitCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("hero", err);
        }

        // ── Faction ──────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateFactionDraft_Valid_ReturnsDef()
        {
            var (def, err) = LLMService.ValidateFactionDraft(ValidFactionJson, FactionCtx());
            Assert.Null(err);
            Assert.NotNull(def);
            Assert.Equal("emberkin", def!.Id);
        }

        [Fact]
        public void ValidateFactionDraft_InvalidUnit_PerUnitLocatedReject()
        {
            // Structurally VALID faction (FactionValidator.Validate passes — it does NOT check unit archetype), but one
            // unit has an unknown archetype: the per-unit UnitDefinitionValidator loop must catch it. This is the
            // deep-validation gap bare faction load leaves open, now closed by the faction draft router.
            string json =
                "{\"id\":\"emberkin\",\"display_name\":\"Emberkin\",\"color\":[0.8,0.3,0.2,1.0],\"ai_preset\":\"balanced\"," +
                "\"units\":[{\"id\":\"mage\",\"display_name\":\"Mage\",\"category\":\"Wizard\"}],\"buildings\":[]}";
            var (def, err) = LLMService.ValidateFactionDraft(json, FactionCtx());
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("mage", err);     // the offending unit id
            Assert.Contains("Wizard", err);   // the offending archetype value
        }

        [Fact]
        public void ValidateFactionDraft_UnitAbilityRef_WithRegistry_LocatedReject()
        {
            // The per-unit REFERENCE checks (ability/behavior/item) only run when the ctx carries a registry — the
            // production faction panel now loads one (mirroring the Finish gate). With a real (empty) AbilityRegistry a
            // drafted unit citing a missing ability id is rejected at DRAFT time, not deferred to Finish. Registry-less,
            // this same input would pass the ref check (null-registry semantics), so this pins the wiring, not just the loop.
            var ctx = new FactionDraftContext
            {
                AiPresets = FactionValidator.KnownAiPresets,
                AbilityRegistry = AbilityRegistry.Empty,
            };
            string json =
                "{\"id\":\"emberkin\",\"display_name\":\"Emberkin\",\"color\":[0.8,0.3,0.2,1.0],\"ai_preset\":\"balanced\"," +
                "\"units\":[{\"id\":\"grunt\",\"display_name\":\"Grunt\",\"category\":\"Melee\",\"abilities\":[\"nope_missing\"]}]," +
                "\"buildings\":[]}";
            var (def, err) = LLMService.ValidateFactionDraft(json, ctx);
            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Contains("nope_missing", err);
        }

        // ── Draft-landing dedup (Story 8.4 review P4) ────────────────────────────
        // The AI unit-draft is validated with no siblings, so the sibling-aware duplicate-id rule is skipped; the ONLY
        // guard keeping the roster duplicate-free on landing is UnitDefinitionValidator.MakeUniqueId (which the Unit Card
        // panel's UniqueId now delegates to for both the manual and AI paths). Pin its behavior here.

        [Fact]
        public void MakeUniqueId_NoCollision_KeepsSanitizedId()
        {
            Assert.Equal("grunt", UnitDefinitionValidator.MakeUniqueId(new[] { "worker", "archer" }, "grunt"));
        }

        [Fact]
        public void MakeUniqueId_Collision_SuffixesUntilFree()
        {
            Assert.Equal("grunt_2", UnitDefinitionValidator.MakeUniqueId(new[] { "grunt" }, "grunt"));
            Assert.Equal("grunt_3", UnitDefinitionValidator.MakeUniqueId(new[] { "grunt", "grunt_2" }, "grunt"));
        }

        [Fact]
        public void MakeUniqueId_SanitizesAndFallsBackOnEmpty()
        {
            Assert.Equal("fire_mage", UnitDefinitionValidator.MakeUniqueId(System.Array.Empty<string>(), "Fire Mage"));
            Assert.Equal("new_unit", UnitDefinitionValidator.MakeUniqueId(System.Array.Empty<string>(), "   "));
        }
    }
}
