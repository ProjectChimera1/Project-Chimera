#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 15.11 (DW-280) — the GROUND-CAST golden. Drives <see cref="GroundCastScenario"/> (one far-away P1 caster
    /// casts a GroundPoint <c>ground_nuke</c> at a Neutral cluster via a CastAbility UnitOrder carrying the two Fixed
    /// ground coords at tick 1) and asserts:
    ///   • two in-process runs are byte-identical (same-machine determinism of the ground-cast path),
    ///   • the sequence reproduces the committed golden — on EVERY OS (all hashed fields integer/<see cref="ProjectChimera.Core.Fixed"/>,
    ///     targets Neutral + Player2 empty so the float AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the energy debit, the three targets' health drop, and the cooldown
    ///     tick-down move the checksum).
    /// This is the NEW golden the story authors for the ground-cast path; the EXISTING goldens must not move.
    /// </summary>
    public class GroundCastGoldenTests
    {
        private const string GoldenFile = "ground-cast-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "ground-cast golden-checksum baseline (Story 15.11, DW-280) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum sequence for GroundCastScenario.Build() (one far-away P1 caster casts a GroundPoint " +
            "ground_nuke [SearchArea(Neutral) → Damage] at a 3-dummy Neutral cluster via a CastAbility UnitOrder carrying " +
            "the two Fixed ground coords at tick 1; Player2 empty so the AI no-ops) stepped via StepOnce at " +
            "ChecksumInterval=1. First golden with a LIVE ground-target cast — exercises the ground-point wire→PendingCast " +
            "plumbing, the SearchArea centred on the ground point, and the ability-cooldown tick-down. All hashed fields " +
            "integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~GroundCastGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = GroundCastScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(GroundCastScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < GroundCastScenario.DefaultTicks; i++)
            {
                GroundCastScenario.ApplyScheduleStep(harness.Host, i); // issue BEFORE step → reflected in tick i+1
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
            Assert.True(a.Count >= GroundCastScenario.DefaultTicks,
                $"Expected >= {GroundCastScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process ground-cast golden runs diverged — same-machine nondeterminism in the ground-cast path.");
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
                "Ground-cast golden sequence is constant — the cast is not exercising the engine (vacuous golden).");
        }

        [Fact]
        public void RecordGroundCastBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= GroundCastScenario.DefaultTicks,
                $"Expected >= {GroundCastScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Ground-cast golden sequence is constant — the cast is not exercising the engine (vacuous golden).");

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
