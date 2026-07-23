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
    /// AC2c (the untrusted wire byte) is now RE-CLAMPED (Story 9.4 landed the receipt re-clamp): the test below is
    /// the signposted re-baseline — a forged 200 is clamped to MAX_DELAY before the max, so it can no longer push
    /// the agreed delay past BUFFER_SIZE and corrupt the ring.
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
        public void AgreeDelay_ReclampsTheUntrustedWireByte_AC2c_ClosedBy_Story_9_4()
        {
            // AC2c (CLOSED): theirDelayRaw is the untrusted wire byte (0..255). AgreeDelay now re-clamps it to
            // [MIN_DELAY, MAX_DELAY] via ClampDelay BEFORE the max — so a forged 200/255 is clamped to MAX_DELAY
            // (12) and can never push the agreed delay past BUFFER_SIZE (16) and corrupt the ring.
            Assert.Equal(DelayMath.MAX_DELAY, DelayMath.AgreeDelay(4, 200)); // max(4, clamp(200)=12) = 12
            Assert.Equal(DelayMath.MAX_DELAY, DelayMath.AgreeDelay(2, 255)); // max(2, clamp(255)=12) = 12
            Assert.Equal(12, DelayMath.MAX_DELAY);                           // pin the ceiling the clamp enforces
            // A forged LOW byte is clamped up to MIN_DELAY, then the honest desire still wins the max.
            Assert.Equal(7, DelayMath.AgreeDelay(7, 0));                     // max(7, clamp(0)=2) = 7
            Assert.Equal(DelayMath.MIN_DELAY, DelayMath.AgreeDelay(2, 0));   // max(2, clamp(0)=2) = 2
        }

        // ── ClampDelay: the receipt-side hardening primitive (Story 9.4) ────────────────────────────

        [Fact]
        public void ClampDelay_ClampsAnyRawValueIntoTheSafeRingRange()
        {
            Assert.Equal(DelayMath.MIN_DELAY, DelayMath.ClampDelay(0));    // below floor → MIN
            Assert.Equal(DelayMath.MIN_DELAY, DelayMath.ClampDelay(-99));  // negative (int) → MIN
            Assert.Equal(DelayMath.MAX_DELAY, DelayMath.ClampDelay(200));  // forged high byte → MAX
            Assert.Equal(DelayMath.MAX_DELAY, DelayMath.ClampDelay(255));  // wire-byte max → MAX
            Assert.Equal(5, DelayMath.ClampDelay(5));                      // in-range → unchanged
            // Every clamped result stays strictly below BUFFER_SIZE (16) so the ring can never be corrupted.
            for (int raw = -5; raw <= 300; raw++)
                Assert.InRange(DelayMath.ClampDelay(raw), DelayMath.MIN_DELAY, DelayMath.MAX_DELAY);
        }

        [Fact]
        public void ResolveDirectiveReceipt_ClampsBothTheAppliedDelayAndTheAckEcho()
        {
            // The Godot-free receipt decision the client's DelayDirective handler delegates to (Tier-1 proof of the
            // headline hardening). A forged 200 → applied delay AND ACK echo both clamp to MAX_DELAY (12); a forged
            // low byte clamps up to MIN_DELAY; both returned values stay in [MIN_DELAY, MAX_DELAY].
            var forgedHigh = DelayMath.ResolveDirectiveReceipt(200);
            Assert.Equal(DelayMath.MAX_DELAY, forgedHigh.appliedDelay);
            Assert.Equal(DelayMath.MAX_DELAY, forgedHigh.ackEcho);
            Assert.Equal(12, DelayMath.MAX_DELAY);

            var forgedLow = DelayMath.ResolveDirectiveReceipt(0);
            Assert.Equal(DelayMath.MIN_DELAY, forgedLow.appliedDelay);
            Assert.Equal(DelayMath.MIN_DELAY, forgedLow.ackEcho);

            var inRange = DelayMath.ResolveDirectiveReceipt(6);
            Assert.Equal(6, inRange.appliedDelay);
            Assert.Equal(6, inRange.ackEcho);

            // The applied delay and the ACK echo are always the SAME clamped value (the server confirms exactly
            // what the client committed) and always inside the safe ring range.
            for (int raw = 0; raw <= 255; raw++)
            {
                var (applied, ack) = DelayMath.ResolveDirectiveReceipt(raw);
                Assert.Equal(applied, ack);
                Assert.InRange(applied, DelayMath.MIN_DELAY, DelayMath.MAX_DELAY);
            }
        }
    }
}
