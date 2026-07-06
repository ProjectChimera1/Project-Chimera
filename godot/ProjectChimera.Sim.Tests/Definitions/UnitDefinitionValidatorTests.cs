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
