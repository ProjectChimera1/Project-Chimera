#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 15-24b — the COMBAT-DICE golden. Drives <see cref="CritDodgeScenario"/> (a 50%-crit fighter vs
    /// a 50%-dodge tank from a fixed stream seed) and asserts: two in-process runs byte-identical; the
    /// sequence reproduces the committed golden on EVERY OS (SplitMix64 + the roll path are integer/Fixed —
    /// NOT Windows-gated, compared on both CI legs); and non-vacuity. This is the cross-platform pin for the
    /// draw-order discipline itself: any peer that draws in a different sequence — or maps a draw to a
    /// different outcome — diverges from this stream on the tick it happens.
    /// </summary>
    public class CritDodgeGoldenTests
    {
        private const string GoldenFile = "crit-dodge-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "combat-dice golden-checksum baseline (Story 15-24b) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v27) sequence for CritDodgeScenario.Build() (a 50%-crit/+50%-crit-damage melee " +
            "fighter vs a 50%-dodge stationary tank, stream seeded 0xC0DED1CE, ~10 swings over 300 ticks) stepped " +
            "via StepOnce at ChecksumInterval=1. First golden with LIVE SimRng draws from combat: every swing draws " +
            "crit-then-dodge on the shared stream, so the folded SimRng.State + the victim's folded Health pin the " +
            "exact roll sequence and outcomes. All integer/Fixed -> byte-identical Win<->Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~CritDodgeGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = CritDodgeScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(CritDodgeScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < CritDodgeScenario.DefaultTicks; i++)
            {
                CritDodgeScenario.ApplyScheduleStep(harness.Host, i);
                harness.Host.StepOnce();
            }
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var a = RecordRun();
            var b = RecordRun();
            Assert.True(a.Count >= CritDodgeScenario.DefaultTicks,
                $"Expected >= {CritDodgeScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process combat-dice golden runs diverged — same-machine nondeterminism in the roll path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var seq = RecordRun();
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_AndTheDiceActuallyDraw()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            GoldenHarness harness = CritDodgeScenario.Build();
            ulong seedState = harness.Host.World.Rng.State;
            var seq = new List<GoldenChecksumReplay.Sample>(CritDodgeScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < CritDodgeScenario.DefaultTicks; i++)
            {
                CritDodgeScenario.ApplyScheduleStep(harness.Host, i);
                harness.Host.StepOnce();
            }

            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Combat-dice golden sequence is constant — the fight is not happening (vacuous golden).");
            Assert.NotEqual(seedState, harness.Host.World.Rng.State); // the dice DREW — the stream moved off its seed
        }

        [Fact]
        public void RecordCritDodgeBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= CritDodgeScenario.DefaultTicks,
                $"Expected >= {CritDodgeScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Combat-dice golden sequence is constant — the fight is not happening (vacuous golden).");

            var seq2 = RecordRun();
            Assert.True(seq.SequenceEqual(seq2),
                "Refusing to record: two in-process runs diverged — fix the nondeterminism before baselining.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(
                    System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header)))
                    .SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip the recorded sequence.");

            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
