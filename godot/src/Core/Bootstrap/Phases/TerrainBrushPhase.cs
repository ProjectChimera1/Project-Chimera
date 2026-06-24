#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "TerrainBrush" phase (runtime position 11). Attaches the terrain sculpting brush (T in Edit
    /// mode). Requires Terrain3D — no-ops on the PlaneMesh fallback. Produces no shared handle. Behavior-identical
    /// to MainScene.SetupTerrainBrush.
    /// </summary>
    public sealed class TerrainBrushPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public TerrainBrushPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "TerrainBrush";

        public void Run()
        {
            if (_ctx.Terrain == null) return; // PlaneMesh fallback: no brush
            var brush = new TerrainBrush();
            _ctx.Scene.AddChild(brush);
            brush.Initialize(_ctx.Terrain, _ctx.Cam, _ctx.NavObstacles, _ctx.GameState);
            GD.Print("[MainScene] TerrainBrush ready — press T in Edit mode to activate.");
        }
    }
}
