#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;                 // Faction, UnitOrder, UnitCommand, Fixed
using ProjectChimera.Multiplayer;          // TickCommandPacket, MergedTickPacket
using ProjectChimera.Multiplayer.Server;   // MergedTickBuilder, ServerPacketRelay, DropCoordinator, FrozenSlotInjector
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-412 (second half) — the DROPPED-SLOT-LED-FRONTIER case: the peer that leaves was RACING AHEAD of the
    /// survivor when it went silent ("drop the lagging/racing player", the common real disconnect). Nothing covered
    /// it. It matters because <c>DedicatedServer.PumpFrozenInjection</c> drains against the GLOBAL
    /// <c>_latestSeenTick</c> frontier, and that frontier carries the departed peer's pre-drop LEAD — so the drain is
    /// asked to fill ticks the surviving player has not reached and for which the frozen slot already has REAL
    /// buffered commands.
    ///
    /// <para>These tests drive the production chain end-to-end and Godot-free — the real
    /// <see cref="ServerPacketRelay.FanInTickCommands"/> (which owns the frontier advance), the real
    /// <see cref="DropCoordinator"/> freeze handshake, and the real <see cref="FrozenSlotInjector.Drain"/> — exactly
    /// as the node sequences them (fan-in, then pump). They pin three properties the ledger called unverified:</para>
    /// <list type="number">
    ///   <item>the drain emits NOTHING past the survivor even when the frontier is far ahead of it (no false/early
    ///   merged tick, and no deadlock — the gap completes later);</item>
    ///   <item>the frozen slot's already-in-flight REAL pre-drop orders still win at every tick it had reached, and
    ///   only ticks past its lead go idle; and</item>
    ///   <item>the frontier can never outrun the builder's accept window, because it only advances on ACCEPTED
    ///   submissions — the "correct only via the ACCEPT_WINDOW reject" reasoning, made a test.</item>
    /// </list>
    /// </summary>
    public class DroppedSlotLedFrontierTests
    {
        private const int Survivor = 0;
        private const int Racer    = 1; // the peer that leads the frontier, then drops

        /// <summary>The server's fan-in + freeze machinery over captured seams (no ENet, no Godot).</summary>
        private sealed class Rig
        {
            public readonly Faction[] SlotFaction = SlotFactionTable.Build(2);
            public readonly MergedTickBuilder Builder;
            public readonly DropCoordinator Co;

            /// <summary>The node's <c>_latestSeenTick</c> — advanced ONLY by the production relay seam.</summary>
            public uint Frontier;

            public readonly List<(uint tick, Dictionary<Faction, int> bundles)> Emitted = new();
            public readonly List<(Faction faction, uint applyAtTick)> Directives = new();
            public readonly List<int> Committed = new();
            public readonly HashSet<int> Connected = new() { 0, 1 };

            private readonly byte[] _injectScratch = new byte[TickCommandPacket.HEADER_BYTES];

            public Rig()
            {
                Builder = new MergedTickBuilder(2, SlotFaction);
                Co = new DropCoordinator(2, SlotFaction,
                    () => Builder.EmittedThrough,
                    s => Connected.Contains(s),
                    (f, t) => Directives.Add((f, t)),
                    slot => Committed.Add(slot));
            }

            /// <summary>One inbound TickCommands packet through the REAL relay (frontier advance included).</summary>
            public CommandFanInResult Submit(int slot, uint tick, int orderCount = 1)
            {
                byte[] packet = WritePacket(tick, SlotFaction[slot], orderCount, out int len);
                return ServerPacketRelay.FanInTickCommands(Builder, slot, packet, len, ref Frontier, Record);
            }

            /// <summary>The node's <c>PumpFrozenInjection</c>: drain the whole gap up to the frontier.</summary>
            public void Pump() => FrozenSlotInjector.Drain(Builder, Co.Controller.FrozenSlots, SlotFaction,
                                                           Frontier, _injectScratch, Record);

            /// <summary>Transport clears the slot first (as ENet does), then the coordinator is told.</summary>
            public DropCoordinator.DisconnectOutcome Disconnect(int slot)
            {
                Connected.Remove(slot);
                return Co.OnPlayerDisconnect(slot);
            }

            /// <summary>The survivor ACKs the last issued directive — completing the freeze handshake.</summary>
            public bool Ack(int ackSlot)
            {
                (Faction f, uint t) = Directives[^1];
                return Co.OnDropAck(ackSlot, (byte)f, t);
            }

            /// <summary>Per-faction order counts of the merged packet emitted for a given tick.</summary>
            public Dictionary<Faction, int> BundlesAt(uint tick)
            {
                foreach ((uint t, Dictionary<Faction, int> b) in Emitted)
                    if (t == tick) return b;
                Assert.Fail($"no merged packet was emitted for tick {tick}");
                return null!;
            }

            private void Record(byte[] merged, int len)
            {
                var factions = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
                var counts   = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
                var orders   = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
                Assert.True(MergedTickPacket.TryRead(merged, len, out uint tick, factions, counts, orders, out int n));

                var map = new Dictionary<Faction, int>();
                for (int b = 0; b < n; b++) map[factions[b]] = counts[b];
                Emitted.Add((tick, map));
            }

            private static byte[] WritePacket(uint tick, Faction faction, int orderCount, out int len)
            {
                var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
                var orders = new UnitOrder[orderCount];
                for (int i = 0; i < orderCount; i++)
                    orders[i] = new UnitOrder(i, UnitCommand.Move, Fixed.FromInt(3), Fixed.FromInt(4));
                len = TickCommandPacket.Write(buf, tick, faction, orders, orderCount);
                return buf;
            }
        }

        [Fact]
        public void DroppedSlotLedTheFrontier_DrainBuildsNothingPastTheSurvivor_ThenTheGapCompletesInOrder()
        {
            var rig = new Rig();

            // Warm-up: both peers fan in ticks 0..4 → five merged ticks emit normally.
            for (uint t = 0; t <= 4; t++) { rig.Submit(Survivor, t); rig.Submit(Racer, t); }
            Assert.Equal(5, rig.Emitted.Count);
            Assert.Equal(4L, rig.Builder.EmittedThrough);

            // The racer now runs AHEAD: it alone fans in 5..20. Each is buffered (fan-in incomplete) but each
            // advances the SHARED frontier — so _latestSeenTick is 16 ticks past anything that can be emitted.
            for (uint t = 5; t <= 20; t++)
                Assert.Equal(CommandFanInResult.Buffered, rig.Submit(Racer, t));
            Assert.Equal(20u, rig.Frontier);
            Assert.Equal(4L, rig.Builder.EmittedThrough);

            // ...and THEN it drops. The survivor ACKs; the freeze commits.
            Assert.Equal(DropCoordinator.DisconnectOutcome.DirectiveIssued, rig.Disconnect(Racer));
            Assert.True(rig.Ack(Survivor));
            Assert.True(rig.Co.Controller.IsFrozen(Racer));
            Assert.Equal(new[] { Racer }, rig.Committed);

            // The pump drains the WHOLE gap (5..20) against a frontier the DEPARTED peer set. Nothing may build:
            // the survivor has reached none of it. A drain that keyed off the frontier instead of per-tick fan-in
            // completeness would broadcast up to 16 merged ticks the survivor never contributed to.
            int emittedBeforePump = rig.Emitted.Count;
            rig.Pump();
            Assert.Equal(emittedBeforePump, rig.Emitted.Count);
            Assert.Equal(4L, rig.Builder.EmittedThrough);
            rig.Pump(); // idempotent — repeated pumps against a stale frontier still emit nothing
            Assert.Equal(emittedBeforePump, rig.Emitted.Count);

            // No deadlock either: as the survivor catches up, every gap tick completes in ascending order — and each
            // carries the frozen slot's REAL pre-drop orders, NOT an injected empty (the idempotent-duplicate guard),
            // so its final in-flight actions still execute.
            for (uint t = 5; t <= 20; t++) { rig.Submit(Survivor, t); rig.Pump(); }
            Assert.Equal(20L, rig.Builder.EmittedThrough);
            for (uint t = 5; t <= 20; t++)
            {
                Dictionary<Faction, int> bundles = rig.BundlesAt(t);
                Assert.Equal(1, bundles[rig.SlotFaction[Survivor]]);
                Assert.Equal(1, bundles[rig.SlotFaction[Racer]]); // real, not the 0-order injected empty
            }

            // Only PAST the departed peer's lead does its stream actually go idle.
            rig.Submit(Survivor, 21u);
            rig.Pump();
            Assert.Equal(21L, rig.Builder.EmittedThrough);
            Assert.Equal(0, rig.BundlesAt(21u)[rig.SlotFaction[Racer]]);

            // Emission stayed strictly ascending and gap-free across the whole run (0..21, each exactly once).
            Assert.Equal(22, rig.Emitted.Count);
            for (int i = 0; i < rig.Emitted.Count; i++) Assert.Equal((uint)i, rig.Emitted[i].tick);
        }

        [Fact]
        public void DirectiveMarker_IsTheEmittedHighWater_NotTheRacersLead()
        {
            // The applyAtTick the directive carries is EmittedThrough+1, captured at ISSUE time — so when the dropped
            // peer led the frontier the marker names a tick well BELOW its buffered lead. That is deliberate: the
            // merged stream stays authoritative (the client only surfaces the marker in UI and echoes it in its ACK),
            // and the marker must stay at/behind the emitted high-water or no survivor could ever ACK a matching pair.
            var rig = new Rig();
            for (uint t = 0; t <= 2; t++) { rig.Submit(Survivor, t); rig.Submit(Racer, t); }
            for (uint t = 3; t <= 30; t++) rig.Submit(Racer, t);

            Assert.Equal(30u, rig.Frontier);
            Assert.Equal(2L, rig.Builder.EmittedThrough);

            rig.Disconnect(Racer);
            (Faction faction, uint applyAtTick) = Assert.Single(rig.Directives);
            Assert.Equal(rig.SlotFaction[Racer], faction);
            Assert.Equal(3u, applyAtTick);                 // EmittedThrough(2) + 1 — NOT frontier(30) + 1
            Assert.True(applyAtTick <= rig.Frontier);      // a marker past the frontier could never be reached

            // And the survivor's ACK of exactly that pair commits the freeze (a frontier-derived marker would not
            // match the directive the survivor received).
            Assert.True(rig.Ack(Survivor));
            Assert.Equal(3u, rig.Co.Controller.FrozenApplyTick(Racer));
        }

        [Fact]
        public void RacingPeerCannotDragTheFrontierPastTheBuildersAcceptWindow()
        {
            // The ledger's reasoning was that the drain's use of the global frontier is "correct only via
            // MergedTickBuilder's ACCEPT_WINDOW reject". This makes that an asserted invariant: the frontier advances
            // ONLY on an ACCEPTED submission, and the builder refuses anything more than ACCEPT_WINDOW (RING/2 = 32)
            // past the emitted high-water — so the gap the drain scans is always bounded, however far a peer races.
            var rig = new Rig();

            int accepted = 0;
            for (uint t = 0; t <= 200; t++)
                if (rig.Submit(Racer, t) == CommandFanInResult.Buffered) accepted++;

            Assert.Equal(32, accepted);                 // ticks 0..31 buffered; 32..200 refused by the window
            Assert.Equal(31u, rig.Frontier);            // the frontier stopped with them
            Assert.Equal(-1L, rig.Builder.EmittedThrough);
            Assert.True(rig.Frontier - rig.Builder.EmittedThrough <= 32);

            // Freeze the racer and drain that (bounded) gap — the survivor has reached nothing, so nothing builds.
            Assert.Equal(DropCoordinator.DisconnectOutcome.DirectiveIssued, rig.Disconnect(Racer));
            Assert.True(rig.Ack(Survivor));
            rig.Pump();
            Assert.Empty(rig.Emitted);

            // Nothing inside the window was lost: the survivor catching up completes all 32 buffered ticks, each
            // still carrying the departed peer's real pre-drop orders.
            for (uint t = 0; t <= 31; t++) { rig.Submit(Survivor, t); rig.Pump(); }
            Assert.Equal(31L, rig.Builder.EmittedThrough);
            Assert.Equal(32, rig.Emitted.Count);
            foreach ((uint tick, Dictionary<Faction, int> bundles) in rig.Emitted)
            {
                Assert.Equal(1, bundles[rig.SlotFaction[Survivor]]);
                Assert.Equal(1, bundles[rig.SlotFaction[Racer]]);
                Assert.True(tick <= 31u);
            }
        }
    }
}
