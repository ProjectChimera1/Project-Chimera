#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 15-1 — the reconnect wire family (D-9) + the thaw state machines: codec round-trips, the
    /// DropController thaw sequence (schedule → confirm → finalize, plus cancel/revert), the checksum quorum
    /// re-admit keyed to the resume boundary, the delay-authority reactivation, the injector's thaw bound, and
    /// the RejoinCoordinator ↔ RejoinClient protocol interlock over scripted seams (no transport, no Godot).
    /// The SIM-level determinism of catch-up is proven separately by <c>RejoinCatchUpHarnessTests</c> (DW-879).
    /// </summary>
    public class RejoinProtocolTests
    {
        // ── Codec round-trips ─────────────────────────────────────────────────

        [Fact]
        public void RejoinToken_RoundTrips()
        {
            var pkt = TickCommandPacket.MakeRejoinToken(3, 0xDEAD_BEEF_1234_5678UL);
            Assert.True(TickCommandPacket.TryReadRejoinToken(pkt, pkt.Length, out int slot, out ulong token));
            Assert.Equal(3, slot);
            Assert.Equal(0xDEAD_BEEF_1234_5678UL, token);
            Assert.False(TickCommandPacket.TryReadRejoinToken(pkt, 9, out _, out _)); // truncated
        }

        [Fact]
        public void RejoinRequest_Accept_Refuse_RoundTrip()
        {
            var req = TickCommandPacket.MakeRejoinRequest(42UL);
            Assert.True(TickCommandPacket.TryReadRejoinRequest(req, req.Length, out ulong token));
            Assert.Equal(42UL, token);

            var acc = TickCommandPacket.MakeRejoinAccept((byte)Faction.Player2, 6, 1234u);
            Assert.True(TickCommandPacket.TryReadRejoinAccept(acc, acc.Length, out byte f, out byte d, out uint t));
            Assert.Equal((byte)Faction.Player2, f);
            Assert.Equal(6, d);
            Assert.Equal(1234u, t);

            var refuse = TickCommandPacket.MakeRejoinRefuse(RejoinRefuseReason.TailUnavailable);
            Assert.True(TickCommandPacket.TryReadRejoinRefuse(refuse, refuse.Length, out RejoinRefuseReason r));
            Assert.Equal(RejoinRefuseReason.TailUnavailable, r);
        }

        [Fact]
        public void SnapshotChunk_RoundTrips_AndRejectsOverrun()
        {
            byte[] body = new byte[100];
            for (int i = 0; i < body.Length; i++) body[i] = (byte)i;
            var pkt = TickCommandPacket.MakeSnapshotChunk(7, 2, 5, 999u, body, 10, 50);
            Assert.True(TickCommandPacket.TryReadSnapshotChunk(pkt, pkt.Length, out byte req, out ushort seq,
                out ushort total, out uint tick, out int off, out int plen));
            Assert.Equal(7, req);
            Assert.Equal(2, seq);
            Assert.Equal(5, total);
            Assert.Equal(999u, tick);
            Assert.Equal(50, plen);
            Assert.Equal(10, pkt[off]); // first payload byte = body[10]

            // A framed length overrunning the packet is malformed, never a partial read.
            Assert.False(TickCommandPacket.TryReadSnapshotChunk(pkt, pkt.Length - 1, out _, out _, out _, out _,
                out _, out _));
        }

        [Fact]
        public void TailFrames_RoundTrip_TicksAscendContiguously()
        {
            var frames = new List<byte[]> { new byte[] { 1, 2, 3 }, new byte[] { 4 }, new byte[] { 5, 6 } };
            var pkt = TickCommandPacket.MakeTailFrames(100u, frames, 0, 3);
            var seen = new List<(uint tick, byte first, int len)>();
            Assert.True(TickCommandPacket.TryReadTailFrames(pkt, pkt.Length,
                (tick, buf, off, len) => seen.Add((tick, buf[off], len))));
            Assert.Equal(new[] { (100u, (byte)1, 3), (101u, (byte)4, 1), (102u, (byte)5, 2) }, seen.ToArray());

            // Truncation fails closed (the caller must treat it as a failed transfer).
            Assert.False(TickCommandPacket.TryReadTailFrames(pkt, pkt.Length - 1, (_, _, _, _) => { }));
        }

        [Fact]
        public void ResumeDirective_Ack_TailAck_RoundTrip()
        {
            var dir = TickCommandPacket.MakeResumeDirective((byte)Faction.Player1, 5000u, 4);
            Assert.True(TickCommandPacket.TryReadResumeDirective(dir, dir.Length, out byte f, out uint at, out byte d));
            Assert.Equal((byte)Faction.Player1, f);
            Assert.Equal(5000u, at);
            Assert.Equal(4, d);

            var ack = TickCommandPacket.MakeResumeAck((byte)Faction.Player1, 5000u);
            Assert.True(TickCommandPacket.TryReadResumeAck(ack, ack.Length, out byte af, out uint aat));
            Assert.Equal((byte)Faction.Player1, af);
            Assert.Equal(5000u, aat);

            var tail = TickCommandPacket.MakeTailAck(777u);
            Assert.True(TickCommandPacket.TryReadTailAck(tail, tail.Length, out uint next));
            Assert.Equal(777u, next);
        }

        // ── SnapshotTransfer chunk/assemble ───────────────────────────────────

        [Fact]
        public void SnapshotTransfer_ChunksAndReassembles_MultiChunkBody()
        {
            var body = new byte[TickCommandPacket.SNAPSHOT_CHUNK_BYTES * 2 + 17];
            var rng = new Random(7);
            rng.NextBytes(body);

            List<byte[]> chunks = SnapshotTransfer.Chunk(3, 250u, body);
            Assert.Equal(3, chunks.Count);

            var asm = new SnapshotTransfer.Assembler();
            bool complete = false;
            foreach (byte[] c in chunks) complete = asm.Feed(c, c.Length);
            Assert.True(complete);
            Assert.Equal(250u, asm.SnapshotTick);
            Assert.Equal(body, asm.ToArray());
        }

        // ── DropController thaw sequence ──────────────────────────────────────

        private static DropController FrozenController(int expected = 2, int frozenSlot = 1, uint applyAt = 100)
        {
            var c = new DropController(expected);
            Assert.True(c.NotifyDrop(frozenSlot, applyAt, new[] { 0 }));
            c.RecordAck(0, frozenSlot, applyAt);
            Assert.True(c.Commit());
            Assert.True(c.IsFrozen(frozenSlot));
            return c;
        }

        [Fact]
        public void ScheduleThaw_RequiresFrozen_BeyondApplyTick_OnePerSlot()
        {
            var c = FrozenController();
            Assert.False(c.ScheduleThaw(0, 200));   // not frozen
            Assert.False(c.ScheduleThaw(1, 100));   // not beyond the frozen applyAtTick
            Assert.True(c.ScheduleThaw(1, 200));
            Assert.False(c.ScheduleThaw(1, 300));   // one resume in flight per slot
            Assert.Equal(200, c.ThawBound(1));
            Assert.Equal(long.MaxValue, c.ThawBound(0));
        }

        [Fact]
        public void CancelThaw_PreCommitOnly_RevertThaw_Always()
        {
            var c = FrozenController();
            Assert.True(c.ScheduleThaw(1, 200));
            Assert.True(c.CancelThaw(1));
            Assert.Equal(long.MaxValue, c.ThawBound(1)); // injector resumes covering everything
            Assert.True(c.IsFrozen(1));                  // still frozen — nothing was lost

            Assert.True(c.ScheduleThaw(1, 200));
            Assert.True(c.ConfirmThaw(1));
            Assert.False(c.CancelThaw(1));               // committed — negotiations can't cancel it
            Assert.True(c.RevertThaw(1));                // ...but a rejoiner DISCONNECT can (the wedge guard)
            Assert.Equal(long.MaxValue, c.ThawBound(1));
            Assert.True(c.IsFrozen(1));
        }

        [Fact]
        public void FinalizeThaws_CompletesOnlyCommitted_AndOnlyAtTheBound()
        {
            var c = FrozenController();
            Assert.True(c.ScheduleThaw(1, 200));
            var thawed = new List<int>();

            Assert.Equal(0, c.FinalizeThaws(500, thawed));  // scheduled but NOT committed → never finalizes
            Assert.True(c.ConfirmThaw(1));
            Assert.Equal(0, c.FinalizeThaws(198, thawed));  // emitted 198 < bound-1 (199) → not yet
            Assert.Equal(1, c.FinalizeThaws(199, thawed));  // emitted through bound-1 → unfreeze
            Assert.Equal(new[] { 1 }, thawed.ToArray());
            Assert.False(c.IsFrozen(1));
            Assert.Empty(c.FrozenSlots);

            // The slot is drop-able again — a LATER disconnect freezes it through the normal directive path.
            Assert.True(c.NotifyDrop(1, 300, new[] { 0 }));
        }

        // ── Checksum quorum re-admit (the ServerHost.cs:208 dual) ─────────────

        [Fact]
        public void AddExpectedReporter_WindowsBelowBoundaryQuorumOverSurvivors_AtBoundaryNeedAll()
        {
            var c = new ServerChecksumCollector(2);
            // Drop slot 1 (the leaver), then re-admit it from tick 300.
            c.DropExpectedReporter(1);
            Assert.True(c.AddExpectedReporter(1, fromTick: 300));
            Assert.False(c.AddExpectedReporter(1, 300)); // idempotent — not excluded anymore

            // A window BELOW the boundary completes on the survivor alone (the rejoiner is not yet expected)...
            var below = c.Record(240, 0, 0xAAAA);
            Assert.True(below.Complete);
            Assert.True(below.HasMajority);

            // ...and a stray catch-up report from the rejoiner below its boundary is DROPPED, never counted.
            var stray = c.Record(299, 1, 0xBBBB);
            Assert.False(stray.Complete);

            // A window AT/AFTER the boundary needs BOTH reporters again — and agreement is a clean window.
            Assert.False(c.Record(300, 0, 0xCCCC).Complete);
            var both = c.Record(300, 1, 0xCCCC);
            Assert.True(both.Complete);
            Assert.True(both.HasMajority);
            Assert.Empty(both.Minority);
        }

        [Fact]
        public void AddExpectedReporter_PostBoundaryDivergence_IsALoudDesync()
        {
            var c = new ServerChecksumCollector(2);
            c.DropExpectedReporter(1);
            c.AddExpectedReporter(1, 60);
            c.Record(60, 0, 0x1111);
            var v = c.Record(60, 1, 0x2222);
            Assert.True(v.Complete);
            Assert.False(v.HasMajority); // 1-vs-1 — no strict majority: the corrupt-donor case desyncs LOUDLY (D-1)
        }

        // ── DelayController reactivation ──────────────────────────────────────

        [Fact]
        public void ReactivateSlot_RestoresQuorum_AndExcusesPendingDirective()
        {
            var slots = new List<int> { 0, 1 };
            var c = new DelayController(slots, 8, 4);
            c.DeactivateSlot(1);
            Assert.Equal(1, c.ActiveCount);

            c.ReactivateSlot(1);
            Assert.Equal(2, c.ActiveCount);
            Assert.True(c.IsActiveSlot(1));
            c.ReactivateSlot(1); // idempotent
            Assert.Equal(2, c.ActiveCount);
        }

        // ── FrozenSlotInjector thaw bound ─────────────────────────────────────

        [Fact]
        public void Injector_NeverInjectsAtOrPastTheThawBound()
        {
            var builder = new MergedTickBuilder(2, new[] { Faction.Player1, Faction.Player2 });
            var scratch = new byte[TickCommandPacket.HEADER_BYTES];
            var emitted = new List<uint>();
            void Broadcast(byte[] buf, int n)
            {
                Assert.True(MergedTickPacket.TryPeekTick(buf, n, out uint t));
                emitted.Add(t);
            }

            // Slot 0 submits ticks 0..9 live; slot 1 is frozen with a thaw bound at tick 6.
            var p1Buf = new byte[TickCommandPacket.HEADER_BYTES];
            for (uint t = 0; t < 10; t++)
            {
                int len = TickCommandPacket.Write(p1Buf, t, Faction.Player1, Array.Empty<UnitOrder>(), 0);
                builder.Submit(0, p1Buf, len, out _);
            }

            FrozenSlotInjector.Drain(builder, new[] { 1 }, new[] { Faction.Player1, Faction.Player2 },
                frontier: 9, scratch, Broadcast, thawBoundOf: _ => 6L);

            // Ticks 0..5 built (injector covered slot 1 below the bound); tick 6+ waits for the REAL client.
            Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, emitted.ToArray());
            Assert.Equal(5, builder.EmittedThrough);

            // The rejoined client's own submission for tick 6 completes it — the seam is exact.
            int len6 = TickCommandPacket.Write(p1Buf, 6, Faction.Player2, Array.Empty<UnitOrder>(), 0);
            builder.Submit(1, p1Buf, len6, out _);
            Assert.True(builder.TryBuild(6, out _, out _));
        }
    }
}
