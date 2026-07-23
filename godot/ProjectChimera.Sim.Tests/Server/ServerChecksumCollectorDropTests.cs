#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.6 — dropping a disconnected reporter from the strict-majority quorum. Proves: the quorum shrinks
    /// (floor 1) so a lone survivor's windows still PASS; an in-flight bucket the reduced quorum now completes is
    /// re-tallied and returned; the dropped reporter's later stale reports are ignored; its contribution is cleared
    /// from any active bucket so it can't skew a re-tally; and dropping is idempotent.
    /// </summary>
    public class ServerChecksumCollectorDropTests
    {
        private static ServerChecksumCollector.Verdict Single(IReadOnlyList<(uint, ServerChecksumCollector.Verdict)> r)
        {
            Assert.Single(r);
            return r[0].Item2;
        }

        [Fact]
        public void DropReporter_LowersQuorum_LoneSurvivorWindowsPass()
        {
            var c = new ServerChecksumCollector(2);
            Assert.Equal(2, c.ExpectedPeerCount);

            c.DropExpectedReporter(1); // slot 1 disconnected
            Assert.Equal(1, c.ExpectedPeerCount);

            // A single survivor now completes each window on its own.
            var v = c.Record(20u, 0, 0xAAu);
            Assert.True(v.Complete);
            Assert.True(v.HasMajority);      // needed = 1/2+1 = 1 → a lone reporter is a majority of 1
            Assert.Equal(0xAAu, v.Canonical);
            Assert.Empty(v.Minority);
        }

        [Fact]
        public void DropReporter_ReTalliesAnInFlightBucket_TheReducedQuorumNowCompletes()
        {
            var c = new ServerChecksumCollector(2);
            // slot 0 reported tick 10; the bucket is pending on slot 1.
            Assert.False(c.Record(10u, 0, 0xAAu).Complete);

            // slot 1 disconnects → the bucket now completes over the lone survivor.
            var results = c.DropExpectedReporter(1);
            var v = Single(results);
            Assert.Equal(10u, results[0].Item1);
            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical);
            Assert.Empty(v.Minority);
        }

        [Fact]
        public void DropReporter_ReTalliesMultipleInFlightBuckets_AscendingByTick()
        {
            var c = new ServerChecksumCollector(2);
            // slot 0 reports TWO distinct in-flight ticks (10 and 11); slot 1 never reports either — both buckets
            // are simultaneously pending on slot 1.
            Assert.False(c.Record(10u, 0, 0xAAu).Complete);
            Assert.False(c.Record(11u, 0, 0xBBu).Complete);

            // Dropping slot 1 must complete BOTH buckets in ONE call, returned ascending by tick (10 then 11) — guards
            // a break/return-after-first-bucket or a mis-ordered results list.
            var results = c.DropExpectedReporter(1);
            Assert.Equal(2, results.Count);

            Assert.Equal(10u, results[0].Item1);
            Assert.True(results[0].Item2.Complete);
            Assert.True(results[0].Item2.HasMajority);
            Assert.Equal(0xAAu, results[0].Item2.Canonical); // slot 0's lone hash is canonical at quorum 1

            Assert.Equal(11u, results[1].Item1);
            Assert.True(results[1].Item2.Complete);
            Assert.True(results[1].Item2.HasMajority);
            Assert.Equal(0xBBu, results[1].Item2.Canonical);
        }

        [Fact]
        public void DropReporter_IgnoresDroppedReportersStaleReports()
        {
            var c = new ServerChecksumCollector(2);
            c.DropExpectedReporter(1);

            // A stale report from the dropped slot must never re-enter the quorum (would falsely fill a bucket).
            Assert.False(c.Record(30u, 1, 0xEEu).Complete);
            var v = c.Record(30u, 0, 0xAAu); // only the survivor's report counts
            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical);
        }

        [Fact]
        public void DropReporter_ClearsItsContributionFromActiveBucket()
        {
            var c = new ServerChecksumCollector(3);
            // slot 0 = 0xAA, slot 1 = 0xBB at tick 5 (pending, 2 of 3).
            c.Record(5u, 0, 0xAAu);
            c.Record(5u, 1, 0xBBu);

            // Drop slot 1: quorum → 2, its 0xBB contribution is cleared, bucket still pending (1 < 2).
            Assert.Empty(c.DropExpectedReporter(1));

            // slot 2 reports 0xAA → completes at quorum 2 with a clean majority — the removed 0xBB must NOT count.
            var v = c.Record(5u, 2, 0xAAu);
            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical);
            Assert.Empty(v.Minority); // slot 1's 0xBB was cleared, so it is not a named minority
        }

        [Fact]
        public void DropReporter_IsIdempotent()
        {
            var c = new ServerChecksumCollector(3);
            Assert.Equal(3, c.ExpectedPeerCount);
            c.DropExpectedReporter(2);
            Assert.Equal(2, c.ExpectedPeerCount);
            // Dropping the same slot again is a no-op (no double-decrement, empty result).
            Assert.Empty(c.DropExpectedReporter(2));
            Assert.Equal(2, c.ExpectedPeerCount);
        }

        [Fact]
        public void DropReporter_FloorsExpectedAtOne()
        {
            var c = new ServerChecksumCollector(2);
            c.DropExpectedReporter(0);
            Assert.Equal(1, c.ExpectedPeerCount);
            c.DropExpectedReporter(1); // cannot go below 1
            Assert.Equal(1, c.ExpectedPeerCount);
        }

        [Fact]
        public void DropReporter_OutOfRange_IsNoOp()
        {
            var c = new ServerChecksumCollector(2);
            Assert.Empty(c.DropExpectedReporter(9));
            Assert.Equal(2, c.ExpectedPeerCount);
        }
    }
}
