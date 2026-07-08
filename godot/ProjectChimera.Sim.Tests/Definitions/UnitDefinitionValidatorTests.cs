#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.4 AC2 / D-9 — the first unit content validator's reject rules, each with a positive case AND a negative
    /// control that is demonstrably RED without the rule (A3 "every gate ships with teeth"). Unlike the ability
    /// validator, <see cref="UnitDefinitionValidator"/> returns ALL located errors (not first-fail), so the multi-error
    /// case asserts more than one badge fires at once. Every reject asserts the located error names BOTH the unit id and
    /// the offending field path (the per-field-badge mapping the panel relies on).
    /// </summary>
    public class UnitDefinitionValidatorTests
    {
        private static readonly UnitDefinitionValidator V = new();

        // A registry with two known abilities so ability-reference validation has something to resolve against.
        private static readonly AbilityRegistry Registry = new(new List<AbilityDefinition>
        {
            new AbilityDefinition { Id = "fireball", Targeting = "Self" },
            new AbilityDefinition { Id = "heal",     Targeting = "Self" },
        });

        /// <summary>A fully-valid, minimal unit — every rule passes. Each negative test mutates exactly one field.</summary>
        private static UnitDefinition Valid() => new UnitDefinition
        {
            Id = "grunt",
            DisplayName = "Grunt",
            Category = "Melee",
            Hp = 100f, Speed = 4f, AttackDamage = 10f, AttackRange = 5f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored",
            CostOre = 50, CostCrystal = 0, Supply = 1, VisionRange = 8f,
            Armor = 0f, TrainTime = 8f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
            SeparationPriority = "Normal",
        };

        private static UnitValidationResult Run(UnitDefinition def, IReadOnlyList<UnitDefinition>? siblings = null) =>
            V.Validate(def, Registry, siblings);

        // Assert an error is present for a given JSON field path and that its message names the id + path.
        private static void AssertError(UnitValidationResult r, string fieldPath, string unitId)
        {
            Assert.False(r.Ok);
            (string FieldPath, string Message) e =
                r.Errors.FirstOrDefault(x => x.FieldPath == fieldPath);
            Assert.True(e.FieldPath == fieldPath, $"expected an error on '{fieldPath}', got: {string.Join(" | ", r.Errors.Select(x => x.FieldPath))}");
            Assert.Contains(unitId, e.Message);
            Assert.Contains(fieldPath, e.Message);
        }

        // ── Positive control ──

        [Fact]
        public void ValidUnit_PassesWithNoErrors()
        {
            UnitValidationResult r = Run(Valid());
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
            Assert.Empty(r.Errors);
        }

        // ── id: non-empty, sanitized, unique ──

        [Fact]
        public void EmptyId_IsRejected()
        {
            var def = Valid(); def.Id = "";
            UnitValidationResult r = Run(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "id");
        }

        [Fact]
        public void NonSanitizedId_IsRejected()
        {
            var def = Valid(); def.Id = "Bad Id!";   // uppercase + space + '!' — SanitizeId(id) != id
            AssertError(Run(def), "id", "Bad Id!");
        }

        [Fact]
        public void DuplicateId_AmongSiblings_IsRejected()
        {
            var existing = Valid(); existing.Id = "grunt";
            var edited = Valid(); edited.Id = "grunt";   // a DIFFERENT instance with the same id = a dup
            var siblings = new List<UnitDefinition> { existing };
            AssertError(Run(edited, siblings), "id", "grunt");
        }

        [Fact]
        public void SameInstance_InSiblings_IsNotADuplicate()
        {
            // The edit path: the unit being validated IS its own entry in the list — reference-equality excludes it,
            // so it is not a false-positive duplicate of itself.
            var def = Valid(); def.Id = "grunt";
            var siblings = new List<UnitDefinition> { def };
            Assert.True(Run(def, siblings).Ok);
        }

        // ── enums (the loader would fail-open these) ──

        [Fact]
        public void UnknownCategory_IsRejected()
        {
            var def = Valid(); def.Category = "Wizard";
            AssertError(Run(def), "category", "grunt");
        }

        [Fact]
        public void UnknownDamageType_IsRejected()
        {
            var def = Valid(); def.DamageType = "Plasma";
            AssertError(Run(def), "damage_type", "grunt");
        }

        [Fact]
        public void UnknownArmorType_IsRejected()
        {
            var def = Valid(); def.ArmorType = "Diamond";
            AssertError(Run(def), "armor_type", "grunt");
        }

        [Fact]
        public void UnknownSeparationPriority_IsRejected()
        {
            var def = Valid(); def.SeparationPriority = "Flee";
            AssertError(Run(def), "separation_priority", "grunt");
        }

        // ── delivery (Story 3.12): null legal (legacy inference); a non-null value must be Hitscan|Projectile ──

        [Fact]
        public void NullDelivery_IsValid()
        {
            var def = Valid(); def.Delivery = null;   // legacy: infers via range — no error
            Assert.True(Run(def).Ok);
        }

        [Theory]
        [InlineData("Hitscan")]
        [InlineData("Projectile")]
        public void KnownDelivery_IsValid(string delivery)
        {
            var def = Valid(); def.Delivery = delivery;
            if (delivery == "Projectile") def.ProjectileSpeed = 18f;   // valid positive speed
            Assert.True(Run(def).Ok, string.Join(" | ", Run(def).Errors.Select(e => e.Message)));
        }

        [Fact]
        public void UnknownDelivery_IsRejected()
        {
            var def = Valid(); def.Delivery = "Beam";
            AssertError(Run(def), "delivery", "grunt");
        }

        // ── projectile_speed (Story 3.12): strictly positive, but ONLY constrained for a Projectile unit ──

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        public void NonPositiveProjectileSpeed_OnProjectileUnit_IsRejected(float speed)
        {
            var def = Valid(); def.Delivery = "Projectile"; def.ProjectileSpeed = speed;
            AssertError(Run(def), "projectile_speed", "grunt");
        }

        [Fact]
        public void NonPositiveProjectileSpeed_OnHitscanUnit_IsIgnored()
        {
            // A Hitscan unit never reads projectile_speed, so a 0/negative value there is NOT an error.
            var def = Valid(); def.Delivery = "Hitscan"; def.ProjectileSpeed = 0f;
            Assert.True(Run(def).Ok);
        }

        [Fact]
        public void OmittedProjectileSpeed_OnProjectileUnit_IsValid()
        {
            // Omitted ⇒ the POCO default 18 ⇒ a valid positive speed (existing ranged data is unchanged).
            var def = Valid(); def.Delivery = "Projectile";   // ProjectileSpeed stays 18f
            Assert.True(Run(def).Ok);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        public void NonPositiveProjectileSpeed_OnInferredProjectileUnit_IsRejected(float speed)
        {
            // A unit that OMITS delivery (null) but has attack_range 5 > 2.5 INFERS Projectile at runtime and reads
            // projectile_speed — so a 0/negative speed MUST be rejected even though the literal delivery is null.
            // (Regression guard: gating on the literal string instead of the resolved delivery let this slip through.)
            var def = Valid(); def.Delivery = null; def.AttackRange = 5f; def.ProjectileSpeed = speed;
            AssertError(Run(def), "projectile_speed", "grunt");
        }

        [Fact]
        public void NonPositiveProjectileSpeed_OnInferredHitscanUnit_IsIgnored()
        {
            // Omitted delivery + short range (1.5 ≤ 2.5) infers Hitscan, which never reads projectile_speed → not an error.
            var def = Valid(); def.Delivery = null; def.AttackRange = 1.5f; def.ProjectileSpeed = 0f;
            Assert.True(Run(def).Ok);
        }

        // ── projectile_speed finiteness / range: enforced for EVERY unit, since the value is quantized and folded
        //    into SimChecksum regardless of delivery — a NaN/Inf/out-of-range value must never reach the hash. ──

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(32768f)]   // == Range (the 16.16 ceiling — excluded)
        [InlineData(99999f)]   // > Range
        public void NonFiniteOrOutOfRangeProjectileSpeed_OnProjectileUnit_IsRejected(float speed)
        {
            var def = Valid(); def.Delivery = "Projectile"; def.ProjectileSpeed = speed;
            AssertError(Run(def), "projectile_speed", "grunt");
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(99999f)]   // > Range
        public void NonFiniteOrOutOfRangeProjectileSpeed_OnHitscanUnit_IsAlsoRejected(float speed)
        {
            // A Hitscan unit ignores projectile_speed at combat, but the value is STILL Fixed.FromFloat-quantized and
            // folded into the deterministic checksum, so a non-finite / out-of-range speed must fail closed here too
            // (regression guard for gating the ENTIRE check on the resolved delivery — the finiteness rule is universal).
            var def = Valid(); def.Delivery = "Hitscan"; def.ProjectileSpeed = speed;
            AssertError(Run(def), "projectile_speed", "grunt");
        }

        // ── xp_bounty (Story 3.13): omitted (null) always valid; authored must be an int in [0, 32768) ──

        [Fact]
        public void XpBounty_Omitted_IsValid()
        {
            var def = Valid(); def.XpBounty = null;   // derived from cost
            Assert.True(Run(def).Ok);
        }

        [Fact]
        public void XpBounty_AuthoredInRange_IsValid()
        {
            var def = Valid(); def.XpBounty = 250;
            Assert.True(Run(def).Ok);
        }

        [Fact]
        public void XpBounty_Negative_IsRejected()
        {
            var def = Valid(); def.XpBounty = -5;
            AssertError(Run(def), "xp_bounty", "grunt");
        }

        [Fact]
        public void XpBounty_OverRange_IsRejected()
        {
            var def = Valid(); def.XpBounty = 32768;
            AssertError(Run(def), "xp_bounty", "grunt");
        }

        // ── hero runtime fields (Story 3.13): xp_share_radius finite & [0, 128); *_per_level finite & [0, 256) —
        //    tighter than the generic Range so the runtime's r*r / 99-stack-sum cannot overflow 16.16 Fixed ──

        /// <summary>A valid hero unit (is_hero + a default-Standard hero block, coherent with the validator).</summary>
        private static UnitDefinition ValidHero()
        {
            var def = Valid();
            def.IsHero = true;
            def.Hero = new HeroDefinition(); // defaults (MaxLevel 10, BaseXp 100, XpGrowth 1.15, XpShareRadius 12, zero growth)
            return def;
        }

        [Fact]
        public void ValidHero_PassesWithNoErrors()
        {
            UnitValidationResult r = Run(ValidHero());
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Theory]
        [InlineData("hero.xp_share_radius")]
        [InlineData("hero.health_per_level")]
        [InlineData("hero.damage_per_level")]
        [InlineData("hero.armor_per_level")]
        public void HeroRuntimeField_Negative_IsRejected(string field)
        {
            var def = ValidHero();
            switch (field)
            {
                case "hero.xp_share_radius":  def.Hero!.XpShareRadius = -1f; break;
                case "hero.health_per_level": def.Hero!.HealthPerLevel = -1f; break;
                case "hero.damage_per_level": def.Hero!.DamagePerLevel = -1f; break;
                case "hero.armor_per_level":  def.Hero!.ArmorPerLevel = -1f; break;
            }
            AssertError(Run(def), field, "grunt");
        }

        [Theory]
        [InlineData("hero.xp_share_radius")]
        [InlineData("hero.health_per_level")]
        [InlineData("hero.damage_per_level")]
        [InlineData("hero.armor_per_level")]
        public void HeroRuntimeField_NonFinite_IsRejected(string field)
        {
            var def = ValidHero();
            switch (field)
            {
                case "hero.xp_share_radius":  def.Hero!.XpShareRadius = float.NaN; break;
                case "hero.health_per_level": def.Hero!.HealthPerLevel = float.PositiveInfinity; break;
                case "hero.damage_per_level": def.Hero!.DamagePerLevel = float.NaN; break;
                case "hero.armor_per_level":  def.Hero!.ArmorPerLevel = 32768f; break; // == ceiling (over-range)
            }
            AssertError(Run(def), field, "grunt");
        }

        [Theory]
        [InlineData("hero.xp_share_radius", 200f)]   // > HeroShareRadiusMax (128): r*r would overflow Fixed
        [InlineData("hero.health_per_level", 1000f)] // > HeroStatGrowthMax (256): the up-to-99-stack sum would overflow
        [InlineData("hero.damage_per_level", 300f)]
        [InlineData("hero.armor_per_level", 500f)]
        public void HeroRuntimeField_AboveFixedSafeCap_IsRejected(string field, float value)
        {
            // These were VALID under the pre-fix generic [0, 32768) rule but overflow the runtime's squaring/stacking.
            var def = ValidHero();
            switch (field)
            {
                case "hero.xp_share_radius":  def.Hero!.XpShareRadius  = value; break;
                case "hero.health_per_level": def.Hero!.HealthPerLevel = value; break;
                case "hero.damage_per_level": def.Hero!.DamagePerLevel = value; break;
                case "hero.armor_per_level":  def.Hero!.ArmorPerLevel  = value; break;
            }
            AssertError(Run(def), field, "grunt");
        }

        [Fact]
        public void HeroRuntimeField_WithinFixedSafeCap_IsValid()
        {
            var def = ValidHero();
            def.Hero!.XpShareRadius  = 100f; // < 128
            def.Hero!.HealthPerLevel = 200f; // < 256
            def.Hero!.DamagePerLevel = 50f;
            def.Hero!.ArmorPerLevel  = 10f;
            Assert.True(Run(def).Ok);
        }

        [Fact]
        public void HeroDamageAndArmorType_AreValid()
        {
            // 'Hero' is a reserved-but-valid combat type (the _unitcard_sample hero authors it). Not a reject.
            var def = Valid(); def.DamageType = "Hero"; def.ArmorType = "Hero";
            Assert.True(Run(def).Ok);
        }

        // ── numeric stat range: finite & [0, 32768) ──

        [Fact]
        public void NegativeStat_IsRejected()
        {
            var def = Valid(); def.Hp = -1f;
            AssertError(Run(def), "hp", "grunt");
        }

        [Fact]
        public void OverRangeStat_IsRejected()
        {
            var def = Valid(); def.AttackDamage = 32768f;   // == ceiling → overflows float→Fixed at spawn
            UnitValidationResult r = Run(def);
            AssertError(r, "attack_damage", "grunt");
            Assert.Contains("32768", r.Errors.First(e => e.FieldPath == "attack_damage").Message);
        }

        [Fact]
        public void NonFiniteStat_IsRejected()
        {
            var def = Valid(); def.Speed = float.NaN;
            AssertError(Run(def), "speed", "grunt");
        }

        [Fact]
        public void JustUnderCeilingStat_IsValid()
        {
            var def = Valid(); def.Hp = 32767f;
            Assert.True(Run(def).Ok);
        }

        // ── supply bound ──

        [Fact]
        public void NegativeSupply_IsRejected()
        {
            var def = Valid(); def.Supply = -1;
            AssertError(Run(def), "supply", "grunt");
        }

        // ── costs: negative (the parked defect) + over-bound ──

        [Fact]
        public void NegativeCrystalCost_IsRejected()
        {
            // The parked 1.3b/2.9b defect: a negative cost ADDS crystal each train.
            var def = Valid(); def.CostCrystal = -5;
            UnitValidationResult r = Run(def);
            AssertError(r, "cost_crystal", "grunt");
            Assert.Contains(">= 0", r.Errors.First(e => e.FieldPath == "cost_crystal").Message);
        }

        [Fact]
        public void NegativeOreCost_IsRejected()
        {
            var def = Valid(); def.CostOre = -1;
            AssertError(Run(def), "cost_ore", "grunt");
        }

        [Fact]
        public void OverBoundCost_IsRejected()
        {
            var def = Valid(); def.CostOre = 40000;
            AssertError(Run(def), "cost_ore", "grunt");
        }

        // ── ability references ──

        [Fact]
        public void DefinedAbilityRef_IsValid()
        {
            var def = Valid(); def.Abilities = new[] { "fireball", "heal" };
            Assert.True(Run(def).Ok);
        }

        [Fact]
        public void UndefinedAbilityRef_IsRejected()
        {
            var def = Valid(); def.Abilities = new[] { "fireball", "nonexistent_spell" };
            UnitValidationResult r = Run(def);
            AssertError(r, "abilities", "grunt");
            Assert.Contains("nonexistent_spell", r.Errors.First(e => e.FieldPath == "abilities").Message);
        }

        [Fact]
        public void AbilityRef_WithNullRegistry_IsSkipped()
        {
            // A caller with no registry cannot validate refs — the check is skipped (not fail-open crash).
            var def = Valid(); def.Abilities = new[] { "anything" };
            Assert.True(V.Validate(def, registry: null, siblings: null).Ok);
        }

        // ── tags (composes UnitTagValidator) ──

        [Fact]
        public void KnownTags_AreValid()
        {
            var def = Valid(); def.Tags = new[] { "Organic", "Magical" };
            Assert.True(Run(def).Ok);
        }

        [Fact]
        public void UnknownTag_IsRejected()
        {
            var def = Valid(); def.Tags = new[] { "Undead" };
            AssertError(Run(def), "tags", "grunt");
        }

        // ── D-9: ALL errors surface at once, not just the first ──

        [Fact]
        public void MultipleInvalidFields_ReturnAllErrors_NotJustFirst()
        {
            var def = Valid();
            def.Category = "Wizard";       // bad enum
            def.Hp = -1f;                  // bad stat
            def.CostCrystal = -5;          // bad cost
            def.Tags = new[] { "Undead" }; // bad tag
            UnitValidationResult r = Run(def);
            Assert.False(r.Ok);
            Assert.True(r.Errors.Count >= 4, $"expected >= 4 errors, got {r.Errors.Count}");
            var paths = r.Errors.Select(e => e.FieldPath).ToHashSet();
            Assert.Contains("category", paths);
            Assert.Contains("hp", paths);
            Assert.Contains("cost_crystal", paths);
            Assert.Contains("tags", paths);
        }

        // ── revives_heroes coherence (Story 3.14) ──

        [Fact]
        public void RevivesHeroes_OnNonStructure_IsRejected()
        {
            var def = Valid(); def.Category = "Melee"; def.RevivesHeroes = true;
            AssertError(Run(def), "revives_heroes", "grunt");
        }

        [Fact]
        public void RevivesHeroes_OnStructure_IsValid()
        {
            var def = Valid(); def.Category = "Structure"; def.RevivesHeroes = true;
            UnitValidationResult r = Run(def);
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void RevivesHeroes_OmittedFalse_AddsNoError()
        {
            var def = Valid(); def.Category = "Melee"; def.RevivesHeroes = false;
            Assert.True(Run(def).Ok);
        }

        // ── SanitizeId helper (shared with the panel's id-minting) ──

        [Theory]
        [InlineData("Grunt", "grunt")]
        [InlineData("new unit", "new_unit")]
        [InlineData("a-b.c", "a_b_c")]
        [InlineData("already_ok_9", "already_ok_9")]
        [InlineData("   ", "")]
        public void SanitizeId_LowercasesAndCollapses(string raw, string expected)
        {
            Assert.Equal(expected, UnitDefinitionValidator.SanitizeId(raw));
        }
    }
}
