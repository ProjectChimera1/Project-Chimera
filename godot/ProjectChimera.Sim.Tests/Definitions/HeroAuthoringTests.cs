#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.7 — the hero authoring data model + validation + persistence (no runtime, no fold). Covers the net-new
    /// <see cref="HeroDefinition"/> POCO + <see cref="HeroDefinition.Clone"/>, the <see cref="HeroLevelingPresets"/>
    /// Bundle/Detect round-trip, the <see cref="UnitDefinitionValidator"/> hero rules (is_hero↔hero coherence, leveling
    /// range, undefined signature/ultimate ref, signature≠ultimate), and the <see cref="FactionWriter"/> <c>hero</c>
    /// persistence (adds when set, absent when non-hero, siblings byte-preserved, POCO re-parses identically). All
    /// Godot-free (Tier-1), mirroring <see cref="BehaviorAndCompositionTests"/>.
    /// </summary>
    public class HeroAuthoringTests
    {
        private static readonly UnitDefinitionValidator V = new();

        /// <summary>A registry with the two abilities a valid hero references (plus an extra), so a SET slot resolves.</summary>
        private static AbilityRegistry MakeAbilityRegistry() => new(new List<AbilityDefinition>
        {
            new AbilityDefinition { Id = "storm_bolt", DisplayName = "Storm Bolt", Activation = "active" },
            new AbilityDefinition { Id = "avatar",     DisplayName = "Avatar",     Activation = "active" },
            new AbilityDefinition { Id = "minor_heal", DisplayName = "Minor Heal", Activation = "active" },
        });

        private static UnitDefinition BaseUnit(string category = "Ranged") => new UnitDefinition
        {
            Id = "archmage", DisplayName = "Archmage", Category = category,
            Hp = 100f, Speed = 4f, AttackDamage = 10f, AttackRange = 5f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored",
            CostOre = 50, CostCrystal = 0, Supply = 1, VisionRange = 8f,
            Armor = 0f, TrainTime = 8f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
            SeparationPriority = "Normal",
        };

        /// <summary>A valid hero unit: is_hero + a Standard-shaped curve + resolvable, distinct slots.</summary>
        private static UnitDefinition ValidHero()
        {
            var def = BaseUnit();
            def.IsHero = true;
            def.Hero = new HeroDefinition
            {
                MaxLevel = 10, BaseXp = 100f, XpGrowth = 1.15f, XpPerKill = 100f,
                SignatureAbility = "storm_bolt", UltimateAbility = "avatar",
            };
            return def;
        }

        private static UnitValidationResult Validate(UnitDefinition def) =>
            V.Validate(def, MakeAbilityRegistry(), behaviorRegistry: null, siblings: null);

        // ── Valid hero ───────────────────────────────────────────────────────────────

        [Fact]
        public void ValidHero_HasNoErrors()
        {
            UnitValidationResult r = Validate(ValidHero());
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void ValidHero_EmptySlots_AreValid()
        {
            // Empty (null) signature/ultimate = "not authored yet" — valid.
            var def = ValidHero();
            def.Hero!.SignatureAbility = null;
            def.Hero!.UltimateAbility = null;
            Assert.True(Validate(def).Ok);
        }

        [Fact]
        public void NonHeroUnit_AddsNoHeroErrors()
        {
            var def = BaseUnit();   // IsHero false, Hero null
            Assert.True(Validate(def).Ok);
            Assert.DoesNotContain(Validate(def).Errors, e => e.FieldPath.StartsWith("hero.") || e.FieldPath == "is_hero");
        }

        // ── Coherence: is_hero ↔ hero (both directions) ──────────────────────────────

        [Fact]
        public void HeroFlagWithoutBlock_IsLocatedError()
        {
            var def = BaseUnit();
            def.IsHero = true;
            def.Hero = null;
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "is_hero");
            Assert.Contains("archmage", r.Errors.First(e => e.FieldPath == "is_hero").Message);
        }

        [Fact]
        public void HeroBlockWithoutFlag_IsLocatedError()
        {
            var def = BaseUnit();
            def.IsHero = false;
            def.Hero = new HeroDefinition();   // block present but flag off
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "is_hero");
        }

        // ── Leveling curve range ─────────────────────────────────────────────────────

        [Theory]
        [InlineData(1)]     // below HeroLevelMin
        [InlineData(0)]
        [InlineData(500)]   // above HeroLevelMax
        public void OutOfRangeMaxLevel_IsLocatedError(int maxLevel)
        {
            var def = ValidHero();
            def.Hero!.MaxLevel = maxLevel;
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.max_level");
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void OutOfRangeBaseXp_IsLocatedError(float baseXp)
        {
            var def = ValidHero();
            def.Hero!.BaseXp = baseXp;
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.base_xp");
        }

        [Theory]
        [InlineData(0.5f)]  // below 1
        [InlineData(0f)]
        [InlineData(100f)]  // at/above the cap (exclusive)
        [InlineData(float.NaN)]
        public void OutOfRangeXpGrowth_IsLocatedError(float growth)
        {
            var def = ValidHero();
            def.Hero!.XpGrowth = growth;
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.xp_growth");
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.NegativeInfinity)]
        public void OutOfRangeXpPerKill_IsLocatedError(float perKill)
        {
            var def = ValidHero();
            def.Hero!.XpPerKill = perKill;
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.xp_per_kill");
        }

        [Fact]
        public void XpPerKillZero_IsValid()
        {
            var def = ValidHero();
            def.Hero!.XpPerKill = 0f;   // ≥ 0 allowed
            Assert.True(Validate(def).Ok);
        }

        // ── Ability slot refs ────────────────────────────────────────────────────────

        [Fact]
        public void UndefinedSignatureRef_IsLocatedError()
        {
            var def = ValidHero();
            def.Hero!.SignatureAbility = "nope";
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            (string FieldPath, string Message) e = r.Errors.First(x => x.FieldPath == "hero.signature_ability");
            Assert.Contains("nope", e.Message);
            Assert.Contains("archmage", e.Message);
        }

        [Fact]
        public void UndefinedUltimateRef_IsLocatedError()
        {
            var def = ValidHero();
            def.Hero!.UltimateAbility = "nope";
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.ultimate_ability");
        }

        [Fact]
        public void NullRegistry_SkipsSlotRefCheck()
        {
            // No registry to validate against ⇒ the ref check is skipped (not a crash); the curve/coherence rules still run.
            var def = ValidHero();
            def.Hero!.SignatureAbility = "does_not_exist";
            UnitValidationResult r = V.Validate(def, registry: null, behaviorRegistry: null, siblings: null);
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(x => x.Message)));
        }

        [Fact]
        public void NullRegistry_StillEnforcesCurveAndComposition()
        {
            var def = ValidHero();
            def.Hero!.MaxLevel = 1;                     // out of range — a non-ref rule
            def.Hero!.UltimateAbility = "storm_bolt";   // == signature — the composition rule
            UnitValidationResult r = V.Validate(def, registry: null, behaviorRegistry: null, siblings: null);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.max_level");
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.ultimate_ability");
        }

        // ── Composition rule: signature ≠ ultimate ───────────────────────────────────

        [Fact]
        public void SignatureEqualsUltimate_IsLocatedError()
        {
            var def = ValidHero();
            def.Hero!.UltimateAbility = "storm_bolt";   // == signature
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            (string FieldPath, string Message) e = r.Errors.First(x => x.FieldPath == "hero.ultimate_ability");
            Assert.Contains("differ", e.Message);
        }

        [Fact]
        public void MultipleHeroViolations_AllReported()
        {
            var def = ValidHero();
            def.Hero!.MaxLevel = 500;
            def.Hero!.BaseXp = -1f;
            def.Hero!.SignatureAbility = "ghost";
            UnitValidationResult r = Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.max_level");
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.base_xp");
            Assert.Contains(r.Errors, e => e.FieldPath == "hero.signature_ability");
        }

        // ── HeroLevelingPresets: Bundle / Detect round-trip ──────────────────────────

        [Fact]
        public void EveryPreset_DetectsBackToItsKind()
        {
            foreach ((HeroLevelingPresets.Kind kind, _) in HeroLevelingPresets.All)
            {
                if (kind == HeroLevelingPresets.Kind.Custom) continue;
                HeroLevelingPresets.Curve c = HeroLevelingPresets.Bundle(kind);
                var hero = new HeroDefinition { MaxLevel = c.MaxLevel, BaseXp = c.BaseXp, XpGrowth = c.XpGrowth, XpPerKill = c.XpPerKill };
                Assert.Equal(kind, HeroLevelingPresets.Detect(hero));
            }
        }

        [Fact]
        public void ArbitraryCurve_DetectsAsCustom()
        {
            var hero = new HeroDefinition { MaxLevel = 42, BaseXp = 333f, XpGrowth = 1.03f, XpPerKill = 7f };
            Assert.Equal(HeroLevelingPresets.Kind.Custom, HeroLevelingPresets.Detect(hero));
        }

        [Fact]
        public void NullHero_DetectsAsCustom()
        {
            Assert.Equal(HeroLevelingPresets.Kind.Custom, HeroLevelingPresets.Detect(null));
        }

        [Fact]
        public void EveryPresetBundle_IsInValidatorRange()
        {
            // The Simple presets must never author a curve the validator would reject.
            foreach ((HeroLevelingPresets.Kind kind, _) in HeroLevelingPresets.All)
            {
                if (kind == HeroLevelingPresets.Kind.Custom) continue;
                HeroLevelingPresets.Curve c = HeroLevelingPresets.Bundle(kind);
                var def = BaseUnit();
                def.IsHero = true;
                def.Hero = new HeroDefinition { MaxLevel = c.MaxLevel, BaseXp = c.BaseXp, XpGrowth = c.XpGrowth, XpPerKill = c.XpPerKill };
                Assert.True(Validate(def).Ok, $"preset {kind} is out of validator range");
            }
        }

        // ── HeroDefinition.Clone independence ────────────────────────────────────────

        [Fact]
        public void Clone_IsIndependentDeepCopy()
        {
            var src = new HeroDefinition
            {
                MaxLevel = 12, BaseXp = 120f, XpGrowth = 1.2f, XpPerKill = 90f,
                SignatureAbility = "storm_bolt", UltimateAbility = "avatar",
            };
            HeroDefinition clone = src.Clone();
            Assert.Equal(src.MaxLevel, clone.MaxLevel);
            Assert.Equal(src.SignatureAbility, clone.SignatureAbility);

            clone.MaxLevel = 99;
            clone.SignatureAbility = "changed";
            Assert.Equal(12, src.MaxLevel);                 // source unaffected
            Assert.Equal("storm_bolt", src.SignatureAbility);
        }

        [Fact]
        public void DuplicatedUnit_ClonesHeroBlock_ValidatesIndependently()
        {
            // Mirrors the editor's CloneUnit(Hero = s.Hero?.Clone()): the clone owns its own hero block.
            var src = ValidHero();
            var clone = new UnitDefinition { Id = "archmage_copy", Hero = src.Hero?.Clone(), IsHero = src.IsHero };
            Assert.NotNull(clone.Hero);
            Assert.False(ReferenceEquals(src.Hero, clone.Hero));
            clone.Hero!.MaxLevel = 50;
            Assert.Equal(10, src.Hero!.MaxLevel);
        }

        // ── FactionWriter: hero persistence ──────────────────────────────────────────

        private const string Faction = """
        {
          "id": "alpha",
          "signature_mechanic": "transmutation",
          "units": [
            {
              "id": "archmage",
              "display_name": "Archmage",
              "category": "Ranged",
              "hp": 100,
              "tags": ["Magical"]
            }
          ]
        }
        """;

        [Fact]
        public void Patch_AddsHeroBlock_AndPreservesSiblingTokens()
        {
            var def = new UnitDefinition
            {
                Id = "archmage", DisplayName = "Archmage", Category = "Ranged", Hp = 100f,
                Tags = new[] { "Magical" },
                IsHero = true,
                Hero = new HeroDefinition
                {
                    MaxLevel = 10, BaseXp = 100f, XpGrowth = 1.15f, XpPerKill = 100f,
                    SignatureAbility = "storm_bolt", UltimateAbility = "avatar",
                },
            };
            string patched = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archmage", Def = def });

            JsonNode root = JsonNode.Parse(patched)!;
            JsonObject unit = root["units"]!.AsArray().Select(n => n!.AsObject()).First(o => (string?)o["id"] == "archmage");

            Assert.True((bool)unit["is_hero"]!);
            JsonObject hero = unit["hero"]!.AsObject();
            Assert.Equal(10, (int)hero["max_level"]!);
            Assert.Equal("storm_bolt", (string?)hero["signature_ability"]);
            Assert.Equal("avatar", (string?)hero["ultimate_ability"]);
            // Untouched sibling tokens preserved.
            Assert.Equal("transmutation", (string?)root["signature_mechanic"]);
            Assert.Equal(new[] { "Magical" }, unit["tags"]!.AsArray().Select(n => (string)n!).ToArray());
        }

        [Fact]
        public void Patch_NonHeroUnit_StaysAbsent()
        {
            var def = new UnitDefinition
            {
                Id = "archmage", DisplayName = "Archmage", Category = "Ranged", Hp = 100f,
                IsHero = false, Hero = null,
            };
            string patched = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archmage", Def = def });

            JsonObject unit = JsonNode.Parse(patched)!["units"]!.AsArray()
                .Select(n => n!.AsObject()).First(o => (string?)o["id"] == "archmage");
            Assert.False(unit.ContainsKey("hero"));      // non-hero → no hero block (no faction JSON churn)
            Assert.False(unit.ContainsKey("is_hero"));   // default false → absent
        }

        [Fact]
        public void Patch_PromoteOff_RemovesHeroBlock()
        {
            // Start from a faction whose unit already carries a hero block, then persist a non-hero def → block dropped.
            const string heroFaction = """
            {
              "id": "alpha",
              "units": [
                { "id": "archmage", "category": "Ranged", "is_hero": true,
                  "hero": { "max_level": 10, "base_xp": 100, "xp_growth": 1.15, "xp_per_kill": 100 } }
              ]
            }
            """;
            var def = new UnitDefinition { Id = "archmage", Category = "Ranged", IsHero = false, Hero = null };
            string patched = FactionWriter.PatchFactionJson(heroFaction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archmage", Def = def });

            JsonObject unit = JsonNode.Parse(patched)!["units"]!.AsArray()
                .Select(n => n!.AsObject()).First(o => (string?)o["id"] == "archmage");
            Assert.False(unit.ContainsKey("hero"));
            Assert.False((bool?)unit["is_hero"] ?? false);
        }

        [Fact]
        public void RoundTrip_HeroUnit_ReparsesToIdenticalDefinition()
        {
            var def = ValidHero();
            string json = FactionWriter.SerializeUnitClean(def);
            UnitDefinition? back = JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions);
            Assert.NotNull(back);
            Assert.True(back!.IsHero);
            Assert.NotNull(back.Hero);
            Assert.Equal(def.Hero!.MaxLevel, back.Hero!.MaxLevel);
            Assert.Equal(def.Hero!.BaseXp, back.Hero!.BaseXp);
            Assert.Equal(def.Hero!.XpGrowth, back.Hero!.XpGrowth);
            Assert.Equal(def.Hero!.XpPerKill, back.Hero!.XpPerKill);
            Assert.Equal(def.Hero!.SignatureAbility, back.Hero!.SignatureAbility);
            Assert.Equal(def.Hero!.UltimateAbility, back.Hero!.UltimateAbility);
            // And it re-validates clean.
            Assert.True(Validate(back).Ok);
        }

        [Fact]
        public void PromotedHero_WithUnsetSlots_OmitsNullSlotKeys()
        {
            // A default-promoted hero (no signature/ultimate authored yet) must NOT balloon the faction JSON with
            // explicit "signature_ability": null keys — omit-on-default, matching every other ApplyFields field.
            var def = new UnitDefinition
            {
                Id = "archmage", DisplayName = "Archmage", Category = "Ranged", Hp = 100f,
                IsHero = true,
                Hero = new HeroDefinition { MaxLevel = 10, BaseXp = 100f, XpGrowth = 1.15f, XpPerKill = 100f },
            };
            string patched = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archmage", Def = def });

            JsonObject hero = JsonNode.Parse(patched)!["units"]!.AsArray()
                .Select(n => n!.AsObject()).First(o => (string?)o["id"] == "archmage")["hero"]!.AsObject();
            Assert.False(hero.ContainsKey("signature_ability"));   // unset ⇒ omitted, not null
            Assert.False(hero.ContainsKey("ultimate_ability"));
            Assert.Equal(10, (int)hero["max_level"]!);             // set fields still written

            // Round-trips: an omitted slot deserializes back to null (unchanged value).
            UnitDefinition? back = JsonSerializer.Deserialize<UnitDefinition>(
                FactionWriter.SerializeUnitClean(def), FactionDefinition.JsonOptions);
            Assert.Null(back!.Hero!.SignatureAbility);
            Assert.Null(back.Hero!.UltimateAbility);
        }

        [Fact]
        public void ShippedUnitCardSampleFixture_EveryUnitValidates()
        {
            // Guards shipped hero data against validator drift: the is_hero↔hero coherence rule (this story) would
            // otherwise silently invalidate any shipped is_hero unit that predates the hero block. The _unitcard_sample
            // fixture is the only shipped faction carrying is_hero, and it is the Unit Card editor's /godot-verify target.
            string factionsDir = ResourceDir("factions");
            var faction = JsonSerializer.Deserialize<FactionDefinition>(
                File.ReadAllText(Path.Combine(factionsDir, "_unitcard_sample.json")), FactionDefinition.JsonOptions);
            Assert.NotNull(faction);

            AbilityRegistry registry = AbilityRegistry.LoadFromDirectory(ResourceDir("abilities"));

            foreach (UnitDefinition unit in faction!.Units)
            {
                UnitValidationResult r = V.Validate(unit, registry, behaviorRegistry: null, faction.Units);
                Assert.True(r.Ok, $"shipped unit '{unit.Id}' fails validation: " +
                    string.Join("; ", r.Errors.Select(e => e.Message)));
            }
        }

        // <repo>/godot/ProjectChimera.Sim.Tests/Definitions/THIS.cs → <repo>/godot/resources/data/<name>
        private static string ResourceDir(string name, [CallerFilePath] string thisFile = "")
        {
            string godot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
            return Path.Combine(godot, "resources", "data", name);
        }
    }
}
