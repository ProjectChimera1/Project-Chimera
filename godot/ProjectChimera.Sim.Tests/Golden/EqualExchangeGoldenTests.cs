#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.10 (AC1.5 / D-5) — the EQUAL EXCHANGE golden. Drives <see cref="EqualExchangeScenario"/> (one stationary
    /// P1 caster casts a Self <c>Sequence[apply_modifier, direct_hp_delta]</c> ability at tick 1) and asserts:
    ///   • two in-process runs are byte-identical (same-machine determinism, AC1.5),
    ///   • the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated: all hashed fields are
    ///     integer/<see cref="ProjectChimera.Core.Fixed"/>, Player2 empty so the float-scoring AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the tick-1 flat HP self-cost + buff install, the modifier expiry, and
    ///     the ability cooldown ticking down all move the checksum).
    ///
    /// The FIRST golden pinning the <c>Sequence[buff, flat-HP-cost]</c> Equal Exchange shape — no prior golden exercises
    /// a flat armor-independent HP self-cost inside a Sequence. Uses a custom record loop (not
    /// <see cref="GoldenChecksumReplay.RunAndRecord"/>) because the cast schedule is issued through <c>OrderApplier</c>
    /// against the host each tick (mirrors <c>AbilityCastGoldenTests</c> / <c>WorkerCastCrystalCostGoldenTests</c>).
    /// </summary>
    public class EqualExchangeGoldenTests
    {
        private const string GoldenFile = "equal-exchange-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "equal-exchange golden-checksum baseline (Story 2.10, AC1.5 / D-5) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v8) sequence for EqualExchangeScenario.Build() (one stationary P1 caster casts a Self " +
            "equal_exchange ability = Sequence[apply_modifier +15 atk, direct_hp_delta -25] via a CastAbility UnitOrder at " +
            "tick 1; Player2 empty so the AI no-ops) stepped via StepOnce at ChecksumInterval=1. First golden with a flat " +
            "armor-independent HP self-cost inside a Sequence — exercises the tick-1 Health 100→75 drop (clamped, NOT via " +
            "the damage matrix), the same-tick EffectiveAttack buff, the modifier expiry, and the v7 cooldown fold. All " +
            "hashed fields integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~EqualExchangeGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host, drive the cast schedule tick-by-tick, capture the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = EqualExchangeScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(EqualExchangeScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < EqualExchangeScenario.DefaultTicks; i++)
            {
                EqualExchangeScenario.ApplyScheduleStep(harness.Host, i); // issue BEFORE step → reflected in tick i+1
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
            Assert.True(a.Count >= EqualExchangeScenario.DefaultTicks,
                $"Expected >= {EqualExchangeScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process equal-exchange golden runs diverged — same-machine nondeterminism in the cast path.");
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
                "Equal-exchange golden sequence is constant — the cast is not exercising the engine (vacuous golden).");
        }

        [Fact]
        public void RecordEqualExchangeBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= EqualExchangeScenario.DefaultTicks,
                $"Expected >= {EqualExchangeScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Equal-exchange golden sequence is constant — the cast is not exercising the engine (vacuous golden).");

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
