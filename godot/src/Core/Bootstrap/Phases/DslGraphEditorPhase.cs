#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 7.10 "DslGraphEditor" phase — mirrors <see cref="TechTreePhase"/>. Constructs the T3 visual
    /// node-graph editor panel and binds it to the live scenario (owned by <c>ScenarioLoad</c>) + game state, then
    /// hands the panel back to the already-constructed T2 <see cref="SceneContext.TriggerPanel"/> so its read-only
    /// "edit in graph view" fallback rows can open T3. MUST run after <c>TriggerEditor</c> in
    /// <c>MainScene.cs</c>'s phase list so <c>_ctx.TriggerPanel</c> already exists.
    ///
    /// <para>Toggle with Y in Edit mode.</para>
    /// </summary>
    public sealed class DslGraphEditorPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public DslGraphEditorPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "DslGraphEditor";

        public void Run()
        {
            _ctx.DslGraphEditorPanel = new DslGraphEditorPanel();
            _ctx.Scene.AddChild(_ctx.DslGraphEditorPanel);
            _ctx.DslGraphEditorPanel.Initialize(_ctx.Scenario, _ctx.GameState);

            // Reciprocal wiring: T2's graph-only fallback rows open this T3 panel.
            _ctx.TriggerPanel.SetGraphEditor(_ctx.DslGraphEditorPanel);

            GD.Print($"[DslGraphEditor] Initialized — press {Definitions.EditorHotkeys.ChordFor(Definitions.EditorPanelId.DslGraph)} in Edit mode to open the node-graph editor.");
        }
    }
}
