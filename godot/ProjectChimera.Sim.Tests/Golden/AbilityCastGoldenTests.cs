#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.4a (AC3) — the ABILITY-CAST golden. Drives <see cref="AbilityCastScenario"/> (one stationary P1
    /// caster casts a Self battle_fury via a CastAbility UnitOrder at tick 1) and asserts:
    ///   • two in-process runs are byte-identical (same-machine determinism),
    ///   • the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated: all hashed fields are
    ///     integer/<see cref="ProjectChimera.Core.Fixed"/>, Player2 empty so the float-scoring AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the energy debit, the modifier install + expiry, and the v7 cooldown
    ///     tick-down actually move the checksum).
    ///
    /// Uses a custom record loop (not <see cref="GoldenChecksumReplay.RunAndRecord"/>) because the cast schedule is
    /// issued through <c>OrderApplier</c> against the host's world, which the generic perturb hook reaches but the
    /// scenario keeps its own schedule for clarity (mirrors <c>ModifierGoldenTests</c>).
    /// </summary>
    public class AbilityCastGoldenTests
    {
        private const string GoldenFile = "ability-cast-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "ability-cast golden-checksum baseline (Story 2.4a, AC3) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v7) sequence for AbilityCastScenario.Build() (one stationary P1 caster casts a Self " +
            "battle_fury [ApplyModifier +atk/+move] via a CastAbility UnitOrder at tick 1; Player2 empty so the AI " +
            "no-ops) stepped via StepOnce at ChecksumInterval=1. First golden with a LIVE ability cast — exercises the " +
            "energy debit, the modifier install + same-tick recompute + expiry, and the v7 ability-cooldown tick-down. " +
            "All hashed fields are integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~AbilityCastGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host, drive the cast schedule tick-by-tick, capture the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = AbilityCastScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(AbilityCastScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < AbilityCastScenario.DefaultTicks; i++)
            {
                AbilityCastScenario.ApplyScheduleStep(harness.Host, i); // issue BEFORE step → reflected in tick i+1
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
            Assert.True(a.Count >= AbilityCastScenario.DefaultTicks,
                $"Expected >= {AbilityCastScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process ability-cast golden runs diverged — same-machine nondeterminism in the cast path.");
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
                "Ability-cast golden sequence is constant — the cast is not exercising the engine (vacuous golden).");
        }

        [Fact]
        public void RecordAbilityCastBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= AbilityCastScenario.DefaultTicks,
                $"Expected >= {AbilityCastScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Ability-cast golden sequence is constant — the cast is not exercising the engine (vacuous golden).");

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
