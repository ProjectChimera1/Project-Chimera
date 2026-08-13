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
    /// Story 15-1 — the server-side <see cref="RejoinCoordinator"/> and the client-side <see cref="RejoinClient"/>
    /// interlocking over scripted seams: the full request → accept → snapshot relay → tail → resume-quorum flow,
    /// every refusal arm, the D-11 deadline split (force-commit over hung survivors, abort on a silent rejoiner),
    /// and the disconnect unwinds. No transport, no Godot, no sim — the sim-level catch-up determinism is
    /// <c>RejoinCatchUpHarnessTests</c>' job (DW-879, already green).
    /// </summary>
    public class RejoinCoordinatorTests
    {
        private static readonly Faction[] Factions = { Faction.Player1, Faction.Player2 };

        /// <summary>The scripted server-side world a coordinator runs against.</summary>
        private sealed class Rig
        {
            public readonly RejoinTokens Tokens = new(8);
            public readonly MergedTickLog Log = new();
            public readonly DropController Controller = new(2);
            public readonly RejoinCoordinator Coord;
            public readonly List<byte[]>[] Outbox = { new List<byte[]>(), new List<byte[]>() };
            public readonly List<(int slot, uint atTick)> Committed = new();
            public long Emitted;
            public ulong Token1;

            public Rig(uint freezeTick = 100)
            {
                // Freeze slot 1 (the eventual rejoiner) the way the drop path does, arm retention.
                Assert.True(Controller.NotifyDrop(1, freezeTick, new[] { 0 }));
                Controller.RecordAck(0, 1, freezeTick);
                Assert.True(Controller.Commit());
                Log.Arm();
                Token1 = Tokens.Mint(1);

                Coord = new RejoinCoordinator(Tokens, Log, Controller, Factions, 2,
                    () => Emitted, () => 4, _ => true,
                    (s, pkt) => Outbox[s].Add(pkt),
                    pkt => { Outbox[0].Add(pkt); Outbox[1].Add(pkt); },
                    (s, at) => Committed.Add((s, at)));
            }

            /// <summary>Append fake retained frames for [from..to] and advance the emitted frontier.</summary>
            public void Emit(uint from, uint to)
            {
                for (uint t = from; t <= to; t++)
                {
                    var frame = new byte[] { 0x14, (byte)t, (byte)(t >> 8), (byte)(t >> 16), (byte)(t >> 24), 0 };
                    Log.Append(t, frame, frame.Length);
                    Emitted = t;
                }
            }

            public byte[]? Pop(int slot, PacketType type)
            {
                for (int i = 0; i < Outbox[slot].Count; i++)
                    if (Outbox[slot][i].Length > 0 && (PacketType)Outbox[slot][i][0] == type)
                    { var p = Outbox[slot][i]; Outbox[slot].RemoveAt(i); return p; }
                return null;
            }
        }

        [Fact]
        public void FullFlow_RequestToCommittedResume_ClientAndServerInterlock()
        {
            var rig = new Rig(freezeTick: 100);
            rig.Emit(100, 150);

            // The rejoiner's client machine: its sends route straight into the coordinator (slot 1 arrival).
            byte[]? restoredBody = null;
            uint restoredTick = 0;
            var appliedFrames = new List<uint>();
            RejoinClient client = null!;
            client = new RejoinClient(
                send: pkt =>
                {
                    var t = (PacketType)pkt[0];
                    if (t == PacketType.RejoinRequest &&
                        TickCommandPacket.TryReadRejoinRequest(pkt, pkt.Length, out ulong tok))
                        rig.Coord.OnRejoinRequest(1, tok);
                    else if (t == PacketType.TailAck &&
                             TickCommandPacket.TryReadTailAck(pkt, pkt.Length, out uint next))
                        rig.Coord.OnTailAck(1, next);
                    else if (t == PacketType.ResumeAck &&
                             TickCommandPacket.TryReadResumeAck(pkt, pkt.Length, out byte f, out uint at))
                        rig.Coord.OnResumeAck(1, f, at);
                },
                onSnapshotReady: (body, tick) => { restoredBody = body; restoredTick = tick; return true; },
                applyFrame: (tick, _, _, _) => appliedFrames.Add(tick));

            client.Begin(rig.Token1);

            // Server accepted and asked the donor. The rejoiner sees the accept.
            byte[]? accept = rig.Pop(1, PacketType.RejoinAccept);
            Assert.NotNull(accept);
            Assert.True(client.TryHandlePacket(accept!, accept!.Length));
            Assert.Equal(RejoinClient.Phase.AwaitingSnapshot, client.State);

            byte[]? snapReq = rig.Pop(0, PacketType.SnapshotRequest);
            Assert.NotNull(snapReq);
            Assert.True(TickCommandPacket.TryReadSnapshotRequest(snapReq!, snapReq!.Length,
                out byte reqId, out uint minTick));
            Assert.Equal(150u, minTick);

            // The match plays on while the donor captures at a boundary past the frontier (D-3)...
            rig.Emit(151, 165);
            uint snapTick = 160;
            var body = new byte[70_000]; // multi-chunk
            new Random(11).NextBytes(body);

            // Donor uploads; the server relays each chunk verbatim; the client machine consumes the relays and
            // (on completion) restores + opens the tail loop. Pump until the queues drain.
            foreach (byte[] chunk in SnapshotTransfer.Chunk(reqId, snapTick, body))
                rig.Coord.OnSnapshotChunk(0, chunk, chunk.Length);
            Pump(rig, client);

            Assert.Equal(body, restoredBody);
            Assert.Equal(snapTick, restoredTick);
            Assert.Equal(RejoinClient.Phase.Resumed, client.State);

            // The tail delivered exactly snapshotTick+1 .. frontier, in order, no gaps.
            Assert.Equal(161u, appliedFrames[0]);
            Assert.Equal(165u, appliedFrames[^1]);
            Assert.Equal(5, appliedFrames.Count);
            Assert.Equal(165, client.AppliedThrough);

            // ResumeDirective went out at R = frontier + margin; the injection bound is R + delay (seam-exact).
            uint expectedR = 165u + RejoinCoordinator.RESUME_MARGIN;
            Assert.Equal(expectedR, client.ResumeAtTick);
            Assert.Equal(expectedR + 4, client.FirstOwnedTick);
            Assert.Equal(expectedR + 4, rig.Controller.ThawBound(1));

            // The survivor ACKs (the client's own ACK already routed in Pump) → commit fires the re-admit seam.
            byte[]? dir = rig.Pop(0, PacketType.ResumeDirective);
            Assert.NotNull(dir);
            Assert.True(TickCommandPacket.TryReadResumeDirective(dir!, dir!.Length, out byte df, out uint dAt, out _));
            rig.Coord.OnResumeAck(0, df, dAt);

            Assert.Equal(new[] { (1, expectedR) }, rig.Committed.ToArray());
            Assert.True(rig.Controller.ThawCommitted(1));
            Assert.False(rig.Coord.Active);

            // Finalization completes once the merged stream reaches the bound; the slot is fully live again.
            var thawed = new List<int>();
            Assert.Equal(0, rig.Controller.FinalizeThaws(expectedR + 4 - 2, thawed));
            Assert.Equal(1, rig.Controller.FinalizeThaws(expectedR + 4 - 1, thawed));
            Assert.False(rig.Controller.IsFrozen(1));
        }

        /// <summary>Route relayed/tail packets from the rejoiner's outbox into the client machine until quiet
        /// (the client's sends re-enter the coordinator synchronously via its send seam).</summary>
        private static void Pump(Rig rig, RejoinClient client)
        {
            bool moved = true;
            while (moved)
            {
                moved = false;
                for (int i = 0; i < rig.Outbox[1].Count;)
                {
                    byte[] pkt = rig.Outbox[1][i];
                    rig.Outbox[1].RemoveAt(i);
                    client.TryHandlePacket(pkt, pkt.Length);
                    moved = true;
                }
            }
        }

        [Fact]
        public void Refusals_BadToken_Busy_NotFrozen_NoTail()
        {
            var rig = new Rig();
            rig.Emit(100, 110);

            rig.Coord.OnRejoinRequest(1, rig.Token1 ^ 1UL); // wrong token
            AssertRefused(rig, RejoinRefuseReason.BadToken);

            rig.Coord.OnRejoinRequest(0, rig.Tokens.Mint(0)); // slot 0 is not frozen
            byte[]? r0 = rig.Pop(0, PacketType.RejoinRefuse);
            Assert.NotNull(r0);
            Assert.True(TickCommandPacket.TryReadRejoinRefuse(r0!, r0!.Length, out RejoinRefuseReason r0r));
            Assert.Equal(RejoinRefuseReason.NotFrozen, r0r);

            rig.Coord.OnRejoinRequest(1, rig.Token1); // valid — occupies the machine
            Assert.True(rig.Coord.Active);
            rig.Coord.OnRejoinRequest(1, rig.Token1); // second concurrent attempt
            AssertRefused(rig, RejoinRefuseReason.Busy);
        }

        [Fact]
        public void TailPastBudget_RefusesTailUnavailable()
        {
            var rig = new Rig();
            rig.Log.DisarmAndClear(); // simulate the byte-budget disarm ("away too long")
            rig.Coord.OnRejoinRequest(1, rig.Token1);
            AssertRefused(rig, RejoinRefuseReason.TailUnavailable);
            Assert.False(rig.Coord.Active);
        }

        [Fact]
        public void SnapshotPhaseTimeout_Aborts_AndLeavesSlotFrozen()
        {
            var rig = new Rig();
            rig.Emit(100, 120);
            rig.Coord.OnRejoinRequest(1, rig.Token1);
            Assert.True(rig.Coord.Active);

            Assert.False(rig.Coord.CheckTimeout(1000));  // arms the deadline
            Assert.True(rig.Coord.CheckTimeout(1000 + RejoinCoordinator.PHASE_TIMEOUT_TICKS));
            Assert.False(rig.Coord.Active);
            Assert.True(rig.Controller.IsFrozen(1));     // unwound clean — the slot just stays frozen
            Assert.False(rig.Controller.ThawScheduled(1));
            AssertRefused(rig, RejoinRefuseReason.TailUnavailable);
        }

        [Fact]
        public void ResumeTimeout_ForceCommitsOverHungSurvivor_ButAbortsOnSilentRejoiner()
        {
            // Arm one rig to the resume phase where only the REJOINER ACKed → force-commit.
            var rig = ArmToResumePhase(out uint r1);
            rig.Coord.OnResumeAck(1, (byte)Faction.Player2, r1); // the rejoiner is at the boundary
            Assert.False(rig.Coord.CheckTimeout(5000));
            Assert.True(rig.Coord.CheckTimeout(5000 + RejoinCoordinator.PHASE_TIMEOUT_TICKS));
            Assert.Single(rig.Committed);
            Assert.True(rig.Controller.ThawCommitted(1));

            // And a rig where the REJOINER never ACKed → abort + thaw cancelled (never commit a silent client).
            var rig2 = ArmToResumePhase(out _);
            rig2.Coord.OnResumeAck(0, (byte)Faction.Player2, rig2.Controller.ThawBound(1) is long b && b != long.MaxValue
                ? (uint)(b - 4) : 0u); // survivor ACKed
            Assert.False(rig2.Coord.CheckTimeout(9000));
            Assert.True(rig2.Coord.CheckTimeout(9000 + RejoinCoordinator.PHASE_TIMEOUT_TICKS));
            Assert.Empty(rig2.Committed);
            Assert.False(rig2.Controller.ThawScheduled(1)); // cancelled — injector owns the slot again
            Assert.True(rig2.Controller.IsFrozen(1));
        }

        [Fact]
        public void RejoinerDisconnectMidTail_Aborts_WithoutRefusePacket()
        {
            var rig = new Rig();
            rig.Emit(100, 120);
            rig.Coord.OnRejoinRequest(1, rig.Token1);
            Assert.True(rig.Coord.Active);
            rig.Coord.OnSlotDisconnected(1);
            Assert.False(rig.Coord.Active);
            Assert.True(rig.Controller.IsFrozen(1));
        }

        /// <summary>Drive a rig through snapshot + tail so a ResumeDirective is pending; returns its R.</summary>
        private static Rig ArmToResumePhase(out uint resumeAt)
        {
            var rig = new Rig();
            rig.Emit(100, 130);
            rig.Coord.OnRejoinRequest(1, rig.Token1);
            byte[]? snapReq = rig.Pop(0, PacketType.SnapshotRequest);
            Assert.NotNull(snapReq);
            TickCommandPacket.TryReadSnapshotRequest(snapReq!, snapReq!.Length, out byte reqId, out _);
            rig.Emit(131, 140);
            foreach (byte[] chunk in SnapshotTransfer.Chunk(reqId, 135, new byte[100]))
                rig.Coord.OnSnapshotChunk(0, chunk, chunk.Length);
            rig.Coord.OnTailAck(1, 141); // past the frontier (140) → directive issues
            resumeAt = 140u + RejoinCoordinator.RESUME_MARGIN;
            Assert.True(rig.Controller.ThawScheduled(1));
            return rig;
        }

        private static void AssertRefused(Rig rig, RejoinRefuseReason expected)
        {
            byte[]? p = rig.Pop(1, PacketType.RejoinRefuse);
            Assert.NotNull(p);
            Assert.True(TickCommandPacket.TryReadRejoinRefuse(p!, p!.Length, out RejoinRefuseReason r));
            Assert.Equal(expected, r);
        }
    }
}
