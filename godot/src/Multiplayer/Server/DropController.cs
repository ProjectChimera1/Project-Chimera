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
    /// packet byte is never trusted as a slot index. One drop directive is in flight at a time (N=2 has ≤1 survivor,
    /// so a second concurrent drop = the match is over; the adapter emits the terminal summary instead).
    ///
    /// Everything here is tick-counted — <c>applyAtTick</c> is a sim tick number, never wall-clock — so the whole
    /// freeze path is deterministic and never folds into <c>SimChecksum</c>.
    /// </summary>
    public sealed class DropController
    {
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
        /// Finalize a fully-ACKed directive: mark the pending slot frozen at its applyAtTick and clear the pending
        /// state so the next directive may be issued. Idempotent — returns <c>false</c> (no-op) unless a fully-ACKed
        /// directive is pending.
        /// </summary>
        public bool Commit()
        {
            if (!AllAcked()) return false;
            int slot = _pendingDroppedSlot;
            _dropped[slot]         = true;
            _frozenApplyTick[slot] = _pendingApplyTick;
            _frozenSlots.Add(slot); // COMMIT order (ascending only incidentally at N=2); no consumer depends on order
            _pending            = false;
            _pendingDroppedSlot = -1;
            return true;
        }
    }
}
