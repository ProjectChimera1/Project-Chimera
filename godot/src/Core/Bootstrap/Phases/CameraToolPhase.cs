#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 6.6 "CameraTool" phase. Attaches the named-camera tool (press V in Edit mode). Runs AFTER ScenarioLoad
    /// (so <c>_ctx.Scenario</c> exists to persist cameras into) and after Camera (so <c>_ctx.Cam</c> + the shared
    /// <c>_ctx.Placer.History</c> exist) — both guaranteed by the canonical phase order and pinned by
    /// <c>PhaseOrderTest</c>. Injects the SAME <see cref="ProjectChimera.UI.EditorHistory"/> the entity placer, terrain
    /// brush, region tool, and pathability tool use, so camera add/delete interleaves LIFO with all other editor
    /// undo/redo. Produces no shared handle.
    /// </summary>
    public sealed class CameraToolPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public CameraToolPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "CameraTool";

        public void Run()
        {
            if (_ctx.Scene == null || _ctx.Cam == null || _ctx.GameState == null || _ctx.Placer == null)
            {
                GD.Print("[CameraTool] Skipped — a required handle (scene/camera/game-state/placer) is unavailable (degraded bootstrap).");
                return;
            }

            var tool = new CameraTool();
            _ctx.Scene.AddChild(tool);
            tool.Initialize(_ctx.Cam, _ctx.GameState, _ctx.Scenario, _ctx.Placer.History);
            GD.Print("[CameraTool] Ready — press V in Edit mode to author named cameras.");
        }
    }
}
