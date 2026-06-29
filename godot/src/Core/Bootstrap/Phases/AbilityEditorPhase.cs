#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 2.5a "AbilityEditor" phase (runtime position 23, last). Creates the ability-authoring panel and wires it
    /// to the live scenario + game state + validated ability registry, then publishes ctx.AbilityEditorPanel. Toggle
    /// with K in Edit mode. Mirrors <see cref="TriggerEditorPhase"/> minus the LLM service + ScenarioDelegateBinder +
    /// toast label (the ability editor is create-only; it reads the registry and writes JSON files).
    /// </summary>
    public sealed class AbilityEditorPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public AbilityEditorPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "AbilityEditor";

        public void Run()
        {
            _ctx.AbilityEditorPanel = new AbilityEditorPanel();
            _ctx.Scene.AddChild(_ctx.AbilityEditorPanel);

            _ctx.AbilityEditorPanel.Initialize(_ctx.Scenario, _ctx.GameState, _ctx.AbilityRegistry);

            GD.Print("[AbilityEditor] Initialized — press K in Edit mode to open.");
        }
    }
}
