using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-912 — the online pacer's contract. The bug these pin: the online loop stepped once per RENDERED FRAME, so
    /// a 252 FPS client ran the sim at ~252 ticks/sec and sent ~252 command packets/sec into a server that admits 60,
    /// silently dropping the 61st. That packet carried tick 64 (60 admitted + INPUT_DELAY 4), its merge never
    /// completed, and both machines hung on tick 64 permanently.
    ///
    /// The load-bearing assertion is <see cref="SendRate_OverOneSecond_StaysUnderServerThrottle"/> — it ties the
    /// pacer directly to <see cref="CommandRateLimiter.MAX_COMMANDS_PER_WINDOW"/>, so if either constant is ever
    /// changed into a deadlock again, this test goes red instead of a LAN run doing it.
    /// </summary>
    public class LockstepPacerTests
    {
        private const float DT = SimulationLoop.DT_SECONDS;

        /// <summary>Drain every tick the pacer currently owes (the caller's gate always succeeding).</summary>
        private static int DrainTicks(LockstepPacer pacer)
        {
            int ticks = 0;
            while (pacer.HasTickBudget) { pacer.ConsumeTick(); ticks++; }
            return ticks;
        }

        [Fact]
        public void FreshPacer_OwesNothing()
        {
            var pacer = new LockstepPacer();
            Assert.False(pacer.HasTickBudget);
            Assert.Equal(0f, pacer.AccumulatorSeconds);
        }

        [Fact]
        public void SubTickFrame_GrantsNoTick()
        {
            var pacer = new LockstepPacer();
            pacer.Accumulate(DT * 0.5f);
            Assert.False(pacer.HasTickBudget);
        }

        [Fact]
        public void SubTickFrames_AccumulateIntoAWholeTick()
        {
            var pacer = new LockstepPacer();
            pacer.Accumulate(DT * 0.5f);
            pacer.Accumulate(DT * 0.5f);
            Assert.True(pacer.HasTickBudget);
            Assert.Equal(1, DrainTicks(pacer));
        }

        /// <summary>
        /// THE regression: a very high frame rate must NOT raise the tick rate. At 252 FPS each frame is worth far
        /// less than one tick, so a whole second of frames yields ~30 ticks — not 252.
        /// </summary>
        [Fact]
        public void HighFrameRate_DoesNotRaiseTickRate()
        {
            var pacer = new LockstepPacer();
            const int fps = 252;
            int ticks = 0;

            for (int frame = 0; frame < fps; frame++)
            {
                pacer.Accumulate(1f / fps);
                ticks += DrainTicks(pacer);
            }

            Assert.InRange(ticks, SimulationLoop.TICKS_PER_SECOND - 1, SimulationLoop.TICKS_PER_SECOND);
        }

        /// <summary>
        /// The bound that keeps the match alive: one tick = one TickCommands packet, so ticks-per-second IS the
        /// client's send rate. Over any one-second window it must stay clear of the server's admission cap — a
        /// single dropped command packet is an unrecoverable deadlock, not a dropped frame.
        /// </summary>
        [Fact]
        public void SendRate_OverOneSecond_StaysUnderServerThrottle()
        {
            var pacer = new LockstepPacer();
            const int fps = 1000; // absurd frame rate — the pacer, not the GPU, must set the rate
            int ticks = 0;

            // Start already owing the maximum backlog, so this measures the true worst case.
            pacer.Accumulate(LockstepPacer.MAX_CATCHUP_TICKS * DT);

            for (int frame = 0; frame < fps; frame++)
            {
                pacer.Accumulate(1f / fps);
                ticks += DrainTicks(pacer);
            }

            Assert.True(ticks <= SimulationLoop.TICKS_PER_SECOND + LockstepPacer.MAX_CATCHUP_TICKS,
                $"worst-case one-second tick count {ticks} exceeded the documented bound.");
            Assert.True(ticks < CommandRateLimiter.MAX_COMMANDS_PER_WINDOW,
                $"a client pacing at {ticks} packets/sec would trip the server's " +
                $"{CommandRateLimiter.MAX_COMMANDS_PER_WINDOW}/sec throttle and deadlock the match (DW-912).");
        }

        [Fact]
        public void LongFrame_CatchUpIsCappedAtMaxCatchupTicks()
        {
            var pacer = new LockstepPacer();
            pacer.Accumulate(10f); // a 10-second freeze: a breakpoint, a level load, a tab-out
            Assert.Equal(LockstepPacer.MAX_CATCHUP_TICKS, DrainTicks(pacer));
        }

        [Fact]
        public void ShortHitch_IsAbsorbedNotLost()
        {
            var pacer = new LockstepPacer();
            pacer.Accumulate(DT * 3f); // a 100 ms hitch — inside the catch-up budget
            Assert.Equal(3, DrainTicks(pacer));
        }

        /// <summary>
        /// A stall banks nothing. Sim time is defined by the command stream, not the wall clock — catching up the
        /// seconds lost to a stall would both fast-forward the match and fire the exact burst that trips the
        /// server throttle, re-creating the deadlock the moment the network recovered.
        /// </summary>
        [Fact]
        public void Stall_BanksNoBacklog_SoRecoveryDoesNotBurst()
        {
            var pacer = new LockstepPacer();

            // Five seconds of frames while the merged stream is stuck: every frame the gate refuses, so the caller
            // accumulates then stalls.
            for (int frame = 0; frame < 5 * 60; frame++)
            {
                pacer.Accumulate(1f / 60f);
                if (pacer.HasTickBudget) pacer.Stall();
            }

            // The network recovers: at most one tick is owed, never the 150 ticks of wall time that elapsed.
            Assert.Equal(1, DrainTicks(pacer));
        }

        /// <summary>
        /// A stall must still leave exactly one tick of budget standing, so the very next frame re-enters the loop
        /// and polls the transport. Recovery latency then tracks the frame rate rather than the 30 Hz tick rate.
        /// </summary>
        [Fact]
        public void Stall_KeepsOneTickOfBudget_SoThePollStaysFrameRateResponsive()
        {
            var pacer = new LockstepPacer();
            pacer.Accumulate(DT * 3f);
            pacer.Stall();
            Assert.True(pacer.HasTickBudget);
            Assert.Equal(1, DrainTicks(pacer));
        }

        [Fact]
        public void Reset_DropsBankedTime()
        {
            var pacer = new LockstepPacer();
            pacer.Accumulate(DT * 3f);
            pacer.Reset();
            Assert.False(pacer.HasTickBudget);
            Assert.Equal(0f, pacer.AccumulatorSeconds);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void NonPositiveDelta_IsIgnored(float delta)
        {
            var pacer = new LockstepPacer();
            pacer.Accumulate(DT);
            pacer.Accumulate(delta);
            Assert.Equal(1, DrainTicks(pacer));
        }

        [Fact]
        public void ConsumeTick_NeverBanksNegativeTime()
        {
            var pacer = new LockstepPacer();
            pacer.ConsumeTick(); // mis-ordered call, no budget held
            Assert.Equal(0f, pacer.AccumulatorSeconds);
            Assert.False(pacer.HasTickBudget);
        }

        /// <summary>
        /// The catch-up budget only earns its keep if it stays well under the throttle's headroom. Pinned as a
        /// relationship, not a number, so raising either constant in isolation cannot silently re-arm the deadlock.
        /// </summary>
        [Fact]
        public void CatchupBudget_LeavesHeadroomUnderTheThrottle()
        {
            Assert.True(
                SimulationLoop.TICKS_PER_SECOND + LockstepPacer.MAX_CATCHUP_TICKS
                    < CommandRateLimiter.MAX_COMMANDS_PER_WINDOW,
                "the worst-case paced send rate must stay under the server's per-slot command admission cap.");
        }
    }
}
