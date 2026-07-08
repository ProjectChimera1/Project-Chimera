#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.4 AC4 (D-1) — the persistence teeth. Targets the pure <see cref="FactionWriter.PatchFactionJson"/>
    /// transform directly with an in-code faction JSON string (the Tier-1 harness can't resolve <c>res://</c> or touch
    /// Godot), proving the JSON-DOM patch is surgical: only the edited unit's changed property differs; every other
    /// unit, every building, and every faction-level key (incl. an unknown <c>signature_mechanic</c>-style key and a
    /// <c>combat_feedback</c> sub-object) is preserved; no computed <c>Parsed*</c> getter and no default-ballooned field
    /// is emitted; and the result still parses on the lenient loader.
    /// </summary>
    public class FactionWriteRoundTripTests
    {
        // A faction with: an unknown faction-level key (signature_mechanic + deferred_mechanics), a unit carrying a
        // combat_feedback sub-object, decimal tokens (5.0 / 0.95), multiple units, and a building. 2-space indented so
        // WriteIndented reproduces untouched nodes byte-for-byte.
        private const string Faction = """
        {
          "id": "alpha",
          "display_name": "Rebel Alchemists",
          "color": [0.85, 0.35, 0.95, 1.0],
          "signature_mechanic": "transmutation",
          "deferred_mechanics": ["philosophers_stone"],
          "units": [
            {
              "id": "grunt",
              "display_name": "Grunt",
              "category": "Melee",
              "hp": 100,
              "speed": 4.0,
              "attack_damage": 10,
              "cost_ore": 50,
              "combat_feedback": {
                "impact_sound_id": "clang",
                "hit_freeze_frames": 3
              }
            },
            {
              "id": "archer",
              "display_name": "Archer",
              "category": "Ranged",
              "hp": 70,
              "speed": 5.0,
              "attack_range": 6.0,
              "attack_speed": 0.95,
              "cost_ore": 80,
              "cost_crystal": 10
            },
            {
              "id": "savant",
              "display_name": "Circle Savant",
              "category": "Ranged",
              "hp": 320,
              "abilities": ["fireball"],
              "tags": ["Magical"],
              "is_hero": true
            }
          ],
          "buildings": [
            { "id": "barracks", "display_name": "Barracks", "category": "Structure", "hp": 800 }
          ]
        }
        """;

        // Parse a faction string and return one unit object's normalized JSON (apples-to-apples: both sides come from a
        // fresh parse, so untouched JsonElement-backed tokens serialize identically regardless of whole-file whitespace).
        private static string UnitJson(string factionJson, string unitId)
        {
            JsonNode root = JsonNode.Parse(factionJson)!;
            foreach (JsonNode? n in root["units"]!.AsArray())
                if (n is JsonObject o && (string?)o["id"] == unitId) return o.ToJsonString();
            return $"<no unit {unitId}>";
        }

        private static float UnitFloat(string factionJson, string unitId, string key)
        {
            JsonNode root = JsonNode.Parse(factionJson)!;
            foreach (JsonNode? n in root["units"]!.AsArray())
                if (n is JsonObject o && (string?)o["id"] == unitId) return o[key]!.GetValue<float>();
            return float.NaN;
        }

        private static FactionDefinition Parse(string json) =>
            JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions)!;

        // ── Update: only the edited unit's changed field differs ──

        [Fact]
        public void Update_ChangesOnlyTheEditedField_PreservesEverythingElse()
        {
            UnitDefinition edited = Parse(Faction).GetUnit("grunt")!;
            edited.Hp = 250f;   // the one change

            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = edited });

            // The edited field moved…
            Assert.Equal(250f, UnitFloat(outJson, "grunt", "hp"));
            // …and every OTHER unit + the building are byte-identical (normalized apples-to-apples).
            Assert.Equal(UnitJson(Faction, "archer"), UnitJson(outJson, "archer"));
            Assert.Equal(UnitJson(Faction, "savant"), UnitJson(outJson, "savant"));

            // Faction-level unknown keys survive (no whole-object re-serialize would have dropped these).
            Assert.Contains("signature_mechanic", outJson);
            Assert.Contains("transmutation", outJson);
            Assert.Contains("deferred_mechanics", outJson);
            Assert.Contains("philosophers_stone", outJson);
            Assert.Contains("barracks", outJson);
        }

        [Fact]
        public void Update_PreservesUntouchedCombatFeedbackSubObject()
        {
            UnitDefinition edited = Parse(Faction).GetUnit("grunt")!;
            edited.AttackDamage = 15f;   // touch a DIFFERENT field; combat_feedback must be preserved verbatim

            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = edited });

            Assert.Contains("combat_feedback", outJson);
            Assert.Contains("impact_sound_id", outJson);
            Assert.Contains("clang", outJson);
            Assert.Contains("hit_freeze_frames", outJson);
        }

        [Fact]
        public void Update_PreservesUntouchedDecimalTokens_ByteForByte()
        {
            // archer.speed is authored "5.0"; editing grunt must not normalize archer's token to "5".
            UnitDefinition edited = Parse(Faction).GetUnit("grunt")!;
            edited.Hp = 111f;
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = edited });
            Assert.Contains("5.0", outJson);
            Assert.Contains("0.95", outJson);
        }

        [Fact]
        public void Update_DoesNotEmitParsedGetters_NorBalloonDefaults()
        {
            UnitDefinition edited = Parse(Faction).GetUnit("grunt")!;
            edited.Hp = 200f;
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = edited });

            // The eight corruption modes a reflection re-serialize would introduce — none may appear.
            Assert.DoesNotContain("Parsed", outJson);
            Assert.DoesNotContain("AbilityIndices", outJson);
            Assert.DoesNotContain("ParsedCategory", outJson);
            // grunt authored no splash_radius/collision_radius/max_energy → they must NOT balloon in.
            Assert.DoesNotContain("splash_radius", UnitJson(outJson, "grunt"));
            Assert.DoesNotContain("collision_radius", UnitJson(outJson, "grunt"));
            Assert.DoesNotContain("max_energy", UnitJson(outJson, "grunt"));
        }

        [Fact]
        public void Update_ReParsesOnLenientLoader()
        {
            UnitDefinition edited = Parse(Faction).GetUnit("grunt")!;
            edited.Hp = 300f;
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = edited });

            FactionDefinition reloaded = Parse(outJson);
            Assert.Equal(3, reloaded.Units.Count);
            Assert.Single(reloaded.Buildings);
            Assert.Equal(300f, reloaded.GetUnit("grunt")!.Hp);
            Assert.Equal("Circle Savant", reloaded.GetUnit("savant")!.DisplayName);
        }

        [Fact]
        public void Update_ChangedMeshPath_And_ClearedMeshPath()
        {
            // Set a mesh on grunt (was absent), then clear it back to none.
            UnitDefinition withMesh = Parse(Faction).GetUnit("grunt")!;
            withMesh.MeshPath = "res://assets/models/x.glb";
            string a = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = withMesh });
            Assert.Contains("res://assets/models/x.glb", a);

            UnitDefinition cleared = Parse(a).GetUnit("grunt")!;
            cleared.MeshPath = "";   // creator cleared it
            string b = FactionWriter.PatchFactionJson(a,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = cleared });
            Assert.DoesNotContain("mesh_path", UnitJson(b, "grunt"));
        }

        // ── delivery + projectile_speed (Story 3.12) ──

        [Fact]
        public void Update_AuthorsDelivery_AndCustomProjectileSpeed()
        {
            UnitDefinition edited = Parse(Faction).GetUnit("archer")!;
            edited.Delivery = "Hitscan";        // an authored long-range hitscan sniper
            edited.ProjectileSpeed = 6f;        // (unused by a hitscan unit, but round-trips)
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archer", Def = edited });

            UnitDefinition reloaded = Parse(outJson).GetUnit("archer")!;
            Assert.Equal("Hitscan", reloaded.Delivery);
            Assert.Equal(6f, reloaded.ProjectileSpeed);
        }

        [Fact]
        public void Update_OmittedDelivery_AndDefaultProjectileSpeed_AreNotWritten()
        {
            // A legacy ranged unit (archer) keeps delivery omitted and projectile_speed at its 18 default →
            // FactionWriter must NOT balloon either key in (existing data round-trips byte-identically).
            UnitDefinition edited = Parse(Faction).GetUnit("archer")!;
            edited.AttackDamage = 12f;   // touch a different field
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archer", Def = edited });

            string node = UnitJson(outJson, "archer");
            Assert.DoesNotContain("delivery", node);            // null default → omitted
            Assert.DoesNotContain("projectile_speed", node);    // 18 default → omitted
        }

        [Fact]
        public void Update_ClearingDelivery_DropsTheKey()
        {
            // Author delivery, then clear it back to null (legacy inference) → the key is dropped.
            UnitDefinition withDelivery = Parse(Faction).GetUnit("archer")!;
            withDelivery.Delivery = "Projectile";
            string a = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archer", Def = withDelivery });
            Assert.Contains("delivery", UnitJson(a, "archer"));

            UnitDefinition cleared = Parse(a).GetUnit("archer")!;
            cleared.Delivery = null;
            string b = FactionWriter.PatchFactionJson(a,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archer", Def = cleared });
            Assert.DoesNotContain("delivery", UnitJson(b, "archer"));
        }

        // ── xp_bounty + hero runtime fields (Story 3.13) ──

        [Fact]
        public void Update_AuthorsXpBounty_RoundTrips()
        {
            UnitDefinition edited = Parse(Faction).GetUnit("archer")!;
            edited.XpBounty = 175;
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archer", Def = edited });

            Assert.Contains("xp_bounty", UnitJson(outJson, "archer"));
            UnitDefinition reloaded = Parse(outJson).GetUnit("archer")!;
            Assert.Equal(175, reloaded.XpBounty);
        }

        [Fact]
        public void Update_OmittedXpBounty_IsNotWritten()
        {
            // xp_bounty omitted (null → derived from cost) must NOT balloon the key.
            UnitDefinition edited = Parse(Faction).GetUnit("archer")!;
            edited.AttackDamage = 12f;   // touch a different field
            edited.XpBounty = null;
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archer", Def = edited });

            Assert.DoesNotContain("xp_bounty", UnitJson(outJson, "archer"));
        }

        [Fact]
        public void Update_AuthorsHeroRuntimeFields_RoundTrip()
        {
            UnitDefinition edited = Parse(Faction).GetUnit("archer")!;
            edited.IsHero = true;
            edited.Hero = new HeroDefinition
            {
                XpShareRadius = 20f, HealthPerLevel = 15f, DamagePerLevel = 3f, ArmorPerLevel = 2f,
            };
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "archer", Def = edited });

            UnitDefinition reloaded = Parse(outJson).GetUnit("archer")!;
            Assert.NotNull(reloaded.Hero);
            Assert.Equal(20f, reloaded.Hero!.XpShareRadius);
            Assert.Equal(15f, reloaded.Hero!.HealthPerLevel);
            Assert.Equal(3f,  reloaded.Hero!.DamagePerLevel);
            Assert.Equal(2f,  reloaded.Hero!.ArmorPerLevel);
        }

        // ── Create: appends a minimal new unit (only non-default fields) ──

        [Fact]
        public void Create_AppendsMinimalUnit()
        {
            var fresh = new UnitDefinition { Id = "new_unit", Category = "Melee" };   // all else default
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Create, Def = fresh });

            FactionDefinition reloaded = Parse(outJson);
            Assert.Equal(4, reloaded.Units.Count);
            Assert.NotNull(reloaded.GetUnit("new_unit"));
            // Minimal: default category (Melee) + default hp (100) are omitted; only the non-default id is written.
            string node = UnitJson(outJson, "new_unit");
            Assert.Contains("new_unit", node);
            Assert.DoesNotContain("category", node);   // Melee is the default → omitted
            Assert.DoesNotContain("hp", node);          // 100 is the default → omitted
        }

        [Fact]
        public void Create_WritesNonDefaultFields()
        {
            var fresh = new UnitDefinition { Id = "sniper", Category = "Ranged", Hp = 60f, CostCrystal = 25 };
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Create, Def = fresh });
            UnitDefinition u = Parse(outJson).GetUnit("sniper")!;
            Assert.Equal("Ranged", u.Category);
            Assert.Equal(60f, u.Hp);
            Assert.Equal(25, u.CostCrystal);
        }

        // ── Duplicate: verbatim clone with a new id ──

        [Fact]
        public void Duplicate_ClonesTargetVerbatim_WithNewId()
        {
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Duplicate, TargetId = "savant", NewId = "savant_copy" });

            FactionDefinition reloaded = Parse(outJson);
            Assert.Equal(4, reloaded.Units.Count);
            UnitDefinition copy = reloaded.GetUnit("savant_copy")!;
            // The clone carries every field of the source (abilities, tags, is_hero, hp) — only the id differs.
            Assert.Equal("savant_copy", copy.Id);
            Assert.Equal(320f, copy.Hp);
            Assert.True(copy.IsHero);
            Assert.Contains("fireball", copy.Abilities);
            Assert.Equal(new[] { "Magical" }, copy.Tags);
        }

        // ── Delete: removes only the target ──

        [Fact]
        public void Delete_RemovesOnlyTheTarget()
        {
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Delete, TargetId = "archer" });

            FactionDefinition reloaded = Parse(outJson);
            Assert.Equal(2, reloaded.Units.Count);
            Assert.Null(reloaded.GetUnit("archer"));
            Assert.NotNull(reloaded.GetUnit("grunt"));
            Assert.NotNull(reloaded.GetUnit("savant"));
        }

        // ── Raw-JSON hatch: the target object is replaced wholesale (D-5) ──

        [Fact]
        public void Update_RawJson_ReplacesTargetObjectWholesale()
        {
            string raw = """{ "id": "grunt", "display_name": "Grunt Prime", "category": "Melee", "hp": 175, "combat_feedback": { "impact_sound_id": "boom" } }""";
            string outJson = FactionWriter.PatchFactionJson(Faction,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", RawUnitJson = raw });

            UnitDefinition u = Parse(outJson).GetUnit("grunt")!;
            Assert.Equal("Grunt Prime", u.DisplayName);
            Assert.Equal(175f, u.Hp);
            Assert.Contains("boom", outJson);
            // Other units untouched.
            Assert.Equal(UnitJson(Faction, "archer"), UnitJson(outJson, "archer"));
        }

        // ── Malformed input / missing target fail closed ──

        [Fact]
        public void MissingTarget_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(() =>
                FactionWriter.PatchFactionJson(Faction,
                    new UnitEdit { Kind = UnitEditKind.Delete, TargetId = "ghost" }));
        }

        // ── SyncFactionUnits: the whole-list Save path (in-memory model → file) ──

        [Fact]
        public void Sync_EditAddDelete_InOnePass_PreservesUntouched()
        {
            FactionDefinition f = Parse(Faction);
            f.GetUnit("grunt")!.Hp = 260f;                                   // edit
            f.Units.RemoveAll(u => u.Id == "archer");                        // delete
            f.Units.Add(new UnitDefinition { Id = "sniper", Category = "Ranged", Hp = 55f }); // add

            string outJson = FactionWriter.SyncFactionUnits(Faction, f.Units);
            FactionDefinition r = Parse(outJson);

            Assert.Equal(260f, r.GetUnit("grunt")!.Hp);
            Assert.Null(r.GetUnit("archer"));
            Assert.Equal("Ranged", r.GetUnit("sniper")!.Category);
            // savant (untouched) byte-identical, faction keys + building preserved.
            Assert.Equal(UnitJson(Faction, "savant"), UnitJson(outJson, "savant"));
            Assert.Contains("signature_mechanic", outJson);
            Assert.Contains("barracks", outJson);
            Assert.DoesNotContain("Parsed", outJson);
        }

        [Fact]
        public void Sync_NoChanges_IsByteStablePerUnit()
        {
            FactionDefinition f = Parse(Faction);
            string outJson = FactionWriter.SyncFactionUnits(Faction, f.Units);
            // Every unit re-emits identically when nothing changed (reconcile writes nothing).
            foreach (string id in new[] { "grunt", "archer", "savant" })
                Assert.Equal(UnitJson(Faction, id), UnitJson(outJson, id));
        }

        [Fact]
        public void Sync_PreservesUntouchedCombatFeedback_WhenEditingAnotherField()
        {
            FactionDefinition f = Parse(Faction);
            f.GetUnit("grunt")!.Speed = 3.0f;   // touch a non-combat_feedback field
            string outJson = FactionWriter.SyncFactionUnits(Faction, f.Units);
            // grunt's combat_feedback object stays verbatim (the POCO round-trips to the same normalized form → skipped),
            // including its unknown "impact_sound_id" sub-key.
            Assert.Contains("combat_feedback", UnitJson(outJson, "grunt"));
            Assert.Contains("\"clang\"", outJson);
            Assert.Contains("impact_sound_id", outJson);
        }

        [Fact]
        public void Sync_WritesEditedCombatFeedback_FromRawHatch()
        {
            FactionDefinition f = Parse(Faction);
            // Simulate a raw-hatch edit that changed combat_feedback on the POCO.
            f.GetUnit("grunt")!.CombatFeedback!.ImpactSoundId = "thud";
            string outJson = FactionWriter.SyncFactionUnits(Faction, f.Units);
            Assert.Contains("thud", outJson);
            Assert.DoesNotContain("clang", outJson);
        }
    }
}
