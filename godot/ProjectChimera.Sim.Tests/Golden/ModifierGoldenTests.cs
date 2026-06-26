#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.2b (AC2/AC3) — the MODIFIER-ACTIVE golden. Drives <see cref="ModifierScenario"/> (four stationary
    /// Player1 units through a fixed apply/refresh/stack/ignore/DoT/HoT/Energy schedule) and asserts:
    ///   • two in-process runs are byte-identical (same-machine determinism),
    ///   • the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated: all hashed fields are
    ///     integer/<see cref="ProjectChimera.Core.Fixed"/>, Player2 empty so the float-scoring AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the modifier schedule + DoT/HoT actually move the v6 fold).
    ///
    /// Uses a custom record loop (not <see cref="GoldenChecksumReplay.RunAndRecord"/>) because the modifier schedule
    /// is applied through <c>host.Modifiers</c>, which the generic perturb hook (world-only) cannot reach.
    /// </summary>
    public class ModifierGoldenTests
    {
        private const string GoldenFile = "modifier-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "modifier-active golden-checksum baseline (Story 2.2b, AC2/AC3) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v6) sequence for ModifierScenario.Build() (four stationary P1 units driven through " +
            "a fixed apply/refresh/stack/ignore/DoT/HoT/Energy schedule; Player2 empty so the AI no-ops) stepped via " +
            "StepOnce at ChecksumInterval=1. First golden with ACTIVE ModifierStore state. All hashed fields are " +
            "integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~ModifierGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host, drive the modifier schedule tick-by-tick, capture the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = ModifierScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(ModifierScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < ModifierScenario.DefaultTicks; i++)
            {
                ModifierScenario.ApplyScheduleStep(harness.Host, i); // apply BEFORE step → reflected in tick i+1
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
            Assert.True(a.Count >= ModifierScenario.DefaultTicks,
                $"Expected >= {ModifierScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process modifier-golden runs diverged — same-machine nondeterminism in the ModifierStore path.");
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
                "Modifier golden sequence is constant — the schedule is not exercising the store (vacuous golden).");
        }

        [Fact]
        public void RecordModifierBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= ModifierScenario.DefaultTicks,
                $"Expected >= {ModifierScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Modifier golden sequence is constant — the schedule is not exercising the store (vacuous golden).");

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
