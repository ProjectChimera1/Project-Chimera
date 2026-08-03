#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Sim;             // ILogSink, NullLogSink
using ProjectChimera.Multiplayer;          // TickCommandPacket, HaltReason
using ProjectChimera.Multiplayer.Server;   // ServerHost
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-237 (Epic 15) — an N≥3 minority that is alerted into a TERMINAL client-side HALT must be dropped from the
    /// checksum quorum, so the surviving majority's desync guard stays live.
    ///
    /// <para>The defect: a <c>DesyncAlert</c> makes the named peer halt (<c>LockstepManager.RaiseHalt</c>) — it stops
    /// advancing its sim and therefore stops sending checksums — but it stays CONNECTED, so the disconnect-driven
    /// <see cref="ServerHost.DropReporter"/> never fires. The collector kept counting it as an expected reporter, so
    /// no later bucket could complete: no PASS window, no further desync detection, no HALT on a subsequent split —
    /// the guard was silently dead for the rest of the match. Unreachable at N=2 (a 1-vs-1 split has no majority, so
    /// it HALTs everyone), live since Stories 9.7/9.15 shipped 4-player MP.</para>
    ///
    /// <para>Every test here is written so it FAILS against the pre-fix host (the quorum stays at N and the later
    /// windows never complete).</para>
    /// </summary>
    public class MinorityHaltQuorumRebaseTests
    {
        /// <summary>Captures everything the host would have put on the wire.</summary>
        private sealed class Captured
        {
            public readonly List<(int slot, byte[] pkt)> Sent = new();
            public readonly List<byte[]> Broadcast = new();
        }

        /// <summary>Captures every Info/Warn line the host writes.</summary>
        private sealed class CapturingLog : ILogSink
        {
            public readonly List<string> Infos = new();
            public readonly List<string> Warns = new();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        private static (ServerHost host, Captured cap, CapturingLog log) Make(int expectedPeers)
        {
            var cap = new Captured();
            var log = new CapturingLog();
            var host = new ServerHost(expectedPeers, log,
                (slot, pkt) => cap.Sent.Add((slot, pkt)),
                pkt => cap.Broadcast.Add(pkt));
            return (host, cap, log);
        }

        /// <summary>Drive one window where <paramref name="minoritySlot"/> diverges from the rest (ascending slots).</summary>
        private static void DivergeOneSlot(ServerHost host, int peers, uint tick, int minoritySlot,
                                           uint canonical = 0xAAAAu, uint divergent = 0xBBBBu)
        {
            for (int s = 0; s < peers; s++)
                host.OnChecksum(s, tick, s == minoritySlot ? divergent : canonical);
        }

        [Fact]
        public void AlertedMinority_IsDroppedFromTheQuorum()
        {
            var (host, cap, _) = Make(3);
            DivergeOneSlot(host, 3, 10u, minoritySlot: 2);

            // The alert still goes out exactly as before (unchanged 1.9a behavior) …
            Assert.Single(cap.Sent);
            Assert.Equal(2, cap.Sent[0].slot);
            Assert.True(TickCommandPacket.TryReadDesyncAlert(cap.Sent[0].pkt, cap.Sent[0].pkt.Length,
                out uint alertTick, out uint canonical));
            Assert.Equal(10u, alertTick);
            Assert.Equal(0xAAAAu, canonical);
            Assert.False(host.Halted);           // the majority plays on

            // … and the halted reporter is now out of the quorum (pre-fix this stayed 3 forever).
            Assert.Equal(2, host.ExpectedPeerCount);
        }

        [Fact]
        public void SurvivingMajority_KeepsCompletingWindows_AfterTheMinorityHalts()
        {
            var (host, _, _) = Make(3);
            DivergeOneSlot(host, 3, 10u, minoritySlot: 2);
            Assert.Equal(1, host.WindowsCompared);

            // Slot 2 has HALTED: it never reports again while staying connected. The two survivors keep reporting —
            // pre-fix _expected stayed 3, so NONE of these windows could ever complete (the guard was dead).
            host.OnChecksum(0, 20u, 0xC0FFEEu);
            host.OnChecksum(1, 20u, 0xC0FFEEu);
            Assert.Equal(2, host.WindowsCompared);

            host.OnChecksum(0, 30u, 0xD00Du);
            host.OnChecksum(1, 30u, 0xD00Du);
            Assert.Equal(3, host.WindowsCompared);

            Assert.False(host.Halted);
            Assert.Equal(1, host.DesyncCount);   // only the original divergence
        }

        [Fact]
        public void SurvivorSplit_AfterTheRebase_StillHalts()
        {
            // The point of keeping the guard alive: a LATER divergence among the survivors must still be caught.
            var (host, cap, _) = Make(3);
            DivergeOneSlot(host, 3, 10u, minoritySlot: 2);

            // Quorum is now {0,1}: a 1-vs-1 split has no strict majority → terminal HALT for everyone.
            host.OnChecksum(0, 20u, 0x1u);
            host.OnChecksum(1, 20u, 0x2u);

            Assert.True(host.Halted);
            Assert.Single(cap.Broadcast);
            Assert.True(TickCommandPacket.TryReadHalt(cap.Broadcast[0], cap.Broadcast[0].Length,
                out uint haltTick, out HaltReason reason));
            Assert.Equal(20u, haltTick);
            Assert.Equal(HaltReason.NoMajority, reason);
        }

        [Fact]
        public void HaltedMinority_StaleReports_CannotReEnterTheQuorum_NorBeAlertedTwice()
        {
            var (host, cap, _) = Make(3);
            DivergeOneSlot(host, 3, 10u, minoritySlot: 2);
            Assert.Single(cap.Sent);

            // A stale/late report from the dropped (halted) peer must be ignored: pre-fix it counted toward the
            // tick-20 bucket, completing it at 3 reports and emitting a SECOND alert for a peer already halted.
            host.OnChecksum(2, 20u, 0xDEADu);
            host.OnChecksum(0, 20u, 0xEEEEu);
            host.OnChecksum(1, 20u, 0xEEEEu);

            Assert.Equal(2, host.WindowsCompared);
            Assert.Equal(1, host.DesyncCount);   // the tick-20 window is CLEAN over the survivors
            Assert.Single(cap.Sent);             // still exactly one alert
        }

        [Fact]
        public void InFlightWindow_PendingOnlyOnTheAlertedSlot_IsReTalliedImmediately()
        {
            var (host, _, _) = Make(3);

            // Tick 20's window is in flight, pending only on slot 2 …
            host.OnChecksum(0, 20u, 0x77u);
            host.OnChecksum(1, 20u, 0x77u);
            Assert.Equal(0, host.WindowsCompared);

            // … when tick 10 completes and names slot 2 the minority. Dropping slot 2 must ALSO re-tally the
            // in-flight tick-20 window over the reduced quorum (pre-fix it hung forever on a halted reporter).
            DivergeOneSlot(host, 3, 10u, minoritySlot: 2, canonical: 0x11u, divergent: 0x99u);

            Assert.Equal(2, host.WindowsCompared); // tick 10 (diverged) + tick 20 (re-tallied clean)
            Assert.Equal(1, host.DesyncCount);
            Assert.False(host.Halted);
        }

        [Fact]
        public void FourPlayers_AlertedMinorityDrop_LeavesAThreeReporterQuorum()
        {
            // The live case (MpSeatCeiling = 4): a 3-vs-1 split alerts the one diverged peer and quorums over 3.
            var (host, cap, _) = Make(4);
            DivergeOneSlot(host, 4, 10u, minoritySlot: 3);

            Assert.Single(cap.Sent);
            Assert.Equal(3, cap.Sent[0].slot);
            Assert.Equal(3, host.ExpectedPeerCount);
            Assert.False(host.Halted);

            // The three survivors complete the next window on their own.
            host.OnChecksum(0, 20u, 0x5u);
            host.OnChecksum(1, 20u, 0x5u);
            host.OnChecksum(2, 20u, 0x5u);
            Assert.Equal(2, host.WindowsCompared);
            Assert.Equal(1, host.DesyncCount);
        }

        [Fact]
        public void TheRebase_IsIdempotent_WithTheLaterDisconnectDrivenDrop()
        {
            var (host, _, _) = Make(3);
            DivergeOneSlot(host, 3, 10u, minoritySlot: 2);
            Assert.Equal(2, host.ExpectedPeerCount);

            // The human eventually closes the HALT overlay and the peer disconnects → the 9.6 path runs for a slot
            // that is already out of the quorum. It must not double-decrement (which would floor the quorum to 1 and
            // turn the survivors' comparison into a trivially-passing lone-reporter liveness check).
            host.DropReporter(2);
            Assert.Equal(2, host.ExpectedPeerCount);

            host.OnChecksum(0, 20u, 0x1u);
            host.OnChecksum(1, 20u, 0x2u);   // still a real 1-vs-1 comparison → HALT, not a false PASS
            Assert.True(host.Halted);
        }

        [Fact]
        public void TheRebase_IsLogged_NamingTheSlotAndTheNewQuorum()
        {
            var (host, _, log) = Make(3);
            DivergeOneSlot(host, 3, 10u, minoritySlot: 2);

            // The DESYNC line (1.9a) plus the new rebase line — a human reading the console must see WHY the quorum
            // shrank, or a 3-player match silently becomes a 2-reporter one.
            Assert.Contains(log.Warns, w => w.Contains("DESYNC") && w.Contains("minority slot(s) 2"));
            Assert.Contains(log.Warns, w => w.Contains("slot 2 dropped from the checksum quorum")
                                         && w.Contains("quorum rebased to 2"));
        }

        [Fact]
        public void UnanimousWindows_AreUnaffected_QuorumNeverShrinks()
        {
            // Guard the happy path: no minority ⇒ no drop (the rebase must never fire on a clean window).
            var (host, cap, log) = Make(4);
            for (uint tick = 10u; tick <= 40u; tick += 10u)
                for (int s = 0; s < 4; s++)
                    host.OnChecksum(s, tick, 0xC0FFEEu);

            Assert.Equal(4, host.ExpectedPeerCount);
            Assert.Equal(4, host.WindowsCompared);
            Assert.True(host.Passing);
            Assert.Empty(cap.Sent);
            Assert.Empty(cap.Broadcast);
            Assert.Empty(log.Warns);
        }
    }
}
