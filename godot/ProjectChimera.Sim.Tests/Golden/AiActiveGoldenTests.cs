#nullable enable
using System;
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.11 (AC1) — Utility-AI smoke test: the AI-ACTIVE golden. Drives <see cref="AiActiveScenario"/>
    /// (Player2 <see cref="ProjectChimera.AI.AiOpponentSystem"/> ACTIVE at index 7) through the same
    /// golden-checksum engine the existing goldens use and asserts three things:
    ///   • AC1a — two in-process runs are byte-identical (same-machine determinism; safe on every OS),
    ///   • AC1b — the sequence reproduces the committed golden (WINDOWS-GATED — see AC1c),
    ///   • non-vacuity — the AI demonstrably ACTS (Player2 building count grows), so the golden pins real
    ///     AI behavior, not a no-op.
    ///
    /// AC1c — why AC1b is Windows-gated and EXCLUDED from the WSL cross-platform gate: the golden encodes
    /// <see cref="ProjectChimera.AI.AiOpponentSystem"/>'s <c>float</c> scoring (AiOpponentSystem.cs:266-271),
    /// which is same-machine-deterministic but cross-platform-suspect (the D2 float→Fixed debt). It was
    /// recorded on Windows (ship-primary); the Linux CI leg runs this suite but the golden-match returns green
    /// without comparing (the two-run test still proves same-machine determinism on Linux). Do NOT add this
    /// golden to godot/tools/cross-platform-determinism-check.ps1 / the determinism-gate Linux job.
    /// </summary>
    public class AiActiveGoldenTests
    {
        private const string GoldenFile = "ai-active-scenario.golden.txt";

        /// <summary>Header so the AI-active golden self-identifies and its embedded re-baseline recipe names the
        /// AiActive filter (running ALL Golden tests in record mode would also rewrite the other goldens).</summary>
        private static readonly GoldenChecksumReplay.GoldenHeader AiHeader = new(
            "AI-active golden-checksum baseline (Story 1.11, AC1) — SAME-MACHINE ONLY; EXCLUDED from the Win↔Linux gate",
            "Pins the SimChecksum sequence for AiActiveScenario.Build() (Player2 AiOpponentSystem ACTIVE at index 7, AiDifficulty.Normal) stepped via StepOnce at ChecksumInterval=1. AiOpponentSystem scores with float (AiOpponentSystem.cs:266-271): PROVEN = same-machine determinism; NOT PROVEN = cross-platform (D2 float→Fixed debt). Deliberately EXCLUDED from the 1.10c WSL cross-platform gate.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~AiActive`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit. Record ONCE on the ship-primary machine (Windows).");

        /// <summary>
        /// AC1a — two FRESH in-process builds produce byte-identical sequences (no static/shared mutable state,
        /// no Dictionary/HashSet enumeration, no wall-clock, no unseeded RNG in the AI). Runs on EVERY OS: two
        /// runs on the same machine are deterministic even where the cross-platform golden-match is skipped.
        /// </summary>
        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var a = GoldenChecksumReplay.RunAndRecord(AiActiveScenario.DefaultTicks, build: AiActiveScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(AiActiveScenario.DefaultTicks, build: AiActiveScenario.Build);

            Assert.True(a.Count >= AiActiveScenario.DefaultTicks,
                $"Expected >= {AiActiveScenario.DefaultTicks} checksum samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b),
                "Two in-process AI-active runs diverged — the AI introduced same-machine nondeterminism " +
                "(Dictionary/HashSet enumeration, wall-clock, or unseeded RNG).");
        }

        /// <summary>
        /// AC1b — reproduces the committed golden. WINDOWS-GATED (AC1c): the golden contains the AI's float path,
        /// which is same-machine-deterministic but cross-platform-suspect (D2). Only compare on the recording
        /// platform (Windows); on Linux this returns green without comparing, so the AI-active golden never
        /// participates in the Win↔Linux cross-platform gate.
        /// </summary>
        [Fact]
        public void MatchesCommittedGolden_OnTheRecordingPlatform()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;        // re-baseline run: golden is being rewritten; skip
            if (!OperatingSystem.IsWindows()) return;             // AC1c: excluded from the Win↔Linux gate

            var seq = GoldenChecksumReplay.RunAndRecord(AiActiveScenario.DefaultTicks, build: AiActiveScenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);

            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        /// <summary>
        /// Non-vacuity — the AI demonstrably ACTED. With 300 ore and no pre-placed Barracks the AI must build at
        /// least one structure, so Player2's building count grows. If this fails the golden pins a no-op AI and
        /// AC1 proves nothing (the scenario starved the AI again). Runs on every OS — a pure state assertion.
        /// </summary>
        [Fact]
        public void AiActuallyActs_Player2BuildingCountGrows()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;

            GoldenHarness h = AiActiveScenario.Build();
            int before = CountFactionBuildings(h, Faction.Player2);
            for (int i = 0; i < AiActiveScenario.DefaultTicks; i++) h.Host.StepOnce();
            int after = CountFactionBuildings(h, Faction.Player2);

            Assert.True(after > before,
                $"AI-active scenario is VACUOUS: Player2 building count did not grow ({before}→{after}). " +
                $"The AI must build at least a Barracks for the golden to pin real AI behavior — check the ore/" +
                $"threshold recipe in AiActiveScenario.");
        }

        /// <summary>
        /// Record hook — in re-baseline mode (CHIMERA_GOLDEN_RECORD=1) writes the golden to the source file under
        /// Golden/. In normal mode it does NOT write; it verifies the harness emits the expected sample count, the
        /// sequence EVOLVES (the AI is exercising the systems), two runs agree, and the format round-trips —
        /// refusing to record anything a second run can't reproduce.
        /// </summary>
        [Fact]
        public void RecordAiActiveBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(AiActiveScenario.DefaultTicks, build: AiActiveScenario.Build);

            Assert.True(seq.Count >= AiActiveScenario.DefaultTicks,
                $"Expected >= {AiActiveScenario.DefaultTicks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "AI-active golden sequence is constant — the AI is not exercising the systems (vacuous golden).");

            var seq2 = GoldenChecksumReplay.RunAndRecord(AiActiveScenario.DefaultTicks, build: AiActiveScenario.Build);
            Assert.True(seq.SequenceEqual(seq2),
                "Refusing to record: two in-process runs diverged — fix the nondeterminism before re-baselining.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(
                    System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, AiHeader)))
                    .SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip the recorded sequence.");

            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, AiHeader);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode); // sanity: only writes in record mode
        }

        /// <summary>Count alive buildings owned by <paramref name="f"/> in the harness's building store.</summary>
        private static int CountFactionBuildings(GoldenHarness h, Faction f)
        {
            int n = 0;
            for (int i = 0; i < h.Buildings.Count; i++)
                if (h.Buildings.Alive[i] && h.Buildings.FactionOf[i] == f) n++;
            return n;
        }
    }
}
