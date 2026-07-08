#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.15 — the fail-closed <see cref="ItemDefinitionValidator"/> gate (mirrors AbilityValidatorTests): a valid
    /// stat item and a valid charged consumable PASS (minting a <see cref="Validated{T}"/>); a dangling/oversized effect
    /// graph, negative charges, an out-of-range modifier delta, and an <c>inventory_slot_count</c> outside <c>[1,6]</c> all
    /// FAIL CLOSED with a single located error.
    /// </summary>
    public class ItemDefinitionValidatorTests
    {
        private static ItemValidationResult V(ItemDefinition d) => new ItemDefinitionValidator().Validate(d);

        [Fact]
        public void ValidStatItem_Passes()
        {
            var r = V(new ItemDefinition { Id = "ring", Charges = 0,
                MaxHealthDelta = Fixed.FromInt(50), ArmorDelta = Fixed.FromInt(2) });
            Assert.True(r.Ok, r.Error);
            Assert.Equal("ring", r.Value.Value.Id);
        }

        [Fact]
        public void ValidConsumable_Passes()
        {
            var r = V(new ItemDefinition { Id = "potion", Charges = 3,
                EffectGraph = new HealEffect(Fixed.FromInt(75)) });
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void EmptyId_Fails()
        {
            var r = V(new ItemDefinition { Id = "" });
            Assert.False(r.Ok);
            Assert.Contains("id", r.Error!);
        }

        [Fact]
        public void NegativeCharges_FailsClosed()
        {
            var r = V(new ItemDefinition { Id = "bad", Charges = -1 });
            Assert.False(r.Ok);
            Assert.Contains("charges", r.Error!);
        }

        [Fact]
        public void ConsumableWithoutEffect_FailsClosed_Dangling()
        {
            var r = V(new ItemDefinition { Id = "empty_potion", Charges = 2, EffectGraph = null });
            Assert.False(r.Ok);
            Assert.Contains("effect", r.Error!);
        }

        [Fact]
        public void StatItemWithEffect_FailsClosed_DanglingGraph()
        {
            var r = V(new ItemDefinition { Id = "weird", Charges = 0, EffectGraph = new HealEffect(Fixed.FromInt(10)) });
            Assert.False(r.Ok);
            Assert.Contains("effect", r.Error!);
        }

        [Fact]
        public void OversizedEffectGraph_FailsClosed()
        {
            // A Sequence with MORE than MaxSequenceChildren (8) children → EffectBounds rejects.
            var children = new EffectNode[9];
            for (int i = 0; i < children.Length; i++) children[i] = new HealEffect(Fixed.FromInt(1));
            var r = V(new ItemDefinition { Id = "huge", Charges = 1, EffectGraph = new SequenceEffect(children) });
            Assert.False(r.Ok);
            Assert.Contains("effect", r.Error!);
        }

        [Fact]
        public void OutOfRangeDelta_FailsClosed()
        {
            var r = V(new ItemDefinition { Id = "overflow", Charges = 0,
                MaxHealthDelta = Fixed.FromRaw(int.MaxValue) }); // ~32767.99998 > 32767
            Assert.False(r.Ok);
            Assert.Contains("max_health_delta", r.Error!);
        }

        [Fact]
        public void StatDelta_JustAboveCap_FailsClosed()
        {
            // Story 3.15 (P5): a modifier delta of 1001 exceeds MAX_ITEM_STAT_DELTA (1000) → fail closed, so a full
            // inventory of extreme items can never wrap an Effective* stat negative.
            var r = V(new ItemDefinition { Id = "toobig", Charges = 0, MaxHealthDelta = Fixed.FromInt(1001) });
            Assert.False(r.Ok);
            Assert.Contains("max_health_delta", r.Error!);
            Assert.Contains("MAX_ITEM_STAT_DELTA", r.Error!);
        }

        [Fact]
        public void StatDelta_AtCap_Passes()
        {
            // Exactly 1000 is within the cap → passes (the boundary is inclusive at ±1000).
            var r = V(new ItemDefinition { Id = "atcap", Charges = 0, MaxHealthDelta = Fixed.FromInt(1000) });
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void NegativeStatDelta_JustBelowCap_FailsClosed()
        {
            // The cap is a magnitude bound — a delta of -1001 fails closed the same as +1001.
            var r = V(new ItemDefinition { Id = "toosmall", Charges = 0, ArmorDelta = Fixed.FromInt(-1001) });
            Assert.False(r.Ok);
            Assert.Contains("armor_delta", r.Error!);
            Assert.Contains("MAX_ITEM_STAT_DELTA", r.Error!);
        }

        [Fact]
        public void FailedValidation_MintsNoUsableToken()
        {
            var r = V(new ItemDefinition { Id = "", Charges = 0 });
            Assert.False(r.Ok);
            // The token is default (unusable) on a failed result — no runnable Validated escapes the gate.
            Assert.Null(r.Value.Value);
        }

        // ── inventory_slot_count ∈ [1,6] is a SCENARIO-level field (validated by ScenarioValidator). ──

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(-1)]
        public void InventorySlotCount_OutOfRange_FailsClosed(int count)
        {
            var model = new ScenarioData
            {
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 100f, BaseX = 0f, BaseZ = 0f } },
                InventorySlotCount = count,
            };
            var r = new ScenarioValidator().Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("inventory_slot_count", r.Error!);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(6)]
        public void InventorySlotCount_InRange_Passes(int count)
        {
            var model = new ScenarioData
            {
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 100f, BaseX = 0f, BaseZ = 0f } },
                InventorySlotCount = count,
            };
            var r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
        }
    }
}
