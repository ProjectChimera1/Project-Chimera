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

        // ── Story 2.10 shape teeth-tests: the shipped Equal Exchange + Sanguine Furnace content loads through the
        //    Validated<AbilityDefinition> gate with the EXACT node shape each mechanic's contract requires. A test that
        //    only checked "loads Ok" would pass even if the HP cost were authored as a matrix `damage` leaf or via
        //    cost_ore/cost_crystal — so these assert the armor-independent single-price contract by construction. ──

        [Fact]
        public void SpikeTransmutation_IsEqualExchange_SelfBuffThenFlatArmorIndependentHpCost()
        {
            string path = Path.Combine(AbilitiesResourceDir(), "spike_transmutation.json");
            AbilityValidationResult r = AbilityLoader.LoadFromFile(path);
            Assert.True(r.Ok, r.Error);
            AbilityDefinition def = r.Value.Value;

            // Self-targeted, and the vitality-price path charges NO matter AND no energy — HP is the sole cost
            // (AC1.3 HP-XOR-matter). CostEnergy is asserted too so a stray cost_energy leak cannot slip past a test
            // whose whole point is "HP is the sole price" (the Transmuter runs at max_energy 0, but assert regardless).
            Assert.Equal(AbilityTargeting.Self, def.ParsedTargeting);
            Assert.Equal(0, def.CostOre);
            Assert.Equal(0, def.CostCrystal);
            Assert.Equal(0, def.CostEnergy.Raw);

            // Sequence[ apply_modifier (beneficial self-buff), direct_hp_delta (negative flat cost) ] — exactly 2 children.
            var seq = Assert.IsType<SequenceEffect>(def.EffectGraph);
            Assert.Equal(2, seq.Children.Length);
            var buff = Assert.IsType<ApplyModifierEffect>(seq.Children[0]);
            // BENEFICIAL: a POSITIVE attack-damage delta. A 0 (no-op) or negative (self-debuff) delta would satisfy the
            // shape-only IsType check while contradicting "beneficial self-buff", so pin the sign of the payload.
            Assert.True(buff.Modifier.AttackDamageDelta.Raw > 0,
                $"Spike Transmutation buff must be a beneficial (positive) attack-damage delta; was {buff.Modifier.AttackDamageDelta.Raw} raw.");

            // The cost is the FLAT armor-independent direct_hp_delta leaf (NOT the matrix `damage` leaf) with a negative
            // delta — the AC1.2 contract that two casters of different armor_type pay the identical flat HP price.
            var cost = Assert.IsType<DirectHpDeltaEffect>(seq.Children[1]);
            Assert.True(cost.Delta.Raw < 0, $"Equal Exchange HP cost must be a negative flat delta; was {cost.Delta.Raw} raw.");
        }

        [Fact]
        public void MendMatter_IsEqualExchange_SelfBuffThenFlatHpCost()
        {
            string path = Path.Combine(AbilitiesResourceDir(), "mend_matter.json");
            AbilityValidationResult r = AbilityLoader.LoadFromFile(path);
            Assert.True(r.Ok, r.Error);
            AbilityDefinition def = r.Value.Value;

            // The Acolyte's HP-priced Equal Exchange — the vitality sibling of matter_infusion; never both prices (AC1.3).
            // CostEnergy is asserted 0 too: the Acolyte carries max_energy 20, so a stray cost_energy on this ability
            // WOULD gate/charge energy — a real leak this "HP is the sole price" test must catch, not just ore/crystal.
            Assert.Equal(AbilityTargeting.Self, def.ParsedTargeting);
            Assert.Equal(0, def.CostOre);
            Assert.Equal(0, def.CostCrystal);
            Assert.Equal(0, def.CostEnergy.Raw);

            var seq = Assert.IsType<SequenceEffect>(def.EffectGraph);
            Assert.Equal(2, seq.Children.Length);
            var buff = Assert.IsType<ApplyModifierEffect>(seq.Children[0]);
            // BENEFICIAL: Mend Matter grants ARMOR (not attack), so pin a POSITIVE armor delta — a 0/negative delta
            // would pass the shape-only IsType check while contradicting the claimed beneficial self-buff.
            Assert.True(buff.Modifier.ArmorDelta.Raw > 0,
                $"Mend Matter buff must be a beneficial (positive) armor delta; was {buff.Modifier.ArmorDelta.Raw} raw.");
            var cost = Assert.IsType<DirectHpDeltaEffect>(seq.Children[1]);
            Assert.True(cost.Delta.Raw < 0, $"Mend Matter HP cost must be a negative flat delta; was {cost.Delta.Raw} raw.");
        }

        [Fact]
        public void FurnacePour_IsWhileAlivePersistentHeal()
        {
            string path = Path.Combine(AbilitiesResourceDir(), "furnace_pour.json");
            AbilityValidationResult r = AbilityLoader.LoadFromFile(path);
            Assert.True(r.Ok, r.Error);
            AbilityDefinition def = r.Value.Value;

            // A while_alive Persistent whose period pulse is a Heal, bounded within the 256-pulse EffectCaps window
            // (AC2.6). (The OnUnitDefinitionApplied -> InstallSelfPassive spawn seam itself is a RUNTIME concern
            // exercised by the passive-runtime tests / Story 2.6 — this is a load-and-inspect shape test, no unit spawns.)
            Assert.Equal(PassiveActivation.WhileAlive, def.ParsedActivation);
            var persistent = Assert.IsType<PersistentEffect>(def.EffectGraph);
            var pourHeal = Assert.IsType<HealEffect>(persistent.PeriodEffect);
            Assert.True(persistent.PeriodTicks > 0, "Furnace HoT must pulse on a positive period.");
            Assert.True(persistent.PeriodCount > 0 && persistent.PeriodCount <= 256,
                "period_count must be in (0, 256] — the EffectCaps.MaxPersistentPeriods window (AC2.1/AC2.6).");
            // A PURE period-heal: no install/expire burst — 'pour' is a clean HoT, not a shaped ability.
            Assert.Null(persistent.InitialEffect);
            Assert.Null(persistent.ExpireEffect);

            // AC2.2 — 'pour' (elites) MUST regenerate at a higher rate than 'trickle' (pawns). Both share period_ticks
            // by design, so the tier ordering lives entirely in the heal amount; assert pour > trickle so a balance or
            // merge edit that equalizes/zeroes the pour amount can't ship green (the whole reason furnace_pour exists).
            string tricklePath = Path.Combine(AbilitiesResourceDir(), "furnace_trickle.json");
            AbilityValidationResult tr = AbilityLoader.LoadFromFile(tricklePath);
            Assert.True(tr.Ok, tr.Error);
            var trickleHeal = Assert.IsType<HealEffect>(
                Assert.IsType<PersistentEffect>(tr.Value.Value.EffectGraph).PeriodEffect);
            Assert.True(pourHeal.Amount.Raw > trickleHeal.Amount.Raw,
                $"Sanguine Furnace 'pour' ({pourHeal.Amount.Raw} raw) must heal MORE per pulse than 'trickle' " +
                $"({trickleHeal.Amount.Raw} raw) — AC2.2 tier ordering.");
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
