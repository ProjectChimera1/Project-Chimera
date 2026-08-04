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
            // Story 9.5: inject the live local-faction getter so the minimap paints own-vs-enemy from the local player's
            // view. _ctx.Lockstep is built later (phase 17); the closure defers the read to gameplay time, and the
            // ?? Player1 guard keeps single-player byte-identical.
            minimap.SetLocalFaction(() => _ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1);
            // DW-406: mirror the spectator/observer reveal into the minimap. FogOfWarBridge.RevealAll is flipped at
            // spectator match start, on local elimination (spectate-out), and back off on reset/edit-return; the
            // deferred read keeps every current AND future flip site driving BOTH views from the one flag (Rendering
            // built FogBridge earlier; ?? false keeps any unwired boot byte-identical to the fogged read).
            minimap.SetRevealAll(() => _ctx.FogBridge?.RevealAll ?? false);
            _ctx.Minimap = minimap;
        }
    }
}
