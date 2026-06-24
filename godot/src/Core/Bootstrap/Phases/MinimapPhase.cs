#nullable enable
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Minimap" phase (runtime position 10). Creates the minimap bridge and attaches it to the HUD
    /// canvas. Publishes ctx.Minimap (consumed by ApplySettingsToSystems' show/hide toggle). Runs after Hud (needs
    /// the UI canvas) and Camera. Behavior-identical to MainScene.SetupMinimap.
    /// </summary>
    public sealed class MinimapPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public MinimapPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Minimap";

        public void Run()
        {
            var minimap = new MinimapBridge();
            _ctx.UiCanvas.AddChild(minimap);
            minimap.Initialize(_ctx.World, _ctx.Buildings, _ctx.Fog, _ctx.Cam);
            _ctx.Minimap = minimap;
        }
    }
}
