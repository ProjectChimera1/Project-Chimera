#nullable enable
using Godot;
using System;

namespace ProjectChimera.UI
{
    /// <summary>
    /// In-game settings panel — shown/hidden with Escape or via HUD settings button.
    /// Persists all changes immediately to user://settings.json.
    ///
    /// Requires a <see cref="SettingsManager"/> node in the scene tree.
    ///
    /// Usage:
    ///   var panel = new SettingsPanel();
    ///   AddChild(panel);
    ///   panel.Initialize(settingsManager);
    ///
    /// Key: Escape toggles the panel (wired in MainScene).
    /// </summary>
    public partial class SettingsPanel : CanvasLayer
    {
        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired when the user clicks Close or presses Escape.</summary>
        public event Action? OnClosed;

        // ── State ─────────────────────────────────────────────────────────────

        private SettingsManager _settings = null!;

        // Sliders / toggles — kept as fields so Apply() can read them.
        private HSlider _cameraSpeedSlider  = null!;
        private HSlider _zoomSpeedSlider    = null!;
        private CheckButton _edgeScrollBtn  = null!;
        private HSlider _masterVolSlider    = null!;
        private HSlider _sfxVolSlider       = null!;
        private HSlider _musicVolSlider     = null!;
        private CheckButton _minimapBtn     = null!;
        private CheckButton _fpsBtn         = null!;
        private CheckButton _colorblindBtn  = null!;

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>Build the settings UI and sync all widgets to current settings.</summary>
        public void Initialize(SettingsManager settings)
        {
            _settings = settings;
            Layer     = 15; // above content browser (10), below nothing
            Visible   = false;

            // ── Anchor root ───────────────────────────────────────────────────
            // Direct children of a CanvasLayer don't use the parent-based anchor
            // system reliably, so we insert a full-rect Control as the anchor
            // parent. It also acts as the primary input blocker so that no clicks
            // reach the 3D scene or other UI layers behind the settings panel.
            var anchorRoot = new Control();
            anchorRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(anchorRoot);

            // ── Root panel (dark overlay) ─────────────────────────────────────
            var root = new PanelContainer();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Stop;
            root.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.05f, 0.08f, 0.92f),
            });
            anchorRoot.AddChild(root);

            // ── Centre card ───────────────────────────────────────────────────
            var centreMargin = new MarginContainer();
            centreMargin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
            centreMargin.CustomMinimumSize = new Vector2(520, 0);
            root.AddChild(centreMargin);

            // Actual visible card.
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor                 = new Color(0.12f, 0.13f, 0.18f, 1f),
                BorderColor             = new Color(0.3f, 0.4f, 0.6f, 0.6f),
                BorderWidthLeft = 1, BorderWidthRight  = 1,
                BorderWidthTop  = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight    = 8,
                CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft = 32, ContentMarginRight  = 32,
                ContentMarginTop  = 28, ContentMarginBottom = 28,
            });
            centreMargin.AddChild(card);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 14);
            card.AddChild(vbox);

            // ── Title + close ─────────────────────────────────────────────────
            var titleRow = new HBoxContainer();
            vbox.AddChild(titleRow);

            var title = new Label { Text = "Settings" };
            title.AddThemeFontSizeOverride("font_size", 26);
            title.AddThemeColorOverride("font_color", Colors.White);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleRow.AddChild(title);

            var closeBtn = new Button { Text = "Close  [Esc]", CustomMinimumSize = new Vector2(110, 34) };
            closeBtn.AddThemeFontSizeOverride("font_size", 13);
            closeBtn.Pressed += Close;
            titleRow.AddChild(closeBtn);

            vbox.AddChild(new HSeparator());

            // ── Camera ────────────────────────────────────────────────────────
            AddSectionHeader(vbox, "CAMERA");

            _cameraSpeedSlider = AddSliderRow(vbox, "Pan speed",
                min: 0.25f, max: 3.0f, step: 0.05f,
                value: _settings.Current.CameraSpeed,
                format: "×{0:0.0#}");

            _zoomSpeedSlider = AddSliderRow(vbox, "Zoom speed",
                min: 0.25f, max: 3.0f, step: 0.05f,
                value: _settings.Current.CameraZoomSpeed,
                format: "×{0:0.0#}");

            _edgeScrollBtn = AddToggleRow(vbox, "Edge scroll",
                "Scroll camera when cursor reaches the screen edge.",
                _settings.Current.EdgeScrollEnabled);

            vbox.AddChild(new HSeparator());

            // ── Audio ─────────────────────────────────────────────────────────
            AddSectionHeader(vbox, "AUDIO");

            _masterVolSlider = AddSliderRow(vbox, "Master volume",
                min: 0f, max: 1f, step: 0.01f,
                value: _settings.Current.MasterVolume,
                format: "{0:0%}");

            _sfxVolSlider = AddSliderRow(vbox, "SFX volume",
                min: 0f, max: 1f, step: 0.01f,
                value: _settings.Current.SfxVolume,
                format: "{0:0%}");

            _musicVolSlider = AddSliderRow(vbox, "Music volume",
                min: 0f, max: 1f, step: 0.01f,
                value: _settings.Current.MusicVolume,
                format: "{0:0%}");

            vbox.AddChild(new HSeparator());

            // ── UI / HUD ──────────────────────────────────────────────────────
            AddSectionHeader(vbox, "HUD");

            _minimapBtn = AddToggleRow(vbox, "Show minimap",
                "Show the overhead minimap in the lower-right corner.",
                _settings.Current.ShowMinimap);

            _fpsBtn = AddToggleRow(vbox, "Show FPS",
                "Display the current frame rate in the HUD.",
                _settings.Current.ShowFps);

            vbox.AddChild(new HSeparator());

            // ── Accessibility ─────────────────────────────────────────────────
            AddSectionHeader(vbox, "ACCESSIBILITY");

            _colorblindBtn = AddToggleRow(vbox, "Colorblind-friendly colors",
                "Changes Player 2 units from red to orange so they are readable in red-green color blindness.",
                _settings.Current.ColorblindMode);

            vbox.AddChild(new HSeparator());

            // ── Apply / Reset ─────────────────────────────────────────────────
            var btnRow = new HBoxContainer();
            btnRow.AddThemeConstantOverride("separation", 10);
            vbox.AddChild(btnRow);

            btnRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

            var resetBtn = new Button { Text = "Reset to Defaults", CustomMinimumSize = new Vector2(150, 34) };
            resetBtn.AddThemeFontSizeOverride("font_size", 13);
            resetBtn.Pressed += ResetToDefaults;
            btnRow.AddChild(resetBtn);

            var applyBtn = new Button { Text = "Apply & Save", CustomMinimumSize = new Vector2(130, 34) };
            applyBtn.AddThemeFontSizeOverride("font_size", 13);
            applyBtn.Pressed += ApplyAndSave;
            btnRow.AddChild(applyBtn);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void ToggleVisible()
        {
            Visible = !Visible;
        }

        public void Close()
        {
            Visible = false;
            OnClosed?.Invoke();
        }

        // ── Keyboard ──────────────────────────────────────────────────────────

        // Use _Input (not _UnhandledInput) so the Escape keystroke is consumed
        // before MainScene's _UnhandledInput can re-open menus behind the panel.
        public override void _Input(InputEvent ev)
        {
            if (!Visible) return;
            if (ev is InputEventKey { Pressed: true, KeyLabel: Key.Escape })
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void AddSectionHeader(Control parent, string text)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", 11);
            lbl.AddThemeColorOverride("font_color", new Color(0.5f, 0.6f, 0.8f));
            parent.AddChild(lbl);
        }

        /// <summary>Add a labeled HSlider row; returns the slider for later reads.</summary>
        private static HSlider AddSliderRow(Control parent, string label,
                                            float min, float max, float step,
                                            float value, string format)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            parent.AddChild(row);

            var nameLbl = new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) };
            nameLbl.AddThemeFontSizeOverride("font_size", 13);
            nameLbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
            row.AddChild(nameLbl);

            var slider = new HSlider
            {
                MinValue              = min,
                MaxValue              = max,
                Step                  = step,
                Value                 = value,
                SizeFlagsHorizontal   = Control.SizeFlags.ExpandFill,
                CustomMinimumSize     = new Vector2(0, 24),
            };
            row.AddChild(slider);

            // Value label that updates live.
            var valLbl = new Label
            {
                Text              = string.Format(format, value),
                CustomMinimumSize = new Vector2(55, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            valLbl.AddThemeFontSizeOverride("font_size", 13);
            valLbl.AddThemeColorOverride("font_color", new Color(0.75f, 0.85f, 1.0f));
            row.AddChild(valLbl);

            string capturedFormat = format;
            slider.ValueChanged += v => valLbl.Text = string.Format(capturedFormat, (float)v);

            return slider;
        }

        /// <summary>Add a labeled CheckButton toggle row; returns the button for later reads.</summary>
        private static CheckButton AddToggleRow(Control parent, string label,
                                                 string tooltip, bool initialValue)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            parent.AddChild(row);

            var nameLbl = new Label
            {
                Text            = label,
                TooltipText     = tooltip,
                CustomMinimumSize = new Vector2(150, 0),
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 13);
            nameLbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
            row.AddChild(nameLbl);

            var toggle = new CheckButton
            {
                ButtonPressed   = initialValue,
                TooltipText     = tooltip,
            };
            row.AddChild(toggle);

            // Hint text.
            var hintLbl = new Label
            {
                Text              = tooltip,
                AutowrapMode      = TextServer.AutowrapMode.Word,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            hintLbl.AddThemeFontSizeOverride("font_size", 11);
            hintLbl.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.55f));
            row.AddChild(hintLbl);

            return toggle;
        }

        // ── Apply / Reset ─────────────────────────────────────────────────────

        private void ApplyAndSave()
        {
            var s = _settings.Current;

            s.CameraSpeed        = (float)_cameraSpeedSlider.Value;
            s.CameraZoomSpeed    = (float)_zoomSpeedSlider.Value;
            s.EdgeScrollEnabled  = _edgeScrollBtn.ButtonPressed;
            s.MasterVolume       = (float)_masterVolSlider.Value;
            s.SfxVolume          = (float)_sfxVolSlider.Value;
            s.MusicVolume        = (float)_musicVolSlider.Value;
            s.ShowMinimap        = _minimapBtn.ButtonPressed;
            s.ShowFps            = _fpsBtn.ButtonPressed;
            s.ColorblindMode     = _colorblindBtn.ButtonPressed;

            _settings.Apply();
            _settings.Save();

            GD.Print("[Settings] Applied and saved.");
        }

        private void ResetToDefaults()
        {
            _settings.Current = new Core.Definitions.SettingsData();
            // Re-sync all widgets to defaults.
            _cameraSpeedSlider.Value = _settings.Current.CameraSpeed;
            _zoomSpeedSlider.Value   = _settings.Current.CameraZoomSpeed;
            _edgeScrollBtn.ButtonPressed  = _settings.Current.EdgeScrollEnabled;
            _masterVolSlider.Value   = _settings.Current.MasterVolume;
            _sfxVolSlider.Value      = _settings.Current.SfxVolume;
            _musicVolSlider.Value    = _settings.Current.MusicVolume;
            _minimapBtn.ButtonPressed     = _settings.Current.ShowMinimap;
            _fpsBtn.ButtonPressed         = _settings.Current.ShowFps;
            _colorblindBtn.ButtonPressed  = _settings.Current.ColorblindMode;

            _settings.Apply();
            _settings.Save();
        }
    }
}
