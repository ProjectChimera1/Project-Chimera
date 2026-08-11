#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.4 (the Godot-free server delay authority) — the dedicated server's single source of truth for the
    /// match's input delay. Lives under <c>src/Multiplayer/Server/**</c> so it compiles into the Tier-1 assembly
    /// (and the determinism analyzer) like <see cref="MergedTickBuilder"/> / <see cref="ServerLobbyPolicy"/>; the
    /// Godot <c>DedicatedServer</c> node is a thin adapter that feeds RTT samples + ACKs in and broadcasts the
    /// directive out.
    ///
    /// Responsibilities:
    ///   • Own the RTT-probe ping sequence (<see cref="NextPingSeq"/>) and fold each slot's seq-guarded,
    ///     wrap-safe Pong echo into its smoothed RTT EWMA (<see cref="RecordPong"/> / <see cref="RecordRtt"/>).
    ///   • Compute ONE dictated delay for the whole match from the MAX active-slot EFFECTIVE RTT — smoothed +
    ///     <see cref="JITTER_HEADROOM"/>·jitter, the TCP-RTO shape, so a spiky link's delay covers its swing band
    ///     (DW-933) — via the existing
    ///     <see cref="DelayMath.ComputeTargetDelay"/> (<see cref="TryComputeDirective"/>), emitted only when it
    ///     differs from the last committed value AND no directive is currently pending un-ACKed — and, since
    ///     DW-931, only once the damping gates pass (target-persistence streak, asymmetric grow/shrink dwell,
    ///     shrink deadband; a grow of ≥ <see cref="URGENT_GROW_STEP"/> ticks bypasses streak + dwell).
    ///   • Drive the all-N-ACK commit state machine (<see cref="RecordAck"/> / <see cref="AllAcked"/> /
    ///     <see cref="Commit"/>): a new directive cannot be issued until every ACTIVE player has ACKed the pending
    ///     one. A slot that drops mid-match is excused via <see cref="DeactivateSlot"/> (DW-400) and a pending
    ///     directive whose surviving ACKs are already complete finalizes via <see cref="TryFinalizePending"/>;
    ///     a wedged ACK from a still-connected client is bounded by <see cref="CheckAckTimeout"/>.
    ///
    /// Slot-map awareness (DW-397): the controller indexes by TRANSPORT SLOT id over an explicit active-slot mask
    /// (built from the match's player-slot set), never by a dense [0, Expected) assumption — so a future
    /// non-contiguous slot layout (Story 9.15 open-slot lobbies) cannot silently drop a high-slot player's
    /// RTT samples or ACKs. The <c>(int expected, ...)</c> constructor remains as dense [0, expected) sugar
    /// (today's layout, byte-identical behavior).
    ///
    /// Determinism note: the <c>float</c> RTT EWMA is a latency/buffering concern that NEVER folds into
    /// <c>SimChecksum</c> — it only shifts WHICH buffer slot a command lands in — so the <c>float</c> here is the
    /// same expected/advisory CHM0001 as <see cref="DelayMath"/>. What MUST hold is that the server dictates ONE
    /// delay and every client re-clamps + commits it identically at the same <c>applyAtTick</c>.
    /// </summary>
    public sealed class DelayController
    {
        /// <summary>EWMA smoothing weight for RTT samples (mirrors LockstepManager.RTT_ALPHA).</summary>
        private const float RTT_ALPHA = 0.125f;

        /// <summary>
        /// Ticks of lead time between the current sim frontier and a directive's <c>applyAtTick</c>. Must be
        /// ≥ <c>2*MAX_DELAY + 8</c> so both peers can pre-seed any empty gap before the change lands (the same
        /// safety budget the P2P <c>ComputeSafeApplyAt</c> uses at its worst case).
        /// </summary>
        public const int SafeMargin = 2 * DelayMath.MAX_DELAY + 8;

        /// <summary>
        /// DW-400: how many ticks of frontier advance a pending directive may await its ACKs before the server
        /// force-commits it (<see cref="CheckAckTimeout"/>). 300 ticks = 10 s at 30 Hz — far beyond any sane ACK
        /// round-trip (the directive's own applyAtTick lead is only <see cref="SafeMargin"/> ticks), so it only
        /// fires when a still-connected client silently never ACKs (a genuine drop is excused sooner via
        /// <see cref="DeactivateSlot"/>).
        /// </summary>
        public const int ACK_TIMEOUT_TICKS = 300;

        // ── DW-931 damping constants (tick units at 30 Hz) ────────────────────
        // The undamped comparator re-dictated the instant the EWMA'd worst-slot RTT crossed a tick boundary; the
        // delay-2/3 boundary sits at smoothed RTT = 66.7 ms, exactly where a LAN match with frame bursts oscillates
        // (2↔3 flapped ~40 times in 4.5 min in the 2026-08-10 11:21 field log). Every widening change costs each
        // client a (newDelay−oldDelay)-tick gap seed in which that player's input cannot land — the felt hiccup —
        // so a target must now PERSIST (streak), respect a minimum spacing (dwell, asymmetric: grow fast / shrink
        // slowly), and clear the shrink boundary by a margin (deadband). A grow of ≥ URGENT_GROW_STEP bypasses
        // streak + dwell: growing is the safe direction (too-small delay stalls every tick; too-large only adds
        // input latency). Server-side decision only — the wire format and the client's obey/gap-seed path are
        // untouched, so damping cannot desync.

        /// <summary>DW-931: frontier ticks a +1 GROW target must hold continuously before it is dictated
        /// (2 s ≈ 2 ping samples). Bypassed by an urgent grow (≥ <see cref="URGENT_GROW_STEP"/>).</summary>
        public const int GROW_STREAK_TICKS = 60;

        /// <summary>DW-931: frontier ticks a SHRINK target must hold continuously before it is dictated (10 s) —
        /// shrinking is the risky direction, so it must prove itself far longer than a grow.</summary>
        public const int SHRINK_STREAK_TICKS = 300;

        /// <summary>DW-931: minimum frontier ticks after the last committed change's applyAtTick before another
        /// GROW directive (3 s). The match's FIRST directive is exempt so match-start adaptation stays prompt.</summary>
        public const int GROW_DWELL_TICKS = 90;

        /// <summary>DW-931: minimum frontier ticks after the last committed change's applyAtTick before a SHRINK
        /// directive (20 s) — each renegotiation is felt (gap seed + reliable directive/ACK traffic), so the
        /// calm-down direction is deliberately slow. Lower THIS (not the streak) if it ever feels laggy.</summary>
        public const int SHRINK_DWELL_TICKS = 600;

        /// <summary>DW-931: a grow of at least this many ticks bypasses streak + dwell — a real RTT jump must
        /// widen fast, and growing is the safe direction.</summary>
        public const int URGENT_GROW_STEP = 2;

        /// <summary>DW-931: shrink deadband (~⅓ tick of RTT) — the smoothed RTT must clear the shrink boundary by
        /// this margin, not merely touch it, before a shrink target counts at all.</summary>
        public const float SHRINK_MARGIN_MS = 12f;

        // ── DW-933 jitter-headroom constants ──────────────────────────────────
        // The smoothed EWMA hides variance: the 2026-08-11 Wi-Fi field run sat at ~60 ms smoothed while samples
        // swung 55–300 ms (spikes 600+), so the dictated delay covered the MEAN and every spike stalled lockstep
        // past the 400 ms banner threshold. Fix = TCP's RTO idiom (RFC 6298: RTO = SRTT + 4·RTTVAR): track each
        // slot's mean absolute RTT deviation alongside its smoothed RTT and dictate from
        // smoothed + JITTER_HEADROOM·jitter, so a spiky link gets a delay that covers its swing band instead of
        // stalling on every spike. A steady link's jitter converges to ~0, leaving behavior identical to the
        // pre-DW-933 controller. Server-side decision only (same shape as DW-931): the wire format, the client's
        // obey/gap-seed path, and the pending/maturity desync guards are untouched.

        /// <summary>DW-933: EWMA weight for the per-slot jitter (mean absolute deviation) track — TCP's RTTVAR
        /// beta (¼, RFC 6298). Heavier than <see cref="RTT_ALPHA"/> so headroom reacts to a spike burst within a
        /// few pongs.</summary>
        private const float JITTER_ALPHA = 0.25f;

        /// <summary>DW-933: how many jitters of headroom the dictated delay covers above the smoothed RTT —
        /// TCP's K (RTO = SRTT + 4·RTTVAR). Trades input latency on spiky links for not stalling on every spike;
        /// lower THIS if a jittery link ever feels too laggy (a steady link is unaffected either way).</summary>
        public const float JITTER_HEADROOM = 4f;

        /// <summary>Number of player slots this controller was constructed with (the match's player count).
        /// Fixed for the match; the LIVE count after mid-match drops is <see cref="ActiveCount"/>.</summary>
        public int Expected { get; }

        // ── Slot-map state (DW-397) ───────────────────────────────────────────
        // All per-slot arrays are sized to _capacity (the transport slot bound) and indexed by TRANSPORT SLOT id;
        // _active[] is the authoritative membership mask. Never assume the players occupy a dense [0, Expected).

        private readonly int    _capacity;
        private readonly bool[] _active;
        private int             _activeCount;

        private readonly float[] _smoothedRtt;
        private readonly bool[]  _rttSeen;

        /// <summary>DW-933: per-slot EWMA of |sample − smoothed| (TCP RTTVAR shape). Seeded ZERO on a slot's
        /// first sample — NOT TCP's R/2 — so a steady link behaves exactly as before DW-933 (no golden moves).</summary>
        private readonly float[] _rttJitter;

        // ── RTT-probe (ping/pong) state (DW-395/DW-401) ───────────────────────
        // The ping seq lives IN the controller (not the Godot node) so a rebuilt controller — a second match in
        // one server process — starts from a fresh seq (DW-401) and the stale-Pong guard is Tier-1 testable.

        /// <summary>The seq the NEXT outgoing ping will carry; the outstanding probe is <c>_pingSeq - 1</c>.</summary>
        private byte _pingSeq;

        /// <summary>False until the first <see cref="NextPingSeq"/> — guards the pre-first-ping window where
        /// <c>(byte)(_pingSeq - 1)</c> would wrap to 255 and a forged seq-255 Pong would pass the stale check.</summary>
        private bool _pingIssued;

        /// <summary>Per-slot "already folded a Pong for the outstanding seq" — a duplicate/forged second echo of
        /// the same probe must not fold twice (one RTT sample per slot per probe). Cleared on each new ping.</summary>
        private readonly bool[] _pongFolded;

        // ── Directive / ACK state machine ─────────────────────────────────────
        private int  _lastCommittedDelay;
        private bool _pending;
        private int  _pendingDelay;
        private uint _pendingApplyTick;
        private readonly bool[] _acked;

        /// <summary>DW-400: the frontier tick at which the pending directive was issued — the base the ACK
        /// timeout (<see cref="CheckAckTimeout"/>) is measured from.</summary>
        private uint _pendingIssuedAtTick;

        // The MATURITY-GATE tick of the last COMMITTED-but-not-yet-matured directive (0 = none / already matured).
        // A committed directive is only SCHEDULED on each client, and applied when that client's EXEC frontier
        // reaches its applyAtTick (LockstepManager.Flush: `currentTick >= _pendingApplyTick`). The confirmed
        // high-water we gate on (MergedTickBuilder.EmittedThrough) is the SUBMISSION/ISSUE frontier — a client
        // submits issueTick = execTick + delay, so the submission frontier LEADS the exec frontier by the current
        // delay (≤ MAX_DELAY). Confirming `EmittedThrough > applyAtTick` therefore only proves every client's exec
        // reached `applyAtTick + 1 − delay`, which can be BELOW applyAtTick — the change may not be applied yet on a
        // lagging client. So the gate tick is applyAtTick + MAX_DELAY: once the submission frontier passes THAT,
        // every client's exec has provably passed applyAtTick (exec ≥ EmittedThrough − delay ≥ applyAtTick + 1), so
        // the prior change is applied everywhere and a new directive can no longer overwrite a still-scheduled one
        // on a slow client (→ two clients holding different delays → command-slot divergence → desync).
        private uint _maturingApplyTick;

        // ── DW-931 damping state ──────────────────────────────────────────────
        // The streak clock is measured in FRONTIER TICKS, not evaluation calls: TryComputeDirective runs per frame
        // but the EWMA only moves on ~1 Hz pongs, so tick-time is the correct persistence clock and per-frame
        // calling stays harmless. All tick deltas use unchecked modular uint subtraction (the DW-396 idiom).

        /// <summary>The candidate target currently accumulating a persistence streak (== the committed delay when
        /// no candidate is in progress). Reset to the committed value on every commit and on any evaluation whose
        /// target returns to the committed value — a dip back to committed restarts the clock from zero.</summary>
        private int _streakTarget;

        /// <summary>The frontier tick at which <see cref="_streakTarget"/> was first observed (streak start).</summary>
        private uint _streakSinceTick;

        /// <summary>The applyAtTick of the last committed delay change — the dwell clock's base.</summary>
        private uint _lastChangeTick;

        /// <summary>False until the first directive commits — exempts the match's FIRST change from dwell.</summary>
        private bool _anyChangeCommitted;

        /// <summary>The delay the server has last confirmed all players committed (starts at the initial delay).</summary>
        public int LastCommittedDelay => _lastCommittedDelay;

        /// <summary>True while a dictated directive is awaiting all-active ACKs.</summary>
        public bool DirectivePending => _pending;

        /// <summary>The number of slots still ACTIVE (constructed set minus <see cref="DeactivateSlot"/>ed drops) —
        /// the live ACK quorum size and RTT dictate span.</summary>
        public int ActiveCount => _activeCount;

        /// <summary>True when <paramref name="slot"/> is an active player slot of this match.</summary>
        public bool IsActiveSlot(int slot) => (uint)slot < (uint)_capacity && _active[slot];

        /// <summary>True while a committed directive has not yet matured (been APPLIED) on all clients — the
        /// confirmed submission high-water has not yet passed its applyAtTick + the MAX_DELAY submission→exec lead.
        /// A new directive is withheld until then.</summary>
        public bool AwaitingMaturity => _maturingApplyTick != 0;

        /// <param name="expected">Number of player slots (in [1, ...]) occupying the DENSE range [0, expected) —
        /// today's slot layout. Sugar over the slot-map constructor.</param>
        /// <param name="initialDelay">The delay the match starts at (LockstepManager.INPUT_DELAY) — the baseline the
        /// first dictated value is compared against so no directive is emitted until the target genuinely changes.</param>
        public DelayController(int expected, int initialDelay)
            : this(DenseSlots(expected), expected, initialDelay)
        {
        }

        /// <summary>
        /// DW-397 (slot-map-aware) — construct from the match's ACTUAL player-slot set. Per-slot state is indexed
        /// by transport slot id up to <paramref name="slotCapacity"/> (the transport's slot bound, e.g.
        /// <c>ServerTransport.MAX_SLOTS</c>), so a non-contiguous layout (a player at slot ≥ playerCount) tracks
        /// RTT/ACKs correctly instead of being silently dropped.
        /// </summary>
        /// <param name="playerSlots">The occupied player slots (distinct, each in [0, slotCapacity)); ≥ 1 entry.</param>
        /// <param name="slotCapacity">The exclusive upper bound on slot ids this controller indexes.</param>
        /// <param name="initialDelay">The delay the match starts at (see the dense constructor).</param>
        public DelayController(IReadOnlyList<int> playerSlots, int slotCapacity, int initialDelay)
        {
            if (playerSlots == null || playerSlots.Count < 1)
                throw new ArgumentException("playerSlots must contain at least one slot.", nameof(playerSlots));
            if (slotCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(slotCapacity), slotCapacity, "slotCapacity must be >= 1.");

            _capacity           = slotCapacity;
            _active             = new bool[slotCapacity];
            _smoothedRtt        = new float[slotCapacity];
            _rttJitter          = new float[slotCapacity];
            _rttSeen            = new bool[slotCapacity];
            _acked              = new bool[slotCapacity];
            _pongFolded         = new bool[slotCapacity];
            _lastCommittedDelay = initialDelay;
            _streakTarget       = initialDelay; // DW-931: no candidate in progress at match start

            for (int i = 0; i < playerSlots.Count; i++)
            {
                int slot = playerSlots[i];
                if ((uint)slot >= (uint)slotCapacity)
                    throw new ArgumentOutOfRangeException(nameof(playerSlots), slot,
                        $"player slot must be in [0, {slotCapacity}).");
                if (_active[slot])
                    throw new ArgumentException($"duplicate player slot {slot}.", nameof(playerSlots));
                _active[slot] = true;
                _activeCount++;
            }
            Expected = _activeCount;
        }

        /// <summary>The dense [0, expected) slot set the legacy constructor wraps (throws for expected &lt; 1,
        /// preserving its original contract).</summary>
        private static int[] DenseSlots(int expected)
        {
            if (expected < 1)
                throw new ArgumentOutOfRangeException(nameof(expected), expected, "expected must be >= 1.");
            var slots = new int[expected];
            for (int i = 0; i < expected; i++) slots[i] = i;
            return slots;
        }

        // ── RTT probe (DW-395/DW-396/DW-401) ──────────────────────────────────

        /// <summary>
        /// Issue the next RTT-probe sequence number — the seq the server stamps on the ping it is about to
        /// broadcast. Advances the outstanding-probe window: Pongs are only folded for THIS seq
        /// (<see cref="RecordPong"/>), and each slot's per-probe duplicate guard is re-armed.
        /// </summary>
        public byte NextPingSeq()
        {
            _pingIssued = true;
            Array.Clear(_pongFolded, 0, _capacity);
            return _pingSeq++;
        }

        /// <summary>
        /// DW-395/DW-396 — fold a slot's Pong echo into its RTT EWMA, guarded like the client's seq-checked
        /// <c>LockstepManager.HandlePong</c>:
        ///   • the echoed <paramref name="seq"/> must match the OUTSTANDING probe (a stale echo of an earlier ping
        ///     — whose elapsed time includes whole ping intervals — is ignored rather than folded as a huge
        ///     "RTT" that over-dictates delay);
        ///   • only ONE echo per slot per probe counts (a duplicated/forged second Pong cannot re-fold);
        ///   • the RTT is computed in WRAP-SAFE uint arithmetic (<c>now − sender</c> mod 2^32): the ping stamps a
        ///     uint-truncated clock, so subtracting against a full-width clock would read ~4.29e9 ms once server
        ///     uptime passes ~49.7 days and every sample would fail the sanity filter, silently freezing delay
        ///     adaptation (DW-396). Modular uint subtraction cancels the wrap for any real RTT &lt; 2^32 ms.
        /// Inactive/out-of-range slots and pre-first-ping echoes are ignored (transport-authoritative slot —
        /// never trusted from a packet byte).
        /// </summary>
        /// <param name="slot">The transport-authoritative sender slot.</param>
        /// <param name="seq">The seq byte echoed in the Pong.</param>
        /// <param name="nowMs">The server clock now, uint-truncated ms (same clock the ping stamped).</param>
        /// <param name="senderMs">The uint ms timestamp echoed back (what the ping stamped).</param>
        public void RecordPong(int slot, byte seq, uint nowMs, uint senderMs)
        {
            if ((uint)slot >= (uint)_capacity || !_active[slot]) return;
            if (!_pingIssued) return;                  // no probe outstanding — a forged pre-ping echo
            if (seq != (byte)(_pingSeq - 1)) return;   // stale pong from a superseded probe — ignore
            if (_pongFolded[slot]) return;             // duplicate echo of this probe — one sample per slot per probe

            uint rttMs = unchecked(nowMs - senderMs);  // wrap-safe modular delta (DW-396)
            if (rttMs == 0u || rttMs > 10_000u) return; // sanity-check (mirrors LockstepManager.HandlePong)

            _pongFolded[slot] = true;
            RecordRtt(slot, rttMs);
        }

        /// <summary>
        /// Fold a fresh RTT sample for <paramref name="slot"/> into its smoothed EWMA and its jitter
        /// (mean-absolute-deviation) EWMA (DW-933). A stale/invalid sample
        /// (non-positive or implausibly large) is ignored. Inactive/out-of-range slots are ignored
        /// (transport-authoritative slot — never trusted from a packet byte).
        /// </summary>
        public void RecordRtt(int slot, float rttMs)
        {
            if ((uint)slot >= (uint)_capacity || !_active[slot]) return;
            if (rttMs <= 0f || rttMs > 10_000f) return; // sanity-check (mirrors LockstepManager.HandlePong)

            if (!_rttSeen[slot])
            {
                _smoothedRtt[slot] = rttMs;
                _rttJitter[slot]   = 0f; // DW-933: zero-seed (see field doc) — a steady link stays byte-identical
                _rttSeen[slot]     = true;
            }
            else
            {
                // DW-933: fold the deviation against the smoothed value BEFORE this sample moves it (RFC 6298
                // order — RTTVAR measures how far samples land from the current estimate, not the next one).
                float deviation    = Math.Abs(rttMs - _smoothedRtt[slot]);
                _rttJitter[slot]   = _rttJitter[slot] * (1f - JITTER_ALPHA) + deviation * JITTER_ALPHA;
                _smoothedRtt[slot] = _smoothedRtt[slot] * (1f - RTT_ALPHA) + rttMs * RTT_ALPHA;
            }
        }

        // ── Mid-match recovery (DW-400) ───────────────────────────────────────

        /// <summary>
        /// DW-400 — excuse a dropped (frozen) player from the delay authority: the slot leaves the ACK quorum and
        /// its (now stale) RTT no longer drives the dictated delay. Called when the drop-freeze COMMITS
        /// (alongside the checksum-quorum <c>DropReporter</c>). After deactivation the caller should
        /// <see cref="TryFinalizePending"/> — a directive that was only waiting on the dropped slot's ACK is then
        /// fully-ACKed by the survivors and commits instead of wedging forever. Idempotent; unknown slots ignored.
        /// </summary>
        public void DeactivateSlot(int slot)
        {
            if ((uint)slot >= (uint)_capacity || !_active[slot]) return;
            _active[slot]      = false;
            _activeCount--;
            _rttSeen[slot]     = false;
            _smoothedRtt[slot] = 0f;
            _rttJitter[slot]   = 0f;
            _acked[slot]       = false;
            _pongFolded[slot]  = false;
        }

        /// <summary>
        /// DW-400 — commit the pending directive iff it is now fully ACKed by the ACTIVE slot set (used after
        /// <see cref="DeactivateSlot"/>, when the departed slot's missing ACK was the only hold-out). Returns the
        /// committed pair for logging. False (no-op) when nothing is pending or ACKs are still outstanding.
        /// </summary>
        public bool TryFinalizePending(out int delay, out uint applyAtTick)
        {
            if (_pending && AllAcked(_pendingDelay, _pendingApplyTick))
            {
                delay       = _pendingDelay;
                applyAtTick = _pendingApplyTick;
                return Commit(delay, applyAtTick);
            }
            delay = 0; applyAtTick = 0;
            return false;
        }

        /// <summary>
        /// DW-400 — bound the un-ACKed window: when a pending directive has waited more than
        /// <see cref="ACK_TIMEOUT_TICKS"/> of frontier advance, force-commit it (returning <c>true</c> with the
        /// committed pair so the caller can log). Safe because the transport is RELIABLE: every still-connected
        /// client received + scheduled the broadcast directive (client application is unconditional aside from the
        /// identical re-clamp) — the ACK is bookkeeping, and a client that actually dropped is excused via
        /// <see cref="DeactivateSlot"/> on the freeze path. Without this, one wedged ACK from a connected client
        /// would leave the directive pending forever, silently disabling adaptive delay for the rest of the match.
        /// The maturity gate still withholds the NEXT directive until this one has provably applied everywhere.
        /// </summary>
        /// <param name="currentTick">The server's current frontier tick (highest submitted).</param>
        public bool CheckAckTimeout(uint currentTick, out int delay, out uint applyAtTick)
        {
            delay = 0; applyAtTick = 0;
            if (!_pending) return false;
            if (currentTick < _pendingIssuedAtTick + (uint)ACK_TIMEOUT_TICKS) return false;

            delay               = _pendingDelay;
            applyAtTick         = _pendingApplyTick;
            _lastCommittedDelay = _pendingDelay;
            _maturingApplyTick  = _pendingApplyTick + (uint)DelayMath.MAX_DELAY; // same gate Commit sets
            _lastChangeTick     = _pendingApplyTick;    // DW-931: a force-commit arms the dwell clock too
            _anyChangeCommitted = true;
            _streakTarget       = _lastCommittedDelay;  // DW-931: the next candidate re-proves from scratch
            _pending            = false;
            return true;
        }

        // ── Directive computation ─────────────────────────────────────────────

        /// <summary>
        /// Try to produce the next server-dictated delay directive. Emits (returns <c>true</c> with
        /// <paramref name="delay"/> in [MIN_DELAY, MAX_DELAY] and an <paramref name="applyAtTick"/> =
        /// <paramref name="currentTick"/> + <see cref="SafeMargin"/>) ONLY when: at least one ACTIVE slot has a
        /// measured RTT, no directive is already pending un-ACKed, the PRIOR committed directive has matured (been
        /// APPLIED) on all clients (<paramref name="confirmedTick"/> — the merged fan-in high-water — has passed its
        /// applyAtTick plus the MAX_DELAY submission→exec lead), the computed delay differs from the last
        /// committed value (the target is computed from the max active-slot EFFECTIVE RTT — smoothed +
        /// <see cref="JITTER_HEADROOM"/>·jitter, DW-933 — so a spiky link is covered across its swing band),
        /// AND the DW-931 damping gates pass: the candidate target has held continuously for
        /// <see cref="GROW_STREAK_TICKS"/>/<see cref="SHRINK_STREAK_TICKS"/> frontier ticks, at least
        /// <see cref="GROW_DWELL_TICKS"/>/<see cref="SHRINK_DWELL_TICKS"/> ticks have passed since the last
        /// committed change landed, and (for a shrink) the smoothed RTT clears the boundary by
        /// <see cref="SHRINK_MARGIN_MS"/>. A grow of ≥ <see cref="URGENT_GROW_STEP"/> ticks bypasses streak +
        /// dwell. Otherwise returns <c>false</c>. On emission the pending directive
        /// is stored, its ACK set is reset, and the ACK-timeout clock (<see cref="CheckAckTimeout"/>) starts.
        /// </summary>
        /// <param name="currentTick">The server's current frontier tick (highest submitted) — the base the safe
        /// applyAtTick is measured forward from.</param>
        /// <param name="confirmedTick">The merged fan-in high-water: the SUBMISSION frontier through which EVERY
        /// player has submitted. It leads each client's EXEC frontier (where a scheduled delay change is actually
        /// applied) by the delay (≤ MAX_DELAY), so the maturity gate adds that lead before treating a prior change
        /// as applied everywhere. Gates directive pipelining.</param>
        public bool TryComputeDirective(uint currentTick, uint confirmedTick, out int delay, out uint applyAtTick)
        {
            delay = _lastCommittedDelay;
            applyAtTick = 0;

            if (_pending) return false; // no new directive while one is pending un-ACKed

            // PATCH 1a (directive-pipelining desync fix): a committed directive is still only SCHEDULED on each
            // client at its applyAtTick. Do NOT issue a new directive until the prior one has MATURED on ALL
            // clients — signalled by the confirmed merged high-water passing its applyAtTick. Otherwise a fast
            // client could apply directive A while a slow client has A overwritten by B before reaching A's
            // applyAtTick → the two hold different _currentDelay for that window → command-slot divergence → desync.
            if (_maturingApplyTick != 0)
            {
                // _maturingApplyTick already includes the +MAX_DELAY submission→exec lead (see Commit), so passing
                // it proves every client's EXEC frontier — not merely its submission frontier — is past applyAtTick.
                if (confirmedTick <= _maturingApplyTick) return false; // prior directive not yet applied everywhere
                _maturingApplyTick = 0;                                // matured (applied) on all clients — clear the gate
            }

            // Dictate from the WORST (max) ACTIVE-slot EFFECTIVE RTT — smoothed + JITTER_HEADROOM·jitter
            // (DW-933) — so the delay covers the highest-latency live player INCLUDING its swing band, not just
            // its mean (a deactivated slot's stale RTT must not keep the whole match at its dead peer's delay —
            // DW-400). On a steady link jitter ≈ 0 and this reduces to the pre-DW-933 smoothed-only dictate.
            float maxEffRtt = 0f;
            bool any = false;
            for (int s = 0; s < _capacity; s++)
            {
                if (!_active[s] || !_rttSeen[s]) continue;
                any = true;
                float effective = _smoothedRtt[s] + JITTER_HEADROOM * _rttJitter[s];
                if (effective > maxEffRtt) maxEffRtt = effective;
            }
            if (!any) return false; // no RTT measured yet → nothing to dictate

            int target = DelayMath.ComputeTargetDelay(maxEffRtt); // already clamped to [MIN_DELAY, MAX_DELAY]

            // ── DW-931 damping (deliberately AFTER the pending/maturity gates so their semantics stay intact) ──
            if (target == _lastCommittedDelay)
            {
                _streakTarget = target; // back at the committed value → any in-progress streak restarts from zero
                return false;           // no change needed
            }

            bool grow   = target > _lastCommittedDelay;
            bool urgent = grow && target - _lastCommittedDelay >= URGENT_GROW_STEP;
            if (!urgent)
            {
                // Shrink deadband: the effective RTT must clear the shrink boundary by SHRINK_MARGIN_MS, not
                // merely touch it. ComputeTargetDelay is monotonic non-decreasing (pinned by DelayMathTests), so
                // probing it with the margin added asks "would a slightly-worse RTT still want the committed
                // delay?". Probing the EFFECTIVE value (DW-933) means residual jitter also holds a shrink back.
                if (!grow && DelayMath.ComputeTargetDelay(maxEffRtt + SHRINK_MARGIN_MS) >= _lastCommittedDelay)
                {
                    _streakTarget = _lastCommittedDelay; // effectively at the boundary → not a candidate
                    return false;
                }

                // Target-persistence streak: the SAME candidate target must hold continuously (in frontier ticks).
                if (target != _streakTarget)
                {
                    _streakTarget    = target;
                    _streakSinceTick = currentTick;
                    return false; // new candidate — start proving itself
                }
                uint held = unchecked(currentTick - _streakSinceTick); // wrap-safe modular delta (DW-396 idiom)
                if (held < (uint)(grow ? GROW_STREAK_TICKS : SHRINK_STREAK_TICKS)) return false;

                // Dwell: minimum frontier distance since the last committed change landed (its applyAtTick).
                if (_anyChangeCommitted &&
                    unchecked(currentTick - _lastChangeTick) < (uint)(grow ? GROW_DWELL_TICKS : SHRINK_DWELL_TICKS))
                    return false;
            }

            _pending             = true;
            _pendingDelay        = target;
            _pendingApplyTick    = currentTick + (uint)SafeMargin;
            _pendingIssuedAtTick = currentTick;
            Array.Clear(_acked, 0, _capacity);

            delay       = target;
            applyAtTick = _pendingApplyTick;
            return true;
        }

        /// <summary>
        /// Record a player's ACK of the pending directive. Only counts when it matches the pending
        /// (<paramref name="delay"/>, <paramref name="applyAtTick"/>) — a stale ACK for a superseded directive is
        /// ignored. Slot is transport-authoritative; inactive/out-of-range slots are ignored.
        /// </summary>
        public void RecordAck(int slot, int delay, uint applyAtTick)
        {
            if ((uint)slot >= (uint)_capacity || !_active[slot]) return;
            if (!_pending || delay != _pendingDelay || applyAtTick != _pendingApplyTick) return;
            _acked[slot] = true;
        }

        /// <summary>
        /// True when EVERY ACTIVE player has ACKed the pending (<paramref name="delay"/>,
        /// <paramref name="applyAtTick"/>) directive. Pure predicate — no side effects (call <see cref="Commit"/>
        /// to finalize). False when no slot remains active (a survivor-less match commits nothing).
        /// </summary>
        public bool AllAcked(int delay, uint applyAtTick)
        {
            if (!_pending || delay != _pendingDelay || applyAtTick != _pendingApplyTick) return false;
            if (_activeCount <= 0) return false;
            for (int s = 0; s < _capacity; s++)
                if (_active[s] && !_acked[s]) return false;
            return true;
        }

        /// <summary>
        /// Finalize a fully-ACKed directive: advance the last-committed delay and clear the pending state so the
        /// next directive may be issued. Idempotent — returns <c>false</c> (no-op) unless the pending directive
        /// matches <paramref name="delay"/>/<paramref name="applyAtTick"/> and is fully ACKed.
        /// </summary>
        public bool Commit(int delay, uint applyAtTick)
        {
            if (!AllAcked(delay, applyAtTick)) return false;
            _lastCommittedDelay = _pendingDelay;
            // The committed change is now SCHEDULED (not yet applied) on every client; a client APPLIES it when its
            // EXEC frontier reaches applyAtTick. The confirmed high-water we gate on is the SUBMISSION frontier,
            // which leads exec by the delay (≤ MAX_DELAY) — so withhold the next directive until the submission
            // high-water passes applyAtTick + MAX_DELAY, which guarantees every client's exec has passed applyAtTick
            // (i.e. the change is APPLIED everywhere), not merely submitted (PATCH 1a maturity gate, corrected).
            _maturingApplyTick  = _pendingApplyTick + (uint)DelayMath.MAX_DELAY;
            _lastChangeTick     = _pendingApplyTick;    // DW-931: dwell is measured from where the change LANDS
            _anyChangeCommitted = true;
            _streakTarget       = _lastCommittedDelay;  // DW-931: the next candidate re-proves from scratch
            _pending            = false;
            return true;
        }
    }
}
