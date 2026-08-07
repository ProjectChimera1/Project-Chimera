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

            // Story 8.4: also pass the LLM service + four-state evaluator + secret store (all present on SceneContext
            // since SettingsPhase/TriggerEditorPhase run earlier) so the editor offers AI ability drafts; nullable —
            // a null evaluator hides the AI row and leaves manual authoring fully usable (older-wiring fallback).
            _ctx.AbilityEditorPanel.Initialize(_ctx.Scenario, _ctx.GameState, _ctx.AbilityRegistry,
                _ctx.LlmService, _ctx.AiEvaluator, _ctx.SecretStore);

            GD.Print($"[AbilityEditor] Initialized — press {Definitions.EditorHotkeys.ChordFor(Definitions.EditorPanelId.Ability)} in Edit mode to open.");
        }
    }
}
