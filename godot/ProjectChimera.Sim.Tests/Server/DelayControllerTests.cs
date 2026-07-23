#nullable enable
using ProjectChimera.Multiplayer;         // DelayMath (MIN/MAX_DELAY)
using ProjectChimera.Multiplayer.Server;   // DelayController
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.4 — the Godot-free server delay authority (<see cref="DelayController"/>): per-slot RTT EWMA, ONE
    /// dictated delay from the MAX active-slot RTT, and the directive / all-N-ACK commit state machine. Covers the
    /// I/O-matrix rows: Server RTT collect, Dictate delay, All-N-ACK, and "no directive while one is pending".
    /// </summary>
    public class DelayControllerTests
    {
        private const int Expected     = 2;
        private const int InitialDelay = 4;  // LockstepManager.INPUT_DELAY

        // ── Dictate: a high RTT emits ONE directive in [MIN_DELAY, MAX_DELAY] with a safe applyAtTick ──

        [Fact]
        public void HighRtt_EmitsOneDirective_InRange_WithSafeApplyAtTick()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f); // a big RTT → target clamps to MAX_DELAY (> InitialDelay), so a change is dictated

            Assert.True(c.TryComputeDirective(100u, confirmedTick: 0u, out int delay, out uint applyAtTick));
            Assert.InRange(delay, DelayMath.MIN_DELAY, DelayMath.MAX_DELAY);
            Assert.Equal(DelayMath.MAX_DELAY, delay);                 // 1000ms RTT → clamp ceiling
            Assert.Equal(100u + (uint)DelayController.SafeMargin, applyAtTick);
            Assert.True(DelayController.SafeMargin >= 2 * DelayMath.MAX_DELAY + 8);
        }

        [Fact]
        public void NoRtt_DictatesNothing()
        {
            var c = new DelayController(Expected, InitialDelay);
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _)); // no measured RTT yet
        }

        [Fact]
        public void TargetEqualsCommitted_DictatesNothing()
        {
            var c = new DelayController(Expected, InitialDelay);
            // An RTT that maps back to the initial delay (~4 ticks) yields no change. OWL/TICK_MS+1 == 4 ⇒ OWL≈100ms
            // ⇒ RTT≈200ms. ComputeTargetDelay(200) = ceil(100/33.33)+1 = ceil(3)+1 = 4 = InitialDelay.
            c.RecordRtt(0, 200f);
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _));
        }

        [Fact]
        public void RecordRtt_SmoothsWithEwma_DoesNotSnapToTheLastSample()
        {
            // Follow-up-review coverage: the multi-sample tests elsewhere feed IDENTICAL repeated values, which
            // converge to the same result whether RecordRtt smooths (EWMA) or just assigns the last sample — so a
            // last-sample-wins mutation would survive them. This distinguishes the two: a 1000ms spike then a 200ms
            // sample blends to 1000*0.875 + 200*0.125 = 900ms (still elevated → a change IS dictated). Under a
            // last-sample-wins mutation the slot would read 200ms (== InitialDelay, per TargetEqualsCommitted above)
            // and dictate NOTHING.
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f);
            c.RecordRtt(0, 200f); // EWMA → 900ms, NOT 200ms
            Assert.True(c.TryComputeDirective(0u, 0u, out int delay, out _));
            Assert.True(delay > InitialDelay); // smoothed 900ms stays elevated; a 200ms last-sample would be == InitialDelay → no directive

            // Cross-check the negative: a lone 200ms sample dictates nothing, proving the elevated result above came
            // from the RETAINED spike (the smoothing), not from the 200ms value.
            var single = new DelayController(Expected, InitialDelay);
            single.RecordRtt(0, 200f);
            Assert.False(single.TryComputeDirective(0u, 0u, out _, out _));
        }

        // ── No directive while one is pending un-ACKed ─────────────────────────────

        [Fact]
        public void WhilePending_NoNewDirectiveIsEmitted()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(0u, 0u, out _, out _));
            Assert.True(c.DirectivePending);

            // Even with a fresh (different) RTT, no second directive issues until the first is fully ACKed.
            c.RecordRtt(1, 600f);
            Assert.False(c.TryComputeDirective(50u, 0u, out _, out _));
        }

        // ── All-N-ACK: partial keeps pending, full commits + advances ──────────────

        [Fact]
        public void PartialAck_KeepsPending_FullAck_Commits()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(10u, 0u, out int delay, out uint at));

            c.RecordAck(0, delay, at);
            Assert.False(c.AllAcked(delay, at)); // only slot 0 → still pending
            Assert.True(c.DirectivePending);

            c.RecordAck(1, delay, at);
            Assert.True(c.AllAcked(delay, at));  // every slot ACKed
            Assert.True(c.Commit(delay, at));
            Assert.Equal(delay, c.LastCommittedDelay);
            Assert.False(c.DirectivePending);
            Assert.False(c.Commit(delay, at));   // idempotent — a second commit is a no-op
        }

        [Fact]
        public void StaleAck_ForASupersededDirective_IsIgnored()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(10u, 0u, out int delay, out uint at));

            // ACKs that do not match the pending (delay, applyAtTick) never count.
            c.RecordAck(0, delay + 1, at);
            c.RecordAck(1, delay, at + 999u);
            Assert.False(c.AllAcked(delay, at));
        }

        [Fact]
        public void AfterCommit_ANewTarget_CanBeDictatedAgain_OnceMatured()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(0u, 0u, out int d1, out uint a1));
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1)); // committed at MAX_DELAY (but only scheduled on clients — not yet matured)

            // Latency drops → the smoothed EWMA converges toward the new low RTT over several samples, at which
            // point a NEW lower target can be dictated. Feed enough samples for the EWMA (alpha 0.125) to fall well
            // below the committed-delay threshold. The prior directive's applyAtTick (a1) must be MATURED (confirmed
            // high-water past it) before the new one issues.
            c.RecordRtt(0, 0f); // invalid, ignored
            for (int i = 0; i < 40; i++) { c.RecordRtt(0, 210f); c.RecordRtt(1, 210f); }
            // Maturity requires the SUBMISSION high-water to pass applyAtTick + MAX_DELAY (the submission→exec lead),
            // not merely applyAtTick — otherwise a lagging client could still be pre-apply. See the dedicated pin below.
            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u;
            Assert.True(c.TryComputeDirective(matured + 50u, confirmedTick: matured, out int d2, out _));
            Assert.NotEqual(d1, d2);
        }

        // ── PATCH 1a: a committed directive is withheld until MATURED, not merely ACKed ────────────

        [Fact]
        public void AfterCommit_NewDirectiveWithheld_UntilPriorMatures_NotMerelyAcked()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(100u, confirmedTick: 0u, out int d1, out uint a1));
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1));      // all-ACKed + committed — but the change is only SCHEDULED on clients
            Assert.True(c.AwaitingMaturity);

            // A new target is warranted (latency dropped), but the prior directive's applyAtTick has NOT been
            // confirmed-APPLIED by the merged high-water → the new directive is WITHHELD (the pipelining-desync guard).
            for (int i = 0; i < 40; i++) { c.RecordRtt(0, 210f); c.RecordRtt(1, 210f); }
            Assert.False(c.TryComputeDirective(a1 + 5u, confirmedTick: a1 - 1u, out _, out _)); // confirmed < applyAtTick
            Assert.False(c.TryComputeDirective(a1 + 5u, confirmedTick: a1, out _, out _));      // confirmed == applyAtTick (must PASS it)
            Assert.True(c.AwaitingMaturity);

            // Once the confirmed high-water PASSES applyAtTick + MAX_DELAY, the new directive issues.
            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u;
            Assert.True(c.TryComputeDirective(matured + 50u, confirmedTick: matured, out int d2, out _));
            Assert.NotEqual(d1, d2);
            Assert.False(c.AwaitingMaturity);
        }

        // ── PATCH (follow-up review): maturity gates on APPLICATION, not merely SUBMISSION ────────────

        [Fact]
        public void MaturityGate_RequiresSubmissionFrontierToPassApplyAtTick_ByMaxDelay_NotJustApplyAtTick()
        {
            // The confirmed high-water (MergedTickBuilder.EmittedThrough) is the SUBMISSION/ISSUE frontier; a client
            // submits issueTick = execTick + delay, so it leads the EXEC frontier — where a scheduled delay change
            // actually applies — by the delay (≤ MAX_DELAY). If the gate cleared at merely `confirmed > applyAtTick`,
            // a lagging client whose exec = applyAtTick + 1 − delay < applyAtTick would still have the prior change
            // PENDING; a second directive would then be dropped on it (PATCH 1b) while fast clients applied it →
            // two clients hold different delays → command-slot divergence → desync. So the gate must not clear until
            // the submission frontier has passed applyAtTick + MAX_DELAY (⇒ every client's exec > applyAtTick).
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(100u, confirmedTick: 0u, out int d1, out uint a1));
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1));
            for (int i = 0; i < 40; i++) { c.RecordRtt(0, 210f); c.RecordRtt(1, 210f); }

            // Submission frontier PAST applyAtTick but NOT yet past applyAtTick + MAX_DELAY → still withheld, because
            // the slowest client's exec (= confirmed − delay) may not have reached applyAtTick yet.
            for (uint confirmed = a1 + 1u; confirmed <= a1 + (uint)DelayMath.MAX_DELAY; confirmed++)
            {
                Assert.False(c.TryComputeDirective(confirmed + 50u, confirmedTick: confirmed, out _, out _));
                Assert.True(c.AwaitingMaturity);
            }

            // One tick past applyAtTick + MAX_DELAY → every client's exec has provably passed applyAtTick → issue.
            Assert.True(c.TryComputeDirective(a1 + 100u, confirmedTick: a1 + (uint)DelayMath.MAX_DELAY + 1u, out _, out _));
            Assert.False(c.AwaitingMaturity);
        }

        // ── RTT collect: invalid samples ignored, MAX drives the dictate ───────────

        [Fact]
        public void InvalidRttSamples_AreIgnored()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 0f);      // non-positive → ignored
            c.RecordRtt(0, -5f);     // negative → ignored
            c.RecordRtt(0, 99999f);  // implausibly large → ignored
            c.RecordRtt(9, 1000f);   // out-of-range slot → ignored
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _)); // nothing valid recorded → no dictate
        }

        [Fact]
        public void DictatesFromTheWorstSlotRtt()
        {
            var c = new DelayController(Expected, InitialDelay);
            c.RecordRtt(0, 200f);  // ~4 ticks
            c.RecordRtt(1, 1000f); // ~12 ticks — the max, which must drive the dictate
            Assert.True(c.TryComputeDirective(0u, 0u, out int delay, out _));
            Assert.Equal(DelayMath.MAX_DELAY, delay);
        }
    }
}
