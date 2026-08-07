#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-265 / Story 15.12 — the ENERGY-REGEN golden: the first golden whose recorded <see cref="SimChecksum"/>
    /// sequence is moved by <see cref="ProjectChimera.Effects.EnergyRegenSystem"/>. One stationary Player1 caster
    /// with <c>max_energy=100</c>, <c>regen_rate=2</c>, seeded to <c>Energy=40</c>, is stepped for 40 ticks (Player2
    /// empty so the float-scoring AI no-ops). Energy is FOLDED (v6), so the sequence evolves as Energy climbs +2/tick
    /// to 100 (reached at tick 30) then holds clamped — proving regen actually runs and clamps, replayed identically.
    ///
    /// <para>Recorded fresh at <c>SimChecksum.AlgoVersion</c> 24 (Story 15.12 does NOT bump the checksum — regen adds
    /// no new fold; its effect reaches the hash through the already-folded Energy). CROSS-PLATFORM SAFE (integer/Fixed
    /// only). Mirrors <see cref="ModifierGoldenTests"/>' custom record loop.</para>
    /// </summary>
    public class EnergyRegenGoldenTests
    {
        private const string GoldenFile = "energy-regen-scenario.golden.txt";
        private const int Ticks = 40;

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "energy-regen golden-checksum baseline (DW-265 / Story 15.12) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v24) sequence for one stationary Player1 caster (max_energy=100, regen_rate=2, seeded " +
            "Energy=40; Player2 empty so the AI no-ops) stepped via StepOnce at ChecksumInterval=1. EnergyRegenSystem " +
            "restores +2 energy/tick, clamped at MaxEnergy — the folded Energy pool climbs 40→100 (tick 30) then holds. " +
            "All hashed fields are integer/Fixed → byte-identical Win↔Linux; NOT Windows-gated.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~EnergyRegen`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>Build the host with one regen caster; step tick-by-tick, capturing the per-tick checksum.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RecordRun()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;
            int caster = w.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                                  Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.MaxEnergy[caster] = Fixed.FromInt(100);
            w.RegenRate[caster] = Fixed.FromInt(2);
            w.Energy[caster]    = Fixed.FromInt(40); // spent → regen recovers it (+2/tick, clamps at 100)

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)

            var seq = new List<GoldenChecksumReplay.Sample>(Ticks);
            host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < Ticks; i++) host.StepOnce();
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = RecordRun();
            var b = RecordRun();
            Assert.True(a.SequenceEqual(b), "Two in-process energy-regen runs diverged — nondeterminism in the regen path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = RecordRun();
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = RecordRun();
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "Energy-regen golden sequence is constant — regen is not moving the folded Energy (vacuous golden).");
        }

        [Fact]
        public void RecordEnergyRegenBaseline()
        {
            var seq = RecordRun();
            Assert.True(seq.Count >= Ticks, $"Expected >= {Ticks} checksum samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "Vacuous golden — regen not exercised.");

            var seq2 = RecordRun();
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
        }
    }
}
