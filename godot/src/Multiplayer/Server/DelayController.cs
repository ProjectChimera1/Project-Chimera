#nullable enable
using System;

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
    ///   • Per-slot smoothed RTT (EWMA) from the server's Ping→Pong probes (<see cref="RecordRtt"/>).
    ///   • Compute ONE dictated delay for the whole match from the MAX active-slot RTT via the existing
    ///     <see cref="DelayMath.ComputeTargetDelay"/> (<see cref="TryComputeDirective"/>), emitted only when it
    ///     differs from the last committed value AND no directive is currently pending un-ACKed.
    ///   • Drive the all-N-ACK commit state machine (<see cref="RecordAck"/> / <see cref="AllAcked"/> /
    ///     <see cref="Commit"/>): a new directive cannot be issued until every player has ACKed the pending one.
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

        /// <summary>Number of player slots (sub-bundles) this controller tracks — [1, ...].</summary>
        public int Expected { get; }

        private readonly float[] _smoothedRtt;
        private readonly bool[]  _rttSeen;

        // ── Directive / ACK state machine ─────────────────────────────────────
        private int  _lastCommittedDelay;
        private bool _pending;
        private int  _pendingDelay;
        private uint _pendingApplyTick;
        private readonly bool[] _acked;

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

        /// <summary>The delay the server has last confirmed all players committed (starts at the initial delay).</summary>
        public int LastCommittedDelay => _lastCommittedDelay;

        /// <summary>True while a dictated directive is awaiting all-N ACKs.</summary>
        public bool DirectivePending => _pending;

        /// <summary>True while a committed directive has not yet matured (been APPLIED) on all clients — the
        /// confirmed submission high-water has not yet passed its applyAtTick + the MAX_DELAY submission→exec lead.
        /// A new directive is withheld until then.</summary>
        public bool AwaitingMaturity => _maturingApplyTick != 0;

        /// <param name="expected">Number of player slots (in [1, ...]).</param>
        /// <param name="initialDelay">The delay the match starts at (LockstepManager.INPUT_DELAY) — the baseline the
        /// first dictated value is compared against so no directive is emitted until the target genuinely changes.</param>
        public DelayController(int expected, int initialDelay)
        {
            if (expected < 1)
                throw new ArgumentOutOfRangeException(nameof(expected), expected, "expected must be >= 1.");

            Expected            = expected;
            _lastCommittedDelay = initialDelay;
            _smoothedRtt        = new float[expected];
            _rttSeen            = new bool[expected];
            _acked              = new bool[expected];
        }

        /// <summary>
        /// Fold a fresh RTT sample for <paramref name="slot"/> into its smoothed EWMA. A stale/invalid sample
        /// (non-positive or implausibly large) is ignored. Out-of-range slots are ignored (transport-authoritative
        /// slot — never trusted from a packet byte).
        /// </summary>
        public void RecordRtt(int slot, float rttMs)
        {
            if ((uint)slot >= (uint)Expected) return;
            if (rttMs <= 0f || rttMs > 10_000f) return; // sanity-check (mirrors LockstepManager.HandlePong)

            if (!_rttSeen[slot])
            {
                _smoothedRtt[slot] = rttMs;
                _rttSeen[slot]     = true;
            }
            else
            {
                _smoothedRtt[slot] = _smoothedRtt[slot] * (1f - RTT_ALPHA) + rttMs * RTT_ALPHA;
            }
        }

        /// <summary>
        /// Try to produce the next server-dictated delay directive. Emits (returns <c>true</c> with
        /// <paramref name="delay"/> in [MIN_DELAY, MAX_DELAY] and an <paramref name="applyAtTick"/> =
        /// <paramref name="currentTick"/> + <see cref="SafeMargin"/>) ONLY when: at least one slot has a measured
        /// RTT, no directive is already pending un-ACKed, the PRIOR committed directive has matured (been APPLIED) on
        /// all clients (<paramref name="confirmedTick"/> — the merged fan-in high-water — has passed its applyAtTick
        /// plus the MAX_DELAY submission→exec lead), and the computed delay differs from the last committed value.
        /// Otherwise returns <c>false</c> without changing state. On emission the pending directive is stored and its
        /// ACK set is reset.
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

            // Dictate from the WORST (max) active-slot RTT so the delay covers the highest-latency player.
            float maxRtt = 0f;
            bool any = false;
            for (int s = 0; s < Expected; s++)
            {
                if (!_rttSeen[s]) continue;
                any = true;
                if (_smoothedRtt[s] > maxRtt) maxRtt = _smoothedRtt[s];
            }
            if (!any) return false; // no RTT measured yet → nothing to dictate

            int target = DelayMath.ComputeTargetDelay(maxRtt); // already clamped to [MIN_DELAY, MAX_DELAY]
            if (target == _lastCommittedDelay) return false;    // no change needed

            _pending          = true;
            _pendingDelay     = target;
            _pendingApplyTick = currentTick + (uint)SafeMargin;
            Array.Clear(_acked, 0, Expected);

            delay       = target;
            applyAtTick = _pendingApplyTick;
            return true;
        }

        /// <summary>
        /// Record a player's ACK of the pending directive. Only counts when it matches the pending
        /// (<paramref name="delay"/>, <paramref name="applyAtTick"/>) — a stale ACK for a superseded directive is
        /// ignored. Slot is transport-authoritative; out-of-range slots are ignored.
        /// </summary>
        public void RecordAck(int slot, int delay, uint applyAtTick)
        {
            if ((uint)slot >= (uint)Expected) return;
            if (!_pending || delay != _pendingDelay || applyAtTick != _pendingApplyTick) return;
            _acked[slot] = true;
        }

        /// <summary>
        /// True when EVERY player has ACKed the pending (<paramref name="delay"/>, <paramref name="applyAtTick"/>)
        /// directive. Pure predicate — no side effects (call <see cref="Commit"/> to finalize).
        /// </summary>
        public bool AllAcked(int delay, uint applyAtTick)
        {
            if (!_pending || delay != _pendingDelay || applyAtTick != _pendingApplyTick) return false;
            for (int s = 0; s < Expected; s++)
                if (!_acked[s]) return false;
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
            _pending            = false;
            return true;
        }
    }
}
