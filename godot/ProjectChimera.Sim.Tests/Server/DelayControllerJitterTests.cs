#nullable enable
using System;
using ProjectChimera.Multiplayer;         // DelayMath (ComputeTargetDelay, MAX_DELAY)
using ProjectChimera.Multiplayer.Server;   // DelayController
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-933 — jitter headroom. The smoothed EWMA hides variance: the 2026-08-11 Wi-Fi field run sat at ~60 ms
    /// smoothed while samples swung 55–300 ms (spikes 600+), so the dictated delay covered the MEAN and every
    /// spike stalled lockstep past the 400 ms banner threshold. The controller now tracks a per-slot mean
    /// absolute deviation EWMA (TCP RTTVAR shape, RFC 6298) and dictates from
    /// smoothed + JITTER_HEADROOM·jitter. Pins: a steady link is byte-identical to the pre-DW-933 controller
    /// (zero-seeded jitter — NOT TCP's R/2); a spiky link dictates above the smoothed-only target; jitter decays
    /// on a calmed link so the delay can come back down; a deactivated slot's jitter leaves the dictate; and the
    /// shrink deadband probes the EFFECTIVE value, so residual jitter holds a shrink back. The DW-931 damping
    /// gates and the pending/maturity desync guards are exercised untouched by the existing suites.
    /// </summary>
    public class DelayControllerJitterTests
    {
        private const int Expected = 2;

        /// <summary>The 2026-08-11 field shape: a link whose samples alternate across a wide band.</summary>
        private static void FeedSpiky(DelayController c, int slot, float lowMs, float highMs, int samples = 60)
        {
            for (int i = 0; i < samples; i++) c.RecordRtt(slot, (i % 2 == 0) ? lowMs : highMs);
        }

        /// <summary>
        /// Mirror of the controller's two EWMAs (RTT alpha ⅛, jitter alpha ¼, deviation folded against the
        /// PRE-update smoothed value — the RFC 6298 order), same float ops in the same order, so the expected
        /// effective RTT is exact, not approximate.
        /// </summary>
        private static (float smoothed, float jitter) MirrorEwma(float lowMs, float highMs, int samples = 60)
        {
            float smoothed = 0f, jitter = 0f;
            bool seen = false;
            for (int i = 0; i < samples; i++)
            {
                float s = (i % 2 == 0) ? lowMs : highMs;
                if (!seen) { smoothed = s; seen = true; continue; }
                float deviation = Math.Abs(s - smoothed);
                jitter   = jitter * 0.75f + deviation * 0.25f;
                smoothed = smoothed * 0.875f + s * 0.125f;
            }
            return (smoothed, jitter);
        }

        // ── 1) Steady link: jitter is zero → byte-identical to the pre-DW-933 controller ──

        [Fact]
        public void SteadyLink_JitterAddsNoHeadroom()
        {
            // 40 identical 80 ms samples: every deviation is 0, so the target is EXACTLY the smoothed-only 3 —
            // a +1 grow that must still be streak-gated. Any leaked headroom would push the target ≥4 (an
            // urgent grow emitting at tick 0) and fail the first assert.
            var c = new DelayController(Expected, initialDelay: 2);
            for (int i = 0; i < 40; i++) c.RecordRtt(0, 80f);
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _)); // arms the +1 grow streak — not urgent
            Assert.True(c.TryComputeDirective((uint)DelayController.GROW_STREAK_TICKS, 0u, out int delay, out _));
            Assert.Equal(3, delay);
        }

        [Fact]
        public void FirstSample_SeedsZeroJitter_NotTcpsHalfSample()
        {
            // One 80 ms sample: TCP's R/2 seeding (jitter 40) would add 4·40 = 160 ms of headroom → effective
            // 240 ms → target 5, an URGENT grow emitting instantly. Zero-seeding keeps it the old streak-gated
            // +1 grow, so a clean LAN's very first probe cannot inflate the delay.
            var c = new DelayController(Expected, initialDelay: 2);
            c.RecordRtt(0, 80f);
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _));
        }

        // ── 2) Spiky link: the dictate covers the swing band, not its mean ──

        [Fact]
        public void SpikyLink_DictatesAboveTheSmoothedOnlyTarget()
        {
            // A 60↔260 ms swing (the field log's laptop band). The smoothed mean alone maps to a modest delay;
            // the effective value must dictate strictly above it, and the jump is ≥ URGENT_GROW_STEP so it
            // emits on the first evaluation (a spike burst must widen fast).
            var c = new DelayController(Expected, initialDelay: 2);
            FeedSpiky(c, 0, 60f, 260f);
            var (smoothed, jitter) = MirrorEwma(60f, 260f);

            int smoothedOnly = DelayMath.ComputeTargetDelay(smoothed);
            Assert.True(c.TryComputeDirective(0u, 0u, out int delay, out _));
            Assert.True(delay > smoothedOnly,
                $"expected headroom above the smoothed-only target {smoothedOnly}, got {delay}");
            Assert.Equal(DelayMath.ComputeTargetDelay(smoothed + DelayController.JITTER_HEADROOM * jitter), delay);
        }

        // ── 3) Calm after the storm: jitter decays → the delay comes back down ──

        [Fact]
        public void JitterDecays_ShrinkBecomesAvailableAgain()
        {
            // Spiky series dictates wide and commits; then a long steady stretch decays both EWMAs. The shrink
            // must become available again once the DW-931 streak + dwell mature — jitter headroom must not pin
            // the match at the storm's delay forever.
            var c = new DelayController(Expected, initialDelay: 2);
            FeedSpiky(c, 0, 60f, 260f);
            Assert.True(c.TryComputeDirective(100u, 0u, out int d1, out uint a1));
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1));

            for (int i = 0; i < 200; i++) c.RecordRtt(0, 40f); // the link calms down for good

            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u; // clears the maturity gate
            uint armTick = matured + 1u;
            Assert.False(c.TryComputeDirective(armTick, confirmedTick: matured, out _, out _)); // arms the streak
            uint emitTick = Math.Max(armTick + (uint)DelayController.SHRINK_STREAK_TICKS,
                                     a1 + (uint)DelayController.SHRINK_DWELL_TICKS);
            Assert.True(c.TryComputeDirective(emitTick, confirmedTick: matured, out int d2, out _));
            Assert.True(d2 < d1, $"expected the calmed link to shrink below the storm delay {d1}, got {d2}");
        }

        // ── 4) Residual jitter holds a shrink back (the deadband probes the EFFECTIVE value) ──

        [Fact]
        public void ResidualJitter_HoldsTheShrinkBack()
        {
            // Smoothed 40 ms alone clears the delay-3 shrink deadband (40 + 12 → target 2, pinned by
            // DelayControllerDampingTests). Park jitter high enough that the EFFECTIVE value maps back to ≥3:
            // the shrink must never arm while the band is still wide, however long it persists.
            var c = new DelayController(Expected, initialDelay: 3);
            FeedSpiky(c, 0, 20f, 60f); // smoothed ≈ 40, jitter ≈ 15+ → effective ≈ 100+ → target 3
            var (smoothed, jitter) = MirrorEwma(20f, 60f);
            // Guard the premise: smoothed alone would shrink; the effective value must not.
            Assert.True(DelayMath.ComputeTargetDelay(smoothed + DelayController.SHRINK_MARGIN_MS) < 3);
            Assert.True(DelayMath.ComputeTargetDelay(
                smoothed + DelayController.JITTER_HEADROOM * jitter + DelayController.SHRINK_MARGIN_MS) >= 3);

            for (uint tick = 0; tick <= 10_000u; tick += 100u)
                Assert.False(c.TryComputeDirective(tick, 0u, out _, out _));
        }

        // ── 5) A dropped slot's jitter no longer drives the match (DW-400 composition) ──

        [Fact]
        public void DeactivatedSlot_JitterIsExcluded()
        {
            // Arm A — spiky slot ACTIVE: its swing band drives an urgent grow on the first evaluation.
            var a = new DelayController(Expected, initialDelay: 2);
            for (int i = 0; i < 40; i++) a.RecordRtt(0, 30f); // steady LAN survivor
            FeedSpiky(a, 1, 60f, 400f);
            Assert.True(a.TryComputeDirective(0u, 0u, out _, out _));

            // Arm B — same feed, spiky slot DROPPED before evaluation: the steady survivor keeps delay 2 and
            // nothing dictates (the dead peer's jitter must not keep the match wide).
            var b = new DelayController(Expected, initialDelay: 2);
            for (int i = 0; i < 40; i++) b.RecordRtt(0, 30f);
            FeedSpiky(b, 1, 60f, 400f);
            b.DeactivateSlot(1);
            for (uint tick = 0; tick < 500u; tick += 50u)
                Assert.False(b.TryComputeDirective(tick, 0u, out _, out _));
        }
    }
}
