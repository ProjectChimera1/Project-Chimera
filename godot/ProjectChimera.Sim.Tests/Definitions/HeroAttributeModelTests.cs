#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 15-21 — the creator-authorable hero attribute system, Godot-free:
    /// the closed derived-stat vocabulary, the resolver (model × hero → per-stat contributions at the single
    /// float→Fixed boundary), the validator rules (fail-closed vocabulary, reference coherence, overflow caps),
    /// the growth channel (base modifier + per-level contributions riding the Story 3.13 growth stacks), the
    /// energy pair (RegenPerTick seam + MaxEnergyOf ceiling), ContentHash movement, and the 7 shipped presets.
    /// </summary>
    public class HeroAttributeModelTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        // ── Fixture: a WC3-shaped model + a hero authored against it ────────────────────────────────────────────

        private static AttributeModelDefinition Wc3Model() => new AttributeModelDefinition
        {
            Attributes = new List<AttributeDeclaration>
            {
                new() { Id = "strength", Name = "Strength" },
                new() { Id = "agility", Name = "Agility" },
                new() { Id = "intelligence", Name = "Intelligence" },
            },
            Derived = new List<DerivedStatRule>
            {
                new() { Attribute = "strength", Stat = "max_health", PerPoint = 25f },
                new() { Attribute = "agility", Stat = "armor", PerPoint = 0.3f },
                new() { Attribute = "intelligence", Stat = "max_energy", PerPoint = 15f },
                new() { Attribute = "intelligence", Stat = "energy_regen", PerPoint = 0.002f },
                new() { Attribute = "primary", Stat = "attack_damage", PerPoint = 1f },
            },
        };

        private static HeroAttributesDefinition HeroAttrs(string primary = "strength") => new HeroAttributesDefinition
        {
            Primary = primary,
            Base = new Dictionary<string, float> { ["strength"] = 20f, ["agility"] = 10f, ["intelligence"] = 16f },
            PerLevel = new Dictionary<string, float> { ["strength"] = 2f, ["agility"] = 1f, ["intelligence"] = 1.5f },
        };

        // ── Resolver ─────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Resolver_FlattensModelTimesHero_IntoPerStatPairs()
        {
            var (b, p) = HeroAttributeResolver.Resolve(Wc3Model(), HeroAttrs());

            Assert.Equal(Fixed.FromInt(500).Raw, b[AttributeStats.MaxHealth].Raw);     // 25 × 20 str
            Assert.Equal(Fixed.FromFloat(3f).Raw, b[AttributeStats.Armor].Raw);        // 0.3 × 10 agi
            Assert.Equal(Fixed.FromInt(240).Raw, b[AttributeStats.MaxEnergy].Raw);     // 15 × 16 int
            Assert.Equal(Fixed.FromInt(20).Raw, b[AttributeStats.AttackDamage].Raw);   // primary(str) ×1 × 20
            Assert.Equal(Fixed.FromInt(50).Raw, p[AttributeStats.MaxHealth].Raw);      // 25 × 2 str/level
            Assert.Equal(Fixed.FromInt(2).Raw, p[AttributeStats.AttackDamage].Raw);    // primary ×1 × 2/level
            Assert.Equal(Fixed.Zero.Raw, b[AttributeStats.MoveSpeed].Raw);             // no rule targets it
        }

        [Fact]
        public void Resolver_PrimarySelector_FollowsTheHeroFlag()
        {
            var (bStr, _) = HeroAttributeResolver.Resolve(Wc3Model(), HeroAttrs("strength"));
            var (bInt, _) = HeroAttributeResolver.Resolve(Wc3Model(), HeroAttrs("intelligence"));
            Assert.Equal(Fixed.FromInt(20).Raw, bStr[AttributeStats.AttackDamage].Raw); // str 20 × 1
            Assert.Equal(Fixed.FromInt(16).Raw, bInt[AttributeStats.AttackDamage].Raw); // int 16 × 1 — the WC3 rule is general
        }

        [Fact]
        public void Resolver_NullModelOrAttributes_YieldsAllZero()
        {
            var (b1, p1) = HeroAttributeResolver.Resolve(null, HeroAttrs());
            var (b2, p2) = HeroAttributeResolver.Resolve(Wc3Model(), null);
            for (int s = 0; s < AttributeStats.Count; s++)
            {
                Assert.Equal(0, b1[s].Raw); Assert.Equal(0, p1[s].Raw);
                Assert.Equal(0, b2[s].Raw); Assert.Equal(0, p2[s].Raw);
            }
        }

        [Fact]
        public void ClosedVocabulary_IndexOrder_IsLoadBearing()
        {
            // The stride layout (HeroStore lanes, resolver output) depends on these exact indices — a reorder is a
            // save-format + consumer break. Appending a 7th stat is the only legal change (and must bump the save
            // FormatVersion for the stride lanes).
            Assert.Equal(0, AttributeStats.MaxHealth);
            Assert.Equal(1, AttributeStats.AttackDamage);
            Assert.Equal(2, AttributeStats.Armor);
            Assert.Equal(3, AttributeStats.MoveSpeed);
            Assert.Equal(4, AttributeStats.MaxEnergy);
            Assert.Equal(5, AttributeStats.EnergyRegen);
            Assert.Equal(6, AttributeStats.Count);
            Assert.Equal(AttributeStats.Count, AttributeStats.Ids.Length);
            Assert.True(AttributeStats.TryIndexOf("energy_regen", out int er) && er == 5);
            Assert.False(AttributeStats.TryIndexOf("attack_speed", out _)); // NOT in the closed set (no channel exists)
            Assert.False(AttributeStats.TryIndexOf(null, out _));
        }

        // ── Validator ────────────────────────────────────────────────────────────────────────────────────────────

        private static FactionDefinition MakeFaction(AttributeModelDefinition? model, HeroAttributesDefinition? attrs)
        {
            var hero = new UnitDefinition
            {
                Id = "hero", DisplayName = "Hero", Category = "Melee", IsHero = true,
                Hp = 100, Speed = 3, AttackDamage = 10, AttackRange = 2, AttackSpeed = 1,
                Hero = new HeroDefinition { Attributes = attrs },
            };
            return new FactionDefinition
            {
                Id = "test_faction", DisplayName = "Test", AttributeModel = model,
                Units = new List<UnitDefinition> { hero },
                Buildings = new List<BuildingDefinition>(),
            };
        }

        private static List<string> Errors(FactionDefinition def)
        {
            var result = FactionValidator.Validate(def);
            var list = new List<string>();
            foreach ((string _, string msg) in result.Errors) list.Add(msg);
            return list;
        }

        [Fact]
        public void Validator_UnknownStat_FailsClosed()
        {
            var model = Wc3Model();
            model.Derived!.Add(new DerivedStatRule { Attribute = "strength", Stat = "attack_speed", PerPoint = 1f });
            Assert.Contains(Errors(MakeFaction(model, HeroAttrs())), e => e.Contains("closed derived-stat vocabulary"));
        }

        [Fact]
        public void Validator_UndeclaredAttributeRefs_AreRejected_EverywhereTheyCanAppear()
        {
            // derived rule → undeclared id
            var m1 = Wc3Model();
            m1.Derived!.Add(new DerivedStatRule { Attribute = "luck", Stat = "armor", PerPoint = 1f });
            Assert.Contains(Errors(MakeFaction(m1, HeroAttrs())), e => e.Contains("'luck' is not a declared attribute id"));

            // hero primary → undeclared id
            Assert.Contains(Errors(MakeFaction(Wc3Model(), HeroAttrs(primary: "luck"))),
                e => e.Contains("'luck' is not a declared attribute id"));

            // hero base key → undeclared id
            var ha = HeroAttrs();
            ha.Base!["luck"] = 5f;
            Assert.Contains(Errors(MakeFaction(Wc3Model(), ha)), e => e.Contains("'luck' is not a declared attribute id"));
        }

        [Fact]
        public void Validator_HeroAttributesWithoutAModel_IsRejected()
            => Assert.Contains(Errors(MakeFaction(null, HeroAttrs())),
                e => e.Contains("declares no attribute_model"));

        [Fact]
        public void Validator_ReservedPrimaryId_And_Duplicates_AreRejected()
        {
            var model = Wc3Model();
            model.Attributes!.Add(new AttributeDeclaration { Id = "primary" });
            model.Attributes!.Add(new AttributeDeclaration { Id = "strength" });
            var errs = Errors(MakeFaction(model, HeroAttrs()));
            Assert.Contains(errs, e => e.Contains("reserved per-hero selector"));
            Assert.Contains(errs, e => e.Contains("duplicate attribute id 'strength'"));
        }

        [Fact]
        public void Validator_NegativeAndOversizedValues_AreRejected()
        {
            var model = Wc3Model();
            model.Derived![0].PerPoint = -1f; // negative per_point (the DW-325 ceiling-collapse class) — rejected v1
            var ha = HeroAttrs();
            ha.Base!["strength"] = 5000f;     // ≥ AttrValueMax
            var errs = Errors(MakeFaction(model, ha));
            Assert.Contains(errs, e => e.Contains("per_point") && e.Contains("must be finite"));
            Assert.Contains(errs, e => e.Contains("base.strength") && e.Contains("must be finite"));
        }

        [Fact]
        public void Validator_ResolvedContributionCaps_GuardTheNinetyNineStackOverflow()
        {
            var model = Wc3Model();
            // 25 hp/pt × 160 str/level = 4000/level — far past the 256 per-level cap even though each input is legal.
            var ha = HeroAttrs();
            ha.PerLevel!["strength"] = 160f;
            Assert.Contains(Errors(MakeFaction(model, ha)),
                e => e.Contains("resolved per-level contribution") && e.Contains("99-stack overflow guard"));
        }

        [Fact]
        public void Validator_ValidModelAndHero_PassClean()
        {
            var errs = Errors(MakeFaction(Wc3Model(), HeroAttrs()));
            Assert.DoesNotContain(errs, e => e.Contains("attribute"));
        }

        // ── Growth channel (base modifier + per-level contributions on the 3.13 stacks) ─────────────────────────

        private sealed class Rig
        {
            public EntityWorld World = new EntityWorld();
            public HeroStore Heroes = new HeroStore();
            public ModifierStore Modifiers = null!;
            public HeroXpSystem Xp = null!;
            public DeathFeed Deaths = new DeathFeed();
            public int Entity; public int Slot;
        }

        private static Rig BuildHero(int level, AttributeModelDefinition? model, HeroAttributesDefinition? attrs)
        {
            var r = new Rig();
            var modSys = new ModifierSystem();
            r.Modifiers = new ModifierStore(r.World, modSys);
            modSys.AttachStore(r.Modifiers);
            r.Entity = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            r.World.BaseMaxHealth[r.Entity] = Fixed.FromInt(100);
            r.World.EffectiveMaxHealth[r.Entity] = Fixed.FromInt(100);
            r.World.BaseAttackDamage[r.Entity] = Fixed.FromInt(10);
            r.World.EffectiveAttackDamage[r.Entity] = Fixed.FromInt(10);
            var (b, p) = HeroAttributeResolver.Resolve(model, attrs);
            r.Slot = r.Heroes.Mint(new HeroId(7), r.Entity, level, Fixed.Zero,
                maxLevel: 10, baseXp: Fixed.FromInt(100), xpGrowth: Fixed.One,
                healthPerLevel: Fixed.FromInt(10), // flat growth beside the attribute term
                attrStatBase: b, attrStatPerLevel: p);
            r.World.HeroIndex[r.Entity] = r.Heroes.PackRef(r.Slot);
            r.Xp = new HeroXpSystem(r.Heroes, r.Modifiers, r.Deaths);
            return r;
        }

        [Fact]
        public void BaseContributions_ApplyAtLevelOne_ThroughTheModifierChannel()
        {
            var r = BuildHero(level: 1, Wc3Model(), HeroAttrs());
            r.Xp.Tick(r.World, Dt);

            // str 20 × 25 = +500 hp; primary(str) 20 × 1 = +20 dmg; agi 10 × 0.3 = +3 armor. No growth stacks at L1.
            Assert.Equal(Fixed.FromInt(600).Raw, r.World.EffectiveMaxHealth[r.Entity].Raw);
            Assert.Equal(Fixed.FromInt(30).Raw, r.World.EffectiveAttackDamage[r.Entity].Raw);
            Assert.Equal(Fixed.FromFloat(3f).Raw, r.World.EffectiveArmor[r.Entity].Raw);

            // Idempotent: a second tick must not re-apply (StackRule.Ignore).
            r.Xp.Tick(r.World, Dt);
            Assert.Equal(Fixed.FromInt(600).Raw, r.World.EffectiveMaxHealth[r.Entity].Raw);
        }

        [Fact]
        public void PerLevelContributions_RideTheGrowthStacks_BesideFlatGrowth()
        {
            var r = BuildHero(level: 3, Wc3Model(), HeroAttrs());
            r.Xp.Tick(r.World, Dt); // deploy-at-level-3 catch-up: 2 growth stacks + the base modifier

            // Per stack: flat 10 hp + attr 50 hp (25×2 str/level) = 60; ×2 stacks = 120. Base: +500. Total 100+500+120.
            Assert.Equal(Fixed.FromInt(720).Raw, r.World.EffectiveMaxHealth[r.Entity].Raw);
            // Damage per stack: flat 0 + primary 2 (1×2 str/level) = 2; ×2 = 4. Base +20. Total 10+20+4.
            Assert.Equal(Fixed.FromInt(34).Raw, r.World.EffectiveAttackDamage[r.Entity].Raw);
            Assert.Equal(2, r.Heroes.GrowthStacksApplied[r.Slot]);
        }

        [Fact]
        public void NoAttributes_IsByteIdenticalToPre1521_NoBaseModifierInstalled()
        {
            var r = BuildHero(level: 2, model: null, attrs: null);
            r.Xp.Tick(r.World, Dt);
            // Flat growth only: 100 + 10×1 stack = 110; and NO ring slot burned on an all-zero base modifier.
            Assert.Equal(Fixed.FromInt(110).Raw, r.World.EffectiveMaxHealth[r.Entity].Raw);
            for (int s = 0; s < 8; s++)
                Assert.NotEqual(HeroXpSystem.HeroAttrBaseModifierId, r.Modifiers.ModifierIdAt(r.Entity, s));
        }

        // ── Energy pair (the 15.12 seam) ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void EnergyPair_RegenAndCeiling_FollowIntelligence_AndLevel()
        {
            var r = BuildHero(level: 1, Wc3Model(), HeroAttrs());
            r.World.MaxEnergy[r.Entity] = Fixed.FromInt(50);
            r.World.Energy[r.Entity]    = Fixed.FromInt(50); // authored-full
            var regen = new EnergyRegenSystem(r.Heroes);

            // Ceiling: 50 authored + int 16 × 15 = 290. Regen: authored 0 + int 16 × 0.002/tick.
            Assert.Equal(Fixed.FromInt(290).Raw, EnergyRegenSystem.MaxEnergyOf(r.World, r.Heroes, r.Entity).Raw);
            Fixed perTick = EnergyRegenSystem.RegenPerTick(r.World, r.Heroes, r.Entity);
            // The resolver accumulates in double and quantizes ONCE (0.002 × 16 → Fixed), so the expectation
            // quantizes the PRODUCT — quantize-then-multiply would land one raw unit low.
            Assert.Equal(Fixed.FromFloat(0.002f * 16f).Raw, perTick.Raw);
            Assert.True(perTick.Raw > 0);

            regen.Tick(r.World, Dt);
            Assert.Equal((Fixed.FromInt(50) + perTick).Raw, r.World.Energy[r.Entity].Raw); // regens past authored max toward the attribute ceiling

            // Level up → the ceiling and rate grow with intelligence (1.5 int/level).
            r.Heroes.Level[r.Slot] = 3;
            Fixed perTickL3 = EnergyRegenSystem.RegenPerTick(r.World, r.Heroes, r.Entity);
            Assert.True(perTickL3.Raw > perTick.Raw);
            Assert.True(EnergyRegenSystem.MaxEnergyOf(r.World, r.Heroes, r.Entity).Raw > Fixed.FromInt(290).Raw);
        }

        [Fact]
        public void EnergyPair_NonHeroAndNullStore_AreByteIdenticalToTheFlatSeam()
        {
            var w = new EntityWorld();
            int e = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.RegenRate[e] = Fixed.FromFloat(0.5f);
            w.MaxEnergy[e] = Fixed.FromInt(20);
            Assert.Equal(EnergyRegenSystem.RegenPerTick(w, e).Raw, EnergyRegenSystem.RegenPerTick(w, new HeroStore(), e).Raw);
            Assert.Equal(Fixed.FromInt(20).Raw, EnergyRegenSystem.MaxEnergyOf(w, null, e).Raw);
        }

        // ── ContentHash movement ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ContentHash_MovesOnAttributeDivergence_HeroAndModel()
        {
            var f1 = MakeFaction(Wc3Model(), HeroAttrs());
            var f2 = MakeFaction(Wc3Model(), HeroAttrs());
            f2.Units[0].Hero!.Attributes!.Base!["strength"] = 21f; // one point of strength
            var f3 = MakeFaction(Wc3Model(), HeroAttrs());
            f3.AttributeModel!.Derived![0].PerPoint = 26f;         // one point of mapping

            ulong h1 = ContentHash.Compute(new[] { f1 }, null, null, null);
            ulong h2 = ContentHash.Compute(new[] { f2 }, null, null, null);
            ulong h3 = ContentHash.Compute(new[] { f3 }, null, null, null);
            ulong h1Again = ContentHash.Compute(new[] { MakeFaction(Wc3Model(), HeroAttrs()) }, null, null, null);

            Assert.NotEqual(h1, h2); // a hero attribute divergence handshake-rejects
            Assert.NotEqual(h1, h3); // a model divergence handshake-rejects
            Assert.Equal(h1, h1Again); // and equal content folds equal (fresh dictionaries — order-independent)
        }

        [Fact]
        public void ContentHash_HeroCurveDivergence_NowRejects_TheClosedPre1521Gap()
        {
            var f1 = MakeFaction(Wc3Model(), HeroAttrs());
            var f2 = MakeFaction(Wc3Model(), HeroAttrs());
            f2.Units[0].Hero!.HealthPerLevel = 11f; // sim-read since 3.13, unfolded until ContentHash v2
            Assert.NotEqual(ContentHash.Compute(new[] { f1 }, null, null, null),
                            ContentHash.Compute(new[] { f2 }, null, null, null));
        }

        // ── Shipped presets ──────────────────────────────────────────────────────────────────────────────────────

        public static IEnumerable<object[]> PresetFiles()
        {
            foreach (string f in Directory.GetFiles(ResolvePresetDir(), "*.json"))
                yield return new object[] { f };
        }

        /// <summary>Walk up from the test bin dir to <c>resources/data/attribute-models</c> (the
        /// BuildingDefinitionValidatorTests.ResolveDataPath idiom — bin-depth-independent).</summary>
        private static string ResolvePresetDir()
        {
            for (var dir = new DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "attribute-models");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/attribute-models above {System.AppContext.BaseDirectory}");
        }

        [Theory]
        [MemberData(nameof(PresetFiles))]
        public void ShippedPresets_LoadAndValidateClean(string file)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var modelJson = doc.RootElement.GetProperty("attribute_model").GetRawText();
            var model = System.Text.Json.JsonSerializer.Deserialize<AttributeModelDefinition>(
                modelJson, FactionDefinition.JsonOptions);
            Assert.NotNull(model);
            Assert.NotNull(model!.Attributes);
            Assert.True(model.Attributes!.Count >= 3);

            // The preset must validate clean against a hero authoring every declared attribute at modest values.
            var attrs = new HeroAttributesDefinition
            {
                Primary = model.Attributes[0].Id,
                Base = new Dictionary<string, float>(),
                PerLevel = new Dictionary<string, float>(),
            };
            foreach (var a in model.Attributes) { attrs.Base![a.Id!] = 20f; attrs.PerLevel![a.Id!] = 2f; }
            var errs = Errors(MakeFaction(model, attrs));
            Assert.DoesNotContain(errs, e => e.Contains("attribute"));

            // And every derived rule must target the closed vocabulary (fail-closed teeth for hand-edited presets).
            foreach (var rule in model.Derived ?? new List<DerivedStatRule>())
                Assert.True(AttributeStats.TryIndexOf(rule.Stat, out _), $"{file}: stat '{rule.Stat}' not in the closed vocabulary");
        }

        // ── FactionWriter: the targeted attribute_model root-key patch (the 3.4 no-whole-reserialize posture) ────

        [Fact]
        public void SyncFactionAttributeModel_PatchesOnlyItsKey_AndRoundTrips()
        {
            const string factionJson = "{\n  \"id\": \"f\",\n  \"display_name\": \"F\",\n  \"signature_mechanic\": \"keep_me_verbatim\",\n  \"units\": [ { \"id\": \"u1\", \"hp\": 50 } ],\n  \"buildings\": []\n}";

            string patched = FactionWriter.SyncFactionAttributeModel(factionJson, Wc3Model());
            Assert.Contains("\"attribute_model\"", patched);
            Assert.Contains("keep_me_verbatim", patched);      // faction-level keys untouched
            Assert.Contains("\"hp\": 50", patched);            // units untouched

            // Round-trip: the lenient faction loader reads the patched model back structurally intact.
            var def = System.Text.Json.JsonSerializer.Deserialize<FactionDefinition>(patched, FactionDefinition.JsonOptions)!;
            Assert.NotNull(def.AttributeModel);
            Assert.Equal(3, def.AttributeModel!.Attributes!.Count);
            Assert.Equal(5, def.AttributeModel.Derived!.Count);
            Assert.Equal("primary", def.AttributeModel.Derived[4].Attribute);

            // Null model drops the key (a model-less faction re-emits without churn).
            string cleared = FactionWriter.SyncFactionAttributeModel(patched, null);
            Assert.DoesNotContain("attribute_model", cleared);
            Assert.Contains("keep_me_verbatim", cleared);
        }

        [Fact]
        public void HeroDefinitionClone_DeepCopiesAttributes()
        {
            var hd = new HeroDefinition { Attributes = HeroAttrs() };
            HeroDefinition clone = hd.Clone();
            clone.Attributes!.Base!["strength"] = 99f;
            Assert.Equal(20f, hd.Attributes!.Base!["strength"]); // the source is unaffected (no dictionary aliasing)
        }
    }
}
