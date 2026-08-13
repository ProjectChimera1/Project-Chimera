#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;             // Fixed
using ProjectChimera.Core.Definitions; // ContentHash, CanonicalModelHash, ScenarioData, ...
using ProjectChimera.Combat;           // DamageTable
using ProjectChimera.Effects;          // DirectHpDeltaEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 9.16 — ABSOLUTE-value pins (P9). The relative mutation tests prove each field MOVES the hash, but a
    /// UNIFORM drift in the FNV primitives — which are now DUPLICATED (<see cref="CanonicalFold"/> for
    /// <see cref="ContentHash"/>, plus CanonicalModelHash's/MatchAgreementHash's own copies) — would move every value
    /// together and slip past every relative test. These pin the exact <c>ulong</c> a fixed fixture folds to, so a
    /// primitive drift (a changed Offset/Prime, a byte-order flip, a UTF-8 vs length-prefix change) turns RED.
    ///
    /// <para>Update a pin ONLY alongside a deliberate <c>AlgoVersion</c> bump / fold-layout change for that hash — a
    /// bare value move with no such change is exactly the drift these guard.</para>
    /// </summary>
    public class ContentHashAbsolutePinTests
    {
        private static ulong FixedContentHash()
        {
            var factions = new List<FactionDefinition>
            {
                new FactionDefinition
                {
                    Id = "alpha", StartingOre = 200f, StartingCrystal = 0f,
                    Units = new List<UnitDefinition> { new UnitDefinition { Id = "warrior", Hp = 120f, AttackDamage = 10f, CostOre = 50 } },
                    Buildings = new List<BuildingDefinition> { new BuildingDefinition { Id = "barracks", Hp = 800f, ConstructionTime = 30f, SupplyBonus = 0, ProducesCategory = "Melee" } },
                },
            };
            var abilities = new AbilityRegistry(new List<AbilityDefinition>
            {
                new AbilityDefinition { Id = "smite", Targeting = "TargetUnit", Activation = "active", CostEnergy = Fixed.FromInt(10), Cooldown = Fixed.FromInt(5), EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(-25)) },
            });
            var items = new ItemRegistry(new List<ItemDefinition>
            {
                new ItemDefinition { Id = "sword", AttackDamageDelta = Fixed.FromInt(5) },
            });
            return ContentHash.Compute(factions, abilities, items, DamageTable.Default);
        }

        private static ScenarioData FixedModel() => new ScenarioData
        {
            Id = "pin", DisplayName = "pin", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.EliminateAllUnits,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, StartCrystal = 50f, BaseX = -30f, BaseZ = 0f } },
        };

        /// <summary>The exact value <see cref="FixedContentHash"/> folds to on ContentHash AlgoVersion 4. Re-pinned for
        /// Story 15-24c (v3→v4): every attribute-model derived row folds its parsed shape ordinal + threshold; the
        /// fixture declares no attribute model, so only the AlgoVersion mix moved it.
        /// <para>Prior re-pins: v2→v3 Story 15-24a (unit <c>health_regen</c> + the item stat-delta vector);
        /// v1→v2 Story 15-21 (the hero fold); DW-272 (regen_rate).</para></summary>
        private const ulong ExpectedContentHash = 17676645355088884384UL;

        /// <summary>The exact value CanonicalModelHash folds <see cref="FixedModel"/> to (AlgoVersion 17). Re-pinned for
        /// Story 15-24a's 16→17 bump (MixModifier folds the canonical sparse stat-delta vector — the fixture embeds no
        /// apply_modifier, so only the AlgoVersion mix moved this). A behavior-preserving refactor must NOT move it
        /// again without a further deliberate bump. (Prior re-pin: DW-941 building_min_gap, 15→16.)</summary>
        private const ulong ExpectedCanonicalModelHash = 9371821912523611146UL;

        [Fact]
        public void ContentHash_FixedFixture_PinsExactValue()
        {
            Assert.Equal(ExpectedContentHash, FixedContentHash());
        }

        [Fact]
        public void CanonicalModelHash_FixedFixture_PinsExactValue()
        {
            Assert.Equal(ExpectedCanonicalModelHash, CanonicalModelHash.Compute(FixedModel()));
        }
    }
}
