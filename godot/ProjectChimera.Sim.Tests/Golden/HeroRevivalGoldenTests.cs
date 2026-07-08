#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.14 (AC) — the HERO-REVIVAL golden. Drives <see cref="HeroRevivalScenario"/> (a level-3 hero killed at a
    /// revive building, revived through the shared <see cref="OrderApplier"/> path, counting down and respawning with
    /// retained Level/Xp + HP fraction + re-materialized growth) and asserts two in-process runs are byte-identical, the
    /// sequence reproduces the committed golden on EVERY OS, and the sequence EVOLVES (the revival cycle is doing real
    /// work). Cross-platform safe (integer/Fixed, Player2 empty) → compared on both CI legs. Exercises the v11 reserved-
    /// revival-field fold end-to-end under the UNCHANGED AlgoVersion 11.
    /// </summary>
    public class HeroRevivalGoldenTests
    {
        private const string GoldenFile = "hero-revival-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "hero-revival golden-checksum baseline (Story 3.14) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v11, reserved revival fields now mutating) sequence for HeroRevivalScenario.Build() " +
            "(a level-3 hero killed at a revives_heroes building, revived via OrderApplier, counting down → respawning at " +
            "50% HP with retained Level/Xp + re-materialized growth; Player2 empty so the AI no-ops) stepped via StepOnce " +
            "at ChecksumInterval=1. All hashed fields integer/Fixed → byte-identical Win↔Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~HeroRevivalGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the harness and step it, DESTROYING the hero at DeathTick (simulating a combat kill) and issuing
        /// a ReviveHero order (through the shared OrderApplier) every tick — it is accepted exactly once (when the hero is
        /// awaiting &amp; not yet counting), rejected otherwise. Records the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> Run(int ticks)
        {
            GoldenHarness h = HeroRevivalScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            h.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            var reviveOrder = new UnitOrder(HeroRevivalScenario.BuildingId, UnitCommand.ReviveHero,
                                            Fixed.FromRaw(HeroRevivalScenario.HeroSlot), Fixed.Zero);
            for (int i = 0; i < ticks; i++)
            {
                if (i == HeroRevivalScenario.DeathTick)
                    h.World.Destroy(HeroRevivalScenario.HeroEntityId); // simulate the hero's death (folded state → detection)
                // Idempotent: rejected until the hero is awaiting, then accepted once, then rejected (already counting).
                OrderApplier.Apply(h.World, in reviveOrder, Faction.Player1, buildings: h.Host.BuildSys);
                h.Host.StepOnce();
            }
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = Run(HeroRevivalScenario.DefaultTicks);
            var b = Run(HeroRevivalScenario.DefaultTicks);
            Assert.True(a.Count >= HeroRevivalScenario.DefaultTicks, $"Expected >= {HeroRevivalScenario.DefaultTicks} samples, got {a.Count}.");
            Assert.True(a.SequenceEqual(b), "Two in-process hero-revival runs diverged — same-machine nondeterminism in the revival path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(HeroRevivalScenario.DefaultTicks);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(HeroRevivalScenario.DefaultTicks);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "hero-revival sequence is constant — the death/revive/respawn cycle is not moving folded state (vacuous golden).");
        }

        [Fact]
        public void RecordHeroRevivalBaseline()
        {
            var seq = Run(HeroRevivalScenario.DefaultTicks);
            Assert.True(seq.Count >= HeroRevivalScenario.DefaultTicks, $"Expected >= {HeroRevivalScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "hero-revival sequence is constant (vacuous golden).");
            var seq2 = Run(HeroRevivalScenario.DefaultTicks);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
