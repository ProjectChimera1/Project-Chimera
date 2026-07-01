#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9a (AC1 / AC3.4) — the COMBAT-AIR-GROUND golden. Drives <see cref="CombatAirGroundScenario"/> (an
    /// anti-air-only unit picking the flier over a nearer ground enemy) and asserts two in-process runs are
    /// byte-identical, the sequence reproduces the committed golden on EVERY OS, and the sequence EVOLVES (the flier's
    /// health drops → the domain filter is doing real work). Cross-platform safe (integer/Fixed, Neutral targets,
    /// Player2 empty) → compared on both CI legs, not Windows-gated.
    /// </summary>
    public class CombatAirGroundGoldenTests
    {
        private const string GoldenFile = "combat-air-ground-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "combat-air-ground golden-checksum baseline (Story 2.9a, AC1) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v8) sequence for CombatAirGroundScenario.Build() (an AttackDomain=Air unit auto-acquires " +
            "the flier and ignores a nearer ground enemy; Player2 empty so the AI no-ops) stepped via StepOnce at " +
            "ChecksumInterval=1. All hashed fields integer/Fixed → byte-identical Win↔Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~CombatAirGroundGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = GoldenChecksumReplay.RunAndRecord(CombatAirGroundScenario.DefaultTicks, build: CombatAirGroundScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(CombatAirGroundScenario.DefaultTicks, build: CombatAirGroundScenario.Build);
            Assert.True(a.Count >= CombatAirGroundScenario.DefaultTicks, $"Expected >= {CombatAirGroundScenario.DefaultTicks} samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b), "Two in-process combat-air-ground runs diverged — same-machine nondeterminism in the domain filter.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(CombatAirGroundScenario.DefaultTicks, build: CombatAirGroundScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(CombatAirGroundScenario.DefaultTicks, build: CombatAirGroundScenario.Build);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "combat-air-ground sequence is constant — the AA unit is not engaging the flier (vacuous golden).");
        }

        [Fact]
        public void RecordCombatAirGroundBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(CombatAirGroundScenario.DefaultTicks, build: CombatAirGroundScenario.Build);
            Assert.True(seq.Count >= CombatAirGroundScenario.DefaultTicks, $"Expected >= {CombatAirGroundScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "combat-air-ground sequence is constant (vacuous golden).");
            var seq2 = GoldenChecksumReplay.RunAndRecord(CombatAirGroundScenario.DefaultTicks, build: CombatAirGroundScenario.Build);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
