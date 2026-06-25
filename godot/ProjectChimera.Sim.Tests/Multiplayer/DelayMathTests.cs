#nullable enable
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 1.11 (AC2a) — Adaptive-Input-Delay PURE-MATH gate. Asserts the RTT→delay computation and the
    /// cross-peer agreement rule (extracted from <see cref="LockstepManager"/> to the Godot-free
    /// <see cref="DelayMath"/>) are deterministic and commutative. This is the Tier-1 half of the
    /// Adaptive-Input-Delay smoke test; AC2b (the loopback no-desync-across-a-delay-change run) is a script run
    /// recorded in the story Change Log.
    ///
    /// The delay value is a buffering concern that NEVER enters <c>SimChecksum</c> — these tests pin the
    /// agreement INVARIANTS (same input → same delay; both peers converge to the same delay), not a hashed value.
    /// AC2c (the unclamped wire byte) is asserted AS-IS below so this suite DOCUMENTS the gap and will go red the
    /// day Story 9.4 adds the receipt re-clamp (an intentional, signposted re-baseline).
    /// </summary>
    public class DelayMathTests
    {
        // ── ComputeTargetDelay: determinism + clamp endpoints ──────────────────────────────────────

        [Fact]
        public void ComputeTargetDelay_SameRtt_IsDeterministicAcrossCalls()
        {
            // Same input → same clamped output on every call (no hidden state, no wall-clock).
            Assert.Equal(DelayMath.ComputeTargetDelay(123.4f), DelayMath.ComputeTargetDelay(123.4f));
            Assert.Equal(DelayMath.ComputeTargetDelay(0f),     DelayMath.ComputeTargetDelay(0f));
            Assert.Equal(DelayMath.ComputeTargetDelay(9999f),  DelayMath.ComputeTargetDelay(9999f));
        }

        [Fact]
        public void ComputeTargetDelay_ZeroRtt_ClampsToMinDelay()
        {
            Assert.Equal(DelayMath.MIN_DELAY, DelayMath.ComputeTargetDelay(0f));
            Assert.Equal(2, DelayMath.MIN_DELAY); // pin the documented floor
        }

        [Fact]
        public void ComputeTargetDelay_HugeRtt_ClampsToMaxDelay()
        {
            Assert.Equal(DelayMath.MAX_DELAY, DelayMath.ComputeTargetDelay(10_000f));
            Assert.Equal(12, DelayMath.MAX_DELAY); // pin the documented ceiling
        }

        [Fact]
        public void ComputeTargetDelay_IsMonotonicNonDecreasing_InRtt_AndAlwaysWithinClamp()
        {
            // A rising RTT never LOWERS the target delay, and every output stays inside [MIN_DELAY, MAX_DELAY].
            // This is the "monotonic RTT→delay table is stable" check (AC2a).
            int prev = DelayMath.ComputeTargetDelay(0f);
            for (float rtt = 0f; rtt <= 1200f; rtt += 33f)
            {
                int d = DelayMath.ComputeTargetDelay(rtt);
                Assert.InRange(d, DelayMath.MIN_DELAY, DelayMath.MAX_DELAY);
                Assert.True(d >= prev,
                    $"delay decreased as RTT rose: ComputeTargetDelay({rtt}) = {d} < previous {prev}");
                prev = d;
            }
        }

        // ── AgreeDelay: commutativity (both peers converge) + the AC2c unclamped-receipt gap ───────

        [Fact]
        public void AgreeDelay_IsCommutative_BothPeersConvergeToTheSameDelay()
        {
            // The headline AC2a case: regardless of who proposed first, both peers compute the same delay.
            Assert.Equal(5, DelayMath.AgreeDelay(3, 5));
            Assert.Equal(5, DelayMath.AgreeDelay(5, 3));
            Assert.Equal(DelayMath.AgreeDelay(3, 5), DelayMath.AgreeDelay(5, 3));

            // Exhaustive small grid: order never changes the result (the property that keeps both peers in sync).
            for (int a = DelayMath.MIN_DELAY; a <= DelayMath.MAX_DELAY; a++)
                for (int b = DelayMath.MIN_DELAY; b <= DelayMath.MAX_DELAY; b++)
                    Assert.Equal(DelayMath.AgreeDelay(a, b), DelayMath.AgreeDelay(b, a));
        }

        [Fact]
        public void AgreeDelay_DoesNotReclampTheUntrustedWireByte_AC2c_OwnedBy_Story_9_4()
        {
            // AC2c: theirDelayRaw is the untrusted wire byte (0..255). The AS-BUILT agreement does NOT re-clamp it
            // to [MIN_DELAY, MAX_DELAY] — so a forged 200 wins outright. Asserting the CURRENT behavior here lets
            // the smoke test DOCUMENT the gap; Story 9.4 (server-dictated delay + receipt re-clamp) will change
            // this result, at which point this test is updated alongside that fix (a signposted re-baseline).
            Assert.Equal(200, DelayMath.AgreeDelay(4, 200)); // NOT re-clamped to MAX_DELAY (12) — the 9.4 gap
            Assert.Equal(255, DelayMath.AgreeDelay(2, 255));
        }
    }
}
