#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9b (AC1.5 / AC4.2) — the WORKER-CAST + CRYSTAL-COST golden. Drives <see cref="WorkerCastCrystalCostScenario"/>
    /// (one gathering P1 worker casts a Self ability costing energy + ore + crystal via a CastAbility UnitOrder at tick
    /// 1) and asserts:
    ///   • two in-process runs are byte-identical (same-machine determinism, AC1.5),
    ///   • the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated: all hashed fields are
    ///     integer/<see cref="ProjectChimera.Core.Fixed"/>, Player2 empty so the float-scoring AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the tick-1 energy/ore/crystal debit AND the worker's ongoing gather
    ///     deposits actually move the checksum).
    ///
    /// Uses a custom record loop (not <see cref="GoldenChecksumReplay.RunAndRecord"/>) because the cast schedule is
    /// issued through <c>OrderApplier</c> against the host's world each tick (mirrors <c>AbilityCastGoldenTests</c>).
    /// </summary>
    public class WorkerCastCrystalCostGoldenTests
    {
        private const string GoldenFile = "worker-cast-crystal-cost-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "worker-cast + crystal-cost golden-checksum baseline (Story 2.9b, AC1.5) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v8) sequence for WorkerCastCrystalCostScenario.Build() (one gathering P1 worker casts " +
            "a Self ability costing energy+ore+crystal via a CastAbility UnitOrder at tick 1; Player2 empty so the AI " +
            "no-ops) stepped via StepOnce at ChecksumInterval=1. First golden with a WORKER cast AND a crystal debit — " +
            "exercises the tick-1 energy/ore/crystal debit and the uninterrupted gather-deposit cycle (AC1.4). All " +
            "hashed fields are integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~WorkerCastCrystalCostGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host, drive the cast schedule tick-by-tick, capture the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = WorkerCastCrystalCostScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(WorkerCastCrystalCostScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < WorkerCastCrystalCostScenario.DefaultTicks; i++)
            {
                WorkerCastCrystalCostScenario.ApplyScheduleStep(harness.Host, i); // issue BEFORE step → reflected in tick i+1
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
            Assert.True(a.Count >= WorkerCastCrystalCostScenario.DefaultTicks,
                $"Expected >= {WorkerCastCrystalCostScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process worker-cast golden runs diverged — same-machine nondeterminism in the cast/gather path.");
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
                "Worker-cast golden sequence is constant — the cast/gather is not exercising the engine (vacuous golden).");
        }

        [Fact]
        public void RecordWorkerCastCrystalCostBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= WorkerCastCrystalCostScenario.DefaultTicks,
                $"Expected >= {WorkerCastCrystalCostScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Worker-cast golden sequence is constant — the cast/gather is not exercising the engine (vacuous golden).");

            var seq2 = RecordRun();
            Assert.True(seq.SequenceEqual(seq2),
                "Refusing to record: two in-process runs diverged — fix the nondeterminism before re-baselining.");
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
