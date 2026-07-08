#nullable enable
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // ItemWriter, ItemLoader, ItemDefinition, ItemValidationResult
using ProjectChimera.Effects;           // HealEffect (the consumable graph)
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.16 (review) — the Godot-free item serializer (<see cref="ItemWriter"/>) round-trips through the fail-closed
    /// reader (<see cref="ItemLoader.Load"/> over <c>ContentJson.Options</c>: <c>UnmappedMemberHandling.Disallow</c> +
    /// Fixed/EffectNode/enum converters), exactly as the item editor's save self-check does. Mirrors
    /// <see cref="AbilityRoundTripTests"/>. Two shapes — a stat item (no graph, with costs) and a charged consumable (with an
    /// EffectGraph) — survive with <c>Charges</c>, the <see cref="Fixed"/> deltas, <c>CostOre</c>/<c>CostCrystal</c>, and the
    /// EffectGraph presence intact.
    /// </summary>
    public class ItemWriterRoundTripTests
    {
        [Fact]
        public void StatItem_WithCosts_SurvivesWriterRoundTrip()
        {
            var def = new ItemDefinition
            {
                Id                = "ring_of_vigor",
                DisplayName       = "Ring of Vigor",
                Charges           = 0,                     // stat item → NO effect graph
                MaxHealthDelta    = Fixed.FromInt(50),
                AttackDamageDelta = Fixed.FromInt(5),
                MoveSpeedDelta    = Fixed.FromInt(1),
                ArmorDelta        = Fixed.FromInt(2),
                CostOre           = Fixed.FromInt(150),
                CostCrystal       = Fixed.FromInt(25),
            };

            ItemValidationResult r = ItemLoader.Load(ItemWriter.Serialize(def), def.Id);
            Assert.True(r.Ok, r.Error);
            ItemDefinition rt = r.Value.Value;

            Assert.Equal(0, rt.Charges);
            Assert.Equal(def.MaxHealthDelta.Raw,    rt.MaxHealthDelta.Raw);
            Assert.Equal(def.AttackDamageDelta.Raw, rt.AttackDamageDelta.Raw);
            Assert.Equal(def.MoveSpeedDelta.Raw,    rt.MoveSpeedDelta.Raw);
            Assert.Equal(def.ArmorDelta.Raw,        rt.ArmorDelta.Raw);
            Assert.Equal(def.CostOre.Raw,           rt.CostOre.Raw);
            Assert.Equal(def.CostCrystal.Raw,       rt.CostCrystal.Raw);
            Assert.Null(rt.EffectGraph);              // a stat item must not carry a graph
        }

        [Fact]
        public void ChargedConsumable_WithEffectGraph_SurvivesWriterRoundTrip()
        {
            var def = new ItemDefinition
            {
                Id          = "healing_potion",
                DisplayName = "Healing Potion",
                Charges     = 3,                          // consumable → MUST carry a graph
                CostOre     = Fixed.FromInt(75),
                EffectGraph = new HealEffect(Fixed.FromInt(120)),
            };

            ItemValidationResult r = ItemLoader.Load(ItemWriter.Serialize(def), def.Id);
            Assert.True(r.Ok, r.Error);
            ItemDefinition rt = r.Value.Value;

            Assert.Equal(3, rt.Charges);
            Assert.Equal(def.CostOre.Raw, rt.CostOre.Raw);
            Assert.Equal(Fixed.Zero.Raw,  rt.CostCrystal.Raw); // default omitted on write, restored to 0 on read
            Assert.NotNull(rt.EffectGraph);                    // the authored consumable graph survives
        }
    }
}
