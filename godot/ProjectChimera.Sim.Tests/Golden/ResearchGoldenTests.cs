#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 4.10 (AC) — the RESEARCH golden. Drives <see cref="ResearchScenario"/> (a Player1 lab starting,
    /// completing, then starting the next level of a 2-level "armor_up" research) and asserts two in-process runs
    /// are byte-identical, the sequence reproduces the committed golden on EVERY OS, and the sequence EVOLVES (the
    /// research order/complete/re-start cycle is doing real work over the newly-folded v14 <see cref="ResearchStore"/>
    /// state). Cross-platform safe (integer/Fixed, Player2 empty).
    /// </summary>
    public class ResearchGoldenTests
    {
        private const string GoldenFile = "research-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "research golden-checksum baseline (Story 4.10) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v14 — FIRST-EVER ResearchStore fold) sequence for ResearchScenario.Build() (a " +
            "Player1 lab starting/completing 'armor_up' level 1, then starting level 2 of the same research; " +
            "Player2 empty so the AI no-ops) stepped via StepOnce at ChecksumInterval=1. All hashed fields " +
            "integer/Fixed → byte-identical Win↔Linux.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~ResearchGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        private static IReadOnlyList<GoldenChecksumReplay.Sample> Run(int ticks)
        {
            var (h, labId) = ResearchScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            h.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            var start = new UnitOrder(labId, UnitCommand.StartResearch,
                                      Fixed.FromRaw(ResearchScenario.ArmorUpIndex), Fixed.Zero);

            for (int i = 0; i < ticks; i++)
            {
                if (i == ResearchScenario.StartTick1 || i == ResearchScenario.StartTick2)
                    OrderApplier.Apply(h.World, in start, Faction.Player1, research: h.Host.ResearchSys);
                h.Host.StepOnce();
            }
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = Run(ResearchScenario.DefaultTicks);
            var b = Run(ResearchScenario.DefaultTicks);
            Assert.True(a.SequenceEqual(b), "Two in-process research runs diverged — same-machine nondeterminism.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(ResearchScenario.DefaultTicks);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(ResearchScenario.DefaultTicks);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "research sequence is constant — the start/complete/re-start cycle is not moving folded state (vacuous golden).");
        }

        [Fact]
        public void RecordResearchBaseline()
        {
            var seq = Run(ResearchScenario.DefaultTicks);
            var seq2 = Run(ResearchScenario.DefaultTicks);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
        }
    }
}
