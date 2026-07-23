#nullable enable
using System;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Godot-free pure-math core of the adaptive input-delay policy, extracted from <see cref="LockstepManager"/>
    /// (Story 1.11, AC2a) so the RTT→delay computation and the cross-peer agreement rule are Tier-1 unit-testable
    /// without Godot/ENet. <see cref="LockstepManager"/> now calls into these — a behavior-neutral extraction.
    ///
    /// The delay value is a latency/buffering concern: it is NEVER folded into
    /// <see cref="ProjectChimera.Core.SimChecksum"/> (it only shifts WHICH buffer slot a command lands in, not sim
    /// state). So the <c>float</c> RTT math here is intentionally NOT determinism-critical — the 1.10b advisory
    /// analyzer (CHM0001) flags the <c>float</c>, which is expected and correct. What MUST hold is that both peers
    /// pick the SAME delay and apply it at the SAME tick: <see cref="AgreeDelay"/>'s commutativity plus
    /// LockstepManager's apply-tick handshake guarantee that.
    /// </summary>
    internal static class DelayMath
    {
        /// <summary>Minimum input delay (ticks) — safe LAN floor (≈ 66 ms at 30 Hz). The clamp's lower bound.</summary>
        internal const int MIN_DELAY = 2;

        /// <summary>
        /// Maximum input delay (ticks) — ≈ 400 ms at 30 Hz. The clamp's upper bound. LockstepManager's
        /// <c>BUFFER_SIZE</c> (16) MUST stay a power of two greater than <see cref="MAX_DELAY"/> + 1.
        /// </summary>
        internal const int MAX_DELAY = 12;

        /// <summary>Milliseconds per sim tick at the fixed 30 Hz simulation rate.</summary>
        internal const float TICK_MS = 1000f / 30f;

        /// <summary>
        /// Compute the ideal input delay from the current smoothed RTT: <c>ceil(OWL / TICK_MS) + 1</c>, clamped to
        /// [<see cref="MIN_DELAY"/>, <see cref="MAX_DELAY"/>]. OWL (one-way latency) = RTT / 2; the +1 is a
        /// one-tick safety margin. Lifted verbatim from LockstepManager so the clamp endpoints are pinned by
        /// AC2a's test (input 0 → MIN_DELAY; a huge RTT → MAX_DELAY). Same input → same output (deterministic on a
        /// given machine).
        /// </summary>
        internal static int ComputeTargetDelay(float smoothedRttMs)
        {
            float owlMs = smoothedRttMs / 2f;
            int ticks   = (int)Math.Ceiling(owlMs / TICK_MS);
            return Math.Clamp(ticks + 1, MIN_DELAY, MAX_DELAY);
        }

        /// <summary>
        /// Story 9.4 — clamp an UNTRUSTED delay value (a wire byte from a peer's proposal or a server directive)
        /// into the safe ring-buffer range [<see cref="MIN_DELAY"/>, <see cref="MAX_DELAY"/>] ⊂ [1, BUFFER_SIZE-1].
        /// The single hardening primitive shared by <see cref="AgreeDelay"/> (P2P path) and the client's
        /// <c>DelayDirective</c> receipt (server-dictated path): a forged/corrupt value can never push the applied
        /// delay past BUFFER_SIZE and corrupt the ring. Deterministic — every peer clamps an identical directive
        /// identically, so all commit the same delay at the same tick.
        /// </summary>
        internal static int ClampDelay(int raw) => Math.Clamp(raw, MIN_DELAY, MAX_DELAY);

        /// <summary>
        /// Story 9.4 — the Godot-free server-dictated-delay RECEIPT decision, extracted from
        /// <c>LockstepManager.HandleDelayDirective</c> so the headline hardening (the receipt re-clamp) is Tier-1
        /// testable. Maps an untrusted directive delay byte to BOTH the delay the client applies AND the value it
        /// echoes in its <c>DelayAck</c> — both the same clamped value, so a forged 200/255 can never push either
        /// past BUFFER_SIZE and corrupt the ring. Returns <c>(appliedDelay, ackEcho)</c>.
        /// </summary>
        internal static (int appliedDelay, int ackEcho) ResolveDirectiveReceipt(int rawDelay)
        {
            int clamped = ClampDelay(rawDelay);
            return (clamped, clamped);
        }

        /// <summary>
        /// The cross-peer agreement rule: both peers converge on <c>max(myDesired, clamp(theirDelay))</c>. This is
        /// COMMUTATIVE over valid inputs — <c>AgreeDelay(a, b) == AgreeDelay(b, a)</c> — which is exactly why both
        /// peers pick the SAME delay regardless of who proposed first (the invariant AC2a asserts).
        ///
        /// AC2c (Story 9.4 — CLOSED): <paramref name="theirDelayRaw"/> is the untrusted wire byte and is now
        /// re-clamped to [<see cref="MIN_DELAY"/>, <see cref="MAX_DELAY"/>] via <see cref="ClampDelay"/> before the
        /// max — a forged proposal (e.g. 200) can no longer push the agreed delay past BUFFER_SIZE and corrupt the
        /// ring buffer. The same clamp guards the server-dictated <c>DelayDirective</c> receipt on the client.
        /// </summary>
        internal static int AgreeDelay(int myDesired, int theirDelayRaw) => Math.Max(myDesired, ClampDelay(theirDelayRaw));
    }
}
