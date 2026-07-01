#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9a (AC2 / AC2.6 / AC3.4) — the ANTI-BUILDING golden. Drives <see cref="AntiBuildingScenario"/> (a melee
    /// + a ranged Siege unit razing a Neutral building; the ranged shell is a real projectile) and asserts two
    /// in-process runs are byte-identical, the sequence reproduces the committed golden on EVERY OS, and the sequence
    /// EVOLVES (the building's folded Health/Alive move as it is razed). Cross-platform safe (integer/Fixed, Neutral
    /// building, Player2 empty) → compared on both CI legs, not Windows-gated.
    /// </summary>
    public class AntiBuildingGoldenTests
    {
        private const string GoldenFile = "anti-building-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "anti-building golden-checksum baseline (Story 2.9a, AC2) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v8) sequence for AntiBuildingScenario.Build() (a melee + a ranged Siege unit ordered " +
            "onto a Neutral building via CommandState=AttackBuilding; the ranged unit chases + fires real projectiles; " +
            "Player2 empty so the AI no-ops) stepped via StepOnce at ChecksumInterval=1. Captures the Fortified matrix " +
            "damage, the projectile flight, and the building's Destroy (folded Health/Alive). Integer/Fixed → byte-identical " +
            "Win↔Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~AntiBuildingGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = GoldenChecksumReplay.RunAndRecord(AntiBuildingScenario.DefaultTicks, build: AntiBuildingScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(AntiBuildingScenario.DefaultTicks, build: AntiBuildingScenario.Build);
            Assert.True(a.Count >= AntiBuildingScenario.DefaultTicks, $"Expected >= {AntiBuildingScenario.DefaultTicks} samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b), "Two in-process anti-building runs diverged — same-machine nondeterminism in the building-combat path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(AntiBuildingScenario.DefaultTicks, build: AntiBuildingScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(AntiBuildingScenario.DefaultTicks, build: AntiBuildingScenario.Build);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "anti-building sequence is constant — the siegers are not damaging the building (vacuous golden).");
        }

        [Fact]
        public void RecordAntiBuildingBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(AntiBuildingScenario.DefaultTicks, build: AntiBuildingScenario.Build);
            Assert.True(seq.Count >= AntiBuildingScenario.DefaultTicks, $"Expected >= {AntiBuildingScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "anti-building sequence is constant (vacuous golden).");
            var seq2 = GoldenChecksumReplay.RunAndRecord(AntiBuildingScenario.DefaultTicks, build: AntiBuildingScenario.Build);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
