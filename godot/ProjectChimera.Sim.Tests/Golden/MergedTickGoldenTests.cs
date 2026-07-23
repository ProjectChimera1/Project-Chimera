#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.3 (FR-39 N=2 regression gate) — the server-authoritative merged-tick rewrite is byte-identical to
    /// the pre-rewrite direct-apply semantics. Guards over the fixed, order-SENSITIVE <see cref="MergedTickN2Scenario"/>:
    ///   (a) the merged-path SimChecksum sequence reproduces a committed golden recorded by a PRIOR process;
    ///   (b) two in-process merged runs are byte-identical (no static/shared-state leak through the merge path);
    ///   (c) the merged-path sequence equals the DIRECT-apply baseline applied per faction ASCENDING (Player1 then
    ///       Player2) — proving the wire round-trip changes transport, not sim; and
    ///   (d) that ascending ≠ descending direct apply — proving the scenario is genuinely apply-order-sensitive, so
    ///       (a)-(c) would FAIL on an apply-order flip (the merge's ascending order is actually locked).
    ///
    /// A moved golden here (or in any pre-existing golden) is a Block-If, NOT a re-baseline: the rewrite touches
    /// networking, not the sim fold, so <c>SimChecksum.AlgoVersion</c> and every committed golden stay unchanged.
    /// </summary>
    public class MergedTickGoldenTests
    {
        [Fact]
        public void RecordMergedN2Baseline()
        {
            var seq = MergedTickN2Scenario.RunMerged();

            Assert.True(seq.Count >= MergedTickN2Scenario.DefaultTicks,
                $"Expected >= {MergedTickN2Scenario.DefaultTicks} checksum samples, got {seq.Count}.");

            // The sequence must EVOLVE — proves the merged orders actually drive the sim.
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Merged N=2 golden sequence is constant — the order stream is not exercising the sim.");

            // Re-baseline safety: two runs must agree, and the golden must round-trip through Format/Parse.
            var seq2 = MergedTickN2Scenario.RunMerged();
            Assert.True(seq.SequenceEqual(seq2),
                "Refusing to record: two in-process merged runs diverged — fix the nondeterminism before re-baselining.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(
                    System.Text.Encoding.UTF8.GetBytes(
                        GoldenChecksumReplay.FormatGolden(seq, MergedTickN2Scenario.Header)))
                    .SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip the recorded sequence.");

            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, MergedTickN2Scenario.GoldenFileName, MergedTickN2Scenario.Header);
            if (wrote)
                Assert.True(GoldenChecksumReplay.IsRecordMode);
        }

        [Fact]
        public void MergedPath_MatchesGolden_AndIsDeterministic()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var seq1 = MergedTickN2Scenario.RunMerged();
            var seq2 = MergedTickN2Scenario.RunMerged();
            Assert.True(seq1.SequenceEqual(seq2),
                "Two in-process merged runs diverged — a static/shared mutable-state leak broke determinism.");

            var golden = GoldenChecksumReplay.LoadGolden(MergedTickN2Scenario.GoldenFileName);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq1);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void MergedPath_EqualsAscendingDirectApplyBaseline()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: skip

            var merged = MergedTickN2Scenario.RunMerged();
            var direct = MergedTickN2Scenario.RunDirect(ascending: true);

            var div = GoldenChecksumReplay.CompareSequences(direct, merged);
            Assert.True(div is null,
                div is null ? "" : "Merged path diverged from the ascending direct-apply baseline (FR-39 regression): "
                                    + GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        /// <summary>
        /// Patch C teeth: the scenario is genuinely APPLY-ORDER-sensitive, so the golden + baseline could FAIL on an
        /// order flip. Ascending (Player1-then-Player2) and descending (Player2-then-Player1) direct apply of the
        /// SAME orders must produce DIFFERENT SimChecksum sequences; if they did not, an applier that reversed
        /// sub-bundle order would slip past every other guard here undetected.
        /// </summary>
        [Fact]
        public void AscendingVsDescendingDirectApply_Diverges()
        {
            var ascending  = MergedTickN2Scenario.RunDirect(ascending: true);
            var descending = MergedTickN2Scenario.RunDirect(ascending: false);

            var div = GoldenChecksumReplay.CompareSequences(ascending, descending);
            Assert.True(div is not null,
                "Ascending and descending direct apply produced identical checksums — the scenario is NOT " +
                "order-sensitive, so the golden could not detect an apply-order flip (Patch C not satisfied).");
        }
    }
}
