#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.7 (FR-39 raised-count regression gate) — the server-authoritative merged tick merges deterministically
    /// at the RAISED player count (N=3 and N=4), byte-identical across two runs and equal to the ascending
    /// direct-apply baseline. A NEW golden per N (never a move of an existing golden). Proves AC4: "a NEW n3/n4
    /// merged-tick golden proves the raised count merges deterministically across two runs."
    /// </summary>
    public class MergedTickN3GoldenTests
    {
        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void RecordMergedBaseline(int n)
        {
            var seq = MergedTickN3Scenario.RunMerged(n);

            Assert.True(seq.Count >= MergedTickN3Scenario.DefaultTicks,
                $"Expected >= {MergedTickN3Scenario.DefaultTicks} checksum samples, got {seq.Count}.");

            // The sequence must EVOLVE — proves the merged orders actually drive the sim.
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                $"Merged N={n} golden sequence is constant — the order stream is not exercising the sim.");

            // Re-baseline safety: two runs must agree, and the golden must round-trip through Format/Parse.
            var seq2 = MergedTickN3Scenario.RunMerged(n);
            Assert.True(seq.SequenceEqual(seq2),
                $"Refusing to record: two in-process merged N={n} runs diverged — fix the nondeterminism first.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(
                    System.Text.Encoding.UTF8.GetBytes(
                        GoldenChecksumReplay.FormatGolden(seq, MergedTickN3Scenario.Header(n))))
                    .SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip the recorded sequence.");

            bool wrote = GoldenChecksumReplay.MaybeRecord(
                seq, MergedTickN3Scenario.GoldenFileName(n), MergedTickN3Scenario.Header(n));
            if (wrote)
                Assert.True(GoldenChecksumReplay.IsRecordMode);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void MergedPath_MatchesGolden_AndIsDeterministic(int n)
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var seq1 = MergedTickN3Scenario.RunMerged(n);
            var seq2 = MergedTickN3Scenario.RunMerged(n);
            Assert.True(seq1.SequenceEqual(seq2),
                $"Two in-process merged N={n} runs diverged — a static/shared mutable-state leak broke determinism.");

            var golden = GoldenChecksumReplay.LoadGolden(MergedTickN3Scenario.GoldenFileName(n));
            var div = GoldenChecksumReplay.CompareSequences(golden, seq1);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void MergedPath_EqualsAscendingDirectApplyBaseline(int n)
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var merged = MergedTickN3Scenario.RunMerged(n);
            var direct = MergedTickN3Scenario.RunDirect(n, ascending: true);

            var div = GoldenChecksumReplay.CompareSequences(direct, merged);
            Assert.True(div is null,
                div is null ? "" : $"Merged path (N={n}) diverged from the ascending direct-apply baseline: "
                                    + GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void AscendingVsDescendingDirectApply_Diverges(int n)
        {
            var ascending  = MergedTickN3Scenario.RunDirect(n, ascending: true);
            var descending = MergedTickN3Scenario.RunDirect(n, ascending: false);

            var div = GoldenChecksumReplay.CompareSequences(ascending, descending);
            Assert.True(div is not null,
                $"Ascending and descending direct apply (N={n}) produced identical checksums — the scenario is NOT " +
                "order-sensitive, so the golden could not detect an apply-order flip.");
        }
    }
}
