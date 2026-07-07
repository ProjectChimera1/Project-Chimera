#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.6 — the orthogonal composition data model + validation (no runtime, no fold). Covers the net-new
    /// <see cref="BehaviorRegistry"/> load path, <see cref="BehaviorDefinition.IsCompatibleWith"/>, the two new
    /// <see cref="UnitDefinitionValidator"/> behavior rules (undefined ref + archetype-incompatible, each with a positive
    /// AND a negative control), the <see cref="UnitCompositionPresets"/> Bundle/Detect round-trip, and the
    /// <see cref="FactionWriter"/> <c>behaviors</c> persistence (adds when set, absent when empty, siblings byte-preserved).
    /// All Godot-free (Tier-1).
    /// </summary>
    public class BehaviorAndCompositionTests
    {
        private static readonly UnitDefinitionValidator V = new();

        // A registry of two behaviors: 'support' (all archetypes) and 'skirmish' (all but Structure).
        private static BehaviorRegistry MakeBehaviorRegistry() => new(new List<BehaviorDefinition>
        {
            new BehaviorDefinition { Id = "support",  CompatibleArchetypes = new[] { "Worker", "Melee", "Ranged", "Siege", "Air", "Structure" } },
            new BehaviorDefinition { Id = "skirmish", CompatibleArchetypes = new[] { "Worker", "Melee", "Ranged", "Siege", "Air" } },
        });

        private static UnitDefinition ValidUnit(string category = "Ranged") => new UnitDefinition
        {
            Id = "healer", DisplayName = "Healer", Category = category,
            Hp = 100f, Speed = 4f, AttackDamage = 10f, AttackRange = 5f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored",
            CostOre = 50, CostCrystal = 0, Supply = 1, VisionRange = 8f,
            Armor = 0f, TrainTime = 8f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
            SeparationPriority = "Normal",
        };

        // ── BehaviorDefinition.IsCompatibleWith ──────────────────────────────────────

        [Fact]
        public void NullOrEmptyCompatibleArchetypes_MeansAll()
        {
            Assert.True(new BehaviorDefinition { Id = "b", CompatibleArchetypes = null }.IsCompatibleWith("Structure"));
            Assert.True(new BehaviorDefinition { Id = "b", CompatibleArchetypes = Array.Empty<string>() }.IsCompatibleWith("Worker"));
        }

        [Fact]
        public void ListedCompatibleArchetypes_MeansOnlyThose()
        {
            var b = new BehaviorDefinition { Id = "skirmish", CompatibleArchetypes = new[] { "Ranged", "Air" } };
            Assert.True(b.IsCompatibleWith("Ranged"));
            Assert.True(b.IsCompatibleWith("Air"));
            Assert.False(b.IsCompatibleWith("Structure"));
            Assert.False(b.IsCompatibleWith("ranged"));   // case-sensitive
        }

        // ── BehaviorRegistry.LoadFromDirectory ───────────────────────────────────────

        [Fact]
        public void LoadFromDirectory_LoadsValid_SkipsInvalid_DeterministicOrder()
        {
            string dir = TempDir();
            try
            {
                // Written out of Id order to prove the registry sorts ascending by Id (deterministic index).
                File.WriteAllText(Path.Combine(dir, "z_support.json"),
                    "{ \"id\": \"support\", \"display_name\": \"Support\" }");
                File.WriteAllText(Path.Combine(dir, "a_skirmish.json"),
                    "{ \"id\": \"skirmish\", \"compatible_archetypes\": [\"Ranged\"] }");
                File.WriteAllText(Path.Combine(dir, "empty_id.json"),
                    "{ \"id\": \"\", \"display_name\": \"Nameless\" }");          // invalid: empty id → skipped
                File.WriteAllText(Path.Combine(dir, "bad_archetype.json"),
                    "{ \"id\": \"bad\", \"compatible_archetypes\": [\"Wizard\"] }"); // invalid: unknown archetype → skipped
                File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json ");   // invalid parse → skipped

                var skipped = new List<string>();
                BehaviorRegistry reg = BehaviorRegistry.LoadFromDirectory(dir, skipped.Add);

                Assert.Equal(2, reg.Count);
                Assert.Equal("skirmish", reg.Get(0).Id);   // ascending by Id, not file order
                Assert.Equal("support", reg.Get(1).Id);
                Assert.True(reg.IndexOf("support") >= 0);
                Assert.Equal(-1, reg.IndexOf("bad"));
                Assert.Equal(3, skipped.Count);   // empty_id + bad_archetype + broken
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void LoadFromDirectory_MissingDir_IsEmpty()
        {
            BehaviorRegistry reg = BehaviorRegistry.LoadFromDirectory(Path.Combine(Path.GetTempPath(), "no_such_dir_" + Guid.NewGuid()));
            Assert.Equal(0, reg.Count);
        }

        // ── Validator: undefined behavior ref ────────────────────────────────────────

        [Fact]
        public void ValidComposition_HasNoErrors()
        {
            var def = ValidUnit("Ranged");
            def.Abilities = Array.Empty<string>();
            def.Behaviors = new[] { "support" };
            UnitValidationResult r = V.Validate(def, AbilityRegistry.Empty, MakeBehaviorRegistry(), null);
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void UndefinedBehaviorRef_IsLocatedError()
        {
            var def = ValidUnit(); def.Behaviors = new[] { "support", "no_such_behavior" };
            UnitValidationResult r = V.Validate(def, AbilityRegistry.Empty, MakeBehaviorRegistry(), null);
            Assert.False(r.Ok);
            (string FieldPath, string Message) e = r.Errors.First(x => x.FieldPath == "behaviors");
            Assert.Contains("healer", e.Message);            // located: names the unit id
            Assert.Contains("no_such_behavior", e.Message);  // names the offending ref
        }

        [Fact]
        public void NullBehaviorElement_IsRejectedNotCrashed()
        {
            // A hand-authored "behaviors": [null] (raw-JSON hatch) must fail closed as an undefined ref, not throw.
            var def = ValidUnit(); def.Behaviors = new string[] { null! };
            UnitValidationResult r = V.Validate(def, AbilityRegistry.Empty, MakeBehaviorRegistry(), null);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, x => x.FieldPath == "behaviors");
        }

        [Fact]
        public void ArchetypeIncompatibleBehavior_IsLocatedError()
        {
            // skirmish excludes Structure → attaching it to a Structure unit is rejected.
            var def = ValidUnit("Structure"); def.Behaviors = new[] { "skirmish" };
            UnitValidationResult r = V.Validate(def, AbilityRegistry.Empty, MakeBehaviorRegistry(), null);
            Assert.False(r.Ok);
            (string FieldPath, string Message) e = r.Errors.First(x => x.FieldPath == "behaviors");
            Assert.Contains("skirmish", e.Message);
            Assert.Contains("Structure", e.Message);
        }

        [Fact]
        public void CompatibleBehavior_OnItsArchetype_IsValid()
        {
            var def = ValidUnit("Ranged"); def.Behaviors = new[] { "skirmish" };   // skirmish includes Ranged
            Assert.True(V.Validate(def, AbilityRegistry.Empty, MakeBehaviorRegistry(), null).Ok);
        }

        [Fact]
        public void NullBehaviorRegistry_SkipsBehaviorChecks()
        {
            // A caller with no behavior registry cannot validate refs — the checks are skipped (not a crash).
            var def = ValidUnit(); def.Behaviors = new[] { "anything_at_all" };
            Assert.True(V.Validate(def, AbilityRegistry.Empty, behaviorRegistry: null, siblings: null).Ok);
        }

        [Fact]
        public void ThreeArgOverload_StillCompilesAndSkipsBehaviors()
        {
            // Existing 3-arg callers are unaffected — behaviors are not checked (no registry supplied).
            var def = ValidUnit(); def.Behaviors = new[] { "anything" };
            Assert.True(V.Validate(def, AbilityRegistry.Empty, siblings: null).Ok);
        }

        // ── UnitCompositionPresets: Bundle / Detect round-trip ───────────────────────

        [Fact]
        public void EveryBundle_DetectsBackToItsKind()
        {
            foreach ((UnitCompositionPresets.Kind kind, _) in UnitCompositionPresets.All)
            {
                if (kind == UnitCompositionPresets.Kind.Custom) continue;
                string[] bundle = UnitCompositionPresets.Bundle(kind);
                Assert.Equal(kind, UnitCompositionPresets.Detect(bundle));
            }
        }

        [Fact]
        public void ArbitrarySet_DetectsAsCustom()
        {
            Assert.Equal(UnitCompositionPresets.Kind.Custom, UnitCompositionPresets.Detect(new[] { "fireball", "battle_fury" }));
            Assert.Equal(UnitCompositionPresets.Kind.Custom, UnitCompositionPresets.Detect(Array.Empty<string>()));
            Assert.Equal(UnitCompositionPresets.Kind.Custom, UnitCompositionPresets.Detect(null));
        }

        [Fact]
        public void CustomBundle_IsEmpty()
        {
            Assert.Empty(UnitCompositionPresets.Bundle(UnitCompositionPresets.Kind.Custom));
        }

        // ── FactionWriter: behaviors persistence ─────────────────────────────────────

        private const string Faction = """
        {
          "id": "alpha",
          "signature_mechanic": "transmutation",
          "units": [
            {
              "id": "healer",
              "display_name": "Healer",
              "category": "Ranged",
              "hp": 100,
              "abilities": ["minor_heal"],
              "tags": ["Magical"]
            }
          ]
        }
        """;

        [Fact]
        public void Patch_AddsBehaviors_AndPreservesSiblingTokens()
        {
            var def = new UnitDefinition
            {
                Id = "healer", DisplayName = "Healer", Category = "Ranged", Hp = 100f,
                Abilities = new[] { "minor_heal" },
                Tags = new[] { "Magical" },   // matches the on-disk token → must be preserved (not dropped by the reconcile)
                Behaviors = new[] { "support" },
            };
            string patched = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "healer", Def = def });

            JsonNode root = JsonNode.Parse(patched)!;
            JsonObject unit = root["units"]!.AsArray().Select(n => n!.AsObject()).First(o => (string?)o["id"] == "healer");

            string[] behaviors = unit["behaviors"]!.AsArray().Select(n => (string)n!).ToArray();
            Assert.Equal(new[] { "support" }, behaviors);
            // Untouched sibling tokens preserved.
            Assert.Equal("transmutation", (string?)root["signature_mechanic"]);
            Assert.Equal(new[] { "minor_heal" }, unit["abilities"]!.AsArray().Select(n => (string)n!).ToArray());
            Assert.Equal(new[] { "Magical" }, unit["tags"]!.AsArray().Select(n => (string)n!).ToArray());
        }

        [Fact]
        public void Patch_EmptyBehaviors_StaysAbsent()
        {
            var def = new UnitDefinition
            {
                Id = "healer", DisplayName = "Healer", Category = "Ranged", Hp = 100f,
                Abilities = new[] { "minor_heal" },
                Behaviors = Array.Empty<string>(),
            };
            string patched = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "healer", Def = def });

            JsonObject unit = JsonNode.Parse(patched)!["units"]!.AsArray()
                .Select(n => n!.AsObject()).First(o => (string?)o["id"] == "healer");
            Assert.False(unit.ContainsKey("behaviors"));   // empty + unchanged → key absent (no ballooning)
        }

        [Fact]
        public void RoundTrip_ComposedUnit_PreservesArchetypeAbilitiesBehaviors()
        {
            var def = new UnitDefinition
            {
                Id = "healer", DisplayName = "Healer", Category = "Ranged", Hp = 100f,
                Abilities = new[] { "minor_heal" },
                Behaviors = new[] { "support" },
            };
            string json = FactionWriter.SerializeUnitClean(def);
            UnitDefinition? back = System.Text.Json.JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions);
            Assert.NotNull(back);
            Assert.Equal("Ranged", back!.Category);
            Assert.Equal(new[] { "minor_heal" }, back.Abilities);
            Assert.Equal(new[] { "support" }, back.Behaviors);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chimera_behaviors_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
