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
        /// The cross-peer agreement rule: both peers converge on <c>max(myDesired, theirDelay)</c>. This is
        /// COMMUTATIVE — <c>AgreeDelay(a, b) == AgreeDelay(b, a)</c> — which is exactly why both peers pick the
        /// SAME delay regardless of who proposed first (the invariant AC2a asserts).
        ///
        /// AC2c — KNOWN GAP (do NOT fix here; owned by Story 9.4): <paramref name="theirDelayRaw"/> is the
        /// untrusted wire byte and is deliberately NOT re-clamped to [MIN_DELAY, MAX_DELAY] here — a forged
        /// proposal could push the agreed delay past BUFFER_SIZE and corrupt the ring buffer. Story 9.4 adds the
        /// server-dictated delay + receipt re-clamp + ACK-commit. This helper preserves the as-built behavior
        /// verbatim so the smoke test can DOCUMENT the gap; <c>DelayMathTests</c> asserts this CURRENT unclamped
        /// result and will flag when 9.4 changes it.
        /// </summary>
        internal static int AgreeDelay(int myDesired, int theirDelayRaw) => Math.Max(myDesired, theirDelayRaw);
    }
}
