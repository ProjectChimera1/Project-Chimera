#nullable enable
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.2 (AC3) + DW-387 — the faction-expansion determinism guard. Proves the extended
    /// <see cref="Core.Faction"/> enum (Player5..Player8) and the widened per-faction arrays
    /// (FACTION_ARRAY_SIZE = 9) introduce no desync at N=3 and at a full N=8.
    ///
    /// N=3 stays a TWO-RUN in-process byte-equality test: its slots all lie inside the span the committed
    /// N=2/N=4 goldens already pin cross-process, so a committed N=3 golden would add no new coverage.
    ///
    /// N=8 (DW-387) is now ALSO pinned by a COMMITTED CROSS-PROCESS GOLDEN (<c>golden-multifaction8.golden.txt</c>):
    /// recorded once by a prior process, embedded at build time, and verified by every fresh <c>dotnet test</c> —
    /// on BOTH legs of the Story 1.10c Windows↔Linux cross-platform gate, since that gate simply runs this suite
    /// on each OS against the same committed bytes. A cross-process or cross-platform divergence in the
    /// newly-active slots 5-8 fold paths is therefore caught, not just a same-process one. The golden-CRLF
    /// tripwire is neutralized by the usual layers (FormatGolden emits '\n' + UTF-8 no BOM, godot/.gitattributes
    /// eol=lf, ParseGolden tolerates '\r') plus the CrossPlatformGoldenGuardTests LF-only byte sweep.
    ///
    /// <para><b>DW-838 (post-merge review, 2026-08-06) — the float-AI fence is a TEST now, not a claim.</b> This
    /// class is NOT OS-gated (unlike <see cref="AiActiveGoldenTests"/>, which returns early off Windows because the
    /// AI scorer is float debt), so the committed bytes below are compared on the Linux leg too. Since DW-838 the
    /// starved P2 remnant DOES act inside the horizon — it razes from tick 281, which is the drift the Phase-C
    /// re-record captured — so the old "the scorer stays inert" justification no longer holds. What holds is
    /// narrower and now enforced: with P2 ore-less, base-less and below the attack threshold, every float-ARITHMETIC
    /// branch of the scorer is unreachable, so its decisions are compile-time constants and the sequence stays
    /// reproducible on both legs. <c>MultiFactionAiFenceTests</c> asserts that precondition every run — if it goes
    /// red, this golden must be OS-gated or the scenario re-starved BEFORE the bytes are trusted again.</para>
    ///
    /// If any of these tests diverge, a static/shared mutable-state leak or a genuine nondeterminism broke —
    /// fix it, never paper over it, and NEVER re-record the golden to make a red run green.
    /// </summary>
    public class MultiFactionExpansionTests
    {
        private const string GoldenFile = "golden-multifaction8.golden.txt";

        /// <summary>Header so the N=8 golden self-identifies as the DW-387 faction-expansion baseline, and so its
        /// embedded re-baseline recipe names the MultiFactionExpansion filter — a broader ~MultiFaction filter in
        /// record mode would ALSO rewrite the Story 1.3a N=4 golden.</summary>
        private static readonly GoldenChecksumReplay.GoldenHeader Mf8Header = new(
            "8-faction expansion golden-checksum baseline (Story 9.2 / DW-387)",
            "Pins the SimChecksum sequence for MultiFaction8Scenario.Build() (8 active factions via FactionRegistry(8); Player2 starved so no float-ARITHMETIC AI branch is reachable — its DW-838 raze from tick 281 is constant-scored, so this stays cross-platform safe; fence asserted by MultiFactionAiFenceTests) stepped via StepOnce at ChecksumInterval=1.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~MultiFactionExpansion`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>N=3: two fresh 3-faction builds, identical inputs, must produce byte-identical checksums.</summary>
        [Fact]
        public void ThreeFactions_RunTwiceInProcess_ByteIdentical()
        {
            var seq1 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction3Scenario.DefaultTicks, build: MultiFaction3Scenario.Build);
            var seq2 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction3Scenario.DefaultTicks, build: MultiFaction3Scenario.Build);

            Assert.True(seq1.Count >= MultiFaction3Scenario.DefaultTicks,
                $"Expected >= {MultiFaction3Scenario.DefaultTicks} samples, got {seq1.Count}.");
            // The sequence must EVOLVE (proves the resized arrays/new slots are actually exercised each tick).
            Assert.True(seq1.Select(s => s.Hash).Distinct().Count() > 1,
                "N=3 sequence is constant — the scenario is not exercising the systems.");
            Assert.True(seq1.SequenceEqual(seq2),
                "Two in-process N=3 runs diverged — the faction expansion introduced nondeterminism.");
        }

        /// <summary>N=8: a full 8-player match. Every per-faction store addresses slot 8 without OOB, and two fresh
        /// builds produce byte-identical checksums.</summary>
        [Fact]
        public void EightFactions_RunTwiceInProcess_ByteIdentical()
        {
            var seq1 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks, build: MultiFaction8Scenario.Build);
            var seq2 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks, build: MultiFaction8Scenario.Build);

            Assert.True(seq1.Count >= MultiFaction8Scenario.DefaultTicks,
                $"Expected >= {MultiFaction8Scenario.DefaultTicks} samples, got {seq1.Count}.");
            Assert.True(seq1.Select(s => s.Hash).Distinct().Count() > 1,
                "N=8 sequence is constant — the scenario is not exercising the systems.");
            Assert.True(seq1.SequenceEqual(seq2),
                "Two in-process N=8 runs diverged — the 8-faction expansion introduced nondeterminism.");
        }

        /// <summary>
        /// DW-387 — records the N=8 golden and, in re-baseline mode (CHIMERA_GOLDEN_RECORD=1), writes it to the
        /// source file under Golden/. In normal mode it does NOT write — it verifies the harness emits the expected
        /// number of samples and that the sequence actually evolves.
        ///
        /// Re-baseline (intentional behavior change only):
        ///   PowerShell: $env:CHIMERA_GOLDEN_RECORD=1; dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~MultiFactionExpansion; Remove-Item Env:\CHIMERA_GOLDEN_RECORD
        ///   then: dotnet build (refreshes the embedded copy); git add the golden; commit.
        /// </summary>
        [Fact]
        public void RecordEightFactionBaseline()
        {
            var seq = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks, build: MultiFaction8Scenario.Build);

            // One sample per tick (ChecksumInterval = 1).
            Assert.True(seq.Count >= MultiFaction8Scenario.DefaultTicks,
                $"Expected >= {MultiFaction8Scenario.DefaultTicks} checksum samples, got {seq.Count}.");

            // The sequence must CHANGE over time — proves P1 (worker/melee) is exercising the systems.
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "N=8 golden sequence is constant — the scenario is not exercising the systems.");

            // Re-baseline safety gate (same as 1.2/1.3a): never commit a golden a second in-process run can't
            // reproduce, or one ParseGolden can't read back.
            var seq2 = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks, build: MultiFaction8Scenario.Build);
            Assert.True(seq.SequenceEqual(seq2),
                "Refusing to record: two in-process runs diverged — fix the nondeterminism before re-baselining.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(
                    System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Mf8Header)))
                    .SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip the recorded sequence.");

            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Mf8Header);
            if (wrote)
                Assert.True(GoldenChecksumReplay.IsRecordMode); // sanity: only writes in record mode
        }

        /// <summary>
        /// DW-387 — THE cross-process pin. The committed golden was generated by a PRIOR process (the record run)
        /// and embedded at build time, so a fresh <c>dotnet test</c> process's run matching it IS the two-process
        /// comparison — and on the Linux leg of the 1.10c gate it is the Windows↔Linux comparison. Before DW-387
        /// this test class was two-run in-process only, so a cross-process divergence in the newly-active
        /// slots 5-8 code paths went unpinned.
        /// </summary>
        [Fact]
        public void EightFactions_MatchGolden_RecordedInSeparateProcess()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var seq = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks, build: MultiFaction8Scenario.Build);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);

            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        /// <summary>
        /// DW-387 — drift in the NEWLY-ACTIVE slot range is detected AND located against the committed golden.
        /// Injecting +1 raw into the Player8 inert unit's Fixed health at loop index K (it is never touched by
        /// combat, so the change persists) must make the harness FAIL with a divergence located at exactly
        /// tick K+1 — proving slot-8 state is genuinely inside the golden-covered hash, not dead weight.
        /// </summary>
        [Fact]
        public void OneTickPerturbation_OfPlayer8Entity_IsDetectedAndLocated()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: skip

            const int k = 100;                                   // perturb at loop index 100 => tick 101
            Assert.True(MultiFaction8Scenario.DefaultTicks > k + 1,
                $"DW-387 needs DefaultTicks ({MultiFaction8Scenario.DefaultTicks}) > k+1 ({k + 1}) so the perturbed tick is recorded.");
            int targetId = MultiFaction8Scenario.Player8UnitId;  // the Player8 inert unit

            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var perturbed = GoldenChecksumReplay.RunAndRecord(
                MultiFaction8Scenario.DefaultTicks,
                perturb: (i, w) =>
                {
                    if (i == k)
                        w.Health[targetId] = Fixed.FromRaw(w.Health[targetId].Raw + 1);
                },
                build: MultiFaction8Scenario.Build);

            var div = GoldenChecksumReplay.CompareSequences(golden, perturbed);

            Assert.True(div is not null, "Perturbation went undetected — the N=8 golden does not actually guard slot 8!");
            Assert.Equal((uint)(k + 1), div!.Value.Tick);
            Assert.NotEqual(div.Value.Expected, div.Value.Actual);
        }
    }
}
