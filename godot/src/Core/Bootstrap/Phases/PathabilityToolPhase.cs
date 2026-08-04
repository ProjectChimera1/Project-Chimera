#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 6.5 "PathabilityTool" phase. Attaches the impassable-terrain paint tool + the WC3 'P' pathability
    /// overlay (press K in Edit mode to paint; P toggles the overlay independently). Runs AFTER ScenarioLoad so
    /// <c>_ctx.Scenario</c> (the live edited scenario whose <c>PathabilityBlocked</c> bitset the tool mutates) and
    /// <c>_ctx.Pathability</c> (the load-time union grid the overlay first renders) exist, and after Camera so
    /// <c>_ctx.Placer</c> (the shared <see cref="ProjectChimera.UI.EditorHistory"/> owner) exists — both guaranteed
    /// by the canonical phase order and pinned by <c>PhaseOrderTest</c>. Injects the SAME EditorHistory the entity
    /// placer / terrain brush / region tool use, so paint/erase strokes interleave LIFO with entity, terrain, and
    /// region undo/redo. Produces no shared handle.
    /// </summary>
    public sealed class PathabilityToolPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public PathabilityToolPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "PathabilityTool";

        public void Run()
        {
            // A degraded bootstrap path can leave a required handle null. Log-skip (never NRE the whole scene) if any
            // handle we dereference below is missing, mirroring the tolerant RegionTool posture. _ctx.Scenario is
            // intentionally NOT required — it may be null on the hardcoded-fallback map (no ScenarioData to persist a
            // painted layer into), and the tool guards that at paint time.
            if (_ctx.Scene == null || _ctx.Cam == null || _ctx.GameState == null || _ctx.Placer == null)
            {
                GD.Print("[PathabilityTool] Skipped — a required handle (scene/camera/game-state/placer) is unavailable " +
                         "(degraded bootstrap).");
                return;
            }

            var tool = new PathabilityTool();
            _ctx.Scene.AddChild(tool);
            _ctx.PathTool = tool; // so the Edit hotkey strip can show the overlay's live state
            // _ctx.Placer.History is the shared editor undo/redo stack (History may itself be null; Initialize accepts it).
            tool.Initialize(_ctx.Cam, _ctx.GameState, _ctx.Scenario, _ctx.Placer.History, _ctx.Pathability);
            GD.Print("[PathabilityTool] Ready — press K in Edit mode to paint impassable terrain; P toggles the overlay.");
        }
    }
}
