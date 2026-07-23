#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;                 // Faction, UnitOrder, UnitCommand, Fixed
using ProjectChimera.Multiplayer;          // TickCommandPacket, MergedTickPacket
using ProjectChimera.Multiplayer.Server;   // MergedTickBuilder, FrozenSlotInjector
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.6 — the empty-injection drain. Proves: with a slot frozen, injecting empties for the WHOLE unemitted
    /// gap up to the survivor's frontier completes every merged tick (survivor's real sub-bundle + the frozen slot's
    /// EMPTY sub-bundle); the drain never keys past the frontier; a tick the survivor has not yet reached does not
    /// build (no deadlock, completes on a later pump); and a frozen slot's already-in-flight REAL command WINS over a
    /// later injected empty (the idempotent-duplicate guard).
    /// </summary>
    public class FrozenSlotInjectorTests
    {
        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };

        private static byte[] RealPacket(uint tick, Faction faction, int unitId, out int len)
        {
            var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            var orders = new[] { new UnitOrder(unitId, UnitCommand.Move, Fixed.FromInt(3), Fixed.FromInt(4)) };
            len = TickCommandPacket.Write(buf, tick, faction, orders, 1);
            return buf;
        }

        /// <summary>Decode one emitted merged packet into (faction → orderCount) for assertions.</summary>
        private static Dictionary<Faction, int> Decode(byte[] merged, int len)
        {
            var outFactions    = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var outOrderCounts = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var outOrders      = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            Assert.True(MergedTickPacket.TryRead(merged, len, out _, outFactions, outOrderCounts, outOrders, out int n));
            var map = new Dictionary<Faction, int>();
            for (int b = 0; b < n; b++) map[outFactions[b]] = outOrderCounts[b];
            return map;
        }

        [Fact]
        public void Drain_FillsWholeGap_CompletingEveryTick()
        {
            var builder = new MergedTickBuilder(2, SlotFaction);
            var scratch = new byte[TickCommandPacket.HEADER_BYTES];
            var emitted = new List<Dictionary<Faction, int>>();
            void Broadcast(byte[] m, int l) => emitted.Add(Decode(m, l));

            // The survivor (slot 0) has submitted ticks 0,1,2 but slot 1 is frozen → nothing has built yet.
            for (uint t = 0; t <= 2; t++)
            {
                byte[] p = RealPacket(t, Faction.Player1, unitId: 0, out int len);
                Assert.True(builder.Submit(0, p, len, out _));
            }
            Assert.Equal(-1, builder.EmittedThrough); // nothing emitted — slot 1 never arrived

            FrozenSlotInjector.Drain(builder, new[] { 1 }, SlotFaction, frontier: 2u, scratch, Broadcast);

            // All three gap ticks now complete: Player1 (1 order) + Player2 (0 orders, injected empty).
            Assert.Equal(3, emitted.Count);
            Assert.Equal(2, builder.EmittedThrough);
            foreach (var m in emitted)
            {
                Assert.Equal(1, m[Faction.Player1]);
                Assert.Equal(0, m[Faction.Player2]); // the frozen slot's EMPTY sub-bundle is present
            }
        }

        [Fact]
        public void Drain_DoesNotBuildTicksTheSurvivorHasNotReached()
        {
            var builder = new MergedTickBuilder(2, SlotFaction);
            var scratch = new byte[TickCommandPacket.HEADER_BYTES];
            int emitted = 0;

            // Survivor submitted only tick 0, but the frontier claims 2 (e.g. the frozen slot led before dropping).
            byte[] p = RealPacket(0u, Faction.Player1, 0, out int len);
            builder.Submit(0, p, len, out _);

            FrozenSlotInjector.Drain(builder, new[] { 1 }, SlotFaction, frontier: 2u, scratch, (_, _) => emitted++);

            // Only tick 0 builds (survivor arrived); ticks 1,2 stay pending — no deadlock, no false emit.
            Assert.Equal(1, emitted);
            Assert.Equal(0, builder.EmittedThrough);
        }

        [Fact]
        public void Drain_RealCommandWinsOverInjectedEmpty()
        {
            var builder = new MergedTickBuilder(2, SlotFaction);
            var scratch = new byte[TickCommandPacket.HEADER_BYTES];
            Dictionary<Faction, int>? built = null;
            void Broadcast(byte[] m, int l) => built = Decode(m, l);

            // Both slots submitted REAL commands for tick 0 (slot 1's final pre-drop action is already in flight).
            byte[] p0 = RealPacket(0u, Faction.Player1, 0, out int l0);
            byte[] p1 = RealPacket(0u, Faction.Player2, 2, out int l1);
            builder.Submit(0, p0, l0, out _);
            builder.Submit(1, p1, l1, out _);

            // Now the injector runs for the same tick — its empty for slot 1 must be an idempotent no-op.
            FrozenSlotInjector.Drain(builder, new[] { 1 }, SlotFaction, frontier: 0u, scratch, Broadcast);

            Assert.NotNull(built);
            Assert.Equal(1, built![Faction.Player1]);
            Assert.Equal(1, built![Faction.Player2]); // the REAL command (1 order) survived, NOT the injected empty (0)
        }

        [Fact]
        public void Drain_NoFrozenSlots_IsNoOp()
        {
            var builder = new MergedTickBuilder(2, SlotFaction);
            var scratch = new byte[TickCommandPacket.HEADER_BYTES];
            int emitted = 0;
            FrozenSlotInjector.Drain(builder, new int[0], SlotFaction, frontier: 5u, scratch, (_, _) => emitted++);
            Assert.Equal(0, emitted);
            Assert.Equal(-1, builder.EmittedThrough);
        }
    }
}
