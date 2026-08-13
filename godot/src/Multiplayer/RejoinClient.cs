#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 15-1 — snapshot transfer helpers shared by the DONOR side (chunking a captured
    /// <c>SaveGameFile</c> body for upload) and the REJOINER side (reassembling the relayed chunks).
    /// Godot-free; the byte body is opaque here (SaveGameFile's own magic/version/integrity checks run at
    /// restore — a corrupt transfer fails closed there, and D-1's post-resume checksum window re-proves the
    /// content end-to-end).
    /// </summary>
    public static class SnapshotTransfer
    {
        /// <summary>Chunk a captured snapshot body into <c>SnapshotChunk</c> packets (donor side).</summary>
        public static List<byte[]> Chunk(byte requestId, uint snapshotTick, byte[] body)
        {
            int total = Math.Max(1, (body.Length + TickCommandPacket.SNAPSHOT_CHUNK_BYTES - 1)
                                    / TickCommandPacket.SNAPSHOT_CHUNK_BYTES);
            if (total > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(body), "snapshot too large to chunk");
            var packets = new List<byte[]>(total);
            for (int i = 0; i < total; i++)
            {
                int off = i * TickCommandPacket.SNAPSHOT_CHUNK_BYTES;
                int len = Math.Min(TickCommandPacket.SNAPSHOT_CHUNK_BYTES, body.Length - off);
                packets.Add(TickCommandPacket.MakeSnapshotChunk(requestId, (ushort)i, (ushort)total,
                                                                snapshotTick, body, off, len));
            }
            return packets;
        }

        /// <summary>Reassembles relayed <c>SnapshotChunk</c>s (rejoiner side). The reliable channel delivers in
        /// order; a seq regression / requestId change resets fail-closed (a fresh attempt supersedes).</summary>
        public sealed class Assembler
        {
            private readonly List<byte> _body = new();
            private byte _requestId;
            private int  _expectedSeq;
            private int  _total = -1;

            /// <summary>The snapshot's capture tick (valid once the first chunk arrived).</summary>
            public uint SnapshotTick { get; private set; }

            /// <summary>Feed one chunk. True when the snapshot is COMPLETE — read <see cref="ToArray"/> then.</summary>
            public bool Feed(byte[] data, int len)
            {
                if (!TickCommandPacket.TryReadSnapshotChunk(data, len, out byte reqId, out ushort seq,
                        out ushort total, out uint tick, out int payloadOff, out int payloadLen)) return false;
                if (_total < 0 || reqId != _requestId)
                {
                    _body.Clear(); _requestId = reqId; _expectedSeq = 0; _total = total; SnapshotTick = tick;
                }
                if (seq != _expectedSeq || total != _total) { Reset(); return false; } // out-of-order ⇒ fail-closed
                for (int i = 0; i < payloadLen; i++) _body.Add(data[payloadOff + i]);
                _expectedSeq++;
                return _expectedSeq >= _total;
            }

            /// <summary>The reassembled snapshot body.</summary>
            public byte[] ToArray() => _body.ToArray();

            public void Reset() { _body.Clear(); _total = -1; _expectedSeq = 0; }
        }
    }

    /// <summary>
    /// Story 15-1 — the Godot-free REJOINER state machine: the client half of <c>Server.RejoinCoordinator</c>'s
    /// protocol. The Godot adapter (MainScene / LockstepManager — the 15-1b in-engine half) owns the transport,
    /// the sim restore, and the frame application; this class owns the protocol so it is Tier-1 testable:
    /// request → accept/refuse → snapshot assembly → tail ACK discipline → resume.
    ///
    /// <para><b>The adapter's duties via the injected seams:</b> <c>send</c> puts a packet on the reliable
    /// channel; <c>onSnapshotReady</c> restores the body into a FRESH SimulationHost (never the SP-load path —
    /// D-7: re-establish <c>OnlineAiPlan=None</c>) and returns true on success; <c>applyFrame</c> applies one
    /// retained merged frame (MergedTickApplier + StepOnce, headless, fast); after <see cref="Resumed"/> the
    /// adapter enters normal lockstep with <see cref="CurrentDelay"/>, consuming live broadcasts from the
    /// arrival ring, and submits its OWN commands only for ticks &gt;= <see cref="FirstOwnedTick"/> (below it
    /// the server's injector owns the stream — a real submission there would race first-wins Submit).</para>
    /// </summary>
    public sealed class RejoinClient
    {
        public enum Phase { Idle, Requested, AwaitingSnapshot, Tailing, Resumed, Refused }

        private readonly Action<byte[]> _send;
        private readonly Func<byte[], uint, bool> _onSnapshotReady;    // (body, snapshotTick) → restored ok
        private readonly Action<uint, byte[], int, int> _applyFrame;   // (tick, buf, offset, length)
        private readonly SnapshotTransfer.Assembler _assembler = new();

        public Phase State { get; private set; } = Phase.Idle;

        /// <summary>Assigned faction from the accept (byte — the adapter casts to <c>Core.Faction</c>).</summary>
        public byte FactionByte { get; private set; }

        /// <summary>The committed input delay handed over in the accept/resume (D-8).</summary>
        public int CurrentDelay { get; private set; }

        /// <summary>The resume boundary R from the ResumeDirective (exec resumes lockstep pacing here).</summary>
        public uint ResumeAtTick { get; private set; }

        /// <summary>The first tick this client's OWN submissions cover: R + delay. Below it the server's
        /// injector owns the slot's stream — the adapter must not submit there.</summary>
        public uint FirstOwnedTick => ResumeAtTick + (uint)CurrentDelay;

        /// <summary>One past the newest tail tick applied (the next tick needed) — the TailAck cursor.</summary>
        public long AppliedThrough { get; private set; } = -1;

        /// <summary>Why the server refused (valid in <see cref="Phase.Refused"/>).</summary>
        public RejoinRefuseReason RefuseReason { get; private set; }

        public RejoinClient(Action<byte[]> send, Func<byte[], uint, bool> onSnapshotReady,
                            Action<uint, byte[], int, int> applyFrame)
        {
            _send = send;
            _onSnapshotReady = onSnapshotReady;
            _applyFrame = applyFrame;
        }

        /// <summary>Present <paramref name="token"/> (from the StartGame-time <c>RejoinToken</c> packet, held by
        /// the adapter across the reconnect) to claim the old slot.</summary>
        public void Begin(ulong token)
        {
            State = Phase.Requested;
            _assembler.Reset();
            AppliedThrough = -1;
            _send(TickCommandPacket.MakeRejoinRequest(token));
        }

        /// <summary>
        /// Feed one inbound packet. Returns true when this machine consumed it (the adapter routes rejoin-family
        /// types here; everything else — live merged ticks, pings — stays on the normal path).
        /// </summary>
        public bool TryHandlePacket(byte[] data, int len)
        {
            if (len < 1 || State == Phase.Idle || State == Phase.Refused) return false;
            switch ((PacketType)data[0])
            {
                case PacketType.RejoinAccept:
                    if (State != Phase.Requested ||
                        !TickCommandPacket.TryReadRejoinAccept(data, len, out byte f, out byte d, out _)) return false;
                    FactionByte = f;
                    CurrentDelay = d;
                    State = Phase.AwaitingSnapshot;
                    return true;

                case PacketType.RejoinRefuse:
                    if (!TickCommandPacket.TryReadRejoinRefuse(data, len, out RejoinRefuseReason reason)) return false;
                    RefuseReason = reason;
                    State = Phase.Refused;
                    return true;

                case PacketType.SnapshotChunk:
                    if (State != Phase.AwaitingSnapshot) return false;
                    if (_assembler.Feed(data, len))
                    {
                        if (!_onSnapshotReady(_assembler.ToArray(), _assembler.SnapshotTick))
                        { RefuseReason = RejoinRefuseReason.TailUnavailable; State = Phase.Refused; return true; }
                        AppliedThrough = _assembler.SnapshotTick; // the snapshot IS state-at-tick
                        State = Phase.Tailing;
                        _send(TickCommandPacket.MakeTailAck((uint)(AppliedThrough + 1)));
                    }
                    return true;

                case PacketType.TailFrames:
                    if (State != Phase.Tailing) return false;
                    // Apply every frame in order; a malformed batch fails closed (refused — never a partial apply
                    // silently treated as success, which would fast-forward into a desync).
                    if (!TickCommandPacket.TryReadTailFrames(data, len, ApplyOneFrame))
                    { RefuseReason = RejoinRefuseReason.TailUnavailable; State = Phase.Refused; return true; }
                    _send(TickCommandPacket.MakeTailAck((uint)(AppliedThrough + 1)));
                    return true;

                case PacketType.ResumeDirective:
                    if (State != Phase.Tailing ||
                        !TickCommandPacket.TryReadResumeDirective(data, len, out byte rf, out uint at, out byte rd))
                        return false;
                    ResumeAtTick = at;
                    CurrentDelay = rd;
                    State = Phase.Resumed;
                    _send(TickCommandPacket.MakeResumeAck(rf, at)); // caught up — the tail loop proved it
                    return true;

                default:
                    return false;
            }
        }

        private void ApplyOneFrame(uint tick, byte[] buf, int off, int flen)
        {
            if (tick != AppliedThrough + 1) return; // duplicate/regressed frame (stop-and-wait makes gaps impossible)
            _applyFrame(tick, buf, off, flen);
            AppliedThrough = tick;
        }
    }
}
