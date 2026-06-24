#nullable enable
using Godot;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "GameOverOverlay" phase (runtime position 16). Creates the full-screen game-over dimming overlay
    /// (hidden until MainScene.ShowGameOver populates it with live match data). Publishes ctx.GameOverOverlay.
    /// Behavior-identical to MainScene.SetupGameOverOverlay.
    /// </summary>
    public sealed class GameOverOverlayPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public GameOverOverlayPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "GameOverOverlay";

        public void Run()
        {
            // Root dimming rect — reused as the overlay root
            var root = new ColorRect { Color = new Color(0f, 0f, 0f, 0.65f), Visible = false };
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _ctx.UiCanvas.AddChild(root);
            _ctx.GameOverOverlay = root;
        }
    }
}
