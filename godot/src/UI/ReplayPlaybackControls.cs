#nullable enable
using Godot;
using ProjectChimera.Multiplayer; // ReplayFormat (shared tick→clock / speed helpers)
using System;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 9.11 — the in-playback control overlay for a running replay: pause/resume, speed steps (1x/2x/4x/8x),
    /// seek-forward-to-tick, a perspective cycle button, and a tick/clock readout. Presentation-only — every action
    /// routes back into MainScene's playback flags / view-only fog viewer via the events below; nothing here touches
    /// sim state or the checksum. Bound + driven by <c>ReplayBrowserPhase</c> / MainScene (which calls
    /// <see cref="SetActive"/> + <see cref="UpdateReadout"/> each frame).
    /// </summary>
    public partial class ReplayPlaybackControls : CanvasLayer
    {
        // ── Events (wired to MainScene's replay-control methods) ──────────────────
        public event Action?       OnTogglePause;
        public event Action<int>?  OnSetSpeed;
        public event Action<uint>? OnSeekForward;
        public event Action?       OnCyclePerspective;

        private static readonly int[] SPEEDS = { 1, 2, 4, 8 };

        private Button    _pauseBtn    = null!;
        private Label     _tickLabel   = null!;
        private Button    _perspBtn    = null!;
        private LineEdit  _seekField   = null!;
        private readonly Button[] _speedBtns = new Button[SPEEDS.Length];

        public void Initialize()
        {
            Layer   = 9; // above HUD (8), below the top overlays (10)
            Visible = false;

            var root = new PanelContainer();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
            root.OffsetTop = -64; root.OffsetLeft = 20; root.OffsetRight = -20; root.OffsetBottom = -14;
            root.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor                = new Color(0.08f, 0.09f, 0.13f, 0.94f),
                BorderColor            = new Color(0.30f, 0.35f, 0.50f, 0.7f),
                BorderWidthTop         = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
                CornerRadiusTopLeft    = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft      = 14, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6,
            });
            AddChild(root);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            root.AddChild(row);

            // Pause / Resume.
            _pauseBtn = new Button { Text = "Pause", CustomMinimumSize = new Vector2(90, 34) };
            _pauseBtn.AddThemeFontSizeOverride("font_size", 14);
            _pauseBtn.Pressed += () => OnTogglePause?.Invoke();
            row.AddChild(_pauseBtn);

            // Speed segmented control.
            var speedLbl = new Label { Text = "Speed:" };
            speedLbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            row.AddChild(speedLbl);
            for (int i = 0; i < SPEEDS.Length; i++)
            {
                int s = SPEEDS[i];
                var b = new Button { Text = $"{s}x", CustomMinimumSize = new Vector2(44, 34) };
                b.AddThemeFontSizeOverride("font_size", 14);
                b.Pressed += () => OnSetSpeed?.Invoke(s);
                _speedBtns[i] = b;
                row.AddChild(b);
            }

            row.AddChild(new VSeparator());

            // Seek-forward-to-tick.
            var seekLbl = new Label { Text = "Seek→tick:" };
            seekLbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            row.AddChild(seekLbl);
            _seekField = new LineEdit { PlaceholderText = "tick", CustomMinimumSize = new Vector2(80, 32) };
            _seekField.TextSubmitted += _ => DoSeek();
            row.AddChild(_seekField);
            var seekBtn = new Button { Text = "Go", CustomMinimumSize = new Vector2(44, 34) };
            seekBtn.Pressed += DoSeek;
            row.AddChild(seekBtn);

            row.AddChild(new VSeparator());

            // Perspective cycle.
            _perspBtn = new Button { Text = "View: Reveal All", CustomMinimumSize = new Vector2(160, 34) };
            _perspBtn.AddThemeFontSizeOverride("font_size", 14);
            _perspBtn.Pressed += () => OnCyclePerspective?.Invoke();
            row.AddChild(_perspBtn);

            row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

            // Tick / clock readout.
            _tickLabel = new Label { Text = "Tick 0" };
            _tickLabel.AddThemeFontSizeOverride("font_size", 14);
            _tickLabel.AddThemeColorOverride("font_color", Colors.White);
            row.AddChild(_tickLabel);
        }

        /// <summary>Show/hide the overlay (active only while a replay is playing).</summary>
        public void SetActive(bool active) => Visible = active;

        /// <summary>Refresh the readout from the current playback state (called each frame by MainScene). While
        /// <paramref name="seeking"/> the pause button reads "Seeking…" so a bounded multi-frame seek is legible.</summary>
        public void UpdateReadout(uint tick, uint finalTick, bool paused, int speed, string perspective, bool seeking = false)
        {
            _pauseBtn.Text  = seeking ? "Seeking…" : paused ? "Resume" : "Pause";
            _perspBtn.Text  = $"View: {perspective}";
            _tickLabel.Text = $"Tick {tick} / {finalTick}    {ReplayFormat.Duration(tick)} / {ReplayFormat.Duration(finalTick)}";

            for (int i = 0; i < _speedBtns.Length; i++)
            {
                bool selected = !paused && !seeking && SPEEDS[i] == speed;
                _speedBtns[i].AddThemeColorOverride("font_color",
                    selected ? new Color(0.4f, 0.9f, 0.5f) : Colors.White);
            }
        }

        private void DoSeek()
        {
            if (uint.TryParse(_seekField.Text.Trim(), out uint target))
                OnSeekForward?.Invoke(target);
        }
    }
}
