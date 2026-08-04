#nullable enable
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-392 — the per-peer protocol-violation counter that BOUNDS attacker-triggerable log writes on the
    /// dedicated server's misbehavior arms (a client-sent TickCommandsMerged spoof, undecodable
    /// Chat/LobbyChat/MapPing, merged fan-in Submit drops). Proves: the 1st violation logs immediately; then only
    /// every LOG_EVERY-th logs (a wire-rate flood of V violations yields exactly 1 + V/LOG_EVERY log decisions —
    /// the pre-fix behavior was one log line PER packet); slots are isolated; Reset gives a recycled slot's new
    /// occupant a clean tally; out-of-range slots are fail-quiet no-ops.
    /// </summary>
    public class ProtocolViolationTrackerTests
    {
        [Fact]
        public void FirstViolation_LogsImmediately()
        {
            var t = new ProtocolViolationTracker(4);

            Assert.True(t.Record(0)); // low-volume misbehavior must still leave an immediate trace
            Assert.Equal(1L, t.Count(0));
        }

        [Fact]
        public void SecondThroughLogEveryMinusOne_Suppressed_ThenLogEveryThLogs()
        {
            var t = new ProtocolViolationTracker(4);

            Assert.True(t.Record(1)); // 1st
            for (long c = 2; c < ProtocolViolationTracker.LOG_EVERY; c++)
                Assert.False(t.Record(1)); // 2..127 suppressed
            Assert.True(t.Record(1));      // the LOG_EVERY-th logs again
            Assert.Equal(ProtocolViolationTracker.LOG_EVERY, t.Count(1));
        }

        [Fact]
        public void Flood_LogWritesBoundedToOnePlusVOverLogEvery()
        {
            // The DW-392 defect: one PrintErr PER spoofed packet = a soft log-write DoS. Prove the bound: a
            // 10_000-violation flood yields exactly 1 + 10_000/128 = 79 log decisions, while the tally still
            // records every violation.
            var t = new ProtocolViolationTracker(2);
            const int flood = 10_000;

            int logged = 0;
            for (int i = 0; i < flood; i++)
                if (t.Record(0)) logged++;

            Assert.Equal(1 + flood / (int)ProtocolViolationTracker.LOG_EVERY, logged);
            Assert.Equal((long)flood, t.Count(0));
        }

        [Fact]
        public void PerSlotIsolation_OneSlotsFloodDoesNotConsumeAnothersFirstLog()
        {
            var t = new ProtocolViolationTracker(4);

            for (int i = 0; i < 500; i++) t.Record(0);

            Assert.True(t.Record(2)); // slot 2's FIRST violation still logs immediately
            Assert.Equal(1L, t.Count(2));
            Assert.Equal(500L, t.Count(0));
        }

        [Fact]
        public void Reset_ClearsTheTally_RecycledSlotStartsClean()
        {
            var t = new ProtocolViolationTracker(4);
            for (int i = 0; i < 300; i++) t.Record(3);
            Assert.Equal(300L, t.Count(3));

            // Slot recycled (a new occupant connects): the new peer is NOT judged by its predecessor...
            t.Reset(3);
            Assert.Equal(0L, t.Count(3));

            // ...and its own first violation logs immediately again.
            Assert.True(t.Record(3));
            Assert.Equal(1L, t.Count(3));
        }

        [Fact]
        public void OutOfRangeSlot_FailQuiet_NoThrowNeverLogs()
        {
            var t = new ProtocolViolationTracker(4);

            Assert.False(t.Record(-1));
            Assert.False(t.Record(4));    // == Slots (0..3 valid)
            Assert.False(t.Record(9999));
            Assert.Equal(0L, t.Count(-1));
            Assert.Equal(0L, t.Count(4));
            t.Reset(-1);
            t.Reset(4);
        }

        [Fact]
        public void Ctor_RejectsNonPositiveSlotCount()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ProtocolViolationTracker(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ProtocolViolationTracker(-1));
        }

        [Fact]
        public void LogCadence_MatchesStory913ThrottleDiagnostic()
        {
            // The DedicatedServer's RATE_LIMIT_LOG_EVERY (Story 9.13) is 128; the violation cadence deliberately
            // matches so every bounded server diagnostic shares one rhythm.
            Assert.Equal(128L, ProtocolViolationTracker.LOG_EVERY);
        }
    }
}
