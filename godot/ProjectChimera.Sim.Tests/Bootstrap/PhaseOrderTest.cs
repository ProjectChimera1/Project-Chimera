#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core.Bootstrap;
using Xunit;

namespace ProjectChimera.Sim.Tests.Bootstrap
{
    /// <summary>
    /// Pins the canonical composition-root phase order that <see cref="ScenePhaseRunner"/> asserts at startup
    /// (Story 1.8c / AR-3 constraint C1, enumerated by AR-35). The order IS the contract — a hidden presentation
    /// timing dependency surfaces as an NRE the golden suite cannot catch, so these tests FAIL loudly the moment
    /// <see cref="ScenePhaseOrder.Canonical"/> drifts. Mirrors the sim-side <c>SystemOrderTest</c> precedent but
    /// uses Godot-free <see cref="string"/> phase names: a concrete presentation phase type is NEVER referenced
    /// here (that would drag GodotSharp into this Godot-free assembly and break <c>GodotFreeBoundaryTest</c>).
    /// </summary>
    public class PhaseOrderTest
    {
        /// <summary>
        /// The canonical order, hardcoded here INDEPENDENTLY of <see cref="ScenePhaseOrder.Canonical"/> so that a
        /// drift in either one fails this test. Derived from <c>MainScene._Ready</c> at Story 1.8c.
        /// </summary>
        private static readonly string[] ExpectedOrder =
        {
            "Settings", "Audio", "GameState", "Lighting", "Terrain", "Navigation", "Camera",
            "Rendering", "Hud", "Minimap", "TerrainBrush", "ScenarioLoad", "FactionVisuals",
            "FlowFieldInit", "WinConditionUi", "GameOverOverlay", "Multiplayer", "ReplayStatus",
            "ContentBrowser", "MainMenu", "TriggerEditor", "MapGenerator",
        };

        /// <summary>A Godot-free stub phase that appends its name to a shared log when run (call-order proof).</summary>
        private sealed class StubPhase : ISetupPhase
        {
            private readonly List<string> _log;
            public StubPhase(string name, List<string> log) { Name = name; _log = log; }
            public string Name { get; }
            public void Run() => _log.Add(Name);
        }

        /// <summary>Build one stub per canonical phase, in canonical order, sharing the given run-log.</summary>
        private static List<ISetupPhase> CanonicalStubs(List<string> log)
        {
            var phases = new List<ISetupPhase>(ScenePhaseOrder.Canonical.Length);
            foreach (string name in ScenePhaseOrder.Canonical)
                phases.Add(new StubPhase(name, log));
            return phases;
        }

        [Fact]
        public void PhaseOrder_IsTheCanonicalSequence_InExactOrder()
        {
            Assert.Equal(ExpectedOrder.Length, ScenePhaseOrder.Canonical.Length);
            for (int i = 0; i < ExpectedOrder.Length; i++)
                Assert.Equal(ExpectedOrder[i], ScenePhaseOrder.Canonical[i]);
        }

        [Fact]
        public void Runner_RunsPhasesInCanonicalOrder_WhenCorrect()
        {
            var log = new List<string>();
            new ScenePhaseRunner(CanonicalStubs(log)).Run();
            Assert.Equal(ScenePhaseOrder.Canonical, log.ToArray());
        }

        [Fact]
        public void Runner_Throws_WhenAPhaseIsReordered()
        {
            var log = new List<string>();
            List<ISetupPhase> phases = CanonicalStubs(log);
            (phases[0], phases[1]) = (phases[1], phases[0]); // swap first two out of order

            Assert.Throws<InvalidOperationException>(() => new ScenePhaseRunner(phases).Run());
            Assert.Empty(log); // AssertOrder must throw BEFORE any phase body runs
        }

        [Fact]
        public void Runner_Throws_WhenAPhaseIsRemoved()
        {
            var log = new List<string>();
            List<ISetupPhase> phases = CanonicalStubs(log);
            phases.RemoveAt(phases.Count - 1);

            Assert.Throws<InvalidOperationException>(() => new ScenePhaseRunner(phases).AssertOrder());
        }

        [Fact]
        public void Runner_Throws_WhenAPhaseIsAdded()
        {
            var log = new List<string>();
            List<ISetupPhase> phases = CanonicalStubs(log);
            phases.Add(new StubPhase("Extra", log));

            Assert.Throws<InvalidOperationException>(() => new ScenePhaseRunner(phases).AssertOrder());
        }
    }
}
