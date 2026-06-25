#nullable enable
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.12 (AC6d) — the COMMAND-VOCABULARY golden. Drives <see cref="CommandVocabularyScenario"/> (one
    /// Player1 unit per order: Move, AttackMove, Stop, HoldPosition, AttackTarget, Patrol, Follow) through the
    /// same golden-checksum engine the other goldens use and asserts:
    ///   • AC6d — two in-process runs are byte-identical (same-machine determinism),
    ///   • AC6d — the sequence reproduces the committed golden — on EVERY OS (NOT Windows-gated),
    ///   • non-vacuity — the sequence EVOLVES (units move, fight, patrol), so the golden pins real behavior.
    ///
    /// Unlike <see cref="AiActiveGoldenTests"/>, the match assertion is NOT gated to Windows: every hashed field
    /// (positions/health + the v4 command fields: CommandTarget + the patrol-route ring) is integer / Fixed, and
    /// Player2 is empty so the float-scoring AI no-ops. So this golden is cross-platform-safe and the normal
    /// Tier-1 run on BOTH CI legs (Windows + Linux/WSL) compares it — closing the loop without a separate gate.
    /// </summary>
    public class CommandVocabularyGoldenTests
    {
        private const string GoldenFile = "command-vocabulary-scenario.golden.txt";

        /// <summary>Header so the golden self-identifies and its embedded re-baseline recipe names the
        /// CommandVocabulary filter (running ALL Golden tests in record mode would rewrite every golden).</summary>
        private static readonly GoldenChecksumReplay.GoldenHeader CmdHeader = new(
            "command-vocabulary golden-checksum baseline (Story 1.12, AC6d) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v4) sequence for CommandVocabularyScenario.Build() (one Player1 unit per order: " +
            "Move, AttackMove, Stop, HoldPosition, AttackTarget, Patrol, Follow; Player2 empty so the AI no-ops) " +
            "stepped via StepOnce at ChecksumInterval=1. All hashed fields are integer/Fixed → byte-identical " +
            "Win↔Linux; this golden is NOT Windows-gated and the normal Tier-1 run compares it on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~CommandVocabularyGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>AC6d — two FRESH in-process builds produce byte-identical sequences (same-machine determinism).</summary>
        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var a = GoldenChecksumReplay.RunAndRecord(CommandVocabularyScenario.DefaultTicks, build: CommandVocabularyScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(CommandVocabularyScenario.DefaultTicks, build: CommandVocabularyScenario.Build);

            Assert.True(a.Count >= CommandVocabularyScenario.DefaultTicks,
                $"Expected >= {CommandVocabularyScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process command-vocabulary runs diverged — a new command path introduced same-machine " +
                "nondeterminism (Dictionary/HashSet enumeration, wall-clock, unseeded RNG, or a stale SoA slot).");
        }

        /// <summary>AC6d — reproduces the committed golden on EVERY OS (cross-platform safe; not Windows-gated).</summary>
        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var seq = GoldenChecksumReplay.RunAndRecord(CommandVocabularyScenario.DefaultTicks, build: CommandVocabularyScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);

            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        /// <summary>
        /// Non-vacuity — the sequence EVOLVES (the units actually move/fight/patrol). A constant sequence would
        /// mean the commands no-op and the golden pins nothing. Runs on every OS — a pure state assertion.
        /// </summary>
        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            var seq = GoldenChecksumReplay.RunAndRecord(CommandVocabularyScenario.DefaultTicks, build: CommandVocabularyScenario.Build);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Command-vocabulary golden sequence is constant — the commands are not exercising the systems " +
                "(vacuous golden). Check that the Player1 units are damage-bearing and the lanes have enemies.");
        }

        /// <summary>
        /// Record hook — in re-baseline mode (CHIMERA_GOLDEN_RECORD=1) writes the golden under Golden/. In normal
        /// mode it does NOT write; it verifies the sample count, that the sequence evolves, that two runs agree,
        /// and that the format round-trips — refusing to record anything a second run can't reproduce.
        /// </summary>
        [Fact]
        public void RecordCommandVocabularyBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(CommandVocabularyScenario.DefaultTicks, build: CommandVocabularyScenario.Build);

            Assert.True(seq.Count >= CommandVocabularyScenario.DefaultTicks,
                $"Expected >= {CommandVocabularyScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Command-vocabulary golden sequence is constant — the commands are not exercising the systems (vacuous golden).");

            var seq2 = GoldenChecksumReplay.RunAndRecord(CommandVocabularyScenario.DefaultTicks, build: CommandVocabularyScenario.Build);
            Assert.True(seq.SequenceEqual(seq2),
                "Refusing to record: two in-process runs diverged — fix the nondeterminism before re-baselining.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(
                    System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, CmdHeader)))
                    .SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip the recorded sequence.");

            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, CmdHeader);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode); // sanity: only writes in record mode
        }
    }
}
