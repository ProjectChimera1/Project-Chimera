#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 6.6 "WaterTool" phase. Attaches the water tool (press N in Edit mode to drag a water rect). Runs AFTER
    /// ScenarioLoad (so <c>_ctx.Scenario</c> exists to persist water into) and after Camera (so <c>_ctx.Cam</c> + the
    /// shared <c>_ctx.Placer.History</c> exist) — both pinned by <c>PhaseOrderTest</c>. Injects the SAME
    /// <see cref="ProjectChimera.UI.EditorHistory"/> every other editor tool uses, so water add/delete interleaves LIFO
    /// with all other undo/redo. The water footprint auto-stamps into the pathability grid at load (ScenarioLoadPhase);
    /// this tool only authors + persists + renders the visual plane. Produces no shared handle.
    /// </summary>
    public sealed class WaterToolPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public WaterToolPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "WaterTool";

        public void Run()
        {
            if (_ctx.Scene == null || _ctx.Cam == null || _ctx.GameState == null || _ctx.Placer == null)
            {
                GD.Print("[WaterTool] Skipped — a required handle (scene/camera/game-state/placer) is unavailable (degraded bootstrap).");
                return;
            }

            var tool = new WaterTool();
            _ctx.Scene.AddChild(tool);
            tool.Initialize(_ctx.Cam, _ctx.GameState, _ctx.Scenario, _ctx.Placer.History);
            GD.Print("[WaterTool] Ready — press N in Edit mode to draw water volumes.");
        }
    }
}
