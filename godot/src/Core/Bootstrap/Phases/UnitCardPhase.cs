#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 3.3/3.4 "UnitCard" phase (runtime position 24, last). Creates the Unit Card Editor panel and wires it to
    /// the current scenario's faction (<c>ctx.FactionDef</c>, the default alpha, populated by <c>ScenarioLoadPhase</c>
    /// well before this phase) + game state + validated ability registry, then publishes <c>ctx.UnitCardPanel</c>.
    /// Toggle with J in Edit mode. Clones <see cref="AbilityEditorPhase"/>'s shape.
    ///
    /// <para>Story 3.4 threads the faction file <c>res://</c> path in so the editor can write edits back (D-8): the
    /// scenario's slot-0 <c>faction_json</c> when a scenario is loaded, else the default P1 alpha path. The panel holds
    /// only the parsed <c>FactionDefinition</c>, so the phase supplies the path (authoring-time file I/O; no sim state).</para>
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

            // D-8: the source faction file to persist edits to — the scenario's slot-0 faction when loaded, else the
            // default P1 alpha faction the panel is bound to.
            string factionPath =
                _ctx.Scenario?.PlayerSlots is { Length: > 0 } slots && !string.IsNullOrEmpty(slots[0].FactionJson)
                    ? slots[0].FactionJson
                    : MainScene.P1_FACTION_JSON;

            // Story 8.4: also pass the LLM service + four-state evaluator + secret store (all present on SceneContext
            // since SettingsPhase/TriggerEditorPhase run earlier) so the editor offers AI unit/hero drafts; nullable —
            // a null evaluator hides the AI row and leaves manual authoring fully usable (older-wiring fallback).
            _ctx.UnitCardPanel.Initialize(_ctx.FactionDef, _ctx.GameState, _ctx.AbilityRegistry, _ctx.BehaviorRegistry,
                factionPath, _ctx.LlmService, _ctx.AiEvaluator, _ctx.SecretStore);

            GD.Print("[UnitCard] Initialized — press J in Edit mode to open (edit/create/duplicate/delete).");
        }
    }
}
