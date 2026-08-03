#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;               // Faction, UnitOrder, UnitCommand, Fixed
using ProjectChimera.Multiplayer;        // PacketType, TickCommandPacket, MergedTickPacket
using ProjectChimera.Multiplayer.Server; // ServerPacketRelay, CommandFanInResult, MergedTickBuilder
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-394 — the Godot-free <c>DedicatedServer</c> delegation seams (<see cref="ServerPacketRelay"/>).
    /// <c>MergedTickBuilderTests</c>/<c>ServerLobbyPolicyTests</c> pin each primitive in isolation; these pin the
    /// COMPOSITION the server node ships: slot/len fed verbatim into <see cref="MergedTickBuilder.Submit"/>, the
    /// monotonic frontier advance, <c>TryBuild</c>-then-broadcast on fan-in completion, the dispatch-level
    /// client-sent-<c>TickCommandsMerged</c> hard-reject, and the Chat decode → re-stamp → re-encode pipeline —
    /// the adapter-level transpositions (wrong slot/len, omitted broadcast, dropped chat re-encode) that used to
    /// be able to ship green while every underlying unit test passed.
    /// </summary>
    public class ServerPacketRelayTests
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

        /// <summary>A broadcast sink that counts calls and COPIES the packet (the builder reuses its scratch).</summary>
        private sealed class BroadcastSpy
        {
            public readonly List<byte[]> Packets = new();
            public void Sink(byte[] buf, int len)
            {
                var copy = new byte[len];
                System.Array.Copy(buf, copy, len);
                Packets.Add(copy);
            }
        }

        // ── The fan-in composition: submit → frontier → build → broadcast ─────────

        [Fact]
        public void FanIn_TwoSlots_BroadcastsExactlyOneCanonicalMergedPacket_AndAdvancesTheFrontier()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var spy = new BroadcastSpy();
            uint frontier = 0;

            // Slot 1 (Player2) submits FIRST — buffered, nothing broadcast, frontier advances to the tick.
            var (d1, l1) = Tick(5u, Faction.Player2, Move(20, 3, 4));
            Assert.Equal(CommandFanInResult.Buffered,
                ServerPacketRelay.FanInTickCommands(b, 1, d1, l1, ref frontier, spy.Sink));
            Assert.Empty(spy.Packets);
            Assert.Equal(5u, frontier);

            // Slot 0 (Player1) completes the tick — the ONE merged packet is broadcast.
            var (d0, l0) = Tick(5u, Faction.Player1, Move(10, 1, 2));
            Assert.Equal(CommandFanInResult.MergedBroadcast,
                ServerPacketRelay.FanInTickCommands(b, 0, d0, l0, ref frontier, spy.Sink));
            Assert.Single(spy.Packets);

            // The broadcast bytes decode to the canonical merged packet: ascending by faction id (Player1 before
            // Player2 despite Player2 arriving first), carrying each slot's orders.
            byte[] merged = spy.Packets[0];
            var of = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var oc = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var oo = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            Assert.True(MergedTickPacket.TryRead(merged, merged.Length, out uint tick, of, oc, oo, out int n));
            Assert.Equal(5u, tick);
            Assert.Equal(2, n);
            Assert.Equal(Faction.Player1, of[0]);
            Assert.Equal(Faction.Player2, of[1]);
            Assert.Equal((ushort)10, oo[0].UnitId);
            Assert.Equal((ushort)20, oo[TickCommandPacket.MAX_ORDERS].UnitId);
        }

        [Fact]
        public void MergedShapedClientPacket_IsRejectedBeforeTheBuilder()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var spy = new BroadcastSpy();
            uint frontier = 0;

            // A client-sent merged-shaped packet is the spoof of the authoritative stream — the relay's
            // dispatch-level guard rejects it before Submit: no broadcast, no frontier movement, no builder state.
            var spoof = new byte[MergedTickPacket.HEADER_BYTES];
            spoof[0] = (byte)PacketType.TickCommandsMerged;
            spoof[5] = 2; // pretend it carries sub-bundles
            Assert.Equal(CommandFanInResult.RejectedMergedFromClient,
                ServerPacketRelay.FanInTickCommands(b, 0, spoof, spoof.Length, ref frontier, spy.Sink));
            Assert.Empty(spy.Packets);
            Assert.Equal(0u, frontier);

            // The builder is genuinely untouched: the real tick-1 fan-in still completes normally.
            var (d0, l0) = Tick(1u, Faction.Player1, Move(1, 0, 0));
            var (d1, l1) = Tick(1u, Faction.Player2, Move(2, 0, 0));
            Assert.Equal(CommandFanInResult.Buffered,
                ServerPacketRelay.FanInTickCommands(b, 0, d0, l0, ref frontier, spy.Sink));
            Assert.Equal(CommandFanInResult.MergedBroadcast,
                ServerPacketRelay.FanInTickCommands(b, 1, d1, l1, ref frontier, spy.Sink));
            Assert.Single(spy.Packets);
        }

        [Fact]
        public void SpoofedFactionPacket_IsDropped_NothingBroadcast()
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var spy = new BroadcastSpy();
            uint frontier = 0;

            // Slot 0 is authoritatively Player1; a packet claiming Player2 is a spoof → the builder drops it and
            // the relay reports Dropped (frontier untouched — a dropped packet must not advance the delay marker).
            var (d, l) = Tick(3u, Faction.Player2, Move(1, 0, 0));
            Assert.Equal(CommandFanInResult.Dropped,
                ServerPacketRelay.FanInTickCommands(b, 0, d, l, ref frontier, spy.Sink));
            Assert.Empty(spy.Packets);
            Assert.Equal(0u, frontier);
        }

        [Fact]
        public void TruncatedPacket_IsDropped()
        {
            // The wrong-len transposition: one byte short of the declared order payload → the read-side reject
            // drops it whole (never a partial decode), nothing broadcast, frontier untouched.
            var b = new MergedTickBuilder(2, SlotFaction);
            var spy = new BroadcastSpy();
            uint frontier = 0;

            var (d, l) = Tick(2u, Faction.Player1, Move(1, 0, 0));
            Assert.Equal(CommandFanInResult.Dropped,
                ServerPacketRelay.FanInTickCommands(b, 0, d, l - 1, ref frontier, spy.Sink));
            Assert.Empty(spy.Packets);
            Assert.Equal(0u, frontier);
        }

        [Fact]
        public void OutOfRangeSlot_IsDropped()
        {
            // The wrong-slot transposition: a spectator (slot ≥ Expected) or negative slot never fans in.
            var b = new MergedTickBuilder(2, SlotFaction);
            var spy = new BroadcastSpy();
            uint frontier = 0;

            var (d, l) = Tick(1u, Faction.Player1, Move(1, 0, 0));
            Assert.Equal(CommandFanInResult.Dropped,
                ServerPacketRelay.FanInTickCommands(b, 2, d, l, ref frontier, spy.Sink));
            Assert.Equal(CommandFanInResult.Dropped,
                ServerPacketRelay.FanInTickCommands(b, -1, d, l, ref frontier, spy.Sink));
            Assert.Empty(spy.Packets);
        }

        [Fact]
        public void NullBuilder_IsANoOp_BeforeTheMatchStarts()
        {
            var spy = new BroadcastSpy();
            uint frontier = 0;
            var (d, l) = Tick(1u, Faction.Player1, Move(1, 0, 0));

            Assert.Equal(CommandFanInResult.NoBuilder,
                ServerPacketRelay.FanInTickCommands(null, 0, d, l, ref frontier, spy.Sink));
            Assert.Empty(spy.Packets);
            Assert.Equal(0u, frontier);
        }

        [Fact]
        public void Frontier_NeverRegresses_WhenAnOlderTickIsBuffered()
        {
            // Story 9.4: the frontier is the delay authority's forward marker — an accepted OLDER tick (legal
            // while unemitted) must not drag it backwards.
            var b = new MergedTickBuilder(2, SlotFaction);
            var spy = new BroadcastSpy();
            uint frontier = 0;

            var (d5, l5) = Tick(5u, Faction.Player1, Move(1, 0, 0));
            Assert.Equal(CommandFanInResult.Buffered,
                ServerPacketRelay.FanInTickCommands(b, 0, d5, l5, ref frontier, spy.Sink));
            Assert.Equal(5u, frontier);

            var (d3, l3) = Tick(3u, Faction.Player1, Move(1, 0, 0));
            Assert.Equal(CommandFanInResult.Buffered,
                ServerPacketRelay.FanInTickCommands(b, 0, d3, l3, ref frontier, spy.Sink));
            Assert.Equal(5u, frontier); // still 5 — never 3
        }

        // ── The Chat re-stamp pipeline: decode → StampChatFaction → re-encode ─────

        [Fact]
        public void SpoofedChatFactionByte_IsRestampedFromTheSenderSlot()
        {
            // The client claims Player2 but the transport-authoritative sender is slot 0 (Player1): the re-encoded
            // packet must carry Player1 with the message intact — no client-supplied faction byte survives.
            byte[] pkt = TickCommandPacket.MakeChat(Faction.Player2, "gg wp");
            byte[]? restamped = ServerPacketRelay.RestampChat(0, pkt, pkt.Length, SlotFaction, maxPlayers: 2);

            Assert.NotNull(restamped);
            Assert.True(TickCommandPacket.TryReadChat(restamped!, restamped!.Length,
                out Faction f, out string msg));
            Assert.Equal(Faction.Player1, f);
            Assert.Equal("gg wp", msg);
        }

        [Fact]
        public void SpectatorChat_IsStampedNeutral()
        {
            byte[] pkt = TickCommandPacket.MakeChat(Faction.Player1, "spectating");
            byte[]? restamped = ServerPacketRelay.RestampChat(2, pkt, pkt.Length, SlotFaction, maxPlayers: 2);

            Assert.NotNull(restamped);
            Assert.True(TickCommandPacket.TryReadChat(restamped!, restamped!.Length,
                out Faction f, out string msg));
            Assert.Equal(Faction.Neutral, f);
            Assert.Equal("spectating", msg);
        }

        [Fact]
        public void OutOfRangeSenderSlot_IsStampedNeutral()
        {
            byte[] pkt = TickCommandPacket.MakeChat(Faction.Player1, "?");
            byte[]? restamped = ServerPacketRelay.RestampChat(-1, pkt, pkt.Length, SlotFaction, maxPlayers: 2);

            Assert.NotNull(restamped);
            Assert.True(TickCommandPacket.TryReadChat(restamped!, restamped!.Length, out Faction f, out _));
            Assert.Equal(Faction.Neutral, f);
        }

        [Fact]
        public void UndecodableChat_ReturnsNull_NothingToRebroadcast()
        {
            // Garbage bytes and a wrong-discriminator packet (LobbyChat routed into the in-match Chat re-stamp)
            // both fail the decode → null → the caller drops + logs instead of relaying raw spoofable bytes.
            Assert.Null(ServerPacketRelay.RestampChat(0, new byte[] { 0xFF, 1, 2 }, 3, SlotFaction, 2));

            byte[] lobby = TickCommandPacket.MakeLobbyChat(Faction.Player1, "hi");
            Assert.Null(ServerPacketRelay.RestampChat(0, lobby, lobby.Length, SlotFaction, 2));
        }

        [Fact]
        public void HonestChat_SurvivesTheReencodeByteIdentical()
        {
            // A non-spoofed chat (claimed faction == slot faction) re-encodes to the identical wire bytes — the
            // re-stamp pipeline changes nothing but the lie.
            byte[] pkt = TickCommandPacket.MakeChat(Faction.Player1, "hello there");
            byte[]? restamped = ServerPacketRelay.RestampChat(0, pkt, pkt.Length, SlotFaction, maxPlayers: 2);

            Assert.NotNull(restamped);
            Assert.Equal(pkt, restamped);
        }
    }
}
