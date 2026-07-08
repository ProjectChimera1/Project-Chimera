#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;               // HeroStore
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.8 — the persistence-manifest authoring data model + catalog + validation + persistence (no runtime, no
    /// fold). Covers the net-new <see cref="PersistableAttributes"/> catalog, the <see cref="PersistenceManifest"/> POCO
    /// (<see cref="PersistenceManifest.Clone"/> / <see cref="PersistenceManifest.DeriveProfileShape"/>), the
    /// <see cref="PersistenceManifestValidator"/> located-multi-error rules, the omit-when-null <see cref="ScenarioData"/>
    /// round-trip, the <see cref="ScenarioValidator"/> fail-closed hook, a shipped-scenario guard, and the golden/hash
    /// neutrality of a null manifest. All Godot-free (Tier-1), mirroring <see cref="HeroAuthoringTests"/>.
    /// </summary>
    public class PersistenceManifestTests
    {
        private static readonly PersistenceManifestValidator V = new();
        private static readonly ScenarioValidator SV = new();

        // ── Catalog ────────────────────────────────────────────────────────────────

        [Fact]
        public void Catalog_EligibleAttributes_AreHeroLevelAndXp_InScopeHero()
        {
            // Story 3.16: hero.inventory joins hero.level/hero.xp as an init-time-eligible Hero attribute.
            Assert.Equal(3, PersistableAttributes.Eligible.Length);
            Assert.Equal("hero.level",     PersistableAttributes.Eligible[0].Key);
            Assert.Equal("hero.xp",        PersistableAttributes.Eligible[1].Key);
            Assert.Equal("hero.inventory", PersistableAttributes.Eligible[2].Key);
            Assert.All(PersistableAttributes.Eligible, a => Assert.Equal(AttributeScope.Hero, a.Scope));

            Assert.True(PersistableAttributes.IsEligible("hero.level"));
            Assert.True(PersistableAttributes.IsEligible("hero.xp"));
            Assert.True(PersistableAttributes.IsEligible("hero.inventory"));
            Assert.False(PersistableAttributes.IsEligible("hero.bogus"));
        }

        [Fact]
        public void Catalog_MidGameKey_HasIneligibleReason()
        {
            Assert.NotNull(PersistableAttributes.IneligibleReason("hero.current_hp"));
            Assert.NotNull(PersistableAttributes.IneligibleReason("player.ore"));
            Assert.Null(PersistableAttributes.IneligibleReason("hero.level"));   // eligible ⇒ not "ineligible-known"
            Assert.Null(PersistableAttributes.IneligibleReason("hero.bogus"));   // unknown ⇒ not "ineligible-known"
        }

        [Fact]
        public void Catalog_ByScope_RendersOnlyPopulatedScopes()
        {
            Assert.Equal(3, PersistableAttributes.ByScope(AttributeScope.Hero).Length); // level, xp, inventory (Story 3.16)
            Assert.Empty(PersistableAttributes.ByScope(AttributeScope.Unit));    // no backing store yet (D-1)
            Assert.Empty(PersistableAttributes.ByScope(AttributeScope.Player));
        }

        // ── Validator ────────────────────────────────────────────────────────────────

        [Fact]
        public void Validate_NullManifest_IsValid()
        {
            Assert.True(V.Validate(null).Ok);
        }

        [Fact]
        public void Validate_ValidSelection_HasNoErrors()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.xp" } };
            Assert.True(V.Validate(m).Ok);
        }

        [Fact]
        public void Validate_EnabledWithZeroAttributes_IsValid()
        {
            var m = new PersistenceManifest { Enabled = true };
            Assert.True(V.Validate(m).Ok);   // enabled-with-zero ⇒ empty profile shape ⇒ legal (never require ≥1)
        }

        [Fact]
        public void Validate_DisabledButValid_IsValid()
        {
            var m = new PersistenceManifest { Enabled = false, Attributes = { "hero.level" } };
            Assert.True(V.Validate(m).Ok);
        }

        [Fact]
        public void Validate_UnknownAttribute_IsLocatedError()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.bogus" } };
            ManifestValidationResult r = V.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "attributes.hero.bogus" && e.Message.Contains("unknown attribute"));
        }

        [Fact]
        public void Validate_MidGameAttribute_IsLocatedMidGameError()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.current_hp" } };
            ManifestValidationResult r = V.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "attributes.hero.current_hp"
                                        && e.Message.Contains("mid-game-only state cannot be persisted"));
        }

        [Fact]
        public void Validate_DuplicateAttribute_IsLocatedError()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.level" } };
            ManifestValidationResult r = V.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "attributes.hero.level" && e.Message.Contains("selected more than once"));
            // The first (eligible) occurrence is not itself an error — only the repeat is reported.
            Assert.Single(r.Errors);
        }

        [Fact]
        public void Validate_AccumulatesEveryError_NotJustFirst()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.bogus", "hero.current_hp" } };
            ManifestValidationResult r = V.Validate(m);
            Assert.Equal(2, r.Errors.Count);
        }

        // ── DeriveProfileShape ─────────────────────────────────────────────────────

        [Fact]
        public void DeriveProfileShape_TwoKeys_YieldsTwoOrderedSlots()
        {
            // Author them out of catalog order to prove the shape is catalog-ordered (producer-independent).
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.xp", "hero.level" } };
            PlayerProfileShape shape = m.DeriveProfileShape();
            Assert.Equal(2, shape.Slots.Count);
            Assert.Equal("hero.level", shape.Slots[0].Key);   // catalog order, not selection order
            Assert.Equal("hero.xp",    shape.Slots[1].Key);
            Assert.All(shape.Slots, s => Assert.Equal(AttributeScope.Hero, s.Scope));
        }

        [Fact]
        public void DeriveProfileShape_SkipsInvalidKeys()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.bogus", "hero.current_hp" } };
            PlayerProfileShape shape = m.DeriveProfileShape();
            Assert.Single(shape.Slots);
            Assert.Equal("hero.level", shape.Slots[0].Key);
        }

        // ── Clone ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Clone_IsIndependentDeepCopy()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.level" } };
            PersistenceManifest c = m.Clone();

            c.Enabled = false;
            c.Attributes.Add("hero.xp");

            Assert.True(m.Enabled);                 // source unchanged
            Assert.Single(m.Attributes);            // source list not shared
            Assert.False(c.Enabled);
            Assert.Equal(2, c.Attributes.Count);
        }

        // ── ScenarioData round-trip (omit-when-null) ─────────────────────────────────

        [Fact]
        public void Serialize_NullManifest_OmitsKey_AndIsByteIdenticalToPreFieldForm()
        {
            var s = new ScenarioData { Id = "t", MapBounds = 120f };   // no manifest set (null default)
            string withNull = ScenarioSerializer.Serialize(s);
            Assert.DoesNotContain("persistence_manifest", withNull);

            // Explicitly null again ⇒ identical bytes (the field is invisible when null).
            s.PersistenceManifest = null;
            Assert.Equal(withNull, ScenarioSerializer.Serialize(s));
        }

        [Fact]
        public void Serialize_PresentManifest_WritesBlock_AndReparsesIdentically()
        {
            var s = new ScenarioData
            {
                Id = "t", MapBounds = 120f,
                PersistenceManifest = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.xp" } },
            };
            string json = ScenarioSerializer.Serialize(s);
            Assert.Contains("persistence_manifest", json);
            Assert.Contains("\"enabled\": true", json);

            ScenarioData? back = ScenarioSerializerRoundTrip(json);
            Assert.NotNull(back);
            Assert.NotNull(back!.PersistenceManifest);
            Assert.True(back.PersistenceManifest!.Enabled);
            Assert.Equal(new[] { "hero.level", "hero.xp" }, back.PersistenceManifest.Attributes.ToArray());
        }

        [Fact]
        public void Serialize_DisabledManifest_WritesEnabledFalse()
        {
            var s = new ScenarioData
            {
                Id = "t", MapBounds = 120f,
                PersistenceManifest = new PersistenceManifest { Enabled = false, Attributes = { "hero.level" } },
            };
            string json = ScenarioSerializer.Serialize(s);
            Assert.Contains("\"enabled\": false", json);
        }

        // ── ScenarioValidator hook (D3 gate) ─────────────────────────────────────────

        [Fact]
        public void ScenarioValidator_NullManifest_Passes()
        {
            ScenarioData s = LoadShipped();
            Assert.Null(s.PersistenceManifest);
            Assert.True(SV.Validate(s).Ok);
        }

        [Fact]
        public void ScenarioValidator_ValidManifest_Passes()
        {
            ScenarioData s = LoadShipped();
            s.PersistenceManifest = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.xp" } };
            Assert.True(SV.Validate(s).Ok);
        }

        [Fact]
        public void ScenarioValidator_UnknownManifestKey_FailsLocated()
        {
            ScenarioData s = LoadShipped();
            s.PersistenceManifest = new PersistenceManifest { Enabled = true, Attributes = { "hero.bogus" } };
            ValidationResult r = SV.Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("persistence_manifest.attributes.hero.bogus", r.Error);
        }

        [Fact]
        public void ScenarioValidator_MidGameManifestKey_FailsLocated()
        {
            ScenarioData s = LoadShipped();
            s.PersistenceManifest = new PersistenceManifest { Enabled = true, Attributes = { "hero.current_hp" } };
            ValidationResult r = SV.Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("mid-game-only state cannot be persisted", r.Error);
        }

        // ── Shipped-scenario guard ───────────────────────────────────────────────────

        [Fact]
        public void ShippedScenario_ValidatesOk_AndSerializesWithoutManifest()
        {
            ScenarioData s = LoadShipped();
            Assert.True(SV.Validate(s).Ok, SV.Validate(s).Error ?? "");
            string json = ScenarioSerializer.Serialize(s);
            Assert.DoesNotContain("persistence_manifest", json);

            // A load→serialize→load→serialize round-trip is stable (no drift introduced by the new nullable field).
            ScenarioData? back = ScenarioSerializerRoundTrip(json);
            Assert.Equal(json, ScenarioSerializer.Serialize(back!));
        }

        // ── Golden / hash neutrality of a null manifest ──────────────────────────────

        [Fact]
        public void CanonicalModelHash_IgnoresTheManifest()
        {
            ScenarioData s = LoadShipped();
            ulong before = CanonicalModelHash.Compute(s);
            s.PersistenceManifest = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.xp" } };
            Assert.Equal(before, CanonicalModelHash.Compute(s));   // authoring-only ⇒ not folded (D-2)
        }

        [Fact]
        public void StartStateHash_IgnoresTheManifest()
        {
            ScenarioData s = LoadShipped();
            var heroes = new HeroStore();
            ulong before = StartStateHash.Compute(s, heroes);
            s.PersistenceManifest = new PersistenceManifest { Enabled = true, Attributes = { "hero.level" } };
            Assert.Equal(before, StartStateHash.Compute(s, heroes));
        }

        // ── Null-safety of a hand-edited "attributes": null (review patch) ───────────

        [Fact]
        public void Deserialize_NullAttributes_CoercesToEmpty_NoThrow()
        {
            // JSON null overrides the field initializer; the setter must coerce it so unguarded readers never NRE.
            PersistenceManifest? m = System.Text.Json.JsonSerializer.Deserialize<PersistenceManifest>(
                "{\"enabled\":true,\"attributes\":null}");
            Assert.NotNull(m);
            Assert.NotNull(m!.Attributes);
            Assert.Empty(m.Attributes);
            Assert.Empty(m.DeriveProfileShape().Slots);   // would have NRE'd before the coercion
            Assert.True(V.Validate(m).Ok);
        }

        // ── Disabled manifest is inert ⇒ Valid regardless of keys (recovery path, review patch) ──

        [Fact]
        public void Validate_DisabledWithInvalidKey_IsValid()
        {
            // A disabled manifest never persists anything, so an inherited/hand-edited bad key must not block the save —
            // flipping persistence off is the recovery path out of an otherwise un-saveable scenario.
            var m = new PersistenceManifest { Enabled = false, Attributes = { "hero.current_hp", "hero.bogus" } };
            Assert.True(V.Validate(m).Ok);

            ScenarioData s = LoadShipped();
            s.PersistenceManifest = m;
            Assert.True(SV.Validate(s).Ok);   // the D3 gate agrees — a disabled manifest passes

            // Re-enabling re-asserts the gate.
            m.Enabled = true;
            Assert.False(V.Validate(m).Ok);
        }

        // ── Editor state transitions (PersistenceManifestEditing — the panel's extracted logic, review patch) ──

        [Fact]
        public void ApplyMasterToggle_On_CreatesEnabledManifest()
        {
            PersistenceManifest? m = PersistenceManifestEditing.ApplyMasterToggle(null, on: true);
            Assert.NotNull(m);
            Assert.True(m!.Enabled);
            Assert.Empty(m.Attributes);
        }

        [Fact]
        public void ApplyMasterToggle_Off_DisablesButRetainsSelection()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.xp" } };
            PersistenceManifest? r = PersistenceManifestEditing.ApplyMasterToggle(m, on: false);
            Assert.Same(m, r);
            Assert.False(r!.Enabled);
            Assert.Equal(new[] { "hero.level", "hero.xp" }, r.Attributes.ToArray());   // RETAINED

            // Toggling back on keeps the retained selection.
            PersistenceManifest? back = PersistenceManifestEditing.ApplyMasterToggle(r, on: true);
            Assert.True(back!.Enabled);
            Assert.Equal(2, back.Attributes.Count);
        }

        [Fact]
        public void ApplyAttributeToggle_Select_AddsKey_EnablesAndDedups()
        {
            PersistenceManifest m = PersistenceManifestEditing.ApplyAttributeToggle(null, "hero.level", selected: true);
            Assert.True(m.Enabled);   // touching the checklist implies persistence on
            Assert.Equal(new[] { "hero.level" }, m.Attributes.ToArray());

            // Re-selecting the same key does not duplicate it.
            PersistenceManifestEditing.ApplyAttributeToggle(m, "hero.level", selected: true);
            Assert.Single(m.Attributes);
        }

        [Fact]
        public void ApplyAttributeToggle_Deselect_RemovesKey()
        {
            var m = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.xp" } };
            PersistenceManifestEditing.ApplyAttributeToggle(m, "hero.level", selected: false);
            Assert.Equal(new[] { "hero.xp" }, m.Attributes.ToArray());
        }

        // ── Present manifest survives the REAL ScenarioSerializer load path (review patch, B5) ──

        [Fact]
        public void PresentManifest_RoundTripsThroughScenarioSerializerSaveAndLoad()
        {
            ScenarioData s = LoadShipped();
            s.PersistenceManifest = new PersistenceManifest { Enabled = true, Attributes = { "hero.level", "hero.xp" } };

            string tmp = Path.Combine(Path.GetTempPath(), "chimera_manifest_serializer_roundtrip.json");
            try
            {
                ScenarioSerializer.SaveToFile(s, tmp);
                ScenarioData? back = ScenarioSerializer.LoadFromFile(tmp);   // the REAL loader + its _options
                Assert.NotNull(back);
                Assert.NotNull(back!.PersistenceManifest);
                Assert.True(back.PersistenceManifest!.Enabled);
                Assert.Equal(new[] { "hero.level", "hero.xp" }, back.PersistenceManifest.Attributes.ToArray());
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────

        private static ScenarioData LoadShipped()
        {
            string path = Path.Combine(ScenariosDir(), "alpha_map_01.json");
            ScenarioData? s = ScenarioSerializer.LoadFromFile(path);
            Assert.NotNull(s);
            return s!;
        }

        private static ScenarioData? ScenarioSerializerRoundTrip(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<ScenarioData>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(), new FixedJsonConverter() },
                });

        // <repo>/godot/ProjectChimera.Sim.Tests/Definitions/THIS.cs → <repo>/godot/resources/data/scenarios
        private static string ScenariosDir([CallerFilePath] string thisFile = "")
        {
            string godot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
            return Path.Combine(godot, "resources", "data", "scenarios");
        }
    }
}
