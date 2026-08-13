#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core; // Faction

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 15-1 (FR-79 / DW-2) — the Godot-free server-side REJOIN state machine: the thaw dual of
    /// <see cref="DropCoordinator"/>. One rejoin in flight at a time. The <c>DedicatedServer</c> node is a thin
    /// adapter (transport packets in, reliable sends out); every decision lives here so it is Tier-1 testable.
    ///
    /// <para><b>The flow (spec-15-1 D-4).</b>
    /// <list type="number">
    ///   <item><b>Request:</b> a reconnecting client presents its <see cref="RejoinTokens"/> token on its OLD slot
    ///     (transport-authoritative — lowest-free-slot allocation puts it back there in a full match; a mismatch is
    ///     refused, the client just reconnects again). Verified + frozen + tail-serviceable ⇒ RejoinAccept, and the
    ///     DONOR (lowest connected un-frozen player) is asked for a between-ticks snapshot at T &gt; the current
    ///     frontier (D-1/D-3).</item>
    ///   <item><b>Snapshot:</b> the donor's <c>SnapshotChunk</c>s are RELAYED verbatim to the rejoiner (the server
    ///     never buffers the snapshot; determinism makes any peer's capture byte-identical, and the post-resume
    ///     checksum window re-proves it end-to-end).</item>
    ///   <item><b>Tail:</b> stop-and-wait <c>TailFrames</c> batches from the <see cref="MergedTickLog"/>, from
    ///     snapshotTick+1. When the rejoiner ACKs past the emitted frontier it is caught up (from there, live
    ///     broadcasts land inside its arrival ring — the batch loop never leaves a gap).</item>
    ///   <item><b>Resume:</b> ResumeDirective(faction, R = frontier + <see cref="RESUME_MARGIN"/>, delay) to ALL
    ///     peers, ACKed by every connected player INCLUDING the rejoiner (its ACK doubles as "at the boundary").
    ///     The injection bound (R + delay) is scheduled at ISSUE — <see cref="DropController.ScheduleThaw"/> — so
    ///     tick ownership is race-free; commit (all-ACK) triggers the caller's quorum re-admit + delay
    ///     reactivation, and <see cref="DropController.FinalizeThaws"/> completes the unfreeze once the merged
    ///     stream reaches the bound.</item>
    /// </list></para>
    ///
    /// <para><b>D-11 (the DW-410 posture, designed in):</b> every phase is deadline-bounded by the caller-pumped
    /// freeze clock. A hung snapshot/tail phase ABORTS (refuse + unwind — the slot simply stays frozen, exactly as
    /// before the attempt). A hung resume quorum splits: if the REJOINER ACKed, hung survivors are force-committed
    /// over (reliable transport — the DropController argument verbatim); if the rejoiner itself never ACKed, the
    /// resume aborts and the thaw is cancelled (the injector resumes covering the slot, no harm done).</para>
    /// </summary>
    public sealed class RejoinCoordinator
    {
        /// <summary>Per-phase deadline in freeze-clock ticks (~10 s at 30 Hz — the DW-410 bound).</summary>
        public const int PHASE_TIMEOUT_TICKS = 300;

        /// <summary>How far past the emitted frontier the resume tick is set (ticks). Deliberately below the
        /// client arrival ring's 16-tick window so every live broadcast from "caught up" on lands inside it.</summary>
        public const int RESUME_MARGIN = 8;

        /// <summary>Tail batch bounds: frames per <c>TailFrames</c> packet and a byte ceiling (whichever hits
        /// first). ~48 KB keeps a batch one comfortable reliable packet even at busy-tick frame sizes.</summary>
        public const int TAIL_BATCH_FRAMES = 512;
        public const int TAIL_BATCH_BYTES  = 48 * 1024;

        private enum Phase { Idle, AwaitingSnapshot, Tailing, AwaitingResumeAcks }

        // ── Injected seams (all Godot-free) ───────────────────────────────────
        private readonly RejoinTokens   _tokens;
        private readonly MergedTickLog  _log;
        private readonly DropController _controller;
        private readonly Faction[]      _slotFaction;
        private readonly int            _expectedPlayers;
        private readonly Func<long>     _emittedThrough;
        private readonly Func<int>      _currentDelay;
        private readonly Func<int, bool> _isSlotConnected;
        private readonly Action<int, byte[]> _sendTo;     // (slot, packet) reliable
        private readonly Action<byte[]>      _broadcast;  // reliable to everyone
        private readonly Action<int, uint>   _onResumeCommitted; // (slot, resumeAtTick) → quorum re-admit + delay reactivate
        private readonly Action<int, string>? _onLog;     // (slot, message) server-console diagnostics

        // ── In-flight state ───────────────────────────────────────────────────
        private Phase _phase = Phase.Idle;
        private int   _rejoinSlot = -1;
        private int   _donorSlot  = -1;
        private byte  _requestId;
        private uint  _snapshotTick;
        private int   _chunksSeen;
        private int   _chunksTotal = -1;
        private long  _nextTailTick = -1;
        private uint  _resumeAtTick;
        private byte  _resumeDelay;
        private readonly bool[] _resumeAcked;
        private bool  _deadlineArmed;
        private uint  _deadlineBase;
        private readonly List<byte[]> _tailScratch = new();

        public RejoinCoordinator(RejoinTokens tokens, MergedTickLog log, DropController controller,
                                 Faction[] slotFaction, int expectedPlayers,
                                 Func<long> emittedThrough, Func<int> currentDelay, Func<int, bool> isSlotConnected,
                                 Action<int, byte[]> sendTo, Action<byte[]> broadcast,
                                 Action<int, uint> onResumeCommitted, Action<int, string>? onLog = null)
        {
            _tokens = tokens; _log = log; _controller = controller;
            _slotFaction = slotFaction; _expectedPlayers = expectedPlayers;
            _emittedThrough = emittedThrough; _currentDelay = currentDelay; _isSlotConnected = isSlotConnected;
            _sendTo = sendTo; _broadcast = broadcast;
            _onResumeCommitted = onResumeCommitted; _onLog = onLog;
            _resumeAcked = new bool[expectedPlayers];
        }

        /// <summary>True while a rejoin is in flight (a second request is refused Busy).</summary>
        public bool Active => _phase != Phase.Idle;

        /// <summary>The in-flight rejoiner's slot (−1 when idle).</summary>
        public int RejoinSlot => _phase == Phase.Idle ? -1 : _rejoinSlot;

        /// <summary>Story 15-1 — while a rejoin is in flight the server withholds NEW delay directives (the resume
        /// seam is computed from the delay at directive issue; a mid-rejoin delay change would split the bound).</summary>
        public bool BlocksDelayDirectives => Active;

        // ── Request ───────────────────────────────────────────────────────────

        /// <summary>
        /// Handle a <c>RejoinRequest</c> from <paramref name="slot"/> (transport-authoritative). Sends the
        /// accept/refuse itself; on accept, asks the donor for the snapshot and arms the phase deadline.
        /// The caller gates on match state (InGame) — this machine assumes a live match.
        /// </summary>
        public void OnRejoinRequest(int slot, ulong token)
        {
            if (Active) { Refuse(slot, RejoinRefuseReason.Busy); return; }
            if ((uint)slot >= (uint)_expectedPlayers || !_tokens.Verify(slot, token))
            { Refuse(slot, RejoinRefuseReason.BadToken); return; }
            if (!_controller.IsFrozen(slot) || _controller.ThawScheduled(slot))
            { Refuse(slot, RejoinRefuseReason.NotFrozen); return; }
            if (!_log.Armed || _log.FirstRetainedTick < 0)
            { Refuse(slot, RejoinRefuseReason.TailUnavailable); return; }

            // D-1: donor = lowest connected, un-frozen player slot other than the rejoiner.
            _donorSlot = -1;
            for (int s = 0; s < _expectedPlayers; s++)
                if (s != slot && _isSlotConnected(s) && !_controller.IsFrozen(s)) { _donorSlot = s; break; }
            if (_donorSlot < 0) { Refuse(slot, RejoinRefuseReason.TailUnavailable); return; }

            _phase        = Phase.AwaitingSnapshot;
            _rejoinSlot   = slot;
            _requestId++;
            _chunksSeen   = 0;
            _chunksTotal  = -1;
            _snapshotTick = 0;
            _nextTailTick = -1;
            _deadlineArmed = false;

            long frontier = _emittedThrough();
            _sendTo(slot, TickCommandPacket.MakeRejoinAccept(
                (byte)_slotFaction[slot], (byte)_currentDelay(), frontier < 0 ? 0u : (uint)frontier));
            _sendTo(_donorSlot, TickCommandPacket.MakeSnapshotRequest(_requestId, frontier < 0 ? 0u : (uint)frontier));
            _onLog?.Invoke(slot, $"rejoin ACCEPTED ({_slotFaction[slot]}) — snapshot requested from donor slot {_donorSlot}.");
        }

        // ── Snapshot relay ────────────────────────────────────────────────────

        /// <summary>
        /// Handle a donor <c>SnapshotChunk</c>: validate source/requestId, RELAY verbatim to the rejoiner, and on
        /// the last chunk begin the tail. The reliable channel delivers in order, so chunk bookkeeping is a count.
        /// </summary>
        public void OnSnapshotChunk(int fromSlot, byte[] data, int len)
        {
            if (_phase != Phase.AwaitingSnapshot || fromSlot != _donorSlot) return;
            if (!TickCommandPacket.TryReadSnapshotChunk(data, len, out byte reqId, out ushort seq, out ushort total,
                                                        out uint snapTick, out _, out _)) return;
            if (reqId != _requestId) return; // a stale upload from an aborted earlier attempt

            if (_chunksTotal < 0)
            {
                _chunksTotal  = total;
                _snapshotTick = snapTick;
                // D-2: the tail must reach back to snapshotTick+1, or the rejoiner cannot bridge snapshot → live.
                // The snapshot was captured AFTER retention armed, so this only fails if the log hit its byte
                // budget (away too long) — fail-closed, refuse.
                if (_log.FirstRetainedTick > (long)snapTick + 1)
                { Abort(RejoinRefuseReason.TailUnavailable, "tail does not reach the snapshot tick"); return; }
            }

            // Relay the exact bytes (the server never reassembles the snapshot).
            byte[] relay = len == data.Length ? data : data[..len];
            _sendTo(_rejoinSlot, relay);
            _chunksSeen++;
            Rearm(); // progress → fresh deadline

            if (_chunksSeen >= _chunksTotal)
            {
                _phase        = Phase.Tailing;
                _nextTailTick = (long)_snapshotTick + 1;
                _onLog?.Invoke(_rejoinSlot, $"snapshot relayed ({_chunksSeen} chunks, tick {_snapshotTick}) — tail begins at {_nextTailTick}.");
                SendTailBatch();
            }
        }

        // ── Tail ──────────────────────────────────────────────────────────────

        /// <summary>Handle the rejoiner's <c>TailAck</c>: advance the tail cursor; send the next batch, or — once
        /// the rejoiner has ACKed past the emitted frontier — issue the ResumeDirective (D-4).</summary>
        public void OnTailAck(int fromSlot, uint nextNeededTick)
        {
            if (_phase != Phase.Tailing || fromSlot != _rejoinSlot) return;
            if ((long)nextNeededTick < _nextTailTick) return; // duplicate/stale ack
            _nextTailTick = nextNeededTick;
            Rearm();

            if (_nextTailTick <= _emittedThrough()) { SendTailBatch(); return; }

            // Caught up — every emitted tick is applied; from here live broadcasts land inside its arrival ring.
            IssueResumeDirective();
        }

        private void SendTailBatch()
        {
            if (!_log.TryCopyRange(_nextTailTick, _tailScratch))
            { Abort(RejoinRefuseReason.TailUnavailable, $"tail range {_nextTailTick}.. unavailable"); return; }

            int count = 0, bytes = 0;
            while (count < _tailScratch.Count && count < TAIL_BATCH_FRAMES && bytes < TAIL_BATCH_BYTES)
            { bytes += 2 + _tailScratch[count].Length; count++; }

            // Zero frames = the log has nothing newer yet (the rejoiner drained it faster than the match emits).
            // The rejoiner re-ACKs on receipt, and by then either new frames exist or it has passed the frontier
            // and the resume issues — from OnTailAck, never from here (one decision point).
            _sendTo(_rejoinSlot, TickCommandPacket.MakeTailFrames((uint)_nextTailTick, _tailScratch, 0, count));
        }

        // ── Resume ────────────────────────────────────────────────────────────

        private void IssueResumeDirective()
        {
            long frontier = _emittedThrough();
            _resumeAtTick = (uint)(frontier < 0 ? RESUME_MARGIN : frontier + RESUME_MARGIN);
            _resumeDelay  = (byte)_currentDelay();
            uint bound    = _resumeAtTick + _resumeDelay;

            if (!_controller.ScheduleThaw(_rejoinSlot, bound))
            { Abort(RejoinRefuseReason.NotFrozen, "thaw could not be scheduled"); return; }

            _phase = Phase.AwaitingResumeAcks;
            Array.Clear(_resumeAcked, 0, _expectedPlayers);
            _deadlineArmed = false;
            Rearm();
            _broadcast(TickCommandPacket.MakeResumeDirective((byte)_slotFaction[_rejoinSlot], _resumeAtTick, _resumeDelay));
            _onLog?.Invoke(_rejoinSlot, $"ResumeDirective issued — {_slotFaction[_rejoinSlot]} resumes at tick " +
                                        $"{_resumeAtTick} (injection bound {bound}); awaiting all-player ACKs.");
        }

        /// <summary>
        /// Handle a <c>ResumeAck</c> from any player (survivors + the rejoiner — the rejoiner's ACK doubles as its
        /// "caught up to the boundary" signal). On all-connected-player ACK: confirm the thaw and fire the commit
        /// seam (checksum re-admit + delay reactivation live there). Finalization completes in
        /// <see cref="DropController.FinalizeThaws"/> once the merged stream reaches the bound.
        /// </summary>
        public void OnResumeAck(int fromSlot, byte faction, uint resumeAtTick)
        {
            if (_phase != Phase.AwaitingResumeAcks) return;
            if ((uint)fromSlot >= (uint)_expectedPlayers) return;
            if (faction != (byte)_slotFaction[_rejoinSlot] || resumeAtTick != _resumeAtTick) return; // stale/mismatched
            _resumeAcked[fromSlot] = true;
            if (AllResumeAcked()) CommitResume("all players ACKed");
        }

        private bool AllResumeAcked()
        {
            for (int s = 0; s < _expectedPlayers; s++)
            {
                if (s == _rejoinSlot) { if (!_resumeAcked[s]) return false; continue; }
                // A survivor must ACK iff it is connected and not itself frozen (a frozen slot's client is absent).
                if (_isSlotConnected(s) && !_controller.IsFrozen(s) && !_resumeAcked[s]) return false;
            }
            return true;
        }

        private void CommitResume(string how)
        {
            int slot = _rejoinSlot;
            uint atTick = _resumeAtTick;
            _controller.ConfirmThaw(slot);
            ResetToIdle();
            _onLog?.Invoke(slot, $"resume COMMITTED at tick {atTick} ({how}) — quorum re-admit + delay reactivation; " +
                                 "the unfreeze finalizes when the merged stream reaches the bound.");
            _onResumeCommitted(slot, atTick);
        }

        // ── D-11: deadlines + disconnect unwind ───────────────────────────────

        /// <summary>
        /// Pump the per-phase deadline from the caller's freeze clock (the DW-410 discipline; arm-on-first-call,
        /// modular uint arithmetic). Snapshot/tail hangs ABORT the attempt; a hung resume quorum force-commits
        /// over hung SURVIVORS (reliable transport — their ACK is bookkeeping) but never over a silent REJOINER,
        /// whose absence aborts instead (its ACK is the "caught up" signal; committing without it would hand the
        /// merged stream to a client that may never submit).
        /// </summary>
        public bool CheckTimeout(uint currentTick)
        {
            if (_phase == Phase.Idle) return false;
            if (!_deadlineArmed) { _deadlineArmed = true; _deadlineBase = currentTick; return false; }
            if (unchecked(currentTick - _deadlineBase) < (uint)PHASE_TIMEOUT_TICKS) return false;

            if (_phase == Phase.AwaitingResumeAcks && _resumeAcked[_rejoinSlot])
            {
                CommitResume("resume-ACK timeout — force-committed over hung survivor(s)");
                return true;
            }
            Abort(RejoinRefuseReason.TailUnavailable, $"phase {_phase} timed out");
            return true;
        }

        /// <summary>
        /// React to a mid-rejoin disconnect. The REJOINER or the DONOR dropping aborts the attempt (the slot simply
        /// stays frozen; a later reconnect starts fresh). Any other survivor dropping mid-resume is pruned from the
        /// ACK set (the DW-409 discipline) — the remaining ACKs may now complete the quorum.
        /// </summary>
        public void OnSlotDisconnected(int slot)
        {
            if (_phase == Phase.Idle) return;
            if (slot == _rejoinSlot) { Abort(null, "rejoiner disconnected mid-rejoin"); return; }
            if (slot == _donorSlot && _phase == Phase.AwaitingSnapshot)
            { Abort(RejoinRefuseReason.TailUnavailable, "snapshot donor disconnected"); return; }
            if (_phase == Phase.AwaitingResumeAcks && (uint)slot < (uint)_expectedPlayers)
            {
                // The freeze path (DropCoordinator) handles the survivor's own drop; here only the ACK set shrinks.
                _resumeAcked[slot] = false; // it can never ACK now; AllResumeAcked skips disconnected slots
                if (AllResumeAcked()) CommitResume("last outstanding ACK belonged to a disconnected survivor");
            }
        }

        private void Refuse(int slot, RejoinRefuseReason reason)
        {
            _sendTo(slot, TickCommandPacket.MakeRejoinRefuse(reason));
            _onLog?.Invoke(slot, $"rejoin REFUSED ({reason}).");
        }

        private void Abort(RejoinRefuseReason? refuseRejoiner, string why)
        {
            int slot = _rejoinSlot;
            // An issued-but-uncommitted thaw is cancelled — the injector resumes covering the slot's backlog.
            if (_phase == Phase.AwaitingResumeAcks) _controller.CancelThaw(slot);
            if (refuseRejoiner != null && _isSlotConnected(slot))
                _sendTo(slot, TickCommandPacket.MakeRejoinRefuse(refuseRejoiner.Value));
            ResetToIdle();
            _onLog?.Invoke(slot, $"rejoin ABORTED — {why}. Slot stays frozen; a later reconnect starts fresh.");
        }

        private void ResetToIdle()
        {
            _phase = Phase.Idle;
            _rejoinSlot = -1;
            _donorSlot = -1;
            _chunksTotal = -1;
            _deadlineArmed = false;
            _tailScratch.Clear();
        }

        private void Rearm() { _deadlineArmed = false; }
    }
}
