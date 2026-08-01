#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-25 — the SNAP-BRANCH golden. Drives <see cref="ProjectileSnapScenario"/> (a high-speed single-target
    /// Projectile unit and a high-speed splash Projectile unit whose shells provably take the <c>step >= dist</c>
    /// snap-to-goal clamp on final approach) and asserts two in-process runs are byte-identical, the sequence
    /// reproduces the committed golden on EVERY OS, and the sequence EVOLVES (Neutral health drops → the snap path is
    /// doing real work). This pins the cross-platform-deterministic snap <c>Position</c> and, via
    /// <c>ProjectileSystem.ApplySplash</c>, the snapped splash center — the DW-25 branch the DeliveryScenario (speeds
    /// 6/18, no overshoot) never exercises. Cross-platform safe (integer/Fixed, Neutral targets, Player2 empty) →
    /// compared on both CI legs, not Windows-gated.
    /// </summary>
    public class ProjectileSnapGoldenTests
    {
        private const string GoldenFile = "projectile-snap-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "projectile-snap golden-checksum baseline (DW-25) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum sequence for ProjectileSnapScenario.Build() (a high-speed single-target Projectile unit " +
            "and a high-speed splash Projectile unit whose shells take the DW-25 step>=dist snap-to-goal clamp on final " +
            "approach, over high-HP Neutral targets; Player2 empty so the AI no-ops) stepped via StepOnce at " +
            "ChecksumInterval=1. Pins the snapped impact Position and the snapped splash center. All hashed fields " +
            "integer/Fixed → byte-identical Win↔Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~ProjectileSnapGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = GoldenChecksumReplay.RunAndRecord(ProjectileSnapScenario.DefaultTicks, build: ProjectileSnapScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(ProjectileSnapScenario.DefaultTicks, build: ProjectileSnapScenario.Build);
            Assert.True(a.Count >= ProjectileSnapScenario.DefaultTicks, $"Expected >= {ProjectileSnapScenario.DefaultTicks} samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b), "Two in-process snap runs diverged — same-machine nondeterminism in the snap/splash path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(ProjectileSnapScenario.DefaultTicks, build: ProjectileSnapScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = GoldenChecksumReplay.RunAndRecord(ProjectileSnapScenario.DefaultTicks, build: ProjectileSnapScenario.Build);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "snap sequence is constant — no unit is engaging its target (vacuous golden).");
        }

        [Fact]
        public void RecordProjectileSnapBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(ProjectileSnapScenario.DefaultTicks, build: ProjectileSnapScenario.Build);
            Assert.True(seq.Count >= ProjectileSnapScenario.DefaultTicks, $"Expected >= {ProjectileSnapScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "snap sequence is constant (vacuous golden).");
            var seq2 = GoldenChecksumReplay.RunAndRecord(ProjectileSnapScenario.DefaultTicks, build: ProjectileSnapScenario.Build);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
