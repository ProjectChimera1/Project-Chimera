#nullable enable
using Godot;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "ReplayStatus" phase (runtime position 18). Creates the small top-right "◉ REC" / "▶ REPLAY"
    /// status label (hidden until recording or replaying). Publishes ctx.ReplayStatusLabel. Behavior-identical to
    /// MainScene.SetupReplayStatusLabel.
    /// </summary>
    public sealed class ReplayStatusPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public ReplayStatusPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "ReplayStatus";

        public void Run()
        {
            var label = new Label
            {
                Text                = "",
                Visible             = false,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            label.AddThemeColorOverride("font_color", new Color(1f, 0.25f, 0.25f));
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            label.OffsetRight  = -10f;
            label.OffsetTop    = 10f;
            label.OffsetLeft   = -200f;
            label.OffsetBottom = 40f;
            _ctx.UiCanvas.AddChild(label);
            _ctx.ReplayStatusLabel = label;
        }
    }
}
