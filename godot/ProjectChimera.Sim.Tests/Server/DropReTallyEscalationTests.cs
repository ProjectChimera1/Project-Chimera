#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Sim;             // ILogSink, NullLogSink
using ProjectChimera.Multiplayer;          // TickCommandPacket, HaltReason
using ProjectChimera.Multiplayer.Server;   // ServerHost, ServerChecksumCollector
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-415 — the disconnect-driven checksum re-tally's ESCALATION branches, previously tested only through to a
    /// clean lone-survivor PASS. At N≥3 a drop can re-tally a still-in-flight bucket into a DESYNC verdict — no
    /// strict majority (→ broadcast HALT) or majority+minority (→ DesyncAlert) — and ServerHost.DropReporter routes
    /// those through ProcessVerdict's alert/HALT logic, honouring the terminal <c>if (Halted) break</c> so no window
    /// after a HALT is routed. Every test here fails if the drop path silently rubber-stamps re-tallied windows or
    /// keeps routing past a HALT.
    /// </summary>
    public class DropReTallyEscalationTests
    {
        private sealed class Captured
        {
            public readonly List<(int slot, byte[] pkt)> Sent = new();
            public readonly List<byte[]> Broadcast = new();
        }

        private static (ServerHost host, Captured cap) Make(int expectedPeers)
        {
            var cap = new Captured();
            var host = new ServerHost(expectedPeers, new NullLogSink(),
                (slot, pkt) => cap.Sent.Add((slot, pkt)),
                pkt => cap.Broadcast.Add(pkt));
            return (host, cap);
        }

        // ── Collector: the re-tallied divergent bucket ─────────────────────────────────────────────────────────

        [Fact]
        public void Collector_DropReTally_TwoSurvivorsDisagree_HasNoMajority()
        {
            // N=3: slots 0 and 1 have reported tick 10 with DIFFERENT hashes; the bucket is pending on slot 2.
            // Slot 2 disconnects → the reduced quorum (2) re-tallies the bucket NOW: a 1-vs-1 split has no strict
            // majority, so the verdict must be Complete + !HasMajority (the DESYNC the caller escalates to HALT).
            var c = new ServerChecksumCollector(3);
            Assert.False(c.Record(10u, 0, 0xAAu).Complete);
            Assert.False(c.Record(10u, 1, 0xBBu).Complete);

            IReadOnlyList<(uint tick, ServerChecksumCollector.Verdict v)> results = c.DropExpectedReporter(2);
            Assert.Single(results);
            Assert.Equal(10u, results[0].tick);
            Assert.True(results[0].v.Complete);
            Assert.False(results[0].v.HasMajority);   // 1-vs-1 at quorum 2 → no canonical hash
        }

        [Fact]
        public void Collector_DropReTally_MajorityWithMinority_NamesTheMinority()
        {
            // N=4: slots 0,1 agree and slot 2 diverges at tick 10 (pending on slot 3). Dropping slot 3 re-tallies
            // at quorum 3: 2-vs-1 → majority 0xAA with minority {2} — the DesyncAlert case, from the DROP path.
            var c = new ServerChecksumCollector(4);
            c.Record(10u, 0, 0xAAu);
            c.Record(10u, 1, 0xAAu);
            c.Record(10u, 2, 0xBBu);

            var results = c.DropExpectedReporter(3);
            Assert.Single(results);
            Assert.True(results[0].v.Complete);
            Assert.True(results[0].v.HasMajority);
            Assert.Equal(0xAAu, results[0].v.Canonical);
            Assert.Equal(new[] { 2 }, results[0].v.Minority);
        }

        // ── ServerHost: the drop path drives HALT / DesyncAlert ────────────────────────────────────────────────

        [Fact]
        public void DropReporter_ReTalliedNoMajority_BroadcastsHalt_AndSetsHalted()
        {
            var (host, cap) = Make(3);
            host.OnChecksum(0, 10u, 0x1u);
            host.OnChecksum(1, 10u, 0x2u);   // in flight, pending on slot 2 — a hidden 1-vs-1 split

            host.DropReporter(2);            // the disconnect re-tally surfaces the split

            Assert.True(host.Halted);
            Assert.False(host.Passing);
            Assert.Equal(1, host.WindowsCompared);
            Assert.Equal(1, host.DesyncCount);
            Assert.Empty(cap.Sent);
            var halt = Assert.Single(cap.Broadcast);
            Assert.True(TickCommandPacket.TryReadHalt(halt, halt.Length, out uint haltTick, out HaltReason reason));
            Assert.Equal(10u, haltTick);
            Assert.Equal(HaltReason.NoMajority, reason);
        }

        [Fact]
        public void DropReporter_ReTalliedMajorityMinority_SendsDesyncAlert_ToTheMinority()
        {
            var (host, cap) = Make(4);
            host.OnChecksum(0, 10u, 0xAAAAu);
            host.OnChecksum(1, 10u, 0xAAAAu);
            host.OnChecksum(2, 10u, 0xBBBBu); // pending on slot 3

            host.DropReporter(3);             // re-tally at quorum 3 → 2-vs-1 → alert slot 2

            Assert.False(host.Halted);        // the majority plays on
            Assert.False(host.Passing);
            Assert.Equal(1, host.DesyncCount);
            Assert.Empty(cap.Broadcast);
            var (slot, pkt) = Assert.Single(cap.Sent);
            Assert.Equal(2, slot);
            Assert.True(TickCommandPacket.TryReadDesyncAlert(pkt, pkt.Length, out uint tick, out uint canonical));
            Assert.Equal(10u, tick);
            Assert.Equal(0xAAAAu, canonical);

            // DW-237 interplay: the alerted minority is ALSO rebased out of the quorum (it halts client-side while
            // staying connected), so the surviving quorum is 4 − the dropped reporter − the alerted minority = 2.
            Assert.Equal(2, host.ExpectedPeerCount);
        }

        [Fact]
        public void DropReporter_HaltIsTerminal_LaterReTalliedWindowsAreNotRouted()
        {
            // TWO buckets in flight, both pending only on slot 2: tick 10 holds a 1-vs-1 split (→ HALT on re-tally)
            // and tick 11 holds a clean agreement. The drop re-tallies ascending; after tick 10 HALTs, the
            // `if (Halted) break` must stop routing — tick 11 is NEVER processed (no second window, no extra wire
            // traffic). Removing the break would count 2 windows and rubber-stamp a post-HALT verdict.
            var (host, cap) = Make(3);
            host.OnChecksum(0, 10u, 0x1u);
            host.OnChecksum(1, 10u, 0x2u);
            host.OnChecksum(0, 11u, 0xC0FFEEu);
            host.OnChecksum(1, 11u, 0xC0FFEEu);

            host.DropReporter(2);

            Assert.True(host.Halted);
            Assert.Equal(1, host.WindowsCompared);   // ONLY tick 10 — tick 11 was not routed past the HALT
            Assert.Equal(1, host.DesyncCount);
            Assert.Single(cap.Broadcast);            // exactly one Halt, nothing after it
            Assert.Empty(cap.Sent);

            // And the host stays terminal: the survivor's later windows are ignored.
            host.OnChecksum(0, 20u, 0x9u);
            host.OnChecksum(1, 20u, 0x9u);
            Assert.Equal(1, host.WindowsCompared);
        }
    }
}
