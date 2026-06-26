#nullable enable
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.3 AC1 — a valid ability JSON deserializes into an <see cref="AbilityDefinition"/> whose <c>effect</c>
    /// compiles DIRECTLY into a 2.1 runtime <see cref="EffectNode"/> graph (Decision #1 = A). Field values are pinned
    /// against INDEPENDENTLY-derived 16.16 raws (no tautological round-trip), and the shipped sample abilities all
    /// load + validate (so Story 2.4 can consume them).
    /// </summary>
    public class AbilityDeserializeTests
    {
        // 16.16 fixed-point: raw == value << 16. Independently derived so a wrong-scale parse is caught.
        private const int Raw50 = 50 * 65536;  // 3_276_800
        private const int Raw25 = 25 * 65536;  // 1_638_400
        private const int Raw40 = 40 * 65536;  // 2_621_440
        private const int Raw6  = 6 * 65536;   //   393_216

        private const string ValidBolt = """
        {
          "id": "test_bolt",
          "display_name": "Test Bolt",
          "targeting": "TargetUnit",
          "cost_energy": 50,
          "cost_ore": 10,
          "cost_crystal": 5,
          "cooldown": 6,
          "effect": {
            "kind": "sequence",
            "children": [
              { "kind": "damage", "amount": 25, "damage_type": "Magic" },
              { "kind": "heal", "amount": 40 }
            ]
          }
        }
        """;

        [Fact]
        public void ValidAbility_Deserializes_WithExpectedScalarFields()
        {
            AbilityValidationResult r = AbilityLoader.Load(ValidBolt, "test_bolt");
            Assert.True(r.Ok, r.Error);
            AbilityDefinition def = r.Value.Value;

            Assert.Equal("test_bolt", def.Id);
            Assert.Equal("Test Bolt", def.DisplayName);
            Assert.Equal(AbilityTargeting.TargetUnit, def.ParsedTargeting);
            Assert.Equal(Raw50, def.CostEnergy.Raw);   // 50 quantized to Fixed at parse (independent raw)
            Assert.Equal(10, def.CostOre);
            Assert.Equal(5, def.CostCrystal);
            Assert.Equal(Raw6, def.Cooldown.Raw);
        }

        [Fact]
        public void ValidAbility_EffectCompiles_ToTheExpectedRuntimeGraph()
        {
            AbilityValidationResult r = AbilityLoader.Load(ValidBolt, "test_bolt");
            Assert.True(r.Ok, r.Error);

            // The compiled root IS a runtime SequenceEffect — the converter built 2.1 types directly (no DTO tree).
            var seq = Assert.IsType<SequenceEffect>(r.Value.Value.EffectGraph);
            Assert.Equal(2, seq.Children.Length);

            var dmg = Assert.IsType<DamageEffect>(seq.Children[0]);
            Assert.Equal(Raw25, dmg.Amount.Raw);
            Assert.Equal(DamageType.Magic, dmg.Type);

            var heal = Assert.IsType<HealEffect>(seq.Children[1]);
            Assert.Equal(Raw40, heal.Amount.Raw);
        }

        [Fact]
        public void OmittedOptionalCosts_DefaultToZero()
        {
            // cost_ore / cost_crystal omitted — Disallow rejects EXTRA fields, never MISSING ones.
            const string json = """
            { "id": "h", "targeting": "Self", "cost_energy": 20, "effect": { "kind": "heal", "amount": 5 } }
            """;
            AbilityValidationResult r = AbilityLoader.Load(json, "h");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0, r.Value.Value.CostOre);
            Assert.Equal(0, r.Value.Value.CostCrystal);
        }

        [Fact]
        public void ShippedSampleAbilityFiles_AllLoadAndValidate()
        {
            string dir = AbilitiesResourceDir();
            Assert.True(Directory.Exists(dir), $"Sample ability dir not found: {dir}");

            string[] files = Directory.GetFiles(dir, "*.json");
            Assert.NotEmpty(files); // the shipped fireball/minor_heal/battle_fury samples

            foreach (string f in files)
            {
                AbilityValidationResult r = AbilityLoader.LoadFromFile(f);
                Assert.True(r.Ok, $"Sample '{Path.GetFileName(f)}' failed to validate: {r.Error}");
                Assert.NotNull(r.Value.Value.EffectGraph);
            }
        }

        // <repo>/godot/ProjectChimera.Sim.Tests/Definitions/THIS.cs → <repo>/godot/resources/data/abilities
        private static string AbilitiesResourceDir([CallerFilePath] string thisFile = "")
        {
            string defsDir  = Path.GetDirectoryName(thisFile)!;
            string testProj = Path.GetDirectoryName(defsDir)!;
            string godot    = Path.GetDirectoryName(testProj)!;
            return Path.Combine(godot, "resources", "data", "abilities");
        }
    }
}
