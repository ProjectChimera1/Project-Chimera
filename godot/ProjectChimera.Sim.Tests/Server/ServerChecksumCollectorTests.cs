#nullable enable
using System.Linq;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 1.9a (AC3, D4) — the pure strict-majority quorum engine. Proves: all-agree → canonical/no-minority;
    /// one-minority at N=3 → canonical is the agreeing pair + the odd slot is named; no-majority (N=2 mismatch and
    /// N=3 all-different) → no canonical; stale ticks dropped; duplicate (slot,tick) idempotent; a bucket stays
    /// incomplete until every expected peer reports; minority order is ascending-slot (stable attribution).
    /// </summary>
    public class ServerChecksumCollectorTests
    {
        [Fact]
        public void AllAgree_DeclaresCanonical_NoMinority()
        {
            var c = new ServerChecksumCollector(3);
            Assert.False(c.Record(10u, 0, 0xAAu).Complete); // waiting
            Assert.False(c.Record(10u, 1, 0xAAu).Complete); // waiting
            var v = c.Record(10u, 2, 0xAAu);                // all 3 reported

            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical);
            Assert.Empty(v.Minority);
        }

        [Fact]
        public void OneMinority_AtN3_NamesTheOddSlot()
        {
            var c = new ServerChecksumCollector(3);
            c.Record(20u, 0, 0xAAu);
            c.Record(20u, 1, 0xAAu);
            var v = c.Record(20u, 2, 0xBBu); // slot 2 disagrees

            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical); // the agreeing pair is canonical
            Assert.Equal(new[] { 2 }, v.Minority.ToArray());
        }

        [Fact]
        public void NoMajority_AtN2_Mismatch_HasNoCanonical()
        {
            var c = new ServerChecksumCollector(2);
            c.Record(7u, 0, 0xAAu);
            var v = c.Record(7u, 1, 0xBBu); // 1-vs-1 is NOT a majority

            Assert.True(v.Complete);
            Assert.False(v.HasMajority);
            Assert.Empty(v.Minority);
        }

        [Fact]
        public void NoMajority_AtN3_AllDifferent_HasNoCanonical()
        {
            var c = new ServerChecksumCollector(3);
            c.Record(9u, 0, 0x1u);
            c.Record(9u, 1, 0x2u);
            var v = c.Record(9u, 2, 0x3u); // three distinct hashes

            Assert.True(v.Complete);
            Assert.False(v.HasMajority);
        }

        [Fact]
        public void Majority_AtN4_NamesMinority_RegardlessOfReportOrder()
        {
            var c = new ServerChecksumCollector(4);
            // A strict majority of 4 is 3, so the minority is at most one slot. Report out of slot order:
            // slots 1,2,3 agree on 0xAA (the majority), slot 0 diverges → attribution must still name slot 0.
            c.Record(3u, 3, 0xAAu);
            c.Record(3u, 0, 0xDDu);
            c.Record(3u, 2, 0xAAu);
            var v = c.Record(3u, 1, 0xAAu);

            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical);
            Assert.Equal(new[] { 0 }, v.Minority.ToArray()); // named correctly despite out-of-order reports
        }

        [Fact]
        public void TwoTwoSplit_AtN4_IsNoMajority()
        {
            var c = new ServerChecksumCollector(4);
            c.Record(6u, 0, 0xAAu);
            c.Record(6u, 1, 0xAAu);
            c.Record(6u, 2, 0xBBu);
            var v = c.Record(6u, 3, 0xBBu); // 2-vs-2 → no strict majority

            Assert.True(v.Complete);
            Assert.False(v.HasMajority);
        }

        [Fact]
        public void StaleTick_OutsideWindow_IsDropped()
        {
            var c = new ServerChecksumCollector(2);
            // Resolve a high tick first so the floor advances well past tick 1.
            c.Record(20u, 0, 0xAAu);
            Assert.True(c.Record(20u, 1, 0xAAu).Complete);

            // Tick 1 is now far below the resolved floor → both reports are dropped (never completes).
            Assert.False(c.Record(1u, 0, 0x1u).Complete);
            Assert.False(c.Record(1u, 1, 0x2u).Complete);
        }

        [Fact]
        public void DuplicateSlotTick_IsIdempotent()
        {
            var c = new ServerChecksumCollector(3);
            Assert.False(c.Record(5u, 0, 0xAAu).Complete);
            // Same (slot,tick) again with a DIFFERENT hash: ignored — neither advances the count nor changes the stored hash.
            Assert.False(c.Record(5u, 0, 0xFFu).Complete);
            Assert.False(c.Record(5u, 1, 0xAAu).Complete);
            var v = c.Record(5u, 2, 0xAAu); // third DISTINCT slot completes the bucket

            Assert.True(v.Complete);
            Assert.True(v.HasMajority);
            Assert.Equal(0xAAu, v.Canonical); // the duplicate 0xFF never counted
            Assert.Empty(v.Minority);
        }

        [Fact]
        public void Incomplete_UntilAllExpectedPeersReport()
        {
            var c = new ServerChecksumCollector(3);
            Assert.False(c.Record(2u, 0, 0xAAu).Complete);
            Assert.False(c.Record(2u, 1, 0xAAu).Complete); // only 2 of 3 → still pending
        }

        [Fact]
        public void CompletedTick_DoesNotReComplete_OnReReport()
        {
            var c = new ServerChecksumCollector(2);
            Assert.True(c.Record(4u, 0, 0xAAu).Complete == false);
            Assert.True(c.Record(4u, 1, 0xAAu).Complete);  // resolved
            // Re-reporting the resolved tick must not open a second verdict.
            Assert.False(c.Record(4u, 0, 0xAAu).Complete);
            Assert.False(c.Record(4u, 1, 0xAAu).Complete);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(5)]
        public void Ctor_RejectsOutOfRangePeerCount(int n)
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ServerChecksumCollector(n));
        }
    }
}
