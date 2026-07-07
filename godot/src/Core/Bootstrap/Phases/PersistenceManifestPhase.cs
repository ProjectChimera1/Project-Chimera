#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 3.8 "PersistenceManifest" phase (runtime position 25, last). Creates the Persistence Manifest editor panel
    /// and wires it to the live scenario (<c>ctx.Scenario</c>, populated by <c>ScenarioLoadPhase</c> well before this
    /// phase) + game state, then publishes <c>ctx.PersistenceManifestPanel</c>. Toggle with V in Edit mode. Clones
    /// <see cref="UnitCardPhase"/>'s shape; the panel persists via <c>ScenarioSerializer.SaveToFile</c> against the
    /// scenario's <c>res://</c> path (the <see cref="WinConditionPhase"/> precedent).
    /// </summary>
    public sealed class PersistenceManifestPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public PersistenceManifestPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "PersistenceManifest";

        public void Run()
        {
            _ctx.PersistenceManifestPanel = new PersistenceManifestPanel();
            _ctx.Scene.AddChild(_ctx.PersistenceManifestPanel);

            // The scenario's res:// path is the write-back target — the same path MainScene loads/saves and the panel
            // globalizes at Save time. The live edited scenario (null on the hardcoded fallback) is bound directly.
            _ctx.PersistenceManifestPanel.Initialize(_ctx.Scenario, _ctx.GameState, _ctx.Scene.ScenarioPath);

            GD.Print("[PersistenceManifest] Initialized — press V in Edit mode to open (choose which hero progression carries forward).");
        }
    }
}
