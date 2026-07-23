#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.2 (AC3) — the faction-expansion determinism guard. Proves the extended <see cref="Core.Faction"/>
    /// enum (Player5..Player8) and the widened per-faction arrays (FACTION_ARRAY_SIZE = 9) introduce no desync at
    /// N=3 and at a full N=8. This is a TWO-RUN in-process byte-equality test with NO committed golden file
    /// (deliberately sidestepping the golden-CRLF tripwire; the existing N=2/N=4 goldens remain the cross-process
    /// pins). If either N diverges between two fresh builds, a static/shared mutable-state leak or a genuine
    /// nondeterminism broke — fix it, never paper over it.
    /// </summary>
    public class MultiFactionExpansionTests
    {
        /// <summary>N=3: two fresh 3-faction builds, identical inputs, must produce byte-identical checksums.</summary>
        [Fact]
        public void ThreeFactions_RunTwiceInProcess_ByteIdentical()
        {
            var seq1 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction3Scenario.DefaultTicks, build: MultiFaction3Scenario.Build);
            var seq2 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction3Scenario.DefaultTicks, build: MultiFaction3Scenario.Build);

            Assert.True(seq1.Count >= MultiFaction3Scenario.DefaultTicks,
                $"Expected >= {MultiFaction3Scenario.DefaultTicks} samples, got {seq1.Count}.");
            // The sequence must EVOLVE (proves the resized arrays/new slots are actually exercised each tick).
            Assert.True(seq1.Select(s => s.Hash).Distinct().Count() > 1,
                "N=3 sequence is constant — the scenario is not exercising the systems.");
            Assert.True(seq1.SequenceEqual(seq2),
                "Two in-process N=3 runs diverged — the faction expansion introduced nondeterminism.");
        }

        /// <summary>N=8: a full 8-player match. Every per-faction store addresses slot 8 without OOB, and two fresh
        /// builds produce byte-identical checksums.</summary>
        [Fact]
        public void EightFactions_RunTwiceInProcess_ByteIdentical()
        {
            var seq1 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks, build: MultiFaction8Scenario.Build);
            var seq2 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks, build: MultiFaction8Scenario.Build);

            Assert.True(seq1.Count >= MultiFaction8Scenario.DefaultTicks,
                $"Expected >= {MultiFaction8Scenario.DefaultTicks} samples, got {seq1.Count}.");
            Assert.True(seq1.Select(s => s.Hash).Distinct().Count() > 1,
                "N=8 sequence is constant — the scenario is not exercising the systems.");
            Assert.True(seq1.SequenceEqual(seq2),
                "Two in-process N=8 runs diverged — the 8-faction expansion introduced nondeterminism.");
        }
    }
}
