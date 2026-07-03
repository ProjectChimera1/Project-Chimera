#nullable enable
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.11 (AC6.2) — the TAG-FILTER golden. Drives <see cref="TagFilterScenario"/> (a stationary P1 caster casts
    /// a Self ability exercising a require_tag: Mechanical SearchArea + a require_tag: Organic heal SearchArea + a
    /// single-target require_tag: Mechanical DAMAGE leaf gate, over a mixed-tag Neutral set) and asserts:
    ///   • two in-process runs are byte-identical (same-machine determinism),
    ///   • the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated: all hashed fields are
    ///     integer/<see cref="ProjectChimera.Core.Fixed"/>, Player2 empty so the float-scoring AI no-ops),
    ///   • non-vacuity — the sequence EVOLVES (the Mechanical unit's HP drops, the Organic unit's HP rises).
    ///
    /// The FIRST golden exercising the Story-2.11 tag axis (require_tag SearchArea + the D-4 single-target leaf gate).
    /// Uses a custom record loop (not <see cref="GoldenChecksumReplay.RunAndRecord"/>) because the cast schedule is
    /// issued through <c>OrderApplier</c> against the host each tick (mirrors <see cref="AbilityDomainFilterScenario"/> /
    /// <c>EqualExchangeGoldenTests</c>).
    /// </summary>
    public class TagFilterGoldenTests
    {
        private const string GoldenFile = "tag-filter-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "tag-filter golden-checksum baseline (Story 2.11, AC6.2) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v8) sequence for TagFilterScenario.Build() (one stationary P1 caster casts a Self " +
            "tag_counter ability = Sequence[ SearchArea(Neutral, require_tag:Mechanical)->Damage, SearchArea(Neutral, " +
            "require_tag:Organic)->Heal, SearchArea(Neutral)->Damage(require_tag:Mechanical leaf gate) ] via a " +
            "CastAbility UnitOrder every 40 ticks, over a Mechanical/Organic/untagged Neutral set; Player2 empty so the " +
            "AI no-ops) stepped via StepOnce at ChecksumInterval=1. First golden exercising the Story-2.11 tag axis — the " +
            "Mechanical unit's HP drops each cast, the pre-damaged Organic unit's HP rises, the untagged unit is untouched. " +
            "All hashed fields integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated, compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~TagFilterGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host, drive the cast schedule tick-by-tick, capture the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            GoldenHarness harness = TagFilterScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(TagFilterScenario.DefaultTicks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            for (int i = 0; i < TagFilterScenario.DefaultTicks; i++)
            {
                TagFilterScenario.ApplyScheduleStep(harness.Host, i); // issue BEFORE step → reflected in tick i+1
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
            Assert.True(a.Count >= TagFilterScenario.DefaultTicks,
                $"Expected >= {TagFilterScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process tag-filter golden runs diverged — same-machine nondeterminism in the tag-consuming cast path.");
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
                "Tag-filter golden sequence is constant — the cast is not exercising the engine (vacuous golden).");
        }

        [Fact]
        public void RecordTagFilterBaseline()
        {
            var seq = RecordRun();

            Assert.True(seq.Count >= TagFilterScenario.DefaultTicks,
                $"Expected >= {TagFilterScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Tag-filter golden sequence is constant — the cast is not exercising the engine (vacuous golden).");

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
