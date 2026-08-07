#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;              // Faction, Fixed, UnitCommand
using ProjectChimera.Core.Definitions;  // AbilityDefinition, TargetAffinity, ContentHash, AbilityRegistry, ItemRegistry, DamageTable
using ProjectChimera.Combat;            // DamageTable
using ProjectChimera.Multiplayer;       // UnitOrder, TickCommandPacket, MergedTickPacket
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 15.11 (DW-280 / DW-286) — the widened 12-byte <see cref="UnitOrder"/> wire (ability slot in its own
    /// byte 11, freeing TargetX/TargetZ for a ground point), the <see cref="TargetAffinity"/> parse mapping, and the
    /// Block-If proof that the new optional <c>target_affinity</c> field folds into NO content hash (absent ==
    /// present-with-any-value → every shipped ability's ContentHash is unchanged). All Godot-free / Tier-1.
    /// </summary>
    public class Story1511WireAndAffinityTests
    {
        // ── 12-byte UnitOrder round-trip (all commands, incl. GroundPoint) ──────────

        [Fact]
        public void UnitOrder_Size_IsTwelve() => Assert.Equal(12, UnitOrder.SIZE);

        public static IEnumerable<object[]> OrderCases()
        {
            // unitId, command, targetXraw, targetZraw, slot
            yield return new object[] { 5,   (byte)UnitCommand.Move,        1234, -5678, (byte)0 };
            yield return new object[] { 42,  (byte)UnitCommand.AttackMove,  0,    0,     (byte)0 };
            yield return new object[] { 7,   (byte)UnitCommand.AttackTarget, 99,  0,     (byte)0 };
            // A TargetUnit cast: slot in byte 11, TargetX = 0, TargetZ = target id.
            yield return new object[] { 3,   (byte)UnitCommand.CastAbility, 0,    17,    (byte)2 };
            // A GroundPoint cast: slot in byte 11, TargetX/TargetZ = the two Fixed ground coords (raw).
            yield return new object[] { 9,   (byte)UnitCommand.CastAbility, Fixed.FromInt(12).Raw, Fixed.FromInt(-8).Raw, (byte)1 };
        }

        [Theory]
        [MemberData(nameof(OrderCases))]
        public void TickCommandPacket_RoundTrips_TwelveByteOrder(int unitId, byte cmd, int tx, int tz, byte slot)
        {
            var orders = new[] { new UnitOrder(unitId, (UnitCommand)cmd, Fixed.FromRaw(tx), Fixed.FromRaw(tz), slot) };
            var buf = new byte[TickCommandPacket.HEADER_BYTES + UnitOrder.SIZE];
            int len = TickCommandPacket.Write(buf, tick: 4, Faction.Player1, orders, 1);
            Assert.Equal(TickCommandPacket.HEADER_BYTES + UnitOrder.SIZE, len);

            var outOrders = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            Assert.True(TickCommandPacket.TryRead(buf, len, out uint tick, out Faction f, outOrders, out int count));
            Assert.Equal(4u, tick);
            Assert.Equal(Faction.Player1, f);
            Assert.Equal(1, count);

            UnitOrder o = outOrders[0];
            Assert.Equal((ushort)unitId, o.UnitId);
            Assert.Equal((UnitCommand)cmd, o.Command);
            Assert.Equal(tx, o.TargetX);
            Assert.Equal(tz, o.TargetZ);
            Assert.Equal(slot, o.Slot); // the byte-11 ability slot survives the round-trip
        }

        [Theory]
        [MemberData(nameof(OrderCases))]
        public void MergedTickPacket_RoundTrips_TwelveByteOrder(int unitId, byte cmd, int tx, int tz, byte slot)
        {
            // The server-merged codec (also the replay body) must carry the slot byte identically to TickCommandPacket.
            var flat = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            flat[0] = new UnitOrder(unitId, (UnitCommand)cmd, Fixed.FromRaw(tx), Fixed.FromRaw(tz), slot);
            var factions = new[] { Faction.Player1 };
            var counts   = new[] { 1 };
            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            int len = MergedTickPacket.Write(buf, tick: 9, factions, counts, flat, subBundleCount: 1);

            var outFactions = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var outCounts   = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var outFlat     = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            Assert.True(MergedTickPacket.TryRead(buf, len, out uint tick, outFactions, outCounts, outFlat, out int n));
            Assert.Equal(9u, tick);
            Assert.Equal(1, n);

            UnitOrder o = outFlat[0];
            Assert.Equal((ushort)unitId, o.UnitId);
            Assert.Equal((UnitCommand)cmd, o.Command);
            Assert.Equal(tx, o.TargetX);
            Assert.Equal(tz, o.TargetZ);
            Assert.Equal(slot, o.Slot);
        }

        // ── ParsedTargetAffinity mapping (DW-286) ───────────────────────────────────

        [Theory]
        [InlineData(null,      null)]                 // absent → null (the enemy-only default; a valid, non-reject state)
        [InlineData("Enemy",   TargetAffinity.Enemy)]
        [InlineData("Ally",    TargetAffinity.Ally)]
        [InlineData("Any",     TargetAffinity.Any)]
        [InlineData("bogus",   null)]                 // unknown non-null string → null (AbilityValidator rejects it)
        public void ParsedTargetAffinity_MapsByExactName(string? raw, TargetAffinity? expected)
        {
            var def = new AbilityDefinition { Id = "x", Targeting = "TargetUnit", TargetAffinity = raw };
            Assert.Equal(expected, def.ParsedTargetAffinity);
        }

        [Fact]
        public void UnparseableAffinity_IsRejected_ByValidator()
        {
            var def = new AbilityDefinition
            {
                Id = "bad_aff", Targeting = "TargetUnit", TargetAffinity = "Frenemy",
                EffectGraph = new ProjectChimera.Effects.HealEffect(Fixed.FromInt(10)),
            };
            AbilityValidationResult r = new AbilityValidator().Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("target_affinity", r.Error);
        }

        [Fact]
        public void SlotBitPack_HasRoomForEveryAbilitySlot()
        {
            // Story 15.11 (adversarial): the queued-cast slot is packed into the byte's high bits at
            // ORDER_QUEUE_SLOT_SHIFT. If MAX_ABILITIES_PER_UNIT ever grows past what those bits hold, `slot << SHIFT`
            // overflows the command byte and silently corrupts queued casts (an MP desync with no error). Pin the
            // invariant so a future cap bump fails HERE, not in a checksum divergence.
            int slotBits = 8 - OrderApplier.ORDER_QUEUE_SLOT_SHIFT;
            int maxSlot = EntityWorld.MAX_ABILITIES_PER_UNIT - 1;
            Assert.True(maxSlot < (1 << slotBits),
                $"MAX_ABILITIES_PER_UNIT ({EntityWorld.MAX_ABILITIES_PER_UNIT}) exceeds the {slotBits}-bit queued-slot " +
                "field; raise ORDER_QUEUE_SLOT_SHIFT (and re-check the UnitCommand ≤ 0x3F budget) before growing the cap.");
        }

        [Fact]
        public void CommandBudget_LeavesRoomForTheQueuedSlotBits()
        {
            // Story 15.11 (review pass 3): the OTHER half of the queued-cast bit-pack invariant. The slot is OR'd into
            // the command byte's high bits (>= ORDER_QUEUE_SLOT_SHIFT), so every UnitCommand value MUST fit in the low
            // ORDER_QUEUE_CMD_MASK bits — otherwise a command's own bits collide with the packed slot and a queued
            // order pops as (wrong command, phantom slot): an MP desync with no error, since the only runtime guard is
            // a Release-stripped Debug.Assert in AppendOrder. SlotBitPack (above) pins the slot side and explicitly
            // punts this one ("re-check the UnitCommand ≤ 0x3F budget"); pin it here so a future command past 0x3F
            // fails at THIS test, not in a checksum divergence. Currently max = CancelTrain (23), well under 63.
            foreach (UnitCommand cmd in System.Enum.GetValues(typeof(UnitCommand)))
            {
                Assert.True((byte)cmd <= OrderApplier.ORDER_QUEUE_CMD_MASK,
                    $"UnitCommand.{cmd} = {(byte)cmd} exceeds ORDER_QUEUE_CMD_MASK (0x{OrderApplier.ORDER_QUEUE_CMD_MASK:X}); " +
                    "its high bit(s) collide with the queued-cast slot pack. Raise ORDER_QUEUE_SLOT_SHIFT before adding " +
                    "commands past 0x3F, or the queued command byte silently corrupts on pop (MP desync).");
            }
        }

        // ── review P8/P3: the two new non-fatal validator warnings actually FIRE (Ok stays true) ─────

        [Fact]
        public void AffinityOnNonTargetUnit_Warns_ButDoesNotReject()
        {
            // target_affinity on a non-TargetUnit ability is meaningless (the click-picker never runs), so the validator
            // WARNS rather than rejecting. Verifies the warn branch fires — the shipped-content "zero warnings" test
            // proves clean content is clean, so it cannot exercise this generation path.
            var def = new AbilityDefinition
            {
                Id = "self_aff", Targeting = "Self", TargetAffinity = "Ally",
                EffectGraph = new ProjectChimera.Effects.HealEffect(Fixed.FromInt(10)),
            };
            AbilityValidationResult r = new AbilityValidator().Validate(def);
            Assert.True(r.Ok); // non-fatal — it loads and runs
            Assert.Contains(r.Warnings, w => w.FieldPath == "target_affinity");
        }

        [Fact]
        public void GroundPointBareLeaf_Warns_ThatItWillNoOp()
        {
            // A GroundPoint ability whose root reads the primary target (a bare Heal/Damage leaf) silently no-ops at the
            // point (the primary target is -1 for a ground cast) — the validator warns the author to wrap it in a
            // SearchArea. Non-fatal. Proves GroundPointResolvesAtPoint's false-branch warn actually fires.
            var bare = new AbilityDefinition
            {
                Id = "ground_bare", Targeting = "GroundPoint",
                EffectGraph = new ProjectChimera.Effects.HealEffect(Fixed.FromInt(10)),
            };
            AbilityValidationResult rb = new AbilityValidator().Validate(bare);
            Assert.True(rb.Ok);
            Assert.Contains(rb.Warnings, w => w.FieldPath == "effect");
        }

        // ── Block-If proof: target_affinity folds into NO content hash ──────────────

        [Fact]
        public void TargetAffinity_IsExcludedFromContentHash_AbsentEqualsPresent()
        {
            // Two identical abilities that differ ONLY in target_affinity (absent vs. every value) must hash identically —
            // proving the field is fold-excluded, so adding it never moves ContentHash for any shipped ability.
            ulong absent = HashOne(null);
            Assert.Equal(absent, HashOne("Enemy"));
            Assert.Equal(absent, HashOne("Ally"));
            Assert.Equal(absent, HashOne("Any"));
        }

        private static ulong HashOne(string? affinity)
        {
            var def = new AbilityDefinition
            {
                Id = "mend", Targeting = "TargetUnit", TargetAffinity = affinity,
                CostEnergy = Fixed.FromInt(20), Cooldown = Fixed.FromInt(4),
                EffectGraph = new ProjectChimera.Effects.HealEffect(Fixed.FromInt(50)),
            };
            var reg = new AbilityRegistry(new List<AbilityDefinition> { def });
            return ContentHash.Compute(new List<FactionDefinition>(), reg, ItemRegistry.Empty, DamageTable.Default);
        }
    }
}
