#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Sim;             // ILogSink
using ProjectChimera.Multiplayer.Server;   // ServerHost
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-414 — after a 1v1 drop the checksum quorum floors to a SINGLE reporter, whose windows are pure liveness
    /// (a lone reporter is trivially its own majority). The human-facing surface must no longer read them as PASS:
    /// the per-window line says "attestation suspended … INCONCLUSIVE" instead of the peer-match PASS line, the
    /// windows are counted in <see cref="ServerHost.SingleReporterWindows"/>, and the MATCH SUMMARY breaks them out
    /// of the cross-peer-attested count — an ALL-single-reporter match is INCONCLUSIVE, a mixed match's PASS names
    /// only the attested windows. A no-drop match keeps the exact legacy wording (guarded by the existing
    /// observability tests; re-asserted here for the summary line).
    /// </summary>
    public class SingleReporterVerdictTests
    {
        private sealed class CapturingLog : ILogSink
        {
            public readonly List<string> Infos = new();
            public readonly List<string> Warns = new();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        private static (ServerHost host, CapturingLog log) Make(int expectedPeers)
        {
            var log = new CapturingLog();
            var host = new ServerHost(expectedPeers, log, (_, _) => { }, _ => { });
            return (host, log);
        }

        [Fact]
        public void FlooredQuorum_WindowLine_SaysAttestationSuspended_NotPeerMatch()
        {
            var (host, log) = Make(2);
            host.DropReporter(1);                    // 1v1 drop → quorum floors to the lone survivor
            host.OnChecksum(0, 30u, 0xFEEDu);        // completes alone

            Assert.Equal(1, host.WindowsCompared);
            Assert.Equal(1, host.SingleReporterWindows);
            Assert.True(host.Passing);               // liveness never fakes a desync …

            string line = Assert.Single(log.Infos);
            Assert.Contains("attestation suspended", line);
            Assert.Contains("INCONCLUSIVE", line);
            Assert.DoesNotContain("peers matched", line); // … but it must not wear the PASS line either
        }

        [Fact]
        public void ReTalliedWindow_OnTheDropItself_CountsAsSingleReporter()
        {
            // The in-flight bucket the drop re-tallies over the lone survivor is ALSO a floor-1 window — it
            // completed with one reporter and must be marked suspended like any later one.
            var (host, log) = Make(2);
            host.OnChecksum(0, 10u, 0xAAu);          // pending on slot 1
            host.DropReporter(1);                    // re-tallied alone

            Assert.Equal(1, host.WindowsCompared);
            Assert.Equal(1, host.SingleReporterWindows);
            Assert.Contains("attestation suspended", Assert.Single(log.Infos));
        }

        [Fact]
        public void MixedMatch_Summary_BreaksOutSingleReporterWindows_AndQualifiesThePass()
        {
            var (host, log) = Make(2);
            host.OnChecksum(0, 10u, 0xC0FFEEu);      // a genuine cross-peer attested window …
            host.OnChecksum(1, 10u, 0xC0FFEEu);
            host.DropReporter(1);                    // … then the 1v1 drop …
            host.OnChecksum(0, 20u, 0xF00Du);        // … and two liveness-only windows
            host.OnChecksum(0, 30u, 0xF33Du);

            Assert.Equal(3, host.WindowsCompared);
            Assert.Equal(2, host.SingleReporterWindows);
            Assert.True(host.Passing);

            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("3 windows compared (1 cross-peer attested, 2 single-reporter)", summary);
            Assert.Contains("PASS over the 1 attested window(s)", summary);
            Assert.Contains("liveness, not attestation", summary);
            Assert.False(summary.EndsWith("— PASS."),
                "A post-drop summary must not end in the bare legacy PASS — it over-claims attestation.");
        }

        [Fact]
        public void AllWindowsSingleReporter_SummaryIsInconclusive_NotPass()
        {
            // The whole match's evidence is one reporter talking to itself (drop before any window completed):
            // nothing was EVER cross-attested, so the verdict is INCONCLUSIVE — not PASS.
            var (host, log) = Make(2);
            host.DropReporter(1);
            host.OnChecksum(0, 10u, 0x1u);
            host.OnChecksum(0, 20u, 0x2u);

            Assert.Equal(2, host.WindowsCompared);
            Assert.Equal(2, host.SingleReporterWindows);
            Assert.True(host.Passing);

            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("2 windows compared (0 cross-peer attested, 2 single-reporter)", summary);
            Assert.Contains("INCONCLUSIVE (single reporter — attestation suspended", summary);
            Assert.DoesNotContain("PASS", summary);
        }

        [Fact]
        public void NoDropMatch_KeepsTheExactLegacySummaryShape()
        {
            // The qualifier must be INVISIBLE when no single-reporter window exists (no drop) — the FR-39 wording
            // the existing console tooling greps for is unchanged.
            var (host, log) = Make(2);
            host.OnChecksum(0, 10u, 0xAAu);
            host.OnChecksum(1, 10u, 0xAAu);

            Assert.Equal(0, host.SingleReporterWindows);
            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("MATCH SUMMARY: 1 windows compared, 0 desync, 0 abandoned", summary);
            Assert.EndsWith("PASS.", summary);
        }

        [Fact]
        public void QuorumOfTwoOrMore_NeverCountsAsSingleReporter()
        {
            // An N=3 match dropping to quorum 2 still genuinely cross-attests — those windows must remain plain
            // attested windows (the suspension is strictly the floor-1 state).
            var (host, log) = Make(3);
            host.DropReporter(2);                    // quorum 3 → 2
            host.OnChecksum(0, 10u, 0x5u);
            host.OnChecksum(1, 10u, 0x5u);

            Assert.Equal(1, host.WindowsCompared);
            Assert.Equal(0, host.SingleReporterWindows);
            Assert.Contains("all 2 peers matched", Assert.Single(log.Infos));

            host.LogSummary();
            Assert.EndsWith("PASS.", log.Infos[^1]);
        }
    }
}
