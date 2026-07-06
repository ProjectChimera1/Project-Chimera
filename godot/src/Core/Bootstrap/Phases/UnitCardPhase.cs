#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 3.3 "UnitCard" phase (runtime position 24, last). Creates the read-only Unit Card panel and wires it to
    /// the current scenario's faction (<c>ctx.FactionDef</c>, the default alpha, populated by <c>ScenarioLoadPhase</c>
    /// well before this phase) + game state + validated ability registry, then publishes <c>ctx.UnitCardPanel</c>.
    /// Toggle with J in Edit mode. Clones <see cref="AbilityEditorPhase"/>'s shape; the panel is display-only — it reads
    /// a <c>FactionDefinition</c> + the registry and mutates nothing (the sacred sim/presentation boundary).
    /// </summary>
    public sealed class UnitCardPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public UnitCardPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "UnitCard";

        public void Run()
        {
            _ctx.UnitCardPanel = new UnitCardPanel();
            _ctx.Scene.AddChild(_ctx.UnitCardPanel);

            _ctx.UnitCardPanel.Initialize(_ctx.FactionDef, _ctx.GameState, _ctx.AbilityRegistry);

            GD.Print("[UnitCard] Initialized — press J in Edit mode to open.");
        }
    }
}
