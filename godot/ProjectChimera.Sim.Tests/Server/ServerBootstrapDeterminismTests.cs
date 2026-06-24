#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;              // Faction
using ProjectChimera.Core.Definitions;  // ScenarioData, FactionDefinition
using ProjectChimera.Core.Sim;          // ServerBootstrap, SimulationHost, ILogSink, NullLogSink
using ProjectChimera.Sim.Tests.Golden;  // GoldenChecksumReplay, GoldenApplierScenario, GoldenHarness
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 1.9a (AC1, D2) — proves the server's sim path == the client's. ServerBootstrap composes the EXACT
    /// 1.8 spine (SimulationHost + ScenarioValidator + ScenarioApplier), so a host it builds from the SAME model
    /// the client-path applier golden uses must reproduce that committed golden byte-identically over 300+ ticks
    /// (i.e. server start-state checksum == client offline start-state). Also proves the server is FAIL-CLOSED:
    /// an invalid scenario returns null and logs, never ticking unvalidated state.
    /// </summary>
    public class ServerBootstrapDeterminismTests
    {
        /// <summary>Build a sim host through ServerBootstrap using the identical model/faction the applier golden pins.</summary>
        private static GoldenHarness BuildViaServerBootstrap()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            SimulationHost? host = ServerBootstrap.Build(
                GoldenApplierScenario.BuildModel(), slotDefs, damageTable: null,
                NullLogSink.Instance, activeFactionCount: 2);

            Assert.NotNull(host); // the golden model is valid → fail-closed must NOT trip
            host!.ChecksumInterval = 1;
            return new GoldenHarness(host, 0);
        }

        [Fact]
        public void ServerBuiltHost_ReproducesClientApplierGolden_300Ticks()
        {
            if (GoldenChecksumReplay.IsRecordMode) return; // re-baseline run: golden is being rewritten; skip

            var serverSeq = GoldenChecksumReplay.RunAndRecord(
                GoldenApplierScenario.DefaultTicks, build: BuildViaServerBootstrap);

            Assert.True(serverSeq.Count >= GoldenApplierScenario.DefaultTicks,
                $"Expected >= {GoldenApplierScenario.DefaultTicks} samples from the server-built host, got {serverSeq.Count}.");

            // Byte-identical to the committed client-path applier golden ⇒ server sim path == client sim path.
            var golden = GoldenChecksumReplay.LoadGolden(GoldenApplierScenario.GoldenFileName);
            var div = GoldenChecksumReplay.CompareSequences(golden, serverSeq);
            Assert.True(div is null,
                div is null ? "" : "Server sim path diverged from the client golden: " + GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void ServerBuiltHost_IsDeterministic_AcrossTwoInProcessBuilds()
        {
            var a = GoldenChecksumReplay.RunAndRecord(GoldenApplierScenario.DefaultTicks, build: BuildViaServerBootstrap);
            var b = GoldenChecksumReplay.RunAndRecord(GoldenApplierScenario.DefaultTicks, build: BuildViaServerBootstrap);
            Assert.True(a.SequenceEqual(b),
                "Two server-built runs diverged — a static/shared mutable-state leak broke determinism.");
        }

        [Fact]
        public void Build_FailsClosed_OnInvalidScenario_ReturnsNullAndLogs()
        {
            var log = new CapturingLogSink();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = GoldenApplierScenario.BuildFaction();
            slotDefs[(int)Faction.Player2] = slotDefs[(int)Faction.Player1];

            // map_bounds <= 0 fails the validator's first check → fail-closed.
            var invalid = new ScenarioData { Id = "bad", MapBounds = -1f };

            SimulationHost? host = ServerBootstrap.Build(invalid, slotDefs, null, log, activeFactionCount: 2);

            Assert.Null(host);
            Assert.Contains(log.Warns, m => m.Contains("REJECTED"));
        }

        /// <summary>Test ILogSink that captures messages so the fail-closed warn can be asserted.</summary>
        private sealed class CapturingLogSink : ILogSink
        {
            public readonly List<string> Infos = new();
            public readonly List<string> Warns = new();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }
    }
}
