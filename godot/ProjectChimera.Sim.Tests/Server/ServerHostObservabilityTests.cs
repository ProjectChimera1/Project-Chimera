#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Sim;             // ILogSink
using ProjectChimera.Multiplayer.Server;   // ServerHost
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 1.9b (AC1) — the server's POSITIVE determinism verdict. The 1.9a ServerHost was silent on a clean
    /// window; this proves the new evidence trail: WindowsCompared / DesyncCount / Passing, a per-clean-window
    /// Info line, and the terminal MATCH SUMMARY (PASS on zero desync, FAIL otherwise). This is the FR-39
    /// "300+ ticks, 0 desync — PASS" readout a human reads on the dedicated-server console.
    /// </summary>
    public class ServerHostObservabilityTests
    {
        /// <summary>Captures every Info/Warn line the host writes.</summary>
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
        public void CleanRun_N2_CountsEveryWindow_AndSummaryIsPass()
        {
            var (host, log) = Make(2);

            // Three clean comparison windows (both peers agree each tick).
            foreach (uint tick in new uint[] { 60u, 120u, 180u })
            {
                host.OnChecksum(0, tick, 0xC0FFEEu);
                host.OnChecksum(1, tick, 0xC0FFEEu);
            }

            Assert.Equal(3, host.WindowsCompared);
            Assert.Equal(0, host.DesyncCount);
            Assert.True(host.Passing);
            Assert.False(host.Halted);

            // One Info line per clean window, none on the wire (no Warn).
            Assert.Equal(3, log.Infos.Count);
            Assert.Empty(log.Warns);

            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("MATCH SUMMARY: 3 windows compared, 0 desync", summary);
            Assert.EndsWith("PASS.", summary);
        }

        [Fact]
        public void CleanWindow_InfoLine_NamesPeerCountHashAndWindowNumber()
        {
            var (host, log) = Make(2);
            host.OnChecksum(0, 60u, 0x0000000Au);
            host.OnChecksum(1, 60u, 0x0000000Au);

            string line = Assert.Single(log.Infos);
            Assert.Contains("tick 60", line);
            Assert.Contains("all 2 peers matched 0x0000000A", line);
            Assert.Contains("window #1", line);
        }

        [Fact]
        public void CleanRun_N3_CountsWindows()
        {
            var (host, log) = Make(3);
            host.OnChecksum(0, 90u, 0x5u);
            host.OnChecksum(1, 90u, 0x5u);
            host.OnChecksum(2, 90u, 0x5u);

            Assert.Equal(1, host.WindowsCompared);
            Assert.True(host.Passing);
            Assert.Contains("all 3 peers matched", Assert.Single(log.Infos));
        }

        [Fact]
        public void NoMajority_N2_FlipsVerdictToFail_AndHalts()
        {
            var (host, log) = Make(2);

            host.OnChecksum(0, 60u, 0xAAAAu);   // clean window first
            host.OnChecksum(1, 60u, 0xAAAAu);
            host.OnChecksum(0, 120u, 0x1u);     // then a 1-vs-1 mismatch → no majority → HALT
            host.OnChecksum(1, 120u, 0x2u);

            Assert.Equal(1, host.WindowsCompared);
            Assert.Equal(1, host.DesyncCount);
            Assert.False(host.Passing);
            Assert.True(host.Halted);

            host.LogSummary();
            string summary = log.Infos[^1];
            Assert.Contains("MATCH SUMMARY: 1 windows compared, 1 desync", summary);
            Assert.EndsWith("FAIL.", summary);
        }

        [Fact]
        public void Minority_N3_FlipsVerdictToFail_ButDoesNotHalt()
        {
            var (host, _) = Make(3);

            host.OnChecksum(0, 60u, 0xAAAAu);
            host.OnChecksum(1, 60u, 0xAAAAu);
            host.OnChecksum(2, 60u, 0xBBBBu);   // slot 2 diverged → majority {0,1}, minority {2}

            Assert.Equal(0, host.WindowsCompared); // not a clean window
            Assert.Equal(1, host.DesyncCount);
            Assert.False(host.Passing);
            Assert.False(host.Halted);             // the majority plays on; only the minority is alerted
        }
    }
}
