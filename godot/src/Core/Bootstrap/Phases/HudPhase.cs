#nullable enable
using Godot;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Hud" phase (runtime position 9). Builds the in-game HUD canvas and its labels: the top-left
    /// game-state panel, the resource strip, the bottom controls strip, and the centered stall banner. Carved
    /// FIRST because it owns <see cref="SceneContext.UiCanvas"/> — the CanvasLayer that Minimap, WinConditionUi,
    /// GameOverOverlay, ReplayStatus, and the TriggerEditor toast all attach to. Layout- and behavior-identical
    /// to the former <c>MainScene.SetupHud</c> (the per-frame label updates stay in <c>MainScene.UpdateHud</c>,
    /// reading these handles back off the context).
    /// </summary>
    public sealed class HudPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public HudPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Hud";

        public void Run()
        {
            var uiCanvas = new CanvasLayer();
            _ctx.Scene.AddChild(uiCanvas);
            _ctx.UiCanvas = uiCanvas;

            // ── Game-state info: top-left, 3 compact lines ────────────────────
            var hudBg = new StyleBoxFlat
            {
                BgColor                 = new Color(0f, 0f, 0f, 0.55f),
                CornerRadiusTopLeft     = 0,
                CornerRadiusTopRight    = 6,
                CornerRadiusBottomLeft  = 0,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft       = 10f,
                ContentMarginRight      = 14f,
                ContentMarginTop        = 6f,
                ContentMarginBottom     = 6f,
            };
            var hudPanel = new PanelContainer();
            hudPanel.AnchorLeft   = 0f;
            hudPanel.AnchorTop    = 0f;
            hudPanel.AnchorRight  = 0f;
            hudPanel.AnchorBottom = 0f;
            hudPanel.OffsetTop    = 4f;
            hudPanel.GrowHorizontal = Control.GrowDirection.End;
            hudPanel.AddThemeStyleboxOverride("panel", hudBg);
            uiCanvas.AddChild(hudPanel);

            var hudLabel = new Label();
            hudLabel.AddThemeColorOverride("font_color", Colors.White);
            hudLabel.AddThemeFontSizeOverride("font_size", 15);
            hudPanel.AddChild(hudLabel);
            _ctx.HudLabel = hudLabel;

            // ── Resource strip: just below the HUD panel ──────────────────────
            var resourceLabel = new Label();
            resourceLabel.AnchorLeft   = 0f;
            resourceLabel.AnchorTop    = 0f;
            resourceLabel.OffsetTop    = 80f;
            resourceLabel.OffsetLeft   = 10f;
            resourceLabel.AddThemeColorOverride("font_color", new Color(1f, 0.88f, 0.25f));
            resourceLabel.AddThemeFontSizeOverride("font_size", 14);
            uiCanvas.AddChild(resourceLabel);
            _ctx.ResourceLabel = resourceLabel;

            // ── Controls strip: bottom-left, context-sensitive shortcut hints ─
            var controlsLabel = new Label();
            controlsLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
            controlsLabel.OffsetBottom = -6f;
            controlsLabel.OffsetLeft   = 6f;
            controlsLabel.OffsetTop    = -32f;
            controlsLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.52f, 0.55f));
            controlsLabel.AddThemeFontSizeOverride("font_size", 12);
            uiCanvas.AddChild(controlsLabel);
            _ctx.ControlsLabel = controlsLabel;

            // ── Stall banner: centered at top, hidden until peer is slow ─────
            var stallBanner = new PanelContainer { Visible = false };
            stallBanner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
            stallBanner.GrowHorizontal = Control.GrowDirection.Both;
            stallBanner.OffsetTop      = 8f;

            var stallStyle = new StyleBoxFlat
            {
                BgColor                 = new Color(0.8f, 0.6f, 0f, 0.88f),
                CornerRadiusTopLeft     = 6,
                CornerRadiusTopRight    = 6,
                CornerRadiusBottomLeft  = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft       = 18f,
                ContentMarginRight      = 18f,
                ContentMarginTop        = 6f,
                ContentMarginBottom     = 6f,
            };
            stallBanner.AddThemeStyleboxOverride("panel", stallStyle);

            var stallLabel = new Label { Text = "Waiting for peer…" };
            stallLabel.AddThemeFontSizeOverride("font_size", 17);
            stallLabel.AddThemeColorOverride("font_color", Colors.White);
            stallBanner.AddChild(stallLabel);

            uiCanvas.AddChild(stallBanner);
            _ctx.StallBanner = stallBanner;
        }
    }
}
