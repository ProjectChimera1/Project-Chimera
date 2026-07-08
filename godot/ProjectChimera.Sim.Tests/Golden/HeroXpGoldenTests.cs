#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.13 (AC) — the HERO-XP golden. Drives <see cref="HeroXpScenario"/> (a deployed hero killing hostile
    /// bounty-carrying Neutral units in range, crossing curve thresholds → level-ups + stat growth) and asserts two
    /// in-process runs are byte-identical, the sequence reproduces the committed golden on EVERY OS, and the sequence
    /// EVOLVES (XP/level/growth are doing real work). Cross-platform safe (integer/Fixed, Neutral targets, Player2 empty)
    /// → compared on both CI legs. Exercises the v11 XpBounty + HeroStore fold end-to-end.
    /// </summary>
    public class HeroXpGoldenTests
    {
        private const string GoldenFile = "hero-xp-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "hero-xp golden-checksum baseline (Story 3.13) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v11) sequence for HeroXpScenario.Build() (a deployed hero killing hostile bounty-carrying " +
            "Neutral units in range, crossing curve thresholds → level-ups + ModifierStore stat growth; Player2 empty so the " +
            "AI no-ops) stepped via StepOnce at ChecksumInterval=1. All hashed fields integer/Fixed → byte-identical " +
            "Win↔Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~HeroXpGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = GoldenChecksumReplay.RunAndRecord(HeroXpScenario.DefaultTicks, build: HeroXpScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(HeroXpScenario.DefaultTicks, build: HeroXpScenario.Build);
            Assert.True(a.Count >= HeroXpScenario.DefaultTicks, $"Expected >= {HeroXpScenario.DefaultTicks} samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b), "Two in-process hero-XP runs diverged — same-machine nondeterminism in the XP path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(HeroXpScenario.DefaultTicks, build: HeroXpScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(HeroXpScenario.DefaultTicks, build: HeroXpScenario.Build);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "hero-XP sequence is constant — the hero is not gaining XP / leveling (vacuous golden).");
        }

        [Fact]
        public void RecordHeroXpBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(HeroXpScenario.DefaultTicks, build: HeroXpScenario.Build);
            Assert.True(seq.Count >= HeroXpScenario.DefaultTicks, $"Expected >= {HeroXpScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "hero-XP sequence is constant (vacuous golden).");
            var seq2 = GoldenChecksumReplay.RunAndRecord(HeroXpScenario.DefaultTicks, build: HeroXpScenario.Build);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
