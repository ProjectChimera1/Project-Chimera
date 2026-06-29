#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.6 (AC4) — the PASSIVE-ACTIVE golden. Drives <see cref="PassiveScenario"/> (an aura owner buffing an
    /// armored ally, a Neutral on-hit attacker striking that ally, and a self-regen unit) and asserts:
    ///   • two in-process runs are byte-identical (same-machine determinism across all three passive drivers),
    ///   • the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated: all hashed fields are
    ///     integer/<see cref="ProjectChimera.Core.Fixed"/>, the attacker is Neutral and Player2 is empty so the
    ///     float-scoring AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the aura's per-tick modifier refresh + the v8 EffectiveArmor fold,
    ///     the on-hit damage, and the self-regen HoT all move the checksum).
    ///
    /// This is the first golden whose hash exercises the v8 <c>EffectiveArmor</c> fold with a NON-ZERO value (the
    /// ally's aura-granted +5 armor) — the existing goldens only ever see EffectiveArmor==0.
    /// </summary>
    public class PassiveGoldenTests
    {
        private const string GoldenFile = "passive-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "passive-active golden-checksum baseline (Story 2.6, AC4) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v8) sequence for PassiveScenario.Build() (an aura owner grants +5 armor to an " +
            "armored ally each tick; a Neutral on-hit attacker melee-strikes that ally [base + on-hit Magic, both " +
            "armor-reduced]; a self-regen unit heals via a while-alive Persistent HoT) stepped via StepOnce at " +
            "ChecksumInterval=1. First golden exercising all three passive drivers AND the v8 EffectiveArmor fold " +
            "with a non-zero armor value. Attacker is Neutral + Player2 empty so the AI no-ops → all hashed fields " +
            "integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~PassiveGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host and step it tick-by-tick (the scenario self-drives via combat), capturing each checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = PassiveScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(PassiveScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < PassiveScenario.DefaultTicks; i++)
                harness.Host.StepOnce();
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var a = RecordRun();
            var b = RecordRun();
            Assert.True(a.Count >= PassiveScenario.DefaultTicks,
                $"Expected >= {PassiveScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process passive golden runs diverged — same-machine nondeterminism in a passive driver.");
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
                "Passive golden sequence is constant — the passives are not exercising the engine (vacuous golden).");
        }

        [Fact]
        public void RecordPassiveBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= PassiveScenario.DefaultTicks,
                $"Expected >= {PassiveScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Passive golden sequence is constant — the passives are not exercising the engine (vacuous golden).");

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
