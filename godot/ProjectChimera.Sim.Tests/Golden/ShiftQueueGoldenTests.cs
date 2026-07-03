#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.12 (AC2.4 / AC2.5 / AC3.5) — the SHIFT-QUEUE golden. Drives <see cref="ShiftQueueScenario"/> (a
    /// Shift-queued 3-waypoint Move chain drained by OrderQueueSystem + a SetRally-then-Train whose grunt walks to the
    /// rally) and asserts:
    ///   • two in-process runs are byte-identical — this IS the AC3.5 loopback proof (two in-process SimulationHosts
    ///     fed the IDENTICAL SetRally+Train+queued-Move schedule each tick via <c>ApplyScheduleStep</c>, SequenceEqual
    ///     for 300+ ticks — there is no Tier-1 two-real-network harness, so the established sim-layer determinism proof
    ///     is the two-host in-process comparison, Decision #4),
    ///   • the sequence reproduces the committed golden on EVERY OS (NOT Windows-gated: all hashed fields are
    ///     integer/<see cref="ProjectChimera.Core.Fixed"/>, Player2 empty so the float-scoring AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the queue drains, the grunt trains + walks to rally).
    ///
    /// The FIRST golden exercising the v9 order-queue + rally fold. Uses a custom record loop (not
    /// <see cref="GoldenChecksumReplay.RunAndRecord"/>) because the schedule is issued through <c>OrderApplier</c>
    /// against the host each tick (mirrors <c>EqualExchangeGoldenTests</c> / <c>AbilityCastGoldenTests</c>).
    /// </summary>
    public class ShiftQueueGoldenTests
    {
        private const string GoldenFile = "shift-queue-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "shift-queue golden-checksum baseline (Story 2.12, AC2.4 / AC3.5) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v9) sequence for ShiftQueueScenario.Build() (one stationary P1 unit given a Shift-queued " +
            "3-waypoint Move chain drained by OrderQueueSystem, plus a P1 Barracks issued SetRally + Train so a grunt spawns " +
            "and walks to rally; Player2 empty so the AI no-ops) stepped via StepOnce at ChecksumInterval=1. First golden " +
            "exercising the v9 per-entity order-queue fold + the per-building rally fold (D-1). All hashed fields integer/Fixed " +
            "→ byte-identical Win<->Linux; NOT Windows-gated, compared on both CI legs. Doubles as the AC3.5 loopback proof.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~ShiftQueueGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host, drive the schedule tick-by-tick, capture the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = ShiftQueueScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(ShiftQueueScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < ShiftQueueScenario.DefaultTicks; i++)
            {
                ShiftQueueScenario.ApplyScheduleStep(harness.Host, i); // issue BEFORE step → reflected in tick i+1
                harness.Host.StepOnce();
            }
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical() // AC3.5 loopback: two in-process hosts, identical schedule, SequenceEqual 300+ ticks
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var a = RecordRun();
            var b = RecordRun();
            Assert.True(a.Count >= ShiftQueueScenario.DefaultTicks,
                $"Expected >= {ShiftQueueScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process shift-queue golden runs diverged — same-machine nondeterminism in the queue/rally path (AC3.5 loopback).");
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
                "Shift-queue golden sequence is constant — the queue/rally path is not exercising the engine (vacuous golden).");
        }

        [Fact]
        public void RecordShiftQueueBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= ShiftQueueScenario.DefaultTicks,
                $"Expected >= {ShiftQueueScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Shift-queue golden sequence is constant — the queue/rally path is not exercising the engine (vacuous golden).");

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
