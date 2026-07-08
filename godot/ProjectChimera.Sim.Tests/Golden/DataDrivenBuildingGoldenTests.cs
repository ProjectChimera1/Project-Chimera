#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 4.1 (AC3) — two INDEPENDENT <see cref="DataDrivenBuildingScenario.Build"/> runs (fresh stores, a fresh
    /// <c>FactionDefinition.LoadFromFile</c> of the real <c>alpha_faction.json</c> each time) produce byte-identical
    /// per-tick <see cref="SimChecksum"/> sequences — proving the data-driven <c>BuildingStore.Create()</c> path is
    /// deterministic through the ACTUAL production placement path (<c>BuildingSystem.PlaceBuildingDirect</c>), not
    /// just at the store level (<c>BuildingStoreDataDrivenTests</c>) or through the switch-fallback goldens (which
    /// never exercise this path at all — this story's "Always" boundary keeps them on the untouched switch). A
    /// same-run comparison only, per the story's task — no committed golden file is needed for this scenario.
    /// </summary>
    public class DataDrivenBuildingGoldenTests
    {
        private static List<uint> Run(int ticks)
        {
            (GoldenHarness harness, int[] _) = DataDrivenBuildingScenario.Build();
            var seq = new List<uint>(ticks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(hash));
            for (int i = 0; i < ticks; i++)
                harness.Host.StepOnce();
            return seq;
        }

        [Fact]
        public void TwoIndependentRuns_ProduceByteIdenticalChecksumSequences()
        {
            List<uint> a = Run(DataDrivenBuildingScenario.DefaultTicks);
            List<uint> b = Run(DataDrivenBuildingScenario.DefaultTicks);

            Assert.Equal(DataDrivenBuildingScenario.DefaultTicks, a.Count);
            Assert.Equal(a, b);
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            List<uint> seq = Run(DataDrivenBuildingScenario.DefaultTicks);
            Assert.True(seq.Distinct().Count() > 1,
                "data-driven building sequence is constant — construction timers are not moving folded state (vacuous scenario).");
        }
    }
}
