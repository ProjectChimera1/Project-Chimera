#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Sim;             // ILogSink
using ProjectChimera.Multiplayer.Server;   // ServerChecksumCollector, ServerHost
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-239 (Epic 15) — a ring overrun ABANDONS an in-flight comparison window, and that must be recorded rather
    /// than silently swallowed.
    ///
    /// <para>The defect: when a newer tick re-keys a still-incomplete bucket (a reporter fell a full ring-window
    /// behind — far more plausible since Story 9.4's adaptive delay than the original "≤2 ticks in flight" premise),
    /// the older window's partial reports were dropped, no verdict was ever emitted for it, and NOTHING recorded the
    /// loss: it did not count as compared, it did not count as a desync, and no line reached the console. The FR-39
    /// evidence trail could therefore read "k windows compared, 0 desync — PASS" while quietly never comparing a
    /// stretch of the match. The ledger's prescribed fix: advance the resolved high-water to the evicted tick and log
    /// it as abandoned.</para>
    /// </summary>
    public class AbandonedChecksumWindowTests
    {
        /// <summary>
        /// Mirrors the collector's private ring size. A bucket collision — hence an eviction — needs two ticks whose
        /// difference is a multiple of this, so every test below pairs tick T with T + RingWindow.
        /// </summary>
        private const uint RingWindow = 8;

        /// <summary>Captures every Info/Warn line the host writes.</summary>
        private sealed class CapturingLog : ILogSink
        {
            public readonly List<string> Infos = new();
            public readonly List<string> Warns = new();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        [Fact]
        public void RingOverrun_CountsTheAbandonedWindow_AndNamesItOnTheSeam()
        {
            var abandoned = new List<(uint tick, int lost)>();
            var c = new ServerChecksumCollector(3, (tick, lost) => abandoned.Add((tick, lost)));

            // Tick 4's window is in flight with 2 of 3 reports …
            Assert.False(c.Record(4u, 0, 0xAAu).Complete);
            Assert.False(c.Record(4u, 1, 0xAAu).Complete);
            Assert.Equal(0, c.AbandonedWindows);

            // … when a reporter a full ring behind pushes tick 12 into the same bucket. Tick 4 can never complete.
            Assert.False(c.Record(4u + RingWindow, 0, 0xBBu).Complete);

            Assert.Equal(1, c.AbandonedWindows);
            Assert.Equal((4u, 2), Assert.Single(abandoned)); // the tick that was lost + how many reports it held
        }

        [Fact]
        public void AbandonedWindow_AdvancesTheResolvedFloor_SoNoOlderVerdictSurfacesAfterIt()
        {
            var c = new ServerChecksumCollector(3);

            // Two windows in flight at different ring slots: tick 3 (one report) and tick 4 (two reports).
            Assert.False(c.Record(3u, 0, 0x11u).Complete);
            Assert.False(c.Record(4u, 0, 0xAAu).Complete);
            Assert.False(c.Record(4u, 1, 0xAAu).Complete);

            // Tick 12 overruns tick 4's bucket → tick 4 is abandoned and the high-water moves to 4.
            Assert.False(c.Record(4u + RingWindow, 0, 0xBBu).Complete);
            Assert.Equal(1, c.AbandonedWindows);

            // The straggler now fills the OLDER tick-3 bucket. Pre-fix this completed and handed the caller a verdict
            // for tick 3 AFTER the ring had already given up on tick 4 — an out-of-order verdict for a window older
            // than the abandoned one. It must now be dropped as stale.
            Assert.False(c.Record(3u, 1, 0x11u).Complete);
            Assert.False(c.Record(3u, 2, 0x11u).Complete);
        }

        [Fact]
        public void AbandonedWindow_DoesNotBlockTheLiveFrontier()
        {
            var c = new ServerChecksumCollector(3);
            c.Record(4u, 0, 0xAAu);
            c.Record(4u, 1, 0xAAu);
            c.Record(4u + RingWindow, 0, 0xBBu); // abandons tick 4, opens tick 12

            // The re-keyed window still completes normally — abandonment closes the OLD tick, never the new one.
            Assert.False(c.Record(4u + RingWindow, 1, 0xBBu).Complete);
            var v = c.Record(4u + RingWindow, 2, 0xBBu);
            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xBBu, v.Canonical);
            Assert.Empty(v.Minority);
            Assert.Equal(1, c.AbandonedWindows); // still exactly the one
        }

        [Fact]
        public void EvictingAnEmptyBucket_IsNotAnAbandonedWindow()
        {
            var abandoned = new List<(uint tick, int lost)>();
            var c = new ServerChecksumCollector(2, (tick, lost) => abandoned.Add((tick, lost)));

            // Story 9.6 leaves a 0-count bucket Active when the dropped reporter was its only contributor …
            Assert.False(c.Record(4u, 1, 0xAAu).Complete);
            Assert.Empty(c.DropExpectedReporter(1));
            Assert.Equal(1, c.ExpectedPeerCount);

            // … and evicting THAT loses nothing, so it must not be reported as an abandoned comparison window.
            Assert.True(c.Record(4u + RingWindow, 0, 0xBBu).Complete); // quorum is 1 now → completes at once
            Assert.Equal(0, c.AbandonedWindows);
            Assert.Empty(abandoned);
        }

        [Fact]
        public void NoOverrun_NeverReportsAnAbandonedWindow()
        {
            var c = new ServerChecksumCollector(3);
            for (uint tick = 10u; tick <= 40u; tick += 10u)
            {
                c.Record(tick, 0, 0x5u);
                c.Record(tick, 1, 0x5u);
                Assert.True(c.Record(tick, 2, 0x5u).Complete);
            }
            Assert.Equal(0, c.AbandonedWindows);
        }

        [Fact]
        public void ServerHost_SurfacesTheAbandonedWindow_OnTheConsoleAndInTheMatchSummary()
        {
            var log = new CapturingLog();
            var host = new ServerHost(3, log, (_, _) => { }, _ => { });

            host.OnChecksum(0, 4u, 0xAAu);
            host.OnChecksum(1, 4u, 0xAAu);
            host.OnChecksum(0, 4u + RingWindow, 0xBBu); // overruns tick 4's incomplete window

            Assert.Equal(1, host.AbandonedWindows);
            Assert.Equal(0, host.WindowsCompared);      // it was never compared — not a compared window …
            Assert.Equal(0, host.DesyncCount);          // … and not a desync either
            Assert.True(host.Passing);
            Assert.False(host.Halted);

            string warn = Assert.Single(log.Warns);
            Assert.Contains("tick 4", warn);
            Assert.Contains("ABANDONED", warn);

            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("MATCH SUMMARY: 0 windows compared, 0 desync, 1 abandoned", summary);
            Assert.EndsWith("INCONCLUSIVE.", summary); // nothing was ever compared, so it is NOT a PASS
        }

        [Fact]
        public void ServerHost_CleanRun_ReportsZeroAbandoned()
        {
            var log = new CapturingLog();
            var host = new ServerHost(2, log, (_, _) => { }, _ => { });
            foreach (uint tick in new uint[] { 60u, 120u, 180u })
            {
                host.OnChecksum(0, tick, 0xC0FFEEu);
                host.OnChecksum(1, tick, 0xC0FFEEu);
            }

            Assert.Equal(0, host.AbandonedWindows);
            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("MATCH SUMMARY: 3 windows compared, 0 desync, 0 abandoned", summary);
            Assert.EndsWith("PASS.", summary);
        }
    }
}
