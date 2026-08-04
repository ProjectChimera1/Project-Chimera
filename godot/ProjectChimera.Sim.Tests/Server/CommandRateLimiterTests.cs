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

        // ── DW-434: cap/window parameterization + the SHARED receive-edge budget ──────────────────────────────
        // The dedicated server now runs a SECOND instance of this limiter at the top of its packet dispatch — one
        // shared per-slot budget across ALL client-sendable packet types (Chat/LobbyChat/MapPing broadcast
        // amplifiers, pings/acks/checksums, unknown/malformed bytes), not just the TickCommands arm.

        [Fact]
        public void ParameterizedCtor_HonorsCustomCapAndWindow()
        {
            var lim = new CommandRateLimiter(2, maxPerWindow: 3, windowMs: 100);
            Assert.Equal(3, lim.MaxPerWindow);
            Assert.Equal(100, lim.WindowMs);

            ulong now = 50_000;
            Assert.True(lim.TryAdmit(0, now));
            Assert.True(lim.TryAdmit(0, now));
            Assert.True(lim.TryAdmit(0, now));
            Assert.False(lim.TryAdmit(0, now));      // 4th in the same window → dropped (custom cap, not 60)
            Assert.False(lim.TryAdmit(0, now + 99)); // custom 100ms window not yet elapsed
            Assert.True(lim.TryAdmit(0, now + 100)); // custom window rolled over → admits again
        }

        [Fact]
        public void DefaultCtor_KeepsTheStory913CommandStreamContract()
        {
            var lim = new CommandRateLimiter(4);
            Assert.Equal(CommandRateLimiter.MAX_COMMANDS_PER_WINDOW, lim.MaxPerWindow);
            Assert.Equal(CommandRateLimiter.WINDOW_MS, lim.WindowMs);
        }

        [Fact]
        public void ReceiveEdge_CombinedLegitTraffic_OneSecond_AllAdmitted()
        {
            // The shared receive-edge instance must admit the worst-case COMBINED legitimate per-slot mix inside
            // one window: 30 TickCommands (30 tps) + 20 Checksums (the loopback self-test's per-pump cadence —
            // production is one per ChecksumInterval=60 ticks ≈ 0.5/sec) + 1 Pong + 2 acks + 10 human
            // chat/map-pings = 63 packets. A receive-edge drop of any of these is a protocol-liveness hazard, so
            // the budget must clear the mix with margin.
            var lim = new CommandRateLimiter(
                8, CommandRateLimiter.MAX_RECEIVE_PER_WINDOW, CommandRateLimiter.WINDOW_MS);

            ulong now = 250_000;
            int admitted = 0;
            for (int i = 0; i < 63; i++)
                if (lim.TryAdmit(3, now + (ulong)(i * 15))) admitted++; // spread across ~945ms — one window

            Assert.Equal(63, admitted);
            Assert.Equal(0L, lim.DroppedCount(3));
        }

        [Fact]
        public void ReceiveEdge_Flood_BoundedToTheReceiveCap()
        {
            // A wire-rate flood (all 2000 packets in the same ms) is bounded to MAX_RECEIVE_PER_WINDOW dispatched
            // packets; the rest drop silently at the receive edge before any type dispatch.
            var lim = new CommandRateLimiter(
                8, CommandRateLimiter.MAX_RECEIVE_PER_WINDOW, CommandRateLimiter.WINDOW_MS);

            const ulong now = 90_000;
            int admitted = 0;
            for (int i = 0; i < 2_000; i++)
                if (lim.TryAdmit(0, now)) admitted++;

            Assert.Equal(CommandRateLimiter.MAX_RECEIVE_PER_WINDOW, admitted);
            Assert.Equal(2_000L - CommandRateLimiter.MAX_RECEIVE_PER_WINDOW, lim.DroppedCount(0));
        }

        [Fact]
        public void ReceiveEdgeCap_SitsAboveCombinedLegitFloor_AndBelowFloodScale()
        {
            // Documents the DW-434 derivation: the shared cap must clear the command stream's own 2×-headroom cap
            // (60) PLUS the summed worst case of every other client-sendable type (loopback per-pump checksums
            // ~20/sec + pong ~1/sec + event-gated acks + human chat ~10/sec ≈ 33/sec) — yet stay far under real
            // flood rates (hundreds-to-thousands/sec) so it still stops one.
            Assert.True(CommandRateLimiter.MAX_RECEIVE_PER_WINDOW >=
                        CommandRateLimiter.MAX_COMMANDS_PER_WINDOW + 33 + 20);
            Assert.True(CommandRateLimiter.MAX_RECEIVE_PER_WINDOW <= 300);
        }

        [Fact]
        public void ParameterizedCtor_RejectsNonPositiveCapOrWindow()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CommandRateLimiter(4, 0, 1000));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CommandRateLimiter(4, -5, 1000));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CommandRateLimiter(4, 60, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CommandRateLimiter(4, 60, -1));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new CommandRateLimiter(0, 60, 1000));
        }
    }
}
