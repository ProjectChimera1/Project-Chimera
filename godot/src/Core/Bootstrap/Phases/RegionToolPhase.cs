#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 6.4 "RegionTool" phase. Attaches the named-region draw tool (press I in Edit mode). Runs AFTER
    /// ScenarioLoad so <c>_ctx.Scenario</c> (the live edited scenario the tool mutates) exists, and after Camera so
    /// <c>_ctx.Placer</c> (the shared <see cref="EditorHistory"/> owner) exists — both guaranteed by the canonical
    /// phase order and pinned by <c>PhaseOrderTest</c>. Injects the SAME <see cref="EditorHistory"/> the entity
    /// placer and terrain brush use, so region add/delete/resize interleave LIFO with entity undo/redo. Produces no
    /// shared handle.
    /// </summary>
    public sealed class RegionToolPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public RegionToolPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "RegionTool";

        public void Run()
        {
            // Review patch: a degraded bootstrap path can leave a required handle null. Log-skip (never NRE the
            // whole scene) if any handle we dereference below is missing, mirroring the tolerant _ctx.Scenario
            // posture. _ctx.Scenario is intentionally NOT required — it may be null on the hardcoded-fallback map
            // (no ScenarioData to persist regions into), and the tool guards that at commit time.
            if (_ctx.Scene == null || _ctx.Cam == null || _ctx.GameState == null || _ctx.Placer == null)
            {
                GD.Print("[RegionTool] Skipped — a required handle (scene/camera/game-state/placer) is unavailable " +
                         "(degraded bootstrap).");
                return;
            }

            var tool = new RegionTool();
            _ctx.Scene.AddChild(tool);
            // _ctx.Placer.History is the shared editor undo/redo stack (History may itself be null; Initialize accepts it).
            tool.Initialize(_ctx.Cam, _ctx.GameState, _ctx.Scenario, _ctx.Placer.History);
            GD.Print("[RegionTool] Ready — press I in Edit mode to draw named regions.");
        }
    }
}
