#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;   // Story 8.2 — AiAvailability(Evaluator/Messages), four-state UI
using ProjectChimera.Core.Definitions; // Story 8.2 — ISecretStore, SecretIds, LlmProviderCatalog
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
    /// fields. Story 11.7 fills the Graphics tab with live video settings (window mode / resolution / vsync /
    /// quality preset / UI scale); Controls stays honestly empty (key rebinding lands in a later story), not
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

        /// <summary>Story 5.9 (NFR-2): fired when the user clicks "Replay onboarding" on the Gameplay tab.
        /// <c>OnboardingPhase</c> subscribes to reset <see cref="Core.Definitions.SettingsData.HasSeenOnboarding"/>
        /// and re-open <see cref="OnboardingPanel"/> — kept as an event (not a direct call) so this panel never
        /// needs a reference to the onboarding panel it's built well before (Onboarding is the LAST phase).</summary>
        public event Action? OnReplayOnboardingRequested;

        // ── State ─────────────────────────────────────────────────────────────

        private SettingsManager _settings = null!;

        // Kit context.
        private GodotTheme        _theme  = null!;

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

        // ── Story 11.7 — Graphics tab (live video settings) ────────────────────
        private OptionButton  _windowModeSelect  = null!;
        private OptionButton  _resolutionSelect  = null!;
        private OptionButton  _qualitySelect     = null!;
        private ChimeraSwitch _vsyncBtn          = null!;
        private ChimeraSlider _uiScaleSlider     = null!;
        // The resolution options offered in the dropdown (curated 16:9 set filtered to ≤ the screen, plus the
        // persisted value), index-aligned with the OptionButton items.
        private readonly List<Vector2I> _resolutionOptions = new();

        // Window-mode / quality-preset id↔label tables (item index == array index; the id is the persisted enum).
        private static readonly (string Id, string Label)[] WindowModes =
        {
            ("windowed",   "Windowed"),
            ("borderless", "Borderless (Windowed Fullscreen)"),
            ("fullscreen", "Fullscreen (Exclusive)"),
        };
        private static readonly (string Id, string Label)[] QualityPresets =
        {
            ("low",    "Low"),
            ("medium", "Medium"),
            ("high",   "High"),
        };
        // Curated 16:9 resolutions, filtered to ≤ the primary screen at build time.
        private static readonly Vector2I[] ResolutionCandidates =
        {
            new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160),
        };

        // Safe-revert (display-mode / resolution change) — a 15 s auto-revert confirm.
        private ChimeraDialog? _safeRevertDialog;
        private Godot.Timer?   _safeRevertTimer;
        private Godot.Timer?   _safeRevertCountdownTimer;
        private Label?         _safeRevertLabel;
        private int            _safeRevertRemaining;
        private string         _safeRevertPrevMode = "windowed";
        private int            _safeRevertPrevW;
        private int            _safeRevertPrevH;

        // ── Story 8.2 — AI Provider config section ─────────────────────────────

        private AiAvailabilityEvaluator? _aiEvaluator;
        private ISecretStore?            _secretStore;
        private OptionButton _providerSelect  = null!;
        private OptionButton _modelSelect     = null!;
        private LineEdit     _modelOverride   = null!;
        private LineEdit     _baseUrlInput    = null!;
        private LineEdit     _apiKeyInput     = null!;
        private Button       _clearKeyBtn     = null!;
        private Button       _testBtn         = null!;
        private ChimeraSpinner _testSpinner   = null!;
        private Label        _aiStatusLabel   = null!;
        private CancellationTokenSource? _testCts;

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>Build the settings UI and sync all widgets to current settings. Story 8.2: the evaluator +
        /// secret store back the AI Provider section (Test-connection + four-state UI + key entry).</summary>
        public void Initialize(SettingsManager settings, AiAvailabilityEvaluator aiEvaluator, ISecretStore secretStore)
        {
            _settings    = settings;
            _aiEvaluator = aiEvaluator;
            _secretStore = secretStore;
            Layer     = 15; // above content browser (10)
            Visible   = false;

            _theme = ChimeraComponents.EnsureInitialized(this); // MUST run before any ChimeraComponents.* call, or the factory throws

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

            var title = ChimeraComponents.Heading("Settings", ThemeTokens.Txl);
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
                "Gameplay", "Graphics", "Audio", "Controls", "Accessibility", "AI Provider");
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
            host.AddChild(BuildAiProviderPage());
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

            AddSectionHeader(v, "Onboarding");
            var replayBtn = ChimeraComponents.Button("Replay \"Your First Scenario\" Onboarding",
                ChimeraComponents.ButtonVariant.Secondary);
            replayBtn.Pressed += () => OnReplayOnboardingRequested?.Invoke();
            ChimeraTooltip.Attach(replayBtn, "Replay onboarding",
                "Reset and reopen the guided \"Your First Scenario\" walkthrough from step 1.",
                ChimeraTooltip.TooltipRole.Field);
            v.AddChild(replayBtn);

            return v;
        }

        private Control BuildGraphicsPage()
        {
            var v = NewPage();
            var s = _settings.Current;

            AddSectionHeader(v, "Display");

            // Window mode (Windowed / Borderless / Fullscreen).
            v.AddChild(ChimeraComponents.FieldLabel("Window mode"));
            _windowModeSelect = ChimeraComponents.Select();
            for (int i = 0; i < WindowModes.Length; i++) _windowModeSelect.AddItem(WindowModes[i].Label, i);
            v.AddChild(_windowModeSelect);
            SyncWindowModeSelect();

            // Resolution (curated 16:9 set filtered to ≤ the screen; the persisted value is always selectable).
            v.AddChild(ChimeraComponents.FieldLabel("Resolution"));
            _resolutionSelect = ChimeraComponents.Select();
            v.AddChild(_resolutionSelect);
            BuildResolutionOptions();
            SyncResolutionSelect();

            AddSectionHeader(v, "Quality");

            // Quality preset (Low / Medium / High → shadows + shadow-atlas + MSAA).
            v.AddChild(ChimeraComponents.FieldLabel("Quality preset"));
            _qualitySelect = ChimeraComponents.Select();
            for (int i = 0; i < QualityPresets.Length; i++) _qualitySelect.AddItem(QualityPresets[i].Label, i);
            v.AddChild(_qualitySelect);
            SyncQualitySelect();

            _vsyncBtn = AddToggleRow(v, "VSync",
                "Cap the frame rate to your monitor's refresh rate to remove screen tearing.",
                s.Vsync);
            _uiScaleSlider = AddSliderRow(v, "UI scale",
                "Scale the HUD and menus for readability (0.75× to 1.5×).",
                min: 0.75f, max: 1.5f, step: 0.25f, value: s.UiScale);

            return v;
        }

        /// <summary>Rebuild the resolution dropdown: the curated 16:9 candidates filtered to ≤ the primary screen,
        /// plus the currently-persisted resolution so it is always selectable, sorted ascending.</summary>
        private void BuildResolutionOptions()
        {
            _resolutionOptions.Clear();
            _resolutionSelect.Clear();

            Vector2I screen;
            try { screen = DisplayServer.ScreenGetSize(); }
            catch { screen = new Vector2I(int.MaxValue, int.MaxValue); }

            foreach (var r in ResolutionCandidates)
                if (r.X <= screen.X && r.Y <= screen.Y) _resolutionOptions.Add(r);

            var cur = new Vector2I(_settings.Current.ResolutionWidth, _settings.Current.ResolutionHeight);
            if (cur.X > 0 && cur.Y > 0 && !_resolutionOptions.Contains(cur)) _resolutionOptions.Add(cur);
            if (_resolutionOptions.Count == 0) _resolutionOptions.Add(new Vector2I(1920, 1080));

            _resolutionOptions.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            for (int i = 0; i < _resolutionOptions.Count; i++)
                _resolutionSelect.AddItem($"{_resolutionOptions[i].X} × {_resolutionOptions[i].Y}", i);
        }

        private void SyncWindowModeSelect()
        {
            int idx = 0;
            for (int i = 0; i < WindowModes.Length; i++)
                if (string.Equals(WindowModes[i].Id, _settings.Current.WindowMode, StringComparison.Ordinal)) idx = i;
            _windowModeSelect.Select(idx);
        }

        private void SyncQualitySelect()
        {
            int idx = 1; // medium fallback
            for (int i = 0; i < QualityPresets.Length; i++)
                if (string.Equals(QualityPresets[i].Id, _settings.Current.QualityPreset, StringComparison.Ordinal)) idx = i;
            _qualitySelect.Select(idx);
        }

        private void SyncResolutionSelect()
        {
            var cur = new Vector2I(_settings.Current.ResolutionWidth, _settings.Current.ResolutionHeight);
            int idx = _resolutionOptions.IndexOf(cur);
            _resolutionSelect.Select(idx < 0 ? 0 : idx);
        }

        private string SelectedWindowModeId()
        {
            int i = _windowModeSelect.Selected;
            return (i >= 0 && i < WindowModes.Length) ? WindowModes[i].Id : "windowed";
        }

        private string SelectedQualityId()
        {
            int i = _qualitySelect.Selected;
            return (i >= 0 && i < QualityPresets.Length) ? QualityPresets[i].Id : "medium";
        }

        private Vector2I SelectedResolution()
        {
            int i = _resolutionSelect.Selected;
            return (i >= 0 && i < _resolutionOptions.Count) ? _resolutionOptions[i] : new Vector2I(1920, 1080);
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

        // ── Story 8.2 — AI Provider page ───────────────────────────────────────

        private Control BuildAiProviderPage()
        {
            var v = NewPage();
            var s = _settings.Current;

            AddSectionHeader(v, "AI Provider");

            // Provider picker (curated catalog, in stable order).
            v.AddChild(ChimeraComponents.FieldLabel("Provider"));
            _providerSelect = ChimeraComponents.Select();
            int selectedProvider = 0;
            for (int i = 0; i < LlmProviderCatalog.Providers.Count; i++)
            {
                var p = LlmProviderCatalog.Providers[i];
                _providerSelect.AddItem(p.DisplayName, i);
                if (string.Equals(p.Id, s.LlmProvider, StringComparison.Ordinal)) selectedProvider = i;
            }
            _providerSelect.Select(selectedProvider);
            // DW-368: keys are stored per provider, so switching provider also refreshes the key field — the
            // placeholder must reflect whether THAT provider has a stored key, and a typed-but-unsaved key belonged
            // to the previously-shown provider and must not be carried over to be saved under the new one.
            _providerSelect.ItemSelected += _ => { RefreshModelList(); RefreshApiKeyField(); UpdateAiStatusLine(); };
            v.AddChild(_providerSelect);

            // Model picker (curated) + free-text override.
            v.AddChild(ChimeraComponents.FieldLabel("Model (curated)"));
            _modelSelect = ChimeraComponents.Select();
            v.AddChild(_modelSelect);

            v.AddChild(ChimeraComponents.FieldLabel("Model override (optional — free text)"));
            _modelOverride = ChimeraComponents.Input("e.g. claude-sonnet-5 or a custom tag");
            v.AddChild(_modelOverride);

            // Base-URL override.
            v.AddChild(ChimeraComponents.FieldLabel("Base URL override (optional)"));
            _baseUrlInput = ChimeraComponents.Input("leave blank to use the provider default");
            _baseUrlInput.Text = s.LlmBaseUrl;
            v.AddChild(_baseUrlInput);

            // API key (masked). Never persisted to settings.json — only ISecretStore.
            v.AddChild(ChimeraComponents.FieldLabel("API key (stored securely, never in settings.json)"));
            var keyRow = new HBoxContainer();
            keyRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _apiKeyInput = ChimeraComponents.Input(
                (_secretStore?.Has(SelectedProviderSecretId()) ?? false) ? "•••••••• (key stored — type to replace)" : "paste your API key");
            _apiKeyInput.Secret = true;
            _apiKeyInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            keyRow.AddChild(_apiKeyInput);
            _clearKeyBtn = ChimeraComponents.Button("Clear key", ChimeraComponents.ButtonVariant.Ghost,
                                                    ChimeraComponents.ButtonSize.Sm);
            _clearKeyBtn.Pressed += OnClearKeyPressed;
            keyRow.AddChild(_clearKeyBtn);
            v.AddChild(keyRow);

            // Test connection row: button + spinner + status.
            var testRow = new HBoxContainer();
            testRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _testBtn = ChimeraComponents.Button("Test connection", ChimeraComponents.ButtonVariant.Secondary);
            _testBtn.Pressed += OnTestConnectionPressed;
            testRow.AddChild(_testBtn);
            _testSpinner = ChimeraSpinner.Create(20);
            _testSpinner.Visible = false;
            testRow.AddChild(_testSpinner);
            v.AddChild(testRow);

            _aiStatusLabel = ChimeraComponents.Body("", ThemeTokens.TextMid, ThemeTokens.Tsm);
            _aiStatusLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _aiStatusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _aiStatusLabel.CustomMinimumSize = new Vector2(0, 40);
            v.AddChild(_aiStatusLabel);

            RefreshModelList();
            UpdateAiStatusLine();
            return v;
        }

        /// <summary>Repopulate the curated model dropdown for the selected provider and pre-select the persisted
        /// model when it is one of the curated picks (else it stays in the free-text override field).</summary>
        private void RefreshModelList()
        {
            _modelSelect.Clear();
            int providerIdx = Math.Max(0, _providerSelect.Selected);
            if (providerIdx >= LlmProviderCatalog.Providers.Count) return;

            var p = LlmProviderCatalog.Providers[providerIdx];
            string current = _settings.Current.LlmModel;
            int sel = -1;
            for (int i = 0; i < p.Models.Count; i++)
            {
                _modelSelect.AddItem(p.Models[i], i);
                if (string.Equals(p.Models[i], current, StringComparison.Ordinal)) sel = i;
            }
            if (sel >= 0)
            {
                _modelSelect.Select(sel);
                _modelOverride.Text = ""; // the persisted model is a curated pick — override stays empty
            }
            else if (!string.IsNullOrWhiteSpace(current))
            {
                // Persisted model is a free-text override — surface it in the override field.
                _modelOverride.Text = current;
                if (_modelSelect.ItemCount > 0) _modelSelect.Select(0);
            }
            else if (_modelSelect.ItemCount > 0)
            {
                _modelSelect.Select(0);
            }
        }

        /// <summary>Synchronous, config-derived availability line (NoProvider / NoKey / candidate) shown on open and
        /// whenever the provider changes — no network call.</summary>
        private void UpdateAiStatusLine()
        {
            if (_aiEvaluator == null || _secretStore == null || _aiStatusLabel == null) return;
            var snapshot = BuildAiSettingsSnapshot();
            AiAvailability state = _aiEvaluator.EvaluateConfig(snapshot, _secretStore);
            _aiStatusLabel.Text = state == AiAvailability.Healthy
                ? "Config looks good, Commander. Run Test connection to confirm the round-trip."
                : AiAvailabilityMessages.Describe(state);
        }

        /// <summary>A SettingsData snapshot reflecting the CURRENT (unsaved) AI-section widget values, so
        /// Test-connection and the status line reflect what the creator is editing before they press Apply.
        /// Normalized via <see cref="SettingsData.MigrateForward"/> — the SAME normalization <see cref="ApplyAndSave"/>
        /// runs before persisting — so the config Test-connection / the status line validate can never disagree with the
        /// config that Apply will save (e.g. an empty model resolves to the default in BOTH paths, so the status line
        /// never reports a config-only "ready" for a config Apply would rewrite).</summary>
        private SettingsData BuildAiSettingsSnapshot()
        {
            var live = _settings.Current;
            int providerIdx = Math.Max(0, _providerSelect.Selected);
            string providerId = providerIdx < LlmProviderCatalog.Providers.Count
                ? LlmProviderCatalog.Providers[providerIdx].Id
                : live.LlmProvider;
            return new SettingsData
            {
                LlmProvider = providerId,
                LlmModel    = ResolveSelectedModel(),
                LlmBaseUrl  = _baseUrlInput.Text.Trim(),
            }.MigrateForward();
        }

        /// <summary>The effective model: the free-text override when present, else the selected curated model.</summary>
        private string ResolveSelectedModel()
        {
            string overrideText = _modelOverride.Text.Trim();
            if (overrideText.Length > 0) return overrideText;
            if (_modelSelect.Selected >= 0 && _modelSelect.ItemCount > 0)
                return _modelSelect.GetItemText(_modelSelect.Selected);
            return _settings.Current.LlmModel;
        }

        /// <summary>DW-368: the PER-PROVIDER secret id for the provider currently shown in the picker (falling back
        /// to the persisted provider when the picker isn't built/selected yet). Every read/write of the API key in
        /// this panel routes through this id — never the legacy shared <c>llm</c> id — so a key entered while one
        /// provider is selected can never be stored for, or sent to, another provider.</summary>
        private string SelectedProviderSecretId()
        {
            string providerId = _settings.Current.LlmProvider;
            if (_providerSelect != null)
            {
                int idx = Math.Max(0, _providerSelect.Selected);
                if (idx < LlmProviderCatalog.Providers.Count)
                    providerId = LlmProviderCatalog.Providers[idx].Id;
            }
            return SecretIds.ForLlmProvider(providerId);
        }

        /// <summary>DW-368: re-sync the key field with the provider now selected — blank any typed-but-unsaved key
        /// (it belonged to the previously-shown provider) and show whether THIS provider has a stored key.</summary>
        private void RefreshApiKeyField()
        {
            if (_apiKeyInput == null) return;
            _apiKeyInput.Text = "";
            _apiKeyInput.PlaceholderText = (_secretStore?.Has(SelectedProviderSecretId()) ?? false)
                ? "•••••••• (key stored — type to replace)"
                : "paste your API key";
        }

        private void OnClearKeyPressed()
        {
            string secretId = SelectedProviderSecretId();
            _secretStore?.Clear(secretId);
            _apiKeyInput.Text = "";
            _apiKeyInput.PlaceholderText = "paste your API key";
            UpdateAiStatusLine();
            GD.Print($"[Settings] Cleared stored LLM key '{secretId}'.");
        }

        private void OnTestConnectionPressed()
        {
            if (_aiEvaluator == null || _secretStore == null) return;

            // The round-trip needs the typed key in the store (LlmProviderFactory reads the key from ISecretStore),
            // but a FAILING test must not destroy a previously-valid stored key. Snapshot the prior value and place the
            // typed key for the probe; CompleteTest keeps it only on Healthy, else restores the prior key and leaves
            // the field populated so the creator can fix a typo. Both the Test AND the Clear-key buttons are disabled
            // below for the duration, so no concurrent secret-store mutation (a Clear on the main thread while the
            // background probe reads/restores the key) can race or undo the restore.
            // DW-368: the key lives under the PER-PROVIDER id of the provider being probed. The id is captured ONCE
            // here and passed through to CompleteTest, so the async restore targets the same id even if the creator
            // switches provider while the probe is in flight (the provider picker is not disabled during the test).
            string secretId   = SelectedProviderSecretId();
            string typedKey   = _apiKeyInput.Text.Trim();
            bool   keyEntered = typedKey.Length > 0;
            string priorKey   = _secretStore.Get(secretId);
            if (keyEntered) _secretStore.Set(secretId, typedKey);

            SettingsData snapshot = BuildAiSettingsSnapshot();
            _testBtn.Disabled     = true;
            _clearKeyBtn.Disabled = true;
            _testSpinner.Visible = true;
            _aiStatusLabel.Text  = "Transmuting…"; // UX-DR52 spinner copy

            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            CancellationToken ct = _testCts.Token;
            AiAvailabilityEvaluator evaluator = _aiEvaluator;
            ISecretStore store = _secretStore;

            Task.Run(async () =>
            {
                AiAvailability state;
                try { state = await evaluator.TestConnectionAsync(snapshot, store, ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { state = AiAvailability.Unreachable; }
                // Marshal the UI update back onto the Godot main thread.
                Callable.From(() => CompleteTest(state, secretId, keyEntered, typedKey, priorKey)).CallDeferred();
            });
        }

        private void CompleteTest(AiAvailability state, string secretId, bool keyEntered, string typedKey, string priorKey)
        {
            // The secret-store restore touches no UI, so it must run even if the panel was freed mid-test: a FAILED
            // test must never leave the unverified probe key persisted in place of a previously-valid one, regardless
            // of whether the panel is still around to update its field. DW-368: the restore targets the SAME
            // per-provider id the probe wrote (captured at press time), never a re-resolved one.
            bool verified = keyEntered && state == AiAvailability.Healthy;
            if (keyEntered && !verified)
            {
                if (priorKey.Length > 0) _secretStore?.Set(secretId, priorKey);
                else                     _secretStore?.Clear(secretId);
            }

            if (!IsInstanceValid(this)) return;

            if (verified)
            {
                // Verified — keep the typed key (already stored); blank the field and show the stored placeholder.
                _apiKeyInput.Text = "";
                _apiKeyInput.PlaceholderText = "•••••••• (key stored — type to replace)";
            }
            else if (keyEntered)
            {
                // Test failed: the prior key was restored above; keep the typed value visible so the creator can fix it.
                _apiKeyInput.Text = typedKey;
            }

            _testSpinner.Visible = false;
            _testBtn.Disabled     = false;
            _clearKeyBtn.Disabled = false;
            _aiStatusLabel.Text  = AiAvailabilityMessages.Describe(state);
        }

        /// <summary>Write the API key to the secret store IFF the creator typed one (a blank field keeps the stored
        /// key untouched). Never persists the key to settings.json. DW-368: the key is stored under the id of the
        /// provider currently selected in the picker — the provider it was typed for.</summary>
        private void PersistApiKeyIfEntered()
        {
            if (_secretStore == null) return;
            string typed = _apiKeyInput.Text.Trim();
            if (typed.Length == 0) return;
            _secretStore.Set(SelectedProviderSecretId(), typed);
            _apiKeyInput.Text = "";
            _apiKeyInput.PlaceholderText = "•••••••• (key stored — type to replace)";
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

            var nameLbl = ChimeraComponents.Body(label, ThemeTokens.TextMid, ThemeTokens.Tsm);
            nameLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
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

            var nameLbl = ChimeraComponents.Body(label, ThemeTokens.TextMid, ThemeTokens.Tsm);
            nameLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
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
            var lbl = ChimeraComponents.Body(text, ThemeTokens.TextLo, ThemeTokens.Tmd);
            lbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            lbl.AutowrapMode = TextServer.AutowrapMode.Word;
            lbl.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            lbl.VerticalAlignment = VerticalAlignment.Center;
            parent.AddChild(lbl);
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

            // Story 11.7: snapshot the previously-persisted display mode + resolution BEFORE overwriting, so
            // MaybeArmSafeRevert can tell whether either changed (and, on timeout, what to revert to).
            string prevWindowMode = s.WindowMode;
            int    prevResW       = s.ResolutionWidth;
            int    prevResH       = s.ResolutionHeight;

            s.CameraSpeed        = (float)_cameraSpeedSlider.Value;
            s.CameraZoomSpeed    = (float)_zoomSpeedSlider.Value;
            s.EdgeScrollEnabled  = _edgeScrollBtn.On;
            s.MasterVolume       = (float)_masterVolSlider.Value;
            s.SfxVolume          = (float)_sfxVolSlider.Value;
            s.MusicVolume        = (float)_musicVolSlider.Value;
            s.ShowMinimap        = _minimapBtn.On;
            s.ShowFps            = _fpsBtn.On;
            s.ColorblindMode     = _colorblindBtn.On;

            // Story 11.7: the five live video settings.
            if (_windowModeSelect != null)
            {
                s.WindowMode       = SelectedWindowModeId();
                var res            = SelectedResolution();
                s.ResolutionWidth  = res.X;
                s.ResolutionHeight = res.Y;
                s.QualityPreset    = SelectedQualityId();
                s.Vsync            = _vsyncBtn.On;
                s.UiScale          = (float)_uiScaleSlider.Value;
            }

            // Story 8.2: persist the AI provider/model/baseUrl into SettingsData (round-trips through settings.json);
            // the API key goes ONLY to the secret store, never into settings.json.
            if (_providerSelect != null)
            {
                int providerIdx = Math.Max(0, _providerSelect.Selected);
                if (providerIdx < LlmProviderCatalog.Providers.Count)
                    s.LlmProvider = LlmProviderCatalog.Providers[providerIdx].Id;
                s.LlmModel   = ResolveSelectedModel();
                s.LlmBaseUrl = _baseUrlInput.Text.Trim();
                PersistApiKeyIfEntered();
                s.MigrateForward(); // normalize (empty model → default, etc.) before persist
                UpdateAiStatusLine();
            }

            _settings.Apply();
            _settings.Save();

            // Story 11.7: a display-mode or resolution change arms a 15 s safe-revert (an unusable mode can't lock
            // the player out). VSync / quality / UI-scale changes do NOT — each is trivially reversible in place.
            MaybeArmSafeRevert(prevWindowMode, prevResW, prevResH);

            GD.Print("[Settings] Applied and saved.");
        }

        // ── Story 11.7 — safe-revert (display-mode / resolution) ───────────────

        /// <summary>If Apply changed the window mode or resolution from the prior persisted values, open a modal with
        /// a live countdown that auto-reverts those two settings (re-syncing the dropdowns and re-persisting) after
        /// 15 s unless the player confirms Keep. A no-op when neither changed.</summary>
        private void MaybeArmSafeRevert(string prevMode, int prevW, int prevH)
        {
            var s = _settings.Current;
            // Story 11.7 review-2: arm only on a change ApplyVideo actually applies to the display. A window-mode
            // change always applies; a resolution change is applied (WindowSetSize) ONLY in windowed mode, so a
            // resolution pick in borderless/fullscreen is inert and must not pop a spurious auto-revert countdown.
            bool modeChanged = !string.Equals(s.WindowMode, prevMode, StringComparison.Ordinal);
            bool resChanged  = (s.ResolutionWidth != prevW || s.ResolutionHeight != prevH)
                               && s.WindowMode == "windowed";
            if (!(modeChanged || resChanged)) return;

            DisarmSafeRevert(); // supersede any prior armed revert (its window state is already stale)

            _safeRevertPrevMode = prevMode;
            _safeRevertPrevW    = prevW;
            _safeRevertPrevH    = prevH;
            _safeRevertRemaining = 15;

            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            var msg = ChimeraComponents.Body("Keep these display settings? They will revert automatically if you don't confirm — in case the new mode is unusable.",
                ThemeTokens.TextMid, ThemeTokens.Tmd);
            msg.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            msg.AutowrapMode = TextServer.AutowrapMode.Word;
            msg.CustomMinimumSize = new Vector2(360, 0);
            body.AddChild(msg);
            _safeRevertLabel = ChimeraComponents.Body($"Reverting in {_safeRevertRemaining}s…", ThemeTokens.TextHi, ThemeTokens.Tlg);
            _safeRevertLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            body.AddChild(_safeRevertLabel);

            var dlg = ChimeraDialog.CreateCustom("Keep display settings?", body);
            dlg.AddConfirm("Keep");
            dlg.Confirmed += OnSafeRevertKeep;   // explicit Keep → new values persist as-is
            dlg.Dismissed += OnSafeRevertExpire; // Esc / scrim → the safe choice is to revert
            dlg.Open(this);
            _safeRevertDialog = dlg;

            _safeRevertCountdownTimer = new Godot.Timer { WaitTime = 1.0, OneShot = false };
            AddChild(_safeRevertCountdownTimer);
            _safeRevertCountdownTimer.Timeout += OnSafeRevertTick;
            _safeRevertCountdownTimer.Start();

            _safeRevertTimer = new Godot.Timer { WaitTime = 15.0, OneShot = true };
            AddChild(_safeRevertTimer);
            _safeRevertTimer.Timeout += OnSafeRevertExpire;
            _safeRevertTimer.Start();
        }

        private void OnSafeRevertTick()
        {
            _safeRevertRemaining--;
            if (_safeRevertLabel != null && IsInstanceValid(_safeRevertLabel))
                _safeRevertLabel.Text = $"Reverting in {Math.Max(0, _safeRevertRemaining)}s…";
        }

        /// <summary>Keep confirm: the new values stay; just tear down the timers and dialog reference.</summary>
        private void OnSafeRevertKeep() => DisarmSafeRevert();

        /// <summary>Timeout (or Esc/scrim dismiss): revert window mode + resolution to the prior values, re-apply,
        /// re-persist, and re-sync the two dropdowns.</summary>
        private void OnSafeRevertExpire()
        {
            // Guard against a second entry (both the 15 s Timer and a scrim/Esc Dismissed can target this) — once
            // disarmed, the state is torn down and a re-entry would redundantly re-apply.
            if (_safeRevertTimer == null && _safeRevertDialog == null) return;

            var s = _settings.Current;
            s.WindowMode       = _safeRevertPrevMode;
            s.ResolutionWidth  = _safeRevertPrevW;
            s.ResolutionHeight = _safeRevertPrevH;
            _settings.Apply();
            _settings.Save();

            // Re-sync the two dropdowns to the reverted values (the prior resolution is guaranteed in the option list).
            if (_windowModeSelect != null) SyncWindowModeSelect();
            if (_resolutionSelect != null) { BuildResolutionOptions(); SyncResolutionSelect(); }

            DisarmSafeRevert();
        }

        /// <summary>Free the safe-revert timers and dialog without reverting (idempotent). Called by Keep, by the
        /// revert path after it applies, and by a superseding Apply / Reset.</summary>
        private void DisarmSafeRevert()
        {
            if (_safeRevertCountdownTimer != null)
            {
                if (IsInstanceValid(_safeRevertCountdownTimer)) _safeRevertCountdownTimer.QueueFree();
                _safeRevertCountdownTimer = null;
            }
            if (_safeRevertTimer != null)
            {
                if (IsInstanceValid(_safeRevertTimer)) _safeRevertTimer.QueueFree();
                _safeRevertTimer = null;
            }
            if (_safeRevertDialog != null)
            {
                // QueueFree does not emit Dismissed, so tearing the dialog down here can't re-enter OnSafeRevertExpire.
                if (IsInstanceValid(_safeRevertDialog)) _safeRevertDialog.QueueFree();
                _safeRevertDialog = null;
            }
            _safeRevertLabel = null;
        }

        private void ResetToDefaults()
        {
            DisarmSafeRevert(); // any in-flight revert would target now-stale values
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

            // Story 11.7: re-sync the Graphics widgets to the reset defaults.
            if (_windowModeSelect != null)
            {
                SyncWindowModeSelect();
                BuildResolutionOptions();
                SyncResolutionSelect();
                SyncQualitySelect();
                _vsyncBtn.SetOn(_settings.Current.Vsync, animate: false);
                _uiScaleSlider.Value = _settings.Current.UiScale;
            }

            // Story 8.2: re-sync the AI Provider widgets to the reset defaults (the stored key is NOT cleared here —
            // "Reset to Defaults" governs settings.json fields, not the secret store; use "Clear key" for that).
            if (_providerSelect != null)
            {
                int providerIdx = 0;
                for (int i = 0; i < LlmProviderCatalog.Providers.Count; i++)
                    if (string.Equals(LlmProviderCatalog.Providers[i].Id, _settings.Current.LlmProvider, StringComparison.Ordinal))
                        providerIdx = i;
                _providerSelect.Select(providerIdx);
                _baseUrlInput.Text  = _settings.Current.LlmBaseUrl;
                _modelOverride.Text = "";
                RefreshModelList();
                UpdateAiStatusLine();
            }

            _settings.Apply();
            _settings.Save();
        }
    }
}
