#nullable enable
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.13 (AC6d) — the FORMATION-SEPARATION golden. Drives <see cref="FormationSeparationScenario"/> (a
    /// moving unit pushing through idle units, units of different CollisionRadius, and a Push-vs-Yield pair)
    /// through the same golden-checksum engine the other goldens use and asserts:
    ///   • AC6d — two in-process runs are byte-identical (same-machine determinism),
    ///   • AC6d — the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated),
    ///   • non-vacuity — the sequence EVOLVES (the moving unit advances, neighbours separate).
    ///
    /// Like <see cref="CommandVocabularyGoldenTests"/> and UNLIKE <see cref="AiActiveGoldenTests"/>, the match
    /// assertion is NOT gated to Windows: every hashed field (positions/health + the v4 command fields + the v5
    /// separation fields CollisionRadius/SeparationPriorityOf) is integer / Fixed, and Player2 is empty so the
    /// float-scoring AI no-ops. So this golden is cross-platform-safe and the normal Tier-1 run on BOTH CI legs
    /// (Windows + Linux/WSL) compares it.
    /// </summary>
    public class FormationSeparationGoldenTests
    {
        private const string GoldenFile = "formation-separation-scenario.golden.txt";

        /// <summary>Header so the golden self-identifies and its embedded re-baseline recipe names the
        /// FormationSeparationGolden filter (running ALL Golden tests in record mode would rewrite every golden).</summary>
        private static readonly GoldenChecksumReplay.GoldenHeader FsHeader = new(
            "formation-separation golden-checksum baseline (Story 1.13, AC6d) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v5) sequence for FormationSeparationScenario.Build() (a moving unit pushing " +
            "through idle units, units of different CollisionRadius, and a Push-vs-Yield pair; Player2 empty so the " +
            "AI no-ops) stepped via StepOnce at ChecksumInterval=1. All hashed fields are integer/Fixed → " +
            "byte-identical Win↔Linux; this golden is NOT Windows-gated and the normal Tier-1 run compares it on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~FormationSeparationGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>AC6d — two FRESH in-process builds produce byte-identical sequences (same-machine determinism).</summary>
        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var a = GoldenChecksumReplay.RunAndRecord(FormationSeparationScenario.DefaultTicks, build: FormationSeparationScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(FormationSeparationScenario.DefaultTicks, build: FormationSeparationScenario.Build);

            Assert.True(a.Count >= FormationSeparationScenario.DefaultTicks,
                $"Expected >= {FormationSeparationScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process formation-separation runs diverged — the separation rewrite introduced same-machine " +
                "nondeterminism (float in sim, Dictionary/HashSet enumeration, wall-clock, unseeded RNG, or a stale SoA slot).");
        }

        /// <summary>AC6d — reproduces the committed golden on EVERY OS (cross-platform safe; not Windows-gated).</summary>
        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var seq = GoldenChecksumReplay.RunAndRecord(FormationSeparationScenario.DefaultTicks, build: FormationSeparationScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);

            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        /// <summary>
        /// Non-vacuity — the sequence EVOLVES (the moving unit advances and neighbours separate). A constant
        /// sequence would mean separation no-ops and the golden pins nothing. Runs on every OS.
        /// </summary>
        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var seq = GoldenChecksumReplay.RunAndRecord(FormationSeparationScenario.DefaultTicks, build: FormationSeparationScenario.Build);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Formation-separation golden sequence is constant — the scenario is not exercising MovementSystem " +
                "(vacuous golden). Check that the mover has the Moving flag and the cluster is on its lane.");
        }

        /// <summary>
        /// Record hook — in re-baseline mode (CHIMERA_GOLDEN_RECORD=1) writes the golden under Golden/. In normal
        /// mode it does NOT write; it verifies the sample count, that the sequence evolves, that two runs agree,
        /// and that the format round-trips — refusing to record anything a second run can't reproduce.
        /// </summary>
        [Fact]
        public void RecordFormationSeparationBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(FormationSeparationScenario.DefaultTicks, build: FormationSeparationScenario.Build);

            Assert.True(seq.Count >= FormationSeparationScenario.DefaultTicks,
                $"Expected >= {FormationSeparationScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Formation-separation golden sequence is constant — the scenario is not exercising MovementSystem (vacuous golden).");

            var seq2 = GoldenChecksumReplay.RunAndRecord(FormationSeparationScenario.DefaultTicks, build: FormationSeparationScenario.Build);
            Assert.True(seq.SequenceEqual(seq2),
                "Refusing to record: two in-process runs diverged — fix the nondeterminism before re-baselining.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(
                    System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, FsHeader)))
                    .SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip the recorded sequence.");

            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, FsHeader);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode); // sanity: only writes in record mode
        }
    }
}
