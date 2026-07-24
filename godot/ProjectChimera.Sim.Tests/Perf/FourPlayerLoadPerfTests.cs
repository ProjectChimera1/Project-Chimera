#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;
using Xunit.Abstractions;

namespace ProjectChimera.Sim.Tests.Perf
{
    /// <summary>
    /// Story 9.15 — the NON-GATED 4-player late-game tick-throughput recorder (input to Story 10.3). Builds a heavy,
    /// near-<see cref="EntityWorld.MAX_ENTITIES"/> 4-faction world (the <c>CanonicalModelHashPerfTests</c> max-caps
    /// spirit) and measures the median wall-clock ms per <see cref="SimulationHost.StepOnce"/> over K ticks
    /// (median-of-5, JIT-warmed). There is DELIBERATELY no timing assertion — the number FEEDS the Story 10.3 budget,
    /// it does not gate here. The observed median is emitted ONLY via <see cref="ITestOutputHelper"/>; the test does
    /// NOT write to any tracked file (the committed <c>_bmad-output/implementation-artifacts/perf-4player-9-15.md</c>
    /// is a one-time static record — mutating it per run would dirty the tree / red the baseline). Read the emitted
    /// <c>ms/tick</c> line from the test output and update the note by hand if it should be refreshed.
    /// </summary>
    public class FourPlayerLoadPerfTests
    {
        private const int Runs         = 5;   // median-of-5 so a single GC/scheduler hiccup cannot skew the record
        private const int WarmupTicks  = 15;  // pay JIT/first-tick allocation before measuring
        private const int MeasureTicks = 120; // K ticks the median ms/tick is computed over

        private readonly ITestOutputHelper _out;
        public FourPlayerLoadPerfTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void FourPlayerLateGame_MedianMsPerTick_IsMeasuredAndRecorded_NoCeiling()
        {
            var samples = new List<double>(Runs);
            int entities = 0, buildings = 0;

            for (int run = 0; run < Runs; run++)
            {
                SimulationHost host = BuildHeavyFourPlayerWorld(out entities, out buildings);

                for (int t = 0; t < WarmupTicks; t++) host.StepOnce(); // JIT + steady-state warm-up (unmeasured)

                var sw = Stopwatch.StartNew();
                for (int t = 0; t < MeasureTicks; t++) host.StepOnce();
                sw.Stop();

                samples.Add(sw.Elapsed.TotalMilliseconds / MeasureTicks);
            }

            samples.Sort();
            double medianMsPerTick = samples[Runs / 2];

            string line =
                $"4-player late-game median: {medianMsPerTick.ToString("F3", CultureInfo.InvariantCulture)} ms/tick " +
                $"(median-of-{Runs}, {MeasureTicks} ticks/run, ~{entities} entities + {buildings} buildings, 4 factions).";
            _out.WriteLine(line);
            _out.WriteLine($"raw ms/tick samples: [{string.Join(", ", samples.ConvertAll(s => s.ToString("F3", CultureInfo.InvariantCulture)))}]");

            // NON-GATED: no ceiling assertion. Just prove the harness actually measured a positive number.
            Assert.True(medianMsPerTick > 0.0, "expected a positive measured ms/tick");
        }

        /// <summary>Build a fresh heavy 4-faction world: ~<see cref="EntityWorld.MAX_ENTITIES"/> units + a full building
        /// set, spread across the four active factions. Direct store spawns (no scenario/validator) so the cost measured
        /// is the per-tick sim loop, not load.</summary>
        private static SimulationHost BuildHeavyFourPlayerWorld(out int entities, out int buildings)
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(4), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1; // fold the full checksum each tick (part of the real per-tick cost)

            // Buildings first: a full store spread across the four factions (each gets a CommandCenter + producers).
            buildings = 0;
            for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
            {
                Faction f = FactionRegistry.ToFaction(i % 4);
                var type = (i % 4 == 0) ? BuildingType.CommandCenter : BuildingType.Barracks;
                int id = host.Buildings.Create(At((i % 16) * 6 - 48, 60 + (i / 16) * 6), f, type);
                if (id < 0) break;
                buildings++;
            }

            // Units: fill toward the world capacity, round-robin across the four factions, spread on a wide grid
            // (mirrors the CanonicalModelHashPerfTests max-caps fixture's (i%200,i/200) spread — a heavy but not
            // pathologically-stacked late-game population).
            entities = 0;
            for (int i = 0; i < EntityWorld.MAX_ENTITIES; i++)
            {
                Faction f = FactionRegistry.ToFaction(i % 4);
                int gx = (i % 200) - 100;
                int gz = (i / 200) - 100;
                int id = host.World.Create(At(gx, gz), f, Fixed.FromInt(100), Fixed.FromInt(3));
                if (id < 0) break;
                entities++;
            }

            return host;
        }

        private static FixedVec3 At(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
    }
}
