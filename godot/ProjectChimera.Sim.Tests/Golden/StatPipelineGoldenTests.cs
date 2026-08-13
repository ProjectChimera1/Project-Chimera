#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 15-24a — the STAT-PIPELINE golden. Drives <see cref="StatPipelineScenario"/> (a melee fighter
    /// whose attack_speed buff lands and EXPIRES mid-run, a health_regen heal, a max_health_percent × flat
    /// composition, and a held cooldown_reduction term) and asserts: two in-process runs byte-identical;
    /// the sequence reproduces the committed golden on EVERY OS (all hashed fields integer/Fixed — NOT
    /// Windows-gated, compared on both CI legs); and non-vacuity. The cross-platform pin for the whole v26
    /// generalized-recompute math: a Fixed-division or MulSaturating divergence between Win↔Linux surfaces
    /// here as a checksum drift, never as a silent LAN desync.
    /// </summary>
    public class StatPipelineGoldenTests
    {
        private const string GoldenFile = "stat-pipeline-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "stat-pipeline golden-checksum baseline (Story 15-24a) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v26) sequence for StatPipelineScenario.Build() (a P1 melee fighter swinging at a " +
            "stationary P2 tank through an attack_speed buff that expires mid-run, a health_regen heal, a " +
            "max_health_percent x flat composition, and a held cooldown_reduction term) stepped via StepOnce at " +
            "ChecksumInterval=1. First golden with ACTIVE StatVocabulary-pipeline state (the v26 bounded fold + the " +
            "generalized recompute's percent/division math). All hashed fields are integer/Fixed -> byte-identical " +
            "Win<->Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~StatPipelineGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = StatPipelineScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(StatPipelineScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < StatPipelineScenario.DefaultTicks; i++)
            {
                StatPipelineScenario.ApplyScheduleStep(harness.Host, i); // apply BEFORE step → reflected in tick i+1
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
            Assert.True(a.Count >= StatPipelineScenario.DefaultTicks,
                $"Expected >= {StatPipelineScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process stat-pipeline golden runs diverged — same-machine nondeterminism in the generalized recompute path.");
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
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var seq = RecordRun();
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Stat-pipeline golden sequence is constant — the schedule is not exercising the pipeline (vacuous golden).");
        }

        [Fact]
        public void RecordStatPipelineBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= StatPipelineScenario.DefaultTicks,
                $"Expected >= {StatPipelineScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Stat-pipeline golden sequence is constant — the schedule is not exercising the pipeline (vacuous golden).");

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
