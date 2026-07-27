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

        // ── move_speed_delta cap (DW-42): a far tighter per-stat cap than the ±1000 the other three deltas keep, so a
        //    validated item cannot set a speed that tunnels a hero through pathing (~1000 wu/tick) or freezes it at 0. ──

        [Fact]
        public void MoveSpeedDelta_JustAboveCap_FailsClosed()
        {
            int over = ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA.ToInt() + 1;
            var r = V(new ItemDefinition { Id = "boots", Charges = 0, MoveSpeedDelta = Fixed.FromInt(over) });
            Assert.False(r.Ok);
            Assert.Contains("move_speed_delta", r.Error!);
            Assert.Contains("MAX_MOVE_SPEED_DELTA", r.Error!);
        }

        [Fact]
        public void MoveSpeedDelta_JustBelowNegativeCap_FailsClosed()
        {
            // The cap is a SYMMETRIC magnitude bound; the -1000-scale FREEZE extreme lives on the negative half. Pin it
            // explicitly (review): the positive over-cap test above cannot catch a regression that drops CheckDelta's
            // `delta < -cap` half, which would re-open the freeze the story closed while every other test still passes.
            int under = -(ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA.ToInt() + 1);
            var r = V(new ItemDefinition { Id = "boots", Charges = 0, MoveSpeedDelta = Fixed.FromInt(under) });
            Assert.False(r.Ok);
            Assert.Contains("move_speed_delta", r.Error!);
            Assert.Contains("MAX_MOVE_SPEED_DELTA", r.Error!);
        }

        [Fact]
        public void MoveSpeedDelta_AtCap_Passes()
        {
            // The magnitude bound is inclusive at ±MAX_MOVE_SPEED_DELTA.
            var r = V(new ItemDefinition { Id = "boots", Charges = 0,
                MoveSpeedDelta = ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA });
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void MoveSpeedDelta_AtNegativeCap_Passes()
        {
            // The inclusive boundary holds on the negative side too — a -50 curse/slow item stays authorable BY DESIGN.
            var r = V(new ItemDefinition { Id = "boots", Charges = 0,
                MoveSpeedDelta = -ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA });
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void MoveCap_AppliesOnlyToMoveSpeed_NotOtherDeltas()
        {
            // The tight move-speed cap (50) applies ONLY to move_speed_delta; the other three keep ±1000. This asserts
            // the BEHAVIOR (not a constant relationship): the SAME magnitude 60 — above the move cap, under the item
            // cap — must FAIL on move_speed_delta but PASS on max_health_delta. Fails if the caps were swapped.
            var speed = V(new ItemDefinition { Id = "boots", Charges = 0, MoveSpeedDelta = Fixed.FromInt(60) });
            Assert.False(speed.Ok);
            Assert.Contains("move_speed_delta", speed.Error!);

            var health = V(new ItemDefinition { Id = "vitality", Charges = 0, MaxHealthDelta = Fixed.FromInt(60) });
            Assert.True(health.Ok, health.Error);

            // The SAME mid-band magnitude must also pass on attack_damage_delta and armor_delta — each CheckDelta call in
            // the chain wires its OWN cap, so a copy/paste slip that passed MAX_MOVE_SPEED_DELTA for one of these would
            // wrongly reject a legit 60-point buff. Pin all three non-speed deltas, not just max_health.
            var attack = V(new ItemDefinition { Id = "blade", Charges = 0, AttackDamageDelta = Fixed.FromInt(60) });
            Assert.True(attack.Ok, attack.Error);
            var armor = V(new ItemDefinition { Id = "plate", Charges = 0, ArmorDelta = Fixed.FromInt(60) });
            Assert.True(armor.Ok, armor.Error);

            // And a hundreds-scale non-speed delta (200) — far above the move cap — still passes on max_health.
            var big = V(new ItemDefinition { Id = "vitality2", Charges = 0, MaxHealthDelta = Fixed.FromInt(200) });
            Assert.True(big.Ok, big.Error);
        }

        [Fact]
        public void HybridBuffConsumable_Passes()
        {
            // DW-38: charges > 0 + a stat delta + an effect graph is a valid WC3-style hybrid buff-consumable — no XOR.
            var r = V(new ItemDefinition { Id = "elixir", Charges = 2,
                MaxHealthDelta = Fixed.FromInt(50), EffectGraph = new HealEffect(Fixed.FromInt(75)) });
            Assert.True(r.Ok, r.Error);
            Assert.Equal("elixir", r.Value.Value.Id);
        }

        [Fact]
        public void TraversalId_FailsClosed_SimGate()
        {
            // DW-47: a path-traversal id must be rejected before it can reach Persist()'s Path.Combine/File.Move.
            var r = V(new ItemDefinition { Id = "../../foo", Charges = 0 });
            Assert.False(r.Ok);
            Assert.Contains("id", r.Error!);
        }

        // ── Shop costs (Story 3.16 review): the SIM gate must reject a negative/out-of-range cost, not just the editor
        //    ValidateFields — a negative cost ADDS resource on buy (SpendOre(faction, -cost) refunds), an infinite-resource
        //    exploit. The sim Validate is the sole Validated<> minter, so this is where it must fail closed. ──

        [Fact]
        public void NegativeCostOre_FailsClosed_SimGate()
        {
            var r = V(new ItemDefinition { Id = "free_money", Charges = 0,
                MaxHealthDelta = Fixed.FromInt(10), CostOre = Fixed.FromInt(-100) });
            Assert.False(r.Ok);
            Assert.Contains("cost_ore", r.Error!);
        }

        [Fact]
        public void NegativeCostCrystal_FailsClosed_SimGate()
        {
            var r = V(new ItemDefinition { Id = "free_crystal", Charges = 0,
                MaxHealthDelta = Fixed.FromInt(10), CostCrystal = Fixed.FromInt(-1) });
            Assert.False(r.Ok);
            Assert.Contains("cost_crystal", r.Error!);
        }

        [Fact]
        public void NonNegativeCosts_Pass_SimGate()
        {
            var r = V(new ItemDefinition { Id = "priced", Charges = 0,
                MaxHealthDelta = Fixed.FromInt(10), CostOre = Fixed.FromInt(150), CostCrystal = Fixed.FromInt(25) });
            Assert.True(r.Ok, r.Error);
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
