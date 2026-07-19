#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Single-source-of-truth refactor coverage for the six <see cref="UnitCategory"/> archetypes
    /// (<c>spec-unit-category-single-source</c>). Locks the derivation contract of <see cref="UnitCategories"/>
    /// (<c>All</c> is enum-derived, in order, and grows with the enum), the exact <c>Combat</c>/<c>PipeList</c>/
    /// <c>CombatOrPhrase</c> fragments the validators embed, and — for each of the three repointed consumers
    /// (<see cref="UnitDefinitionValidator"/>, <see cref="BehaviorRegistry"/>, <see cref="FactionValidator"/>) — a
    /// positive + negative control proving behavior is byte-identical to the pre-refactor literals. All Godot-free
    /// (Tier-1). Mirrors the temp-file <c>LoadFromDirectory</c> helper in <c>BehaviorAndCompositionTests</c> and the
    /// <c>ValidFaction()</c> builder shape in <c>FactionValidatorTests</c>.
    /// </summary>
    public class UnitCategorySingleSourceTests
    {
        private static readonly UnitDefinitionValidator V = new();

        // ── Derivation contract ──────────────────────────────────────────────────────

        [Fact]
        public void All_MatchesTheEnumMembers_InOrder()
        {
            // This hardcoded literal is the deliberate tripwire: because All is derived from the enum
            // (Enum.GetNames), any enum member added/renamed/reordered makes this assertion FAIL, forcing whoever
            // changes the enum to review the downstream archetype seams (validators, dropdowns, and the ParsedCategory
            // string→enum mapping) rather than have a new category silently propagate half-way. Asserting against
            // Enum.GetNames itself would be tautological (All IS that) and guard nothing.
            Assert.Equal(new[] { "Worker", "Melee", "Ranged", "Siege", "Air", "Structure" }, UnitCategories.All);
        }

        [Fact]
        public void Combat_PipeList_CombatOrPhrase_HaveExactExpectedValues()
        {
            Assert.Equal(new[] { "Melee", "Ranged", "Siege", "Air" }, UnitCategories.Combat);
            Assert.Equal("Worker|Melee|Ranged|Siege|Air|Structure", UnitCategories.PipeList);
            Assert.Equal("Melee, Ranged, Siege, or Air", UnitCategories.CombatOrPhrase);
        }

        // ── UnitDefinitionValidator: category check + error text ─────────────────────

        [Fact]
        public void EveryKnownArchetype_ProducesNoCategoryError()
        {
            foreach (string category in UnitCategories.All)
            {
                UnitValidationResult r = V.Validate(ValidUnit(category), AbilityRegistry.Empty, siblings: null);
                Assert.DoesNotContain(r.Errors, e => e.FieldPath == "category");
            }
        }

        [Fact]
        public void UnknownArchetype_ProducesLocatedCategoryError_ContainingPipeList()
        {
            UnitValidationResult r = V.Validate(ValidUnit("Caster"), AbilityRegistry.Empty, siblings: null);
            Assert.False(r.Ok);
            (string FieldPath, string Message) e = r.Errors.First(x => x.FieldPath == "category");
            Assert.Contains("Caster", e.Message);
            Assert.Contains(UnitCategories.PipeList, e.Message);
        }

        // ── BehaviorRegistry: archetype-token gate ───────────────────────────────────

        [Fact]
        public void BehaviorListingEveryArchetype_IsKept()
        {
            string dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "all_archetypes.json"),
                    "{ \"id\": \"support\", \"compatible_archetypes\": [" +
                    string.Join(", ", UnitCategories.All.Select(a => $"\"{a}\"")) + "] }");

                var skipped = new List<string>();
                BehaviorRegistry reg = BehaviorRegistry.LoadFromDirectory(dir, skipped.Add);

                Assert.Equal(1, reg.Count);
                Assert.True(reg.IndexOf("support") >= 0);
                Assert.Empty(skipped);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void BehaviorWithOutOfSetToken_IsSkipped()
        {
            string dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "bad.json"),
                    "{ \"id\": \"bad\", \"compatible_archetypes\": [\"Wizard\"] }");

                var skipped = new List<string>();
                BehaviorRegistry reg = BehaviorRegistry.LoadFromDirectory(dir, skipped.Add);

                Assert.Equal(0, reg.Count);
                Assert.Single(skipped);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ── FactionValidator: required-combat-role check + error text ────────────────

        [Fact]
        public void RosterWithWorkerPlusCombatUnit_HasNoMissingCombatError()
        {
            FactionDefinition def = WorkerFaction();
            def.Units.Add(CombatUnit("Ranged"));

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.DoesNotContain(complete.Errors,
                e => e.FieldPath == "units" && e.Message.Contains("combat"));
        }

        [Fact]
        public void WorkerOnlyRoster_ProducesLocatedMissingCombatError_ContainingCombatOrPhrase()
        {
            FactionDefinition def = WorkerFaction();   // a Worker but no combat unit

            FactionValidationResult complete = FactionValidator.ValidateComplete(def);
            Assert.False(complete.Ok);
            Assert.Contains(complete.Errors, e =>
                e.FieldPath == "units" && e.Message.Contains(UnitCategories.CombatOrPhrase));
        }

        // ── Builders / helpers ───────────────────────────────────────────────────────

        private static UnitDefinition ValidUnit(string category) => new()
        {
            Id = "u", DisplayName = "U", Category = category,
            Hp = 100f, Speed = 4f, AttackDamage = 10f, AttackRange = 5f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored",
            CostOre = 50, CostCrystal = 0, Supply = 1, VisionRange = 8f,
            Armor = 0f, TrainTime = 8f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
            SeparationPriority = "Normal",
        };

        private static UnitDefinition Worker(string id = "worker") => new()
        {
            Id = id, DisplayName = id, Category = "Worker", MeshPath = "res://assets/worker.glb", Hp = 50f,
        };

        private static UnitDefinition CombatUnit(string category, string id = "fighter") => new()
        {
            Id = id, DisplayName = id, Category = category, MeshPath = "res://assets/fighter.glb", Hp = 50f,
        };

        /// <summary>A faction with exactly one Worker (and a valid Structure building) — used to isolate the
        /// missing-combat axis.</summary>
        private static FactionDefinition WorkerFaction()
        {
            var def = new FactionDefinition { Id = "test_faction", DisplayName = "Test Faction" };
            def.Units.Add(Worker());
            def.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "command_center", Category = "Structure",
                MeshPath = "res://assets/command_center.glb", Hp = 100f,
                ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Worker",
            });
            return def;
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chimera_unit_category_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
