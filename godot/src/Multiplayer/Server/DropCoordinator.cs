#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core; // Faction

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// DW-411 (Story 9.6 hardening) — the Godot-free disconnect/ACK DECISION GLUE extracted from the
    /// <c>DedicatedServer</c> node so it is Tier-1 unit-testable like <see cref="DropController"/>. The node was the
    /// only owner of: the survivors&gt;0 vs survivors&lt;=0 ("match truly over") gating, the applyAtTick capture
    /// (<c>EmittedThrough + 1</c> at issue time), the faction-byte→slot mapping for a <c>DropAck</c>
    /// (<see cref="FactionToSlot"/> — a packet byte is never trusted as a slot index), and the
    /// DropAck→commit→DropReporter→inject chain — none of which had xUnit coverage (only the DEBUG-only loopback
    /// smoke exercised the survivors==1 happy path). All of that decision logic now lives here; the node is a thin
    /// adapter that supplies the transport/builder/host seams and does the actual broadcasting/printing/pumping.
    ///
    /// <para>DW-409 — this class also closes the N≥3 freeze deadlock: a SECOND disconnect while a drop directive is
    /// pending is no longer swallowed (<see cref="DropController.NotifyDrop"/>'s one-directive-at-a-time guard used
    /// to drop it, leaving the second slot un-frozen and the merge fan-in stalled forever). Now it is
    /// QUEUE-OR-ESCALATED: the concurrent drop is queued and its directive is issued as soon as the pending freeze
    /// commits (with a FRESH applyAtTick + survivor set captured at issue time), and the disconnecting slot is
    /// pruned from the pending directive's survivor-ACK set via <see cref="DropController.RemoveSurvivor"/> — with
    /// an immediate commit re-check, since the remaining survivors may already have all ACKed. The escalation arm is
    /// the survivor-less case: when no player remains connected the outcome is
    /// <see cref="DisconnectOutcome.MatchOver"/> and the adapter ends the match (terminal MATCH SUMMARY).</para>
    ///
    /// <para>Everything here is tick-counted / slot-indexed pure C# — no Godot, no wall-clock, nothing folded into
    /// <c>SimChecksum</c>.</para>
    /// </summary>
    public sealed class DropCoordinator
    {
        /// <summary>The outcome of an in-match player disconnect (what the adapter should do next).</summary>
        public enum DisconnectOutcome
        {
            /// <summary>Nothing to do (out-of-range slot, already frozen, or already pending/queued).</summary>
            Ignored,
            /// <summary>No surviving player remains — the match is truly over; the adapter emits the terminal
            /// summary and recomputes lobby state. No directive is issued (a survivor-less freeze cannot commit).</summary>
            MatchOver,
            /// <summary>A <c>DropDirective</c> for this slot was handed to the broadcast seam; survivors must ACK.</summary>
            DirectiveIssued,
            /// <summary>DW-409 — a directive is already pending, so this slot's freeze was QUEUED; its directive is
            /// issued automatically when the pending freeze commits.</summary>
            Queued,
        }

        private readonly DropController _controller;
        private readonly Faction[] _slotFaction;
        private readonly int _playerCount;

        // ── Injected seams (the Godot node supplies transport/builder/host edges) ──
        private readonly Func<long> _emittedThrough;             // () => MergedTickBuilder.EmittedThrough
        private readonly Func<int, bool> _isPlayerSlotConnected; // transport truth for player slots [0, playerCount)
        private readonly Action<Faction, uint> _broadcastDirective; // (droppedFaction, applyAtTick) → wire + log
        private readonly Action<int> _onFreezeCommitted;         // droppedSlot → DropReporter + injection pump + log

        /// <summary>DW-409 — slots whose freeze is waiting behind the pending directive (FIFO, deduped).</summary>
        private readonly List<int> _queuedDrops = new();

        /// <param name="playerCount">Player slots this match quorums over (the fan-in width).</param>
        /// <param name="slotFaction">Slot → authoritative faction (the <c>SLOT_FACTION</c> table).</param>
        /// <param name="emittedThrough">The merged fan-in's emitted high-water — read at ISSUE time so
        /// <c>applyAtTick = emittedThrough + 1</c> names the first idle tick (the merge is stalled on the silent
        /// dropped slot until the freeze commits, so the marker cannot be overtaken).</param>
        /// <param name="isPlayerSlotConnected">Transport-authoritative player-slot connectivity.</param>
        /// <param name="broadcastDirective">Wire seam: broadcast a <c>DropDirective(droppedFaction, applyAtTick)</c>.</param>
        /// <param name="onFreezeCommitted">Commit seam: the adapter drops the leaver from the checksum quorum
        /// (<c>ServerHost.DropReporter</c>) and pumps frozen injection. Runs BEFORE any queued follow-on directive is
        /// issued, so that directive's applyAtTick is read from the post-pump frontier.</param>
        public DropCoordinator(int playerCount, Faction[] slotFaction,
                               Func<long> emittedThrough,
                               Func<int, bool> isPlayerSlotConnected,
                               Action<Faction, uint> broadcastDirective,
                               Action<int> onFreezeCommitted)
        {
            if (playerCount < 1) throw new ArgumentOutOfRangeException(nameof(playerCount));
            if (slotFaction == null || slotFaction.Length < playerCount)
                throw new ArgumentException("slotFaction must cover every player slot.", nameof(slotFaction));
            // DW-412 — FAIL CLOSED on a table FactionToSlot cannot invert. Its first-match scan is a true inverse
            // only while the player prefix is INJECTIVE and player-faction-only; a duplicate faction would resolve a
            // survivor's DropAck to the WRONG slot, DropController.RecordAck would then drop it as a mismatch
            // (droppedSlot != _pendingDroppedSlot) with no log, and the freeze would never commit — the merged
            // fan-in stalls on the departed peer forever. Injective by construction today (ToFaction(i) == i+1);
            // this is the pin, so a future roster-sourced/hand-built table cannot silently reopen the stall.
            SlotFactionTable.AssertValid(slotFaction, playerCount, nameof(slotFaction));
            _playerCount           = playerCount;
            _slotFaction           = slotFaction;
            _emittedThrough        = emittedThrough        ?? throw new ArgumentNullException(nameof(emittedThrough));
            _isPlayerSlotConnected = isPlayerSlotConnected ?? throw new ArgumentNullException(nameof(isPlayerSlotConnected));
            _broadcastDirective    = broadcastDirective    ?? throw new ArgumentNullException(nameof(broadcastDirective));
            _onFreezeCommitted     = onFreezeCommitted     ?? throw new ArgumentNullException(nameof(onFreezeCommitted));
            _controller            = new DropController(playerCount);
        }

        /// <summary>The wrapped ACK-gated freeze state machine (the adapter reads <c>FrozenSlots</c> for injection).</summary>
        public DropController Controller => _controller;

        /// <summary>DW-409 — slots whose freeze directive is queued behind the pending one (issue order).</summary>
        public IReadOnlyList<int> QueuedDrops => _queuedDrops;

        /// <summary>
        /// Map a DropAck's (transport-untrusted) faction byte back to a player slot via the authoritative
        /// slot→faction table — never trusting it as a slot index. −1 if no player slot matches.
        /// (Extracted verbatim from <c>DedicatedServer.FactionToSlot</c>.)
        ///
        /// <para>DW-412 — this first-match scan is a TRUE INVERSE because the constructor rejects any table whose
        /// player prefix is not injective / not player-faction-only (<see cref="SlotFactionTable.AssertValid"/>).
        /// Without that guard a duplicate entry would silently resolve an ACK to the wrong slot (stall) and a
        /// Neutral entry would resolve an unknown byte to a real slot instead of −1.</para>
        /// </summary>
        public int FactionToSlot(Faction faction)
        {
            for (int s = 0; s < _playerCount; s++)
                if (_slotFaction[s] == faction) return s;
            return -1;
        }

        /// <summary>
        /// Handle an IN-MATCH player disconnect. The transport has already cleared the slot, so connectivity reads
        /// are the post-disconnect truth. Decides match-over vs freeze-and-continue, reconciles any pending
        /// directive's survivor-ACK set (DW-409 — committing it immediately if the remaining survivors have all
        /// ACKed, which also issues any queued follow-on directive), then issues this slot's own directive or
        /// queues it behind the pending one.
        /// </summary>
        public DisconnectOutcome OnPlayerDisconnect(int slot)
        {
            if ((uint)slot >= (uint)_playerCount) return DisconnectOutcome.Ignored; // spectator/out-of-range

            // Survivor-less drop = the match is truly over (the "escalate" arm). No freeze is issued — a
            // survivor-less directive could never be ACKed, and the adapter ends the match instead.
            if (CountConnectedPlayers() <= 0) return DisconnectOutcome.MatchOver;

            // DW-409 (survivor-prune half): the disconnecting slot can no longer ACK the pending directive. Prune
            // it and re-check the commit — the remaining survivors may already have all ACKed, in which case the
            // pending freeze commits NOW (and the next queued directive, if any, is issued).
            if (_controller.RemoveSurvivor(slot))
                TryCommitPending();

            // This slot's own freeze.
            if (_controller.IsFrozen(slot)) return DisconnectOutcome.Ignored; // defensive — already frozen
            if (_controller.DirectivePending)
            {
                // DW-409 (queue half): one directive is in flight at a time — queue this drop; its directive is
                // issued (fresh applyAtTick + survivor set) the moment the pending freeze commits.
                if (_controller.PendingDroppedSlot == slot || _queuedDrops.Contains(slot))
                    return DisconnectOutcome.Ignored;
                _queuedDrops.Add(slot);
                return DisconnectOutcome.Queued;
            }
            return IssueDirective(slot) ? DisconnectOutcome.DirectiveIssued : DisconnectOutcome.Ignored;
        }

        /// <summary>
        /// Handle a survivor's <c>DropAck</c>. <paramref name="ackSlot"/> is TRANSPORT-AUTHORITATIVE (the ENet
        /// peer→slot callback slot); <paramref name="ackFactionByte"/> is the packet's dropped-faction byte, mapped
        /// back to the dropped slot via <see cref="FactionToSlot"/>. When every recorded survivor has ACKed the same
        /// (droppedSlot, applyAtTick), the freeze commits: the commit seam runs (quorum drop + injection pump) and
        /// any queued follow-on directive is issued. Returns <c>true</c> when a freeze committed on this ACK.
        /// </summary>
        public bool OnDropAck(int ackSlot, byte ackFactionByte, uint applyAtTick)
        {
            int droppedSlot = FactionToSlot((Faction)ackFactionByte);
            if (droppedSlot < 0) return false;
            _controller.RecordAck(ackSlot, droppedSlot, applyAtTick);
            return TryCommitPending();
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private int CountConnectedPlayers()
        {
            int n = 0;
            for (int s = 0; s < _playerCount; s++)
                if (_isPlayerSlotConnected(s)) n++;
            return n;
        }

        /// <summary>The connected player slots other than <paramref name="droppedSlot"/> — the set that must ACK.</summary>
        private int[] SurvivingPlayerSlots(int droppedSlot)
        {
            var survivors = new List<int>();
            for (int s = 0; s < _playerCount; s++)
                if (s != droppedSlot && _isPlayerSlotConnected(s)) survivors.Add(s);
            return survivors.ToArray();
        }

        /// <summary>
        /// Issue the freeze directive for <paramref name="slot"/>: capture applyAtTick from the CURRENT emitted
        /// high-water (+1 = the first idle tick) and the CURRENT survivor set, arm the controller, and hand the
        /// directive to the broadcast seam. Returns false when it cannot be issued (no survivor to ACK it, or the
        /// controller rejected it) — never arms an un-committable survivor-less directive.
        /// </summary>
        private bool IssueDirective(int slot)
        {
            int[] survivors = SurvivingPlayerSlots(slot);
            if (survivors.Length == 0) return false; // survivor-less — the match-over path owns this
            uint applyAtTick = (uint)(_emittedThrough() + 1);
            if (!_controller.NotifyDrop(slot, applyAtTick, survivors)) return false;
            _broadcastDirective(_slotFaction[slot], applyAtTick);
            return true;
        }

        /// <summary>
        /// Commit the pending directive if every remaining survivor has ACKed: run the commit seam (checksum-quorum
        /// drop + injection pump) FIRST, then issue the next queued directive — its applyAtTick is therefore read
        /// from the post-pump frontier, naming the first genuinely idle tick of the next frozen slot.
        /// </summary>
        private bool TryCommitPending()
        {
            if (!_controller.AllAcked()) return false;
            int committedSlot = _controller.PendingDroppedSlot; // capture BEFORE Commit clears it
            if (!_controller.Commit()) return false;
            _onFreezeCommitted(committedSlot);
            IssueNextQueued();
            return true;
        }

        /// <summary>DW-409 — issue the next queued drop's directive (skipping anything already frozen).</summary>
        private void IssueNextQueued()
        {
            while (_queuedDrops.Count > 0)
            {
                int slot = _queuedDrops[0];
                _queuedDrops.RemoveAt(0);
                if (_controller.IsFrozen(slot)) continue; // defensive — already committed elsewhere
                if (IssueDirective(slot)) return;
                // Could not issue (no survivor left to ACK it) — the match is ending; keep it queued so the state
                // is inspectable, and stop. The adapter's match-over path owns the shutdown.
                _queuedDrops.Insert(0, slot);
                return;
            }
        }
    }
}
