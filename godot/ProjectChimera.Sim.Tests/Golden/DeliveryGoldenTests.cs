#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.12 (AC6) — the DELIVERY golden. Drives <see cref="DeliveryScenario"/> (a long-range Hitscan sniper, a
    /// short-range custom-speed Projectile unit, and a splash Projectile unit over a Neutral cluster) and asserts two
    /// in-process runs are byte-identical, the sequence reproduces the committed golden on EVERY OS, and the sequence
    /// EVOLVES (target health drops → delivery is doing real work). Cross-platform safe (integer/Fixed, Neutral targets,
    /// Player2 empty) → compared on both CI legs, not Windows-gated. Exercises the v10 Delivery + ProjectileSpeed fold.
    /// </summary>
    public class DeliveryGoldenTests
    {
        private const string GoldenFile = "delivery-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "delivery golden-checksum baseline (Story 3.12, AC6) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v10) sequence for DeliveryScenario.Build() (a long-range Hitscan sniper, a short-range " +
            "custom-speed Projectile unit, and a splash Projectile unit over high-HP Neutral targets; Player2 empty so the " +
            "AI no-ops) stepped via StepOnce at ChecksumInterval=1. All hashed fields integer/Fixed → byte-identical " +
            "Win↔Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~DeliveryGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = GoldenChecksumReplay.RunAndRecord(DeliveryScenario.DefaultTicks, build: DeliveryScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(DeliveryScenario.DefaultTicks, build: DeliveryScenario.Build);
            Assert.True(a.Count >= DeliveryScenario.DefaultTicks, $"Expected >= {DeliveryScenario.DefaultTicks} samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b), "Two in-process delivery runs diverged — same-machine nondeterminism in the delivery path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(DeliveryScenario.DefaultTicks, build: DeliveryScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(DeliveryScenario.DefaultTicks, build: DeliveryScenario.Build);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "delivery sequence is constant — no unit is engaging its target (vacuous golden).");
        }

        [Fact]
        public void RecordDeliveryBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(DeliveryScenario.DefaultTicks, build: DeliveryScenario.Build);
            Assert.True(seq.Count >= DeliveryScenario.DefaultTicks, $"Expected >= {DeliveryScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "delivery sequence is constant (vacuous golden).");
            var seq2 = GoldenChecksumReplay.RunAndRecord(DeliveryScenario.DefaultTicks, build: DeliveryScenario.Build);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
