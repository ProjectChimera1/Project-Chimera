#nullable enable
using System;
using ProjectChimera.Multiplayer;         // DelayMath (MIN/MAX_DELAY)
using ProjectChimera.Multiplayer.Server;   // DelayController
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-934 — the network-stability floor. The adaptive path (DW-933) is reactive — it learns a link's swing
    /// band from ~0.5 Hz probes, so the first spike of every cluster still stalls before the cushion widens. The
    /// floor is the WC3 answer: the host's <c>network_stability</c> setting pins a minimum dictated delay
    /// (`balanced` = 6 ticks / 200 ms, `stable` = 9 ticks / 300 ms) that is simply always there. Pins: the
    /// closed setting→ticks vocabulary; a floor above the start delay dictates urgently at match start even on a
    /// pristine LAN; a floored match never shrinks below the floor however quiet the link; the adaptive path
    /// still grows ABOVE the floor and shrinks back TO it (not below); an out-of-band floor is clamped; and the
    /// default floor is a no-op (the whole pre-existing suite runs on it).
    /// </summary>
    public class DelayControllerStabilityFloorTests
    {
        private const int Expected     = 2;
        private const int InitialDelay = 4;  // LockstepManager.INPUT_DELAY — what clients hard-start at

        [Fact]
        public void StabilityFloorTicks_ClosedVocabulary_UnknownFallsBackToAdaptive()
        {
            Assert.Equal(DelayMath.MIN_DELAY,                        DelayController.StabilityFloorTicks("responsive"));
            Assert.Equal(DelayController.BALANCED_FLOOR_TICKS,       DelayController.StabilityFloorTicks("balanced"));
            Assert.Equal(DelayController.STABLE_FLOOR_TICKS,         DelayController.StabilityFloorTicks("stable"));
            Assert.Equal(DelayMath.MIN_DELAY,                        DelayController.StabilityFloorTicks("garbage"));
            Assert.Equal(DelayMath.MIN_DELAY,                        DelayController.StabilityFloorTicks(null));
            Assert.Equal(DelayMath.MIN_DELAY,                        DelayController.StabilityFloorTicks(""));
        }

        [Fact]
        public void FloorAboveStartDelay_DictatesUrgently_EvenOnAPristineLan()
        {
            // Stable floor 9 vs the INPUT_DELAY-4 start: the very first evaluation after the first probe must
            // dictate the floor (a ≥2-tick grow → urgent, no streak/dwell wait), even though the measured link
            // (10 ms, zero jitter) wants delay 2. This is the one match-start renegotiation the floor costs.
            var c = new DelayController(Expected, InitialDelay, DelayController.STABLE_FLOOR_TICKS);
            c.RecordRtt(0, 10f);
            Assert.True(c.TryComputeDirective(0u, 0u, out int delay, out _));
            Assert.Equal(DelayController.STABLE_FLOOR_TICKS, delay);
        }

        [Fact]
        public void FlooredMatch_NeverShrinksBelowTheFloor_HoweverQuietTheLink()
        {
            // Commit the floor, then hold a pristine link for 20k ticks of evaluations: the target computes to
            // max(2, floor) == the committed value every time, so nothing may ever dictate again.
            var c = new DelayController(Expected, InitialDelay, DelayController.STABLE_FLOOR_TICKS);
            c.RecordRtt(0, 10f);
            Assert.True(c.TryComputeDirective(100u, 0u, out int d1, out uint a1));
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1));

            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u;
            for (uint tick = matured; tick < matured + 20_000u; tick += 500u)
                Assert.False(c.TryComputeDirective(tick, confirmedTick: matured, out _, out _));
        }

        [Fact]
        public void AboveTheFloor_TheAdaptivePathStillGoverns_BothDirections()
        {
            // A genuinely bad link must still grow PAST the floor, and the calm-down shrink must land back ON
            // the floor (not below it) after the DW-931 streak + dwell mature.
            var c = new DelayController(Expected, InitialDelay, DelayController.BALANCED_FLOOR_TICKS);
            c.RecordRtt(0, 1000f); // sustained terrible RTT → target MAX_DELAY, above the floor
            Assert.True(c.TryComputeDirective(100u, 0u, out int d1, out uint a1));
            Assert.Equal(DelayMath.MAX_DELAY, d1);
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1));

            for (int i = 0; i < 200; i++) c.RecordRtt(0, 10f); // the link recovers for good

            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u;
            uint armTick = matured + 1u;
            Assert.False(c.TryComputeDirective(armTick, confirmedTick: matured, out _, out _)); // arms the streak
            uint emitTick = Math.Max(armTick + (uint)DelayController.SHRINK_STREAK_TICKS,
                                     a1 + (uint)DelayController.SHRINK_DWELL_TICKS);
            Assert.True(c.TryComputeDirective(emitTick, confirmedTick: matured, out int d2, out _));
            Assert.Equal(DelayController.BALANCED_FLOOR_TICKS, d2); // back to the floor, never through it
        }

        [Fact]
        public void OutOfBandFloor_IsClampedIntoTheDelayBand()
        {
            // 99 clamps to MAX_DELAY (a floor cannot exceed the ring-safe ceiling)…
            var high = new DelayController(Expected, InitialDelay, 99);
            high.RecordRtt(0, 10f);
            Assert.True(high.TryComputeDirective(0u, 0u, out int delay, out _));
            Assert.Equal(DelayMath.MAX_DELAY, delay);

            // …and 0/negative clamps to MIN_DELAY — a no-op floor, identical to the default (from a committed
            // delay of 2 and a pristine link, target == committed → nothing dictates).
            var low = new DelayController(Expected, initialDelay: 2, minDelayFloor: -3);
            low.RecordRtt(0, 10f);
            Assert.False(low.TryComputeDirective(0u, 0u, out _, out _));
        }
    }
}
