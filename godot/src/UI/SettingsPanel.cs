#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using ProjectChimera.UI.Components; // ChimeraComponents, ChimeraTabs, ChimeraSlider, ChimeraSwitch, ChimeraTooltip
using ProjectChimera.UI.Theme;       // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;       // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// In-game settings panel (Story 3.11 restyle — UX-DR73) — shown/hidden with Escape or the HUD settings
    /// button, reachable from BOTH the Commander branch (Escape in Play) and the Creator branch (Escape in
    /// Edit / Title → Settings). Drawn from the shared design system: a chamfered kit panel, a
    /// <see cref="ChimeraTabs"/> header (Gameplay / Graphics / Audio / Controls / Accessibility) over a
    /// per-tab content host, themed sliders/switches, and hover-and-focus field tooltips.
    ///
    /// Persists all changes to user://settings.json exactly as before — <see cref="ApplyAndSave"/> /
    /// <see cref="ResetToDefaults"/> read and write the same <see cref="Core.Definitions.SettingsData"/>
    /// fields. Graphics and Controls are honestly empty (live video / rebinding land in Story 11.12), not
    /// padded with non-functional controls.
    ///
    /// Requires a <see cref="SettingsManager"/> node in the scene tree.
    /// Key: Escape toggles the panel (wired in MainScene).
    /// </summary>
    public partial class SettingsPanel : CanvasLayer
    {
        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired when the user clicks Close or presses Escape.</summary>
        public event Action? OnClosed;

        // ── State ─────────────────────────────────────────────────────────────

        private SettingsManager _settings = null!;

        // Kit context (self-owned; _accent only created when this overlay is the first kit consumer).
        private GodotTheme        _theme  = null!;
        private AccentController?  _accent;

        // Tab pages, toggled on TabChanged.
        private readonly List<Control> _pages = new();

        // Sliders / switches — kept as fields so Apply()/Reset() can read/write them.
        private ChimeraSlider _cameraSpeedSlider = null!;
        private ChimeraSlider _zoomSpeedSlider   = null!;
        private ChimeraSwitch _edgeScrollBtn     = null!;
        private ChimeraSlider _masterVolSlider   = null!;
        private ChimeraSlider _sfxVolSlider      = null!;
        private ChimeraSlider _musicVolSlider    = null!;
        private ChimeraSwitch _minimapBtn        = null!;
        private ChimeraSwitch _fpsBtn            = null!;
        private ChimeraSwitch _colorblindBtn     = null!;

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>Build the settings UI and sync all widgets to current settings.</summary>
        public void Initialize(SettingsManager settings)
        {
            _settings = settings;
            Layer     = 15; // above content browser (10)
            Visible   = false;

            EnsureKitInitialized(); // MUST run before any ChimeraComponents.* call, or the factory throws

            // ── Anchor root (full-rect Control; the primary input blocker; carries the Theme) ──
            var anchorRoot = new Control();
            anchorRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.MouseFilter = Control.MouseFilterEnum.Stop;
            anchorRoot.Theme = _theme; // a CanvasLayer has no Theme — apply on its root Control, which propagates
            AddChild(anchorRoot);

            // ── Scrim (void surface token, dimmed) so the scene behind reads as inactive ──
            Color voidC = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type);
            var scrim = new ColorRect { Color = new Color(voidC.R, voidC.G, voidC.B, 0.82f) };
            scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            scrim.MouseFilter = Control.MouseFilterEnum.Ignore;
            anchorRoot.AddChild(scrim);

            // ── Centre card ───────────────────────────────────────────────────
            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.AddChild(center);

            var card = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            card.CustomMinimumSize = new Vector2(560, 0);
            center.AddChild(card);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            card.AddChild(vbox);

            // ── Title + close ─────────────────────────────────────────────────
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            vbox.AddChild(titleRow);

            var title = Heading("Settings", ThemeTokens.Txl);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(title);

            var closeBtn = ChimeraComponents.Button("Close  [Esc]", ChimeraComponents.ButtonVariant.Ghost,
                                                    ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += Close;
            ChimeraTooltip.Attach(closeBtn, "Close", "Close settings and return (Escape).", ChimeraTooltip.TooltipRole.Field);
            titleRow.AddChild(closeBtn);

            // ── Tab header (UX-DR73) ──────────────────────────────────────────
            var tabs = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Underline,
                "Gameplay", "Graphics", "Audio", "Controls", "Accessibility");
            vbox.AddChild(tabs);

            // ── Content host (pages swap on TabChanged) ───────────────────────
            // A Container (not a bare Control) so the visible page's content height drives the card:
            // the min height keeps the card stable across tabs, and a taller page (e.g. a longer
            // localized string or larger font) grows the card instead of clipping into the footer below.
            var host = new VBoxContainer { CustomMinimumSize = new Vector2(0, 300) };
            host.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            vbox.AddChild(host);

            _pages.Clear();
            host.AddChild(BuildGameplayPage());
            host.AddChild(BuildGraphicsPage());
            host.AddChild(BuildAudioPage());
            host.AddChild(BuildControlsPage());
            host.AddChild(BuildAccessibilityPage());
            ShowPage(0);

            tabs.TabChanged += OnTabChanged;

            // ── Apply / Reset footer ──────────────────────────────────────────
            var btnRow = new HBoxContainer();
            btnRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            vbox.AddChild(btnRow);

            btnRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

            var resetBtn = ChimeraComponents.Button("Reset to Defaults", ChimeraComponents.ButtonVariant.Secondary);
            resetBtn.Pressed += ResetToDefaults;
            ChimeraTooltip.Attach(resetBtn, "Reset to Defaults",
                "Restore every setting to its default value and save.", ChimeraTooltip.TooltipRole.Field);
            btnRow.AddChild(resetBtn);

            var applyBtn = ChimeraComponents.Button("Apply & Save", ChimeraComponents.ButtonVariant.Primary);
            applyBtn.Pressed += ApplyAndSave;
            ChimeraTooltip.Attach(applyBtn, "Apply & Save",
                "Apply changes live and persist them to disk.", ChimeraTooltip.TooltipRole.Field);
            btnRow.AddChild(applyBtn);
        }

        // ── Kit bootstrap (mirrors HeroPickerOverlay.EnsureKitInitialized) ─────────────────

        private void EnsureKitInitialized()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── Tab pages ─────────────────────────────────────────────────────────

        private Control BuildGameplayPage()
        {
            var v = NewPage();

            AddSectionHeader(v, "Camera");
            _cameraSpeedSlider = AddSliderRow(v, "Pan speed",
                "How fast the camera pans across the map.",
                min: 0.25f, max: 3.0f, step: 0.05f, value: _settings.Current.CameraSpeed);
            _zoomSpeedSlider = AddSliderRow(v, "Zoom speed",
                "How fast the camera zooms in and out.",
                min: 0.25f, max: 3.0f, step: 0.05f, value: _settings.Current.CameraZoomSpeed);
            _edgeScrollBtn = AddToggleRow(v, "Edge scroll",
                "Scroll the camera when the cursor reaches the screen edge.",
                _settings.Current.EdgeScrollEnabled);

            AddSectionHeader(v, "HUD");
            _minimapBtn = AddToggleRow(v, "Show minimap",
                "Show the overhead minimap in the lower-right corner.",
                _settings.Current.ShowMinimap);
            _fpsBtn = AddToggleRow(v, "Show FPS",
                "Display the current frame rate in the HUD.",
                _settings.Current.ShowFps);

            return v;
        }

        private Control BuildGraphicsPage()
        {
            var v = NewPage();
            AddEmptyState(v,
                "No graphics settings yet. Live video options (resolution, quality, vsync) arrive in a later update.");
            return v;
        }

        private Control BuildAudioPage()
        {
            var v = NewPage();
            _masterVolSlider = AddSliderRow(v, "Master volume",
                "Overall output level for all audio.",
                min: 0f, max: 1f, step: 0.01f, value: _settings.Current.MasterVolume);
            _sfxVolSlider = AddSliderRow(v, "SFX volume",
                "Level for gameplay sound effects.",
                min: 0f, max: 1f, step: 0.01f, value: _settings.Current.SfxVolume);
            _musicVolSlider = AddSliderRow(v, "Music volume",
                "Level for background music.",
                min: 0f, max: 1f, step: 0.01f, value: _settings.Current.MusicVolume);
            return v;
        }

        private Control BuildControlsPage()
        {
            var v = NewPage();
            AddEmptyState(v,
                "No rebindable controls yet. Key binding arrives in a later update.");
            return v;
        }

        private Control BuildAccessibilityPage()
        {
            var v = NewPage();
            _colorblindBtn = AddToggleRow(v, "Colorblind-friendly colors",
                "Changes Player 2 units from red to orange so they read in red-green color blindness.",
                _settings.Current.ColorblindMode);
            return v;
        }

        private VBoxContainer NewPage()
        {
            // Laid out by the host Container (not FullRect-anchored) so the page's content height drives
            // the host/card — a page taller than the host min grows the card rather than overflowing it.
            var v = new VBoxContainer();
            v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            v.SizeFlagsVertical   = Control.SizeFlags.ExpandFill;
            v.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _pages.Add(v);
            return v;
        }

        private void OnTabChanged(int index) => ShowPage(index);

        private void ShowPage(int index)
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visible = i == index;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void ToggleVisible() => Visible = !Visible;

        public void Close()
        {
            Visible = false;
            OnClosed?.Invoke();
        }

        // ── Keyboard ──────────────────────────────────────────────────────────

        // Use _Input (not _UnhandledInput) so the Escape keystroke is consumed before MainScene's
        // _UnhandledInput can re-open menus behind the panel.
        public override void _Input(InputEvent ev)
        {
            if (!Visible) return;
            if (ev is InputEventKey { Pressed: true, KeyLabel: Key.Escape })
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Row builders ──────────────────────────────────────────────────────

        private void AddSectionHeader(Control parent, string text)
        {
            var lbl = ChimeraComponents.FieldLabel(text); // uppercase display, text-lo (matches the kit)
            parent.AddChild(lbl);
        }

        /// <summary>Add a labeled themed slider row; returns the slider for later reads.</summary>
        private ChimeraSlider AddSliderRow(Control parent, string label, string tip,
                                           float min, float max, float step, float value)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            parent.AddChild(row);

            var nameLbl = Body(label, ThemeTokens.TextMid, ThemeTokens.Tsm);
            nameLbl.CustomMinimumSize = new Vector2(150, 0);
            AttachFieldTip(nameLbl, label, tip);
            row.AddChild(nameLbl);

            var slider = ChimeraSlider.Create(value, min, max, step);
            slider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            slider.SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter;
            row.AddChild(slider);

            return slider;
        }

        /// <summary>Add a labeled themed switch row; returns the switch for later reads.</summary>
        private ChimeraSwitch AddToggleRow(Control parent, string label, string tip, bool initialValue)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            parent.AddChild(row);

            var nameLbl = Body(label, ThemeTokens.TextMid, ThemeTokens.Tsm);
            nameLbl.CustomMinimumSize = new Vector2(150, 0);
            nameLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            AttachFieldTip(nameLbl, label, tip);
            row.AddChild(nameLbl);

            var toggle = ChimeraSwitch.Create(initialValue);
            toggle.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            ChimeraTooltip.Attach(toggle, label, tip, ChimeraTooltip.TooltipRole.Field); // switch is a focusable Button
            row.AddChild(toggle);

            return toggle;
        }

        private void AddEmptyState(Control parent, string text)
        {
            var lbl = Body(text, ThemeTokens.TextLo, ThemeTokens.Tmd);
            lbl.AutowrapMode = TextServer.AutowrapMode.Word;
            lbl.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            lbl.VerticalAlignment = VerticalAlignment.Center;
            parent.AddChild(lbl);
        }

        // ── Small shared builders ───────────────────────────────────────────────

        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            return l;
        }

        private Label Body(string text, StringName colorToken, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontUi, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(colorToken, ThemeTokens.Type));
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return l;
        }

        // A hover-AND-keyboard-focus field tip attached to a row's label (UX-DR53 / NFR-2). The label is made
        // a focus + hover target so the keyboard half fires; the interactive control beside it stays operable.
        private void AttachFieldTip(Control target, string term, string body)
        {
            target.FocusMode  = Control.FocusModeEnum.All;
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            ChimeraTooltip.Attach(target, term, body, ChimeraTooltip.TooltipRole.Field);
        }

        // ── Apply / Reset ─────────────────────────────────────────────────────

        private void ApplyAndSave()
        {
            var s = _settings.Current;

            s.CameraSpeed        = (float)_cameraSpeedSlider.Value;
            s.CameraZoomSpeed    = (float)_zoomSpeedSlider.Value;
            s.EdgeScrollEnabled  = _edgeScrollBtn.On;
            s.MasterVolume       = (float)_masterVolSlider.Value;
            s.SfxVolume          = (float)_sfxVolSlider.Value;
            s.MusicVolume        = (float)_musicVolSlider.Value;
            s.ShowMinimap        = _minimapBtn.On;
            s.ShowFps            = _fpsBtn.On;
            s.ColorblindMode     = _colorblindBtn.On;

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
            _edgeScrollBtn.SetOn(_settings.Current.EdgeScrollEnabled, animate: false);
            _masterVolSlider.Value   = _settings.Current.MasterVolume;
            _sfxVolSlider.Value      = _settings.Current.SfxVolume;
            _musicVolSlider.Value    = _settings.Current.MusicVolume;
            _minimapBtn.SetOn(_settings.Current.ShowMinimap, animate: false);
            _fpsBtn.SetOn(_settings.Current.ShowFps, animate: false);
            _colorblindBtn.SetOn(_settings.Current.ColorblindMode, animate: false);

            _settings.Apply();
            _settings.Save();
        }
    }
}
