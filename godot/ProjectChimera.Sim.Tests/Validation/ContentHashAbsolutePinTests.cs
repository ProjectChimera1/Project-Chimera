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

        /// <summary>The exact value <see cref="FixedContentHash"/> folds to on ContentHash AlgoVersion 1. Re-pinned for
        /// DW-272 / Story 15.12: FoldUnitCommon now folds the new UnitDefinition.RegenRate stat (default 0 ⇒ a MixInt(0)
        /// after MaxEnergy), a deliberate fold-layout change that moves every unit/building's content hash.</summary>
        private const ulong ExpectedContentHash = 0x74227B59C03CEDE9UL;

        /// <summary>The exact value CanonicalModelHash folds <see cref="FixedModel"/> to (AlgoVersion 16). Re-pinned for
        /// DW-941's AlgoVersion 15→16 bump (the new building_min_gap fold — the fixture authors none, so it folds the
        /// resolved 1.0u default; the AlgoVersion mix + the new default fold moved this). A behavior-preserving
        /// refactor must NOT move it again without a further deliberate bump.</summary>
        private const ulong ExpectedCanonicalModelHash = 8063299176883624929UL;

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
