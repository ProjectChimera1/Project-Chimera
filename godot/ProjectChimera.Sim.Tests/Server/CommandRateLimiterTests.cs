#nullable enable
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.13 — the per-slot command-rate throttle (anti-spam). Proves every I/O-matrix row with injected
    /// synthetic wall-clock ms: legitimate sustained + short bursts are fully admitted; a flood is admitted up to the
    /// cap then dropped silently (admitted == cap, DroppedCount == overflow); the window recovers after WINDOW_MS
    /// elapses; slots are fully isolated; Reset clears a slot's window/count (a recycled slot starts clean); and an
    /// out-of-range slot is always rejected.
    /// </summary>
    public class CommandRateLimiterTests
    {
        [Fact]
        public void LegitSustained_30Tps_AllAdmitted()
        {
            var lim = new CommandRateLimiter(4);

            // 1 packet/slot every ~33ms (30 tps) over 5 simulated seconds → 150 packets, all admitted.
            ulong now = 1_000_000; // start at an arbitrary high wall-clock, as Time.GetTicksMsec would report
            int admitted = 0;
            for (int i = 0; i < 150; i++)
            {
                if (lim.TryAdmit(0, now)) admitted++;
                now += 33;
            }

            Assert.Equal(150, admitted);
            Assert.Equal(0L, lim.DroppedCount(0));
        }

        [Fact]
        public void LegitBurst_DelayPipelineCatchup_AllAdmitted()
        {
            var lim = new CommandRateLimiter(4);

            // A burst of MAX_DELAY(12)+ packets from one slot at the SAME ms (delay-pipeline catch-up). Well below
            // the 60-per-window cap → all admitted.
            const ulong now = 500_000;
            int admitted = 0;
            for (int i = 0; i < 20; i++)
                if (lim.TryAdmit(1, now)) admitted++;

            Assert.Equal(20, admitted);
            Assert.Equal(0L, lim.DroppedCount(1));
        }

        [Fact]
        public void SpamFlood_AdmitsUpToCapThenDropsSilently()
        {
            var lim = new CommandRateLimiter(4);

            // Hundreds of packets from one slot at the SAME ms → first MAX_COMMANDS_PER_WINDOW admitted, remainder
            // dropped.
            const ulong now = 42_000;
            const int flood = 500;
            int admitted = 0;
            for (int i = 0; i < flood; i++)
                if (lim.TryAdmit(0, now)) admitted++;

            Assert.Equal(CommandRateLimiter.MAX_COMMANDS_PER_WINDOW, admitted);
            Assert.Equal(flood - CommandRateLimiter.MAX_COMMANDS_PER_WINDOW, lim.DroppedCount(0));
        }

        [Fact]
        public void WindowRecovery_AdmissionsResumeAfterWindowElapses()
        {
            var lim = new CommandRateLimiter(4);
            ulong now = 10_000;

            // Fill the window to the cap.
            for (int i = 0; i < CommandRateLimiter.MAX_COMMANDS_PER_WINDOW; i++)
                Assert.True(lim.TryAdmit(2, now));
            Assert.False(lim.TryAdmit(2, now)); // now capped

            // Advance past WINDOW_MS → a fresh window; admissions resume.
            now += CommandRateLimiter.WINDOW_MS;
            Assert.True(lim.TryAdmit(2, now));
            Assert.True(lim.TryAdmit(2, now)); // and the next one, in the new window
        }

        [Fact]
        public void WindowBoundary_JustBeforeElapse_StillCapped()
        {
            var lim = new CommandRateLimiter(4);
            ulong start = 10_000;

            for (int i = 0; i < CommandRateLimiter.MAX_COMMANDS_PER_WINDOW; i++)
                Assert.True(lim.TryAdmit(0, start));

            // One ms shy of the window length → still the same window → dropped.
            Assert.False(lim.TryAdmit(0, start + (CommandRateLimiter.WINDOW_MS - 1)));
            // Exactly WINDOW_MS later → window rolls over → admitted.
            Assert.True(lim.TryAdmit(0, start + CommandRateLimiter.WINDOW_MS));
        }

        [Fact]
        public void PerSlotIsolation_OneFloodedSlotDoesNotAffectAnother()
        {
            var lim = new CommandRateLimiter(4);
            const ulong now = 7_000;

            // Slot 0 floods and caps out.
            for (int i = 0; i < 300; i++) lim.TryAdmit(0, now);
            Assert.True(lim.DroppedCount(0) > 0);

            // Slot 1 sends a legitimate rate at the same ms → every packet admitted, unaffected by slot 0.
            int admitted = 0;
            for (int i = 0; i < 30; i++)
                if (lim.TryAdmit(1, now)) admitted++;

            Assert.Equal(30, admitted);
            Assert.Equal(0L, lim.DroppedCount(1));
        }

        [Fact]
        public void Reset_ClearsWindowAndCount_ButNotLifetimeDropTally()
        {
            var lim = new CommandRateLimiter(4);
            const ulong now = 3_000;

            // Cap out slot 2 and accrue drops.
            for (int i = 0; i < 100; i++) lim.TryAdmit(2, now);
            long droppedBefore = lim.DroppedCount(2);
            Assert.True(droppedBefore > 0);
            Assert.False(lim.TryAdmit(2, now)); // still capped

            // Reset (slot reuse): window/count cleared so the reused slot admits immediately at the SAME ms...
            lim.Reset(2);
            Assert.True(lim.TryAdmit(2, now));

            // ...but the lifetime diagnostic tally is preserved (grew by 1 from the still-capped probe above).
            Assert.Equal(droppedBefore + 1, lim.DroppedCount(2));
        }

        [Fact]
        public void OutOfRangeSlot_AlwaysRejected_NoThrow()
        {
            var lim = new CommandRateLimiter(4);

            Assert.False(lim.TryAdmit(-1, 1_000));
            Assert.False(lim.TryAdmit(4, 1_000));   // == Slots (0..3 valid)
            Assert.False(lim.TryAdmit(9999, 1_000));

            // DroppedCount / Reset on an out-of-range slot are safe no-ops.
            Assert.Equal(0L, lim.DroppedCount(-1));
            Assert.Equal(0L, lim.DroppedCount(4));
            lim.Reset(-1);
            lim.Reset(4);
        }

        [Fact]
        public void CapFloor_SitsAboveWorstCaseLegitimatePlay()
        {
            // Documents the floor: the cap must be >= 2× the 30 tps sustained rate.
            Assert.True(CommandRateLimiter.MAX_COMMANDS_PER_WINDOW >= 2 * 30);
            Assert.Equal(1000, CommandRateLimiter.WINDOW_MS);
        }

        [Fact]
        public void Ctor_RejectsNonPositiveSlotCount()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CommandRateLimiter(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CommandRateLimiter(-1));
        }
    }
}
