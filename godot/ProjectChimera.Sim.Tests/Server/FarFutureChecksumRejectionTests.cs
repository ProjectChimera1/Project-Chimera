#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Sim;             // ILogSink
using ProjectChimera.Multiplayer;          // DelayMath.MAX_DELAY — the accept slack's single source
using ProjectChimera.Multiplayer.Server;   // ServerChecksumCollector, ServerHost
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-511 (Epic 15) — <c>ServerChecksumCollector.Record</c> accepted ANY tick a client claimed, so ONE
    /// misbehaving peer could blind the whole desync guard.
    ///
    /// <para><b>The defect.</b> A far-future checksum tick (a) EVICTS whichever in-flight ring bucket its index
    /// lands on — destroying an honest comparison window the rest of the quorum was mid-way through — and (b) drags
    /// the collector's <c>_resolvedThrough</c> high-water up to that fabricated value, after which EVERY honest
    /// report is dropped by the cheap stale check for the rest of the match. No majority needed, no desync raised:
    /// the FR-39 evidence trail simply flatlines. DW-239 made the eviction OBSERVABLE (an ABANDONED line +
    /// <c>AbandonedWindows</c>) but never rejected the input.</para>
    ///
    /// <para><b>The fix these tests pin.</b> An acceptance bound measured against a SERVER-AUTHORITATIVE frontier
    /// (<c>MergedTickBuilder.EmittedThrough</c> in production — a tick no single client can advance, since a merged
    /// tick is emitted only when ALL expected players submitted it), not against the collector's own
    /// <c>_resolvedThrough</c>. That distinction is the whole point: <c>_resolvedThrough</c> starts at −1 while real
    /// checksum ticks start at the checksum interval (60), so a naive copy of <c>MergedTickBuilder.ACCEPT_WINDOW</c>
    /// would reject the FIRST legitimate report of every match — pinned below by
    /// <see cref="FirstLegitimateReport_AtTheChecksumInterval_IsAccepted_EvenThoughNoWindowHasResolvedYet"/>.</para>
    /// </summary>
    public class FarFutureChecksumRejectionTests
    {
        /// <summary>Mirrors the collector's private ring size — a far-future tick only EVICTS when it collides.</summary>
        private const uint RingWindow = 8;

        /// <summary>
        /// A fabricated tick that (a) is astronomically past any real frontier and (b) is congruent to tick 60 mod
        /// <see cref="RingWindow"/>, so pre-fix it landed on — and evicted — tick 60's in-flight bucket.
        /// </summary>
        private const uint FarFutureCollidingWith60 = 60u + RingWindow * 1_000_000u;

        /// <summary>Captures every Info/Warn line the host writes.</summary>
        private sealed class CapturingLog : ILogSink
        {
            public readonly List<string> Infos = new();
            public readonly List<string> Warns = new();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        // ── Collector: the acceptance bound ───────────────────────────────────────────────────────

        [Fact]
        public void FarFutureTick_IsRejected_AndTheHonestInFlightWindowStillCompletes()
        {
            long frontier = 60;                       // the server has confirmed merged commands through tick 60
            var c = new ServerChecksumCollector(3, tickFrontier: () => frontier);

            // Two honest peers are mid-window on tick 60 …
            Assert.False(c.Record(60u, 0, 0xAAu).Complete);
            Assert.False(c.Record(60u, 1, 0xAAu).Complete);

            // … when slot 2 claims a tick it cannot possibly have executed, aimed at tick 60's ring bucket.
            Assert.False(c.Record(FarFutureCollidingWith60, 2, 0xDEADu).Complete);

            Assert.Equal(1, c.FarFutureRejections);
            Assert.Equal(0, c.AbandonedWindows);      // pre-fix this evicted tick 60's window (AbandonedWindows == 1)

            // The honest window is untouched: slot 2's REAL report still completes it.
            var v = c.Record(60u, 2, 0xAAu);
            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical);
            Assert.Empty(v.Minority);
        }

        [Fact]
        public void FarFutureTick_DoesNotPoisonTheResolvedFloor_SoTheGuardSurvivesTheRestOfTheMatch()
        {
            long frontier = 60;
            var c = new ServerChecksumCollector(3, tickFrontier: () => frontier);

            // The starvation attempt: one fabricated report at the top of the uint range, before anything else.
            Assert.False(c.Record(uint.MaxValue, 2, 0xDEADu).Complete);
            Assert.Equal(1, c.FarFutureRejections);

            // Pre-fix _resolvedThrough was now 4294967295, so EVERY window below fell out of the cheap stale check
            // and the desync guard was silently dead. Post-fix all three compare normally.
            int compared = 0;
            foreach (uint tick in new uint[] { 60u, 120u, 180u })
            {
                frontier = tick;                      // the merged frontier advances as the match plays on
                Assert.False(c.Record(tick, 0, 0x5u).Complete);
                Assert.False(c.Record(tick, 1, 0x5u).Complete);
                var v = c.Record(tick, 2, 0x5u);
                Assert.True(v.Complete);
                Assert.True(v.HasMajority);
                compared++;
            }

            Assert.Equal(3, compared);
            Assert.Equal(0, c.AbandonedWindows);
        }

        [Fact]
        public void FirstLegitimateReport_AtTheChecksumInterval_IsAccepted_EvenThoughNoWindowHasResolvedYet()
        {
            // THE trap the ledger names: on a fresh collector _resolvedThrough is −1, but the first real checksum
            // tick is the checksum interval (60). A window measured from _resolvedThrough (MergedTickBuilder's
            // ACCEPT_WINDOW copied naively) would cap acceptance at 31 and reject this — deadlocking the very
            // quorum the guard exists to protect. Measured from the SERVER frontier it is plainly legitimate.
            var c = new ServerChecksumCollector(3, tickFrontier: () => 60L);

            Assert.False(c.Record(60u, 0, 0xAAu).Complete);
            Assert.False(c.Record(60u, 1, 0xAAu).Complete);
            var v = c.Record(60u, 2, 0xAAu);

            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0, c.FarFutureRejections);
        }

        [Fact]
        public void AcceptSlack_MatchesTheMaxDelay_TheOnlyWayAnHonestExecTickCanLeadTheFrontier()
        {
            // A client may execute a tick the server never merged ONLY where it self-seeds empty ticks
            // (MergedArrivalRing bootstrap gap + delay-growth gap), and both are bounded by the delay clamp.
            // So frontier + MAX_DELAY is accepted and one past it is not.
            long frontier = 100;
            var c = new ServerChecksumCollector(2, tickFrontier: () => frontier);

            uint atBound = (uint)(frontier + DelayMath.MAX_DELAY);
            Assert.False(c.Record(atBound, 0, 0xAAu).Complete);
            Assert.True(c.Record(atBound, 1, 0xAAu).Complete);   // accepted → the window compares
            Assert.Equal(0, c.FarFutureRejections);

            Assert.False(c.Record(atBound + 1u, 0, 0xAAu).Complete); // one past the bound → dropped
            Assert.Equal(1, c.FarFutureRejections);
        }

        [Fact]
        public void BootstrapGap_BeforeAnyMergedTickIsEmitted_IsStillAccepted()
        {
            // At StartGame the merged frontier is −1 (the server emits nothing for ticks 0..delay−1 — the clients
            // self-seed them empty). The bound must not strand those bootstrap ticks.
            var c = new ServerChecksumCollector(2, tickFrontier: () => -1L);

            uint lastSeeded = (uint)(DelayMath.MAX_DELAY - 1); // the highest tick a client can self-seed
            Assert.False(c.Record(lastSeeded, 0, 0xAAu).Complete);
            Assert.True(c.Record(lastSeeded, 1, 0xAAu).Complete);
            Assert.Equal(0, c.FarFutureRejections);

            // One tick further is past anything a client could have executed without the server's commands.
            Assert.False(c.Record(lastSeeded + 1u, 0, 0xAAu).Complete);
            Assert.Equal(1, c.FarFutureRejections);
        }

        [Fact]
        public void NoFrontierWired_KeepsUnboundedAcceptance_TheTrustedHarnessDefault()
        {
            // Documented default: with no server-authoritative frontier the bound is disarmed (unit tests and the
            // in-process loopback self-test, where every reporter is our own code). A real transport must wire one.
            var c = new ServerChecksumCollector(2);

            Assert.False(c.Record(uint.MaxValue, 0, 0xAAu).Complete);
            Assert.Equal(0, c.FarFutureRejections);
        }

        [Fact]
        public void RejectionIsNotAnAbandonedWindow_NorADesync()
        {
            // The report never reached the ring, so nothing was lost and nothing was compared — the far-future
            // counter must not leak into either of the FR-39 totals.
            var c = new ServerChecksumCollector(3, tickFrontier: () => 60L);
            c.Record(60u, 0, 0xAAu);
            c.Record(FarFutureCollidingWith60, 1, 0xDEADu);
            c.Record(FarFutureCollidingWith60, 1, 0xDEADu);

            Assert.Equal(2, c.FarFutureRejections);
            Assert.Equal(0, c.AbandonedWindows);
        }

        // ── ServerHost: observability + the quorum stays live ─────────────────────────────────────

        [Fact]
        public void ServerHost_DropsTheFarFutureReport_AndKeepsComparingHonestWindows()
        {
            long frontier = 60;
            var log = new CapturingLog();
            var host = new ServerHost(3, log, (_, _) => { }, _ => { }, () => frontier);

            host.OnChecksum(0, 60u, 0x5u);
            host.OnChecksum(1, 60u, 0x5u);
            host.OnChecksum(2, FarFutureCollidingWith60, 0xDEADu); // the grief attempt at tick 60's bucket
            host.OnChecksum(2, 60u, 0x5u);                          // slot 2's real report still completes it

            Assert.Equal(1, host.FarFutureChecksumRejections);
            Assert.Equal(1, host.WindowsCompared);   // pre-fix: 0 compared, 1 abandoned, guard dead from here on
            Assert.Equal(0, host.AbandonedWindows);
            Assert.Equal(0, host.DesyncCount);
            Assert.True(host.Passing);
            Assert.False(host.Halted);

            // The match keeps being verified after the attempt.
            foreach (uint tick in new uint[] { 120u, 180u })
            {
                frontier = tick;
                host.OnChecksum(0, tick, 0x7u);
                host.OnChecksum(1, tick, 0x7u);
                host.OnChecksum(2, tick, 0x7u);
            }
            Assert.Equal(3, host.WindowsCompared);
            Assert.True(host.Passing);
        }

        [Fact]
        public void ServerHost_LogsTheRejectionOncePerSlot_SoItIsNotAClientDrivableLogFlood()
        {
            var log = new CapturingLog();
            var host = new ServerHost(3, log, (_, _) => { }, _ => { }, () => 60L);

            for (int i = 0; i < 50; i++) host.OnChecksum(2, FarFutureCollidingWith60, 0xDEADu);
            host.OnChecksum(1, uint.MaxValue, 0xDEADu);

            Assert.Equal(51, host.FarFutureChecksumRejections); // every one counted …
            Assert.Equal(2, log.Warns.Count);                   // … one line per offending slot, not per packet
            Assert.Contains("slot 2", log.Warns[0]);
            Assert.Contains("DROPPED", log.Warns[0]);
            Assert.Contains("slot 1", log.Warns[1]);
        }

        [Fact]
        public void ServerHost_MatchSummary_ReportsTheRejections_WithoutFlippingTheVerdict()
        {
            long frontier = 60;
            var log = new CapturingLog();
            var host = new ServerHost(2, log, (_, _) => { }, _ => { }, () => frontier);

            host.OnChecksum(1, uint.MaxValue, 0xDEADu);
            host.OnChecksum(0, 60u, 0xC0FFEEu);
            host.OnChecksum(1, 60u, 0xC0FFEEu);

            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("1 windows compared, 0 desync, 0 abandoned", summary);
            Assert.Contains("1 far-future report(s) rejected", summary);
            Assert.EndsWith("PASS.", summary);   // dropped input is not a desync — the verdict is untouched
        }

        [Fact]
        public void ServerHost_CleanMatch_WithTheGuardArmed_KeepsTheLegacySummaryFormat()
        {
            long frontier = 0;
            var log = new CapturingLog();
            var host = new ServerHost(2, log, (_, _) => { }, _ => { }, () => frontier);

            foreach (uint tick in new uint[] { 60u, 120u, 180u })
            {
                frontier = tick;
                host.OnChecksum(0, tick, 0xC0FFEEu);
                host.OnChecksum(1, tick, 0xC0FFEEu);
            }

            Assert.Equal(0, host.FarFutureChecksumRejections);
            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("MATCH SUMMARY: 3 windows compared, 0 desync, 0 abandoned — PASS.", summary);
            Assert.DoesNotContain("far-future", summary);
        }
    }
}
