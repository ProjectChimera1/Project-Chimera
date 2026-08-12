#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.6 (the Godot-free server freeze authority) — the dedicated server's ACK-gated drop state machine.
    /// Lives under <c>src/Multiplayer/Server/**</c> so it compiles into the Tier-1 assembly (and the determinism
    /// analyzer) like <see cref="MergedTickBuilder"/> / <see cref="DelayController"/>; the Godot
    /// <c>DedicatedServer</c> node is a thin adapter that feeds the disconnect + ACKs in and broadcasts the
    /// directive out.
    ///
    /// Mirrors the Story 9.4 <see cref="DelayController"/> directive/ACK discipline: on an in-match disconnect the
    /// server records ONE pending <c>DropDirective(faction, applyAtTick)</c> and collects a <c>DropAck</c> from
    /// every SURVIVING player before it commits the freeze. No freeze commits until all survivors ACK the same
    /// <c>(droppedSlot, applyAtTick)</c> pair. Once committed the slot is marked frozen and the server begins
    /// injecting an empty <c>TickCommandPacket</c> for it each tick (via <see cref="FrozenSlotInjector"/>) so the
    /// merged fan-in keeps completing and the sim continues bit-identically — the dropped faction stays in the sim
    /// and in <c>SimChecksum</c>, only its command stream goes empty.
    ///
    /// Slot identity is TRANSPORT-AUTHORITATIVE throughout: the adapter passes the ENet peer→slot callback slot for
    /// the acking survivor and maps the ACK's faction byte back to the dropped slot via <c>SLOT_FACTION</c> — a
    /// packet byte is never trusted as a slot index. One drop directive is in flight at a time; a CONCURRENT drop
    /// (a second disconnect while a directive is pending — reachable at N≥3) is QUEUED by
    /// <see cref="DropCoordinator"/> and issued after the pending freeze commits, and the concurrently-dropped slot
    /// is pruned from the pending directive's survivor-ACK set via <see cref="RemoveSurvivor"/> so the fan-in never
    /// waits on an ACK that can no longer arrive (DW-409). At N=2 a second drop means no survivor remains — the
    /// adapter emits the terminal summary instead.
    ///
    /// DW-410 closes the remaining liveness hole in that machine: a survivor that stays transport-connected but hung
    /// is never pruned and never ACKs, so before this the freeze stayed pending FOREVER and every other survivor +
    /// spectator stalled. <see cref="CheckAckTimeout"/> is the tick-bounded escalation — force-commit the freeze over
    /// the survivors that DID ACK and report the ones that did not, so the caller can drop them in turn and the match
    /// keeps making progress.
    ///
    /// Everything here is tick-counted — <c>applyAtTick</c> is a sim tick number, never wall-clock — so the whole
    /// freeze path is deterministic and never folds into <c>SimChecksum</c>.
    /// </summary>
    public sealed class DropController
    {
        /// <summary>
        /// DW-410 — how many ticks of the caller's freeze clock a pending drop directive may await its survivor ACKs
        /// before <see cref="CheckAckTimeout"/> force-commits it. 300 ticks = 10 s at 30 Hz, the same bound
        /// <see cref="DelayController.ACK_TIMEOUT_TICKS"/> uses, and far beyond any sane ACK round-trip on a reliable
        /// channel — so it only fires when a still-connected survivor never ACKs at all (a survivor that genuinely
        /// disconnects is pruned sooner, and precisely, via <see cref="RemoveSurvivor"/>).
        /// </summary>
        public const int ACK_TIMEOUT_TICKS = 300;

        /// <summary>Number of player slots this controller tracks — [1, ...].</summary>
        public int Expected { get; }

        // Committed frozen state (per slot).
        private readonly bool[] _dropped;
        private readonly uint[] _frozenApplyTick;
        // Committed frozen slots in COMMIT (insertion) order — allocation-free to enumerate per pump. This happens to
        // be ascending at N=2 (≤1 drop), but at N≥3 sequential descending-slot drops would append out of order; NO
        // consumer relies on the order (FrozenSlotInjector re-stamps each slot's faction and the builder re-sorts
        // sub-bundles ascending by faction id), so insertion order is intentionally fine.
        private readonly List<int> _frozenSlots = new();

        // ── Pending directive / ACK state ─────────────────────────────────────
        private bool _pending;
        private int  _pendingDroppedSlot = -1;
        private uint _pendingApplyTick;
        private readonly bool[] _isSurvivor; // which slots must ACK the pending directive
        private readonly bool[] _acked;      // which survivors have ACKed it

        // ── DW-410 ACK-timeout state ──────────────────────────────────────────
        // The deadline clock is armed LAZILY, on the first CheckAckTimeout call after a directive goes pending,
        // rather than captured inside NotifyDrop. That is deliberate: NotifyDrop's `applyAtTick` is read from the
        // MERGED-EMISSION frontier, which is exactly the frontier the un-committed freeze has STALLED — measuring the
        // deadline against it would compare two different clocks and could never elapse. Arming from the pump's own
        // clock makes the elapsed measurement self-consistent in whatever unit the caller pumps (see CheckAckTimeout).
        private bool _timeoutArmed;
        private uint _timeoutBaseTick;

        /// <param name="expected">Number of player slots (in [1, ...]).</param>
        public DropController(int expected)
        {
            if (expected < 1)
                throw new ArgumentOutOfRangeException(nameof(expected), expected, "expected must be >= 1.");

            Expected         = expected;
            _dropped         = new bool[expected];
            _frozenApplyTick = new uint[expected];
            _isSurvivor      = new bool[expected];
            _acked           = new bool[expected];
        }

        /// <summary>True while a drop directive is awaiting all-survivor ACKs.</summary>
        public bool DirectivePending => _pending;

        /// <summary>The slot named by the pending directive (−1 when none pending).</summary>
        public int PendingDroppedSlot => _pending ? _pendingDroppedSlot : -1;

        /// <summary>The tick-counted idle-from marker of the pending directive (0 when none pending).</summary>
        public uint PendingApplyTick => _pending ? _pendingApplyTick : 0u;

        /// <summary>The committed frozen slots in commit order (a live view — do not mutate). Order is not relied on
        /// by any consumer; the injector re-stamps faction per slot and the builder re-sorts sub-bundles by faction id.</summary>
        public IReadOnlyList<int> FrozenSlots => _frozenSlots;

        /// <summary>True once <paramref name="slot"/>'s freeze has been committed.</summary>
        public bool IsFrozen(int slot) => (uint)slot < (uint)Expected && _dropped[slot];

        /// <summary>The committed idle-from tick for a frozen slot (0 if not frozen).</summary>
        public uint FrozenApplyTick(int slot) => (uint)slot < (uint)Expected ? _frozenApplyTick[slot] : 0u;

        /// <summary>
        /// Record a mid-match disconnect: set ONE pending directive for <paramref name="slot"/> at
        /// <paramref name="applyAtTick"/> and reset the survivor-ACK set to <paramref name="survivorSlots"/>.
        /// Returns <c>false</c> (no state change) if the slot is out of range, already dropped, or a directive is
        /// already pending (one drop directive at a time). Slot is transport-authoritative.
        /// </summary>
        public bool NotifyDrop(int slot, uint applyAtTick, int[] survivorSlots)
        {
            if ((uint)slot >= (uint)Expected) return false;
            if (_dropped[slot]) return false;
            if (_pending) return false;

            Array.Clear(_isSurvivor, 0, Expected);
            Array.Clear(_acked, 0, Expected);
            if (survivorSlots != null)
                foreach (int s in survivorSlots)
                    if ((uint)s < (uint)Expected && s != slot && !_dropped[s]) _isSurvivor[s] = true;

            _pending            = true;
            _pendingDroppedSlot = slot;
            _pendingApplyTick   = applyAtTick;
            _timeoutArmed       = false; // DW-410 — a fresh directive gets a fresh deadline (armed at the next pump)
            return true;
        }

        /// <summary>
        /// Record a survivor's ACK of the pending directive. Only counts when it matches the pending
        /// (<paramref name="droppedSlot"/>, <paramref name="applyAtTick"/>) pair AND
        /// <paramref name="survivorSlot"/> is a recorded survivor — a stale/mismatched ACK is ignored. Slot is
        /// transport-authoritative; out-of-range slots are ignored.
        /// </summary>
        public void RecordAck(int survivorSlot, int droppedSlot, uint applyAtTick)
        {
            if ((uint)survivorSlot >= (uint)Expected) return;
            if (!_pending || droppedSlot != _pendingDroppedSlot || applyAtTick != _pendingApplyTick) return;
            if (!_isSurvivor[survivorSlot]) return;
            _acked[survivorSlot] = true;
        }

        /// <summary>
        /// True when EVERY recorded survivor has ACKed the pending directive. Pure predicate — no side effects
        /// (call <see cref="Commit"/> to finalize). If there are no survivors this returns <c>false</c> (a
        /// survivor-less drop = match over, handled by the adapter, never committed here).
        /// </summary>
        public bool AllAcked()
        {
            if (!_pending) return false;
            bool anySurvivor = false;
            for (int s = 0; s < Expected; s++)
            {
                if (!_isSurvivor[s]) continue;
                anySurvivor = true;
                if (!_acked[s]) return false;
            }
            return anySurvivor;
        }

        /// <summary>
        /// DW-409 — reconcile the pending directive's survivor-ACK set with a LATER in-match disconnect: a survivor
        /// that disconnects before ACKing can never ACK, so leaving it in the recorded survivor set would deadlock
        /// <see cref="AllAcked"/> (and therefore the freeze commit + the merge fan-in) forever. Clears
        /// <paramref name="slot"/> from BOTH the survivor and ACK sets of the pending directive. No-op (returns
        /// <c>false</c>) when no directive is pending, the slot is out of range, or the slot is not a recorded
        /// survivor (including the pending dropped slot itself). Returns <c>true</c> when the pending ACK set
        /// changed — the caller MUST then re-check <see cref="AllAcked"/> (and <see cref="Commit"/>), because the
        /// remaining survivors may already have all ACKed.
        ///
        /// <para>If this empties the survivor set entirely, the pending directive can never commit
        /// (<see cref="AllAcked"/> stays <c>false</c> — a survivor-less freeze is deliberately impossible); that
        /// state is only reachable when every survivor disconnected, which the adapter handles as match-over.</para>
        /// </summary>
        public bool RemoveSurvivor(int slot)
        {
            if ((uint)slot >= (uint)Expected) return false;
            if (!_pending) return false;
            if (!_isSurvivor[slot]) return false;
            _isSurvivor[slot] = false;
            _acked[slot]      = false;
            return true;
        }

        /// <summary>
        /// Finalize a fully-ACKed directive: mark the pending slot frozen at its applyAtTick and clear the pending
        /// state so the next directive may be issued. Idempotent — returns <c>false</c> (no-op) unless a fully-ACKed
        /// directive is pending.
        /// </summary>
        public bool Commit()
        {
            if (!AllAcked()) return false;
            CommitPending();
            return true;
        }

        // ── DW-410: the un-ACKed-freeze deadline ──────────────────────────────

        /// <summary>
        /// DW-410 — bound the un-ACKed FREEZE window, the drop-domain mirror of
        /// <see cref="DelayController.CheckAckTimeout"/>. Today the freeze commits only on
        /// <see cref="AllAcked"/> over the survivor set, with no deadline: ONE survivor that is
        /// transport-connected but hung (never sends <c>DropAck</c>) leaves the freeze uncommitted forever, so
        /// <c>FrozenSlotInjector</c> never runs and every other survivor plus every spectator stalls indefinitely.
        /// Once <paramref name="currentTick"/> has advanced <see cref="ACK_TIMEOUT_TICKS"/> past the deadline base,
        /// the pending freeze is COMMITTED over the survivors that did ACK and the ones that did not are reported in
        /// <paramref name="hungSurvivors"/> so the caller can treat them as dropped in turn — the match always makes
        /// progress. (Aborting the match instead is deliberately NOT the escalation: that hands one hung client the
        /// power to end everyone's game, the hostage dynamic the whole freeze-and-continue policy removes.)
        ///
        /// <para>Safe for the same reason the delay-side force-commit is: the transport is RELIABLE, so every
        /// still-connected survivor RECEIVED the broadcast <c>DropDirective</c>; the ACK is bookkeeping, and the
        /// freeze itself is applied at a tick-counted <c>applyAtTick</c> every peer already holds.</para>
        ///
        /// <para><b>The clock.</b> <paramref name="currentTick"/> is whatever monotonic tick-rate counter the caller
        /// pumps; the deadline base is armed on the FIRST call after a directive goes pending, so the measurement is
        /// self-consistent in the caller's own unit. It deliberately must NOT be the merged-emission frontier: an
        /// uncommitted freeze is precisely what stalls that frontier (survivors run at most `delay` ticks ahead and
        /// then stop), so a deadline measured against it could never elapse and this guard would be inert. All deltas
        /// use unchecked modular <c>uint</c> subtraction (the DW-396 idiom) so a wrapped clock still measures right.</para>
        /// </summary>
        /// <param name="currentTick">The caller's freeze-deadline clock (monotonic, ~sim-rate).</param>
        /// <param name="droppedSlot">The slot whose freeze was force-committed (−1 when nothing fired).</param>
        /// <param name="applyAtTick">That freeze's committed idle-from tick (0 when nothing fired).</param>
        /// <param name="hungSurvivors">Recorded survivors that never ACKed, ascending by slot — empty when nothing
        /// fired. Never null.</param>
        /// <returns><c>true</c> when a freeze was force-committed on this call.</returns>
        public bool CheckAckTimeout(uint currentTick, out int droppedSlot, out uint applyAtTick,
                                    out int[] hungSurvivors)
        {
            droppedSlot = -1; applyAtTick = 0; hungSurvivors = Array.Empty<int>();

            if (!_pending)
            {
                _timeoutArmed = false; // nothing pending — the next directive arms a fresh deadline
                return false;
            }
            if (!_timeoutArmed)
            {
                _timeoutArmed    = true;
                _timeoutBaseTick = currentTick;
                return false;
            }
            if (unchecked(currentTick - _timeoutBaseTick) < (uint)ACK_TIMEOUT_TICKS) return false;

            // Ascending slot order — the deterministic contract, and the order the caller escalates them in.
            var hung = new List<int>();
            for (int s = 0; s < Expected; s++)
                if (_isSurvivor[s] && !_acked[s]) hung.Add(s);

            droppedSlot   = _pendingDroppedSlot;
            applyAtTick   = _pendingApplyTick;
            hungSurvivors = hung.ToArray();
            CommitPending();
            return true;
        }

        /// <summary>Finalize the pending directive unconditionally (the shared tail of <see cref="Commit"/> and the
        /// <see cref="CheckAckTimeout"/> force-commit) — freeze the slot, record its applyAtTick, clear pending.</summary>
        private void CommitPending()
        {
            int slot = _pendingDroppedSlot;
            _dropped[slot]         = true;
            _frozenApplyTick[slot] = _pendingApplyTick;
            _frozenSlots.Add(slot); // COMMIT order (ascending only incidentally at N=2); no consumer depends on order
            _pending            = false;
            _pendingDroppedSlot = -1;
            _timeoutArmed       = false; // DW-410 — the deadline dies with the directive it bounded
        }
    }
}
