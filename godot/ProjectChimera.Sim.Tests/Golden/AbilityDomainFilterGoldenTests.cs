#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9a (AC6 / AC3.4) — the ABILITY-DOMAIN-FILTER golden. Drives <see cref="AbilityDomainFilterScenario"/>
    /// (a caster repeatedly casts a Self <c>SearchArea(Air)</c> → Damage that hits only the flier and spares the
    /// co-located ground unit) and asserts two in-process runs are byte-identical, the sequence reproduces the
    /// committed golden on EVERY OS, and it EVOLVES (the flier's health drops each cast). Uses a custom record loop
    /// because the cast schedule is issued through OrderApplier against the host's world (mirrors AbilityCastGoldenTests).
    /// Cross-platform safe (integer/Fixed, Neutral candidates, Player2 empty) → compared on both CI legs.
    /// </summary>
    public class AbilityDomainFilterGoldenTests
    {
        private const string GoldenFile = "ability-domain-filter-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "ability-domain-filter golden-checksum baseline (Story 2.9a, AC6) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v8) sequence for AbilityDomainFilterScenario.Build() (a caster casts a Self SearchArea(Air) " +
            "→ Damage every 40 ticks; the domain filter hits only the flier and spares the co-located ground unit; Player2 " +
            "empty so the AI no-ops) stepped via StepOnce at ChecksumInterval=1. Integer/Fixed → byte-identical Win↔Linux.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~AbilityDomainFilterGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host, drive the cast schedule tick-by-tick, capture the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = AbilityDomainFilterScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(AbilityDomainFilterScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < AbilityDomainFilterScenario.DefaultTicks; i++)
            {
                AbilityDomainFilterScenario.ApplyScheduleStep(harness.Host, i);
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
            Assert.True(a.Count >= AbilityDomainFilterScenario.DefaultTicks, $"Expected >= {AbilityDomainFilterScenario.DefaultTicks} samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b), "Two in-process ability-domain-filter runs diverged — same-machine nondeterminism.");
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
                "ability-domain-filter sequence is constant — the cast is not damaging the flier (vacuous golden).");
        }

        [Fact]
        public void RecordAbilityDomainFilterBaseline()
        {
            var seq = RecordRun();
            Assert.True(seq.Count >= AbilityDomainFilterScenario.DefaultTicks, $"Expected >= {AbilityDomainFilterScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "ability-domain-filter sequence is constant (vacuous golden).");
            var seq2 = RecordRun();
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
