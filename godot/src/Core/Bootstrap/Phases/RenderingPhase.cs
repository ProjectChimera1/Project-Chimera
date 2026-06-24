#nullable enable
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Rendering" phase (runtime position 8). Builds the scenario-independent presentation bridges:
    /// resource-node visuals, the fog-of-war overlay (published as ctx.FogBridge for the match lifecycle), the
    /// projectile visuals, and the combat-feedback bridge (which needs the camera). The faction-dependent
    /// unit/building visuals are the separate FactionVisuals phase (after the scenario assigns slots).
    /// Behavior-identical to MainScene.SetupRendering.
    /// </summary>
    public sealed class RenderingPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public RenderingPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Rendering";

        public void Run()
        {
            // Unit and building visuals are faction-dependent and created later, in FactionVisuals — after the
            // scenario assigns each slot's faction — so per-slot meshes (alpha vs beta) render correctly.
            // Everything below here is scenario-independent and safe to build now.

            // Resource node visuals
            var nodeBridge = new ResourceNodeBridge();
            _ctx.Scene.AddChild(nodeBridge);
            nodeBridge.Initialize(_ctx.Nodes);

            // Fog of war overlay
            var fogBridge = new FogOfWarBridge();
            _ctx.Scene.AddChild(fogBridge);
            fogBridge.Initialize(_ctx.Fog);
            _ctx.FogBridge = fogBridge;

            // Projectile visuals
            var projBridge = new ProjectileBridge();
            _ctx.Scene.AddChild(projBridge);
            projBridge.Initialize(_ctx.Projectiles);

            // Combat feedback: hit flashes and camera shake
            var feedbackBridge = new CombatFeedbackBridge();
            _ctx.Scene.AddChild(feedbackBridge);
            feedbackBridge.Initialize(_ctx.CombatEvents, _ctx.Cam);
        }
    }
}
