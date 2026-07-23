#nullable enable
using ProjectChimera.Core;                 // Faction, UnitOrder, UnitCommand, Fixed
using ProjectChimera.Multiplayer;           // PacketType, TickCommandPacket, MergedTickPacket
using ProjectChimera.Multiplayer.Server;    // MergedTickBuilder
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.3 — the Godot-free authoritative fan-in <see cref="MergedTickBuilder"/>. Covers every I/O-matrix
    /// edge: faction re-stamp / spoof-drop, over-count drop-not-clamp, merged-from-client hard-reject, ascending
    /// sub-bundle sort, fan-in completion (emit once, incomplete = no emit), and late/duplicate ignore.
    /// </summary>
    public class MergedTickBuilderTests
    {
        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };

        private static UnitOrder Move(int unitId, int rx, int rz) =>
            new UnitOrder(unitId, UnitCommand.Move, Fixed.FromRaw(rx), Fixed.FromRaw(rz));

        /// <summary>Serialise a single-faction TickCommands packet for a slot.</summary>
        private static (byte[] data, int len) Tick(uint tick, Faction faction, params UnitOrder[] orders)
        {
            var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            int len = TickCommandPacket.Write(buf, tick, faction, orders, orders.Length);
            return (buf, len);
        }

        private static (Faction[] f, int[] c, UnitOrder[] o) Scratch() => (
            new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
            new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
            new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS]);

        [Fact]
        public void FanInComplete_EmitsOnce_AscendingByFaction()
        {
            var b = new MergedTickBuilder(2, SlotFaction);

            var (d1, l1) = Tick(5u, Faction.Player1, Move(10, 1, 2));
            var (d0, l0) = Tick(5u, Faction.Player2, Move(20, 3, 4)); // submit P2 first to prove sort, not arrival order

            // Slot 1 (Player2) submits first — no merged yet (fan-in incomplete).
            Assert.True(b.Submit(1, d0, l0, out uint t1));
            Assert.Equal(5u, t1);
            Assert.False(b.TryBuild(5u, out _, out _));

            // Slot 0 (Player1) completes the tick.
            Assert.True(b.Submit(0, d1, l1, out uint t0));
            Assert.True(b.TryBuild(5u, out byte[] merged, out int len));

            var (of, oc, oo) = Scratch();
            Assert.True(MergedTickPacket.TryRead(merged, len, out uint tick, of, oc, oo, out int n));
            Assert.Equal(5u, tick);
            Assert.Equal(2, n);
            // Ascending: Player1 (id 1) before Player2 (id 2) despite Player2 arriving first.
            Assert.Equal(Faction.Player1, of[0]);
            Assert.Equal(Faction.Player2, of[1]);
            Assert.Equal((ushort)10, oo[0].UnitId);
            Assert.Equal((ushort)20, oo[TickCommandPacket.MAX_ORDERS].UnitId);

            // Emit exactly once: a second TryBuild for the same tick yields nothing.
            Assert.False(b.TryBuild(5u, out _, out _));
        }

        [Fact]
        public void FanInIncomplete_DoesNotEmit()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var (d, l) = Tick(7u, Faction.Player1, Move(1, 0, 0));
            Assert.True(b.Submit(0, d, l, out _));
            Assert.False(b.TryBuild(7u, out _, out _)); // only 1 of 2 → wait
        }

        [Fact]
        public void FactionSpoof_DropsWholeBundle()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            // Slot 0 is authoritatively Player1, but the packet claims Player2 → spoof → drop.
            var (d, l) = Tick(3u, Faction.Player2, Move(1, 0, 0));
            Assert.False(b.Submit(0, d, l, out _));

            // The legit P2 submit alone cannot complete the tick (slot 0 was dropped).
            var (d2, l2) = Tick(3u, Faction.Player2, Move(2, 0, 0));
            Assert.True(b.Submit(1, d2, l2, out _));
            Assert.False(b.TryBuild(3u, out _, out _));
        }

        [Fact]
        public void OverCount_IsDroppedNotClamped()
        {
            var b = new MergedTickBuilder(2, SlotFaction);

            // Hand-craft a TickCommands packet whose header claims 33 orders (> MAX_ORDERS 32). TryRead rejects it,
            // so Submit drops the whole bundle (no clamp to 32).
            var buf = new byte[TickCommandPacket.HEADER_BYTES + (TickCommandPacket.MAX_ORDERS + 1) * UnitOrder.SIZE];
            buf[0] = (byte)PacketType.TickCommands;
            // tick = 9 (LE)
            buf[1] = 9;
            buf[5] = (byte)Faction.Player1;
            buf[6] = (byte)(TickCommandPacket.MAX_ORDERS + 1); // over-count
            Assert.False(b.Submit(0, buf, buf.Length, out _));
        }

        [Fact]
        public void MergedShapedPacketFromClient_IsHardRejected()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var merged = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            merged[0] = (byte)PacketType.TickCommandsMerged;
            merged[5] = 2; // pretend it carries sub-bundles
            Assert.False(b.Submit(0, merged, MergedTickPacket.HEADER_BYTES, out _));
        }

        [Fact]
        public void OutOfRangeSlot_IsDropped()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var (d, l) = Tick(1u, Faction.Player1, Move(1, 0, 0));
            Assert.False(b.Submit(2, d, l, out _));  // slot 2 = spectator, never merges in
            Assert.False(b.Submit(-1, d, l, out _));
        }

        [Fact]
        public void LateSubmitForEmittedTick_IsIgnored()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var (d0, l0) = Tick(2u, Faction.Player1, Move(1, 0, 0));
            var (d1, l1) = Tick(2u, Faction.Player2, Move(2, 0, 0));
            Assert.True(b.Submit(0, d0, l0, out _));
            Assert.True(b.Submit(1, d1, l1, out _));
            Assert.True(b.TryBuild(2u, out _, out _));

            // A late duplicate for the already-emitted tick is ignored, and does not re-emit.
            var (dLate, lLate) = Tick(2u, Faction.Player1, Move(9, 9, 9));
            Assert.False(b.Submit(0, dLate, lLate, out _));
            Assert.False(b.TryBuild(2u, out _, out _));
        }

        [Fact]
        public void RingReuse_AcrossManyTicks_StaysConsistent()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            for (uint t = 0; t < 200; t++)
            {
                var (d0, l0) = Tick(t, Faction.Player1, Move((int)(t & 0xFFF), (int)t, 0));
                var (d1, l1) = Tick(t, Faction.Player2, Move((int)((t + 1) & 0xFFF), 0, (int)t));
                Assert.True(b.Submit(0, d0, l0, out _));
                Assert.True(b.Submit(1, d1, l1, out _));
                Assert.True(b.TryBuild(t, out byte[] merged, out int len));

                var (of, oc, oo) = Scratch();
                Assert.True(MergedTickPacket.TryRead(merged, len, out uint tick, of, oc, oo, out int n));
                Assert.Equal(t, tick);
                Assert.Equal(2, n);
            }
        }

        // ── Patch A robustness guards ─────────────────────────────────────────────

        [Fact]
        public void DuplicateSubmit_SameTick_KeepsFirstBundle_NotLastWriter()
        {
            var b = new MergedTickBuilder(2, SlotFaction);

            // Slot 0 submits tick 5 with unit 10; a SECOND (not-yet-emitted) tick-5 submit from slot 0 (unit 99)
            // must be an idempotent no-op — the FIRST bundle survives (no order-revision window).
            var (dFirst, lFirst)   = Tick(5u, Faction.Player1, Move(10, 1, 2));
            var (dSecond, lSecond) = Tick(5u, Faction.Player1, Move(99, 9, 9));
            Assert.True(b.Submit(0, dFirst, lFirst, out _));
            Assert.False(b.Submit(0, dSecond, lSecond, out _)); // duplicate (slot,tick) after arrival → dropped

            var (d1, l1) = Tick(5u, Faction.Player2, Move(20, 3, 4));
            Assert.True(b.Submit(1, d1, l1, out _));
            Assert.True(b.TryBuild(5u, out byte[] merged, out int len));

            var (of, oc, oo) = Scratch();
            Assert.True(MergedTickPacket.TryRead(merged, len, out _, of, oc, oo, out int n));
            Assert.Equal(2, n);
            // Player1 (ascending first) sub-bundle still holds the FIRST bundle's unit 10, NOT 99.
            Assert.Equal(1, oc[0]);
            Assert.Equal((ushort)10, oo[0].UnitId);
        }

        [Fact]
        public void AliasedFarFutureTick_DoesNotWipeInFlightArrivals()
        {
            var b = new MergedTickBuilder(2, SlotFaction);

            // Tick 5 is in flight (slot 0 arrived; waiting on slot 1).
            var (d0, l0) = Tick(5u, Faction.Player1, Move(10, 0, 0));
            Assert.True(b.Submit(0, d0, l0, out _));

            // A submission for tick 5 + RING (69) aliases onto the SAME ring index. It must be DROPPED (out of the
            // accept window) WITHOUT re-keying/clearing tick 5's honest slot-0 arrival.
            const uint RING = 64;
            var (dAlias, lAlias) = Tick(5u + RING, Faction.Player1, Move(77, 7, 7));
            Assert.False(b.Submit(0, dAlias, lAlias, out _));

            // The honest tick 5 still fans in when slot 1 submits it.
            var (d1, l1) = Tick(5u, Faction.Player2, Move(20, 0, 0));
            Assert.True(b.Submit(1, d1, l1, out _));
            Assert.True(b.TryBuild(5u, out byte[] merged, out int len));

            var (of, oc, oo) = Scratch();
            Assert.True(MergedTickPacket.TryRead(merged, len, out uint tick, of, oc, oo, out int n));
            Assert.Equal(5u, tick);
            Assert.Equal(2, n);
            Assert.Equal((ushort)10, oo[0].UnitId); // slot-0's ORIGINAL tick-5 order, not the aliased 77
        }

        [Fact]
        public void StaleOlderTick_BelowResolvedHighWater_IsDropped()
        {
            var b = new MergedTickBuilder(2, SlotFaction);

            // Emit ticks 0..20 so the resolved high-water advances to 20.
            for (uint t = 0; t <= 20; t++)
            {
                var (d0, l0) = Tick(t, Faction.Player1, Move(1, 0, 0));
                var (d1, l1) = Tick(t, Faction.Player2, Move(2, 0, 0));
                Assert.True(b.Submit(0, d0, l0, out _));
                Assert.True(b.Submit(1, d1, l1, out _));
                Assert.True(b.TryBuild(t, out _, out _));
            }

            // A submission for an already-resolved older tick (5 <= 20) is dropped, and never re-emits.
            var (dStale, lStale) = Tick(5u, Faction.Player1, Move(1, 0, 0));
            Assert.False(b.Submit(0, dStale, lStale, out _));
            Assert.False(b.TryBuild(5u, out _, out _));
        }
    }
}
