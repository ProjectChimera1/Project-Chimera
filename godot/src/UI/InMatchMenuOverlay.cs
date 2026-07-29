#nullable enable
using Godot;
using System;
using ProjectChimera.UI.Components; // ChimeraComponents, ChimeraDialog
using ProjectChimera.UI.Theme;       // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;      // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.2 (FR-66) — the in-match menu: an Esc/F10 overlay over the running (or paused) match. Composed from the
    /// 3.1x kit (mirrors <see cref="SettingsPanel"/>): Resume / Settings / Save / Load / Concede / Quit to Menu, plus a
    /// game-speed selector {0.5,1,2,3} and a Pause toggle. Save/Load are present-but-DISABLED ("coming in 11.3") so the
    /// layout is stable for 11.3 to wire while nothing unbuilt is launchable (the 11.7 honesty principle). Concede and
    /// Quit route through a <see cref="ChimeraDialog"/> danger confirm.
    ///
    /// <para>MP asymmetry is explicit (<see cref="SetOnline"/>): online, Speed + Save/Load are disabled and the menu
    /// does NOT pause the sim (peers can't be paused); Settings / Concede / Quit stay available. This overlay is pure
    /// presentation — it emits events the scene wires; it holds no sim state and never touches the tick loop directly.</para>
    /// </summary>
    public partial class InMatchMenuOverlay : CanvasLayer
    {
        // ── Events (wired by MainScene) ───────────────────────────────────────
        /// <summary>Resume: close the menu and (offline) un-pause the sim.</summary>
        public event Action? OnResume;
        /// <summary>Open the shared settings panel.</summary>
        public event Action? OnSettings;
        /// <summary>The player confirmed Concede — issue a Concede order for the local faction.</summary>
        public event Action? OnConcede;
        /// <summary>The player confirmed Quit to Menu — reset to Edit and re-show the main menu.</summary>
        public event Action? OnQuitToMenu;
        /// <summary>The player picked a game speed (0.5/1/2/3). Offline only.</summary>
        public event Action<float>? OnSpeedChanged;
        /// <summary>The player toggled the Pause switch (true = paused). Offline only.</summary>
        public event Action<bool>? OnPauseToggled;

        private static readonly float[] Speeds = { 0.5f, 1f, 2f, 3f };

        // Kit context (self-owned; _accent only created when this overlay is the first kit consumer).
        private GodotTheme        _theme  = null!;
        private AccentController? _accent;

        private bool _online;
        private ChimeraDialog? _activeDialog; // a live concede/quit confirm — Esc is owned by it while open

        private Button _resumeBtn  = null!;
        private Button _settingsBtn = null!;
        private Button _saveBtn     = null!;
        private Button _loadBtn     = null!;
        private Button _concedeBtn  = null!;
        private Button _quitBtn     = null!;
        private Button _pauseBtn    = null!;
        private Label  _speedLabel  = null!;
        private HBoxContainer _speedRow = null!;
        private readonly Button[] _speedBtns = new Button[Speeds.Length];

        /// <summary>Build the menu UI (hidden). Call once at bootstrap.</summary>
        public void Initialize()
        {
            Layer   = 14; // below the settings panel (15) so Settings opens over the menu
            Visible = false;

            EnsureKitInitialized(); // MUST run before any ChimeraComponents.* call, or the factory throws

            var anchorRoot = new Control();
            anchorRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.MouseFilter = Control.MouseFilterEnum.Stop; // eat clicks behind the menu
            anchorRoot.Theme = _theme; // a CanvasLayer has no Theme — apply on its root Control, which propagates
            AddChild(anchorRoot);

            Color voidC = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type);
            var scrim = new ColorRect { Color = new Color(voidC.R, voidC.G, voidC.B, 0.82f) };
            scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            scrim.MouseFilter = Control.MouseFilterEnum.Ignore;
            anchorRoot.AddChild(scrim);

            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.AddChild(center);

            var card = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            card.CustomMinimumSize = new Vector2(380, 0);
            center.AddChild(card);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            card.AddChild(vbox);

            var title = new Label { Text = "MENU", HorizontalAlignment = HorizontalAlignment.Center };
            title.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontDisplay));
            title.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tlg));
            title.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextHi));
            vbox.AddChild(title);

            vbox.AddChild(new HSeparator());

            // ── Game speed selector (offline only) ──
            _speedLabel = ChimeraComponents.FieldLabel("Game Speed");
            vbox.AddChild(_speedLabel);

            _speedRow = new HBoxContainer();
            _speedRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            var speedGroup = new ButtonGroup();
            for (int i = 0; i < Speeds.Length; i++)
            {
                float sp = Speeds[i];
                var b = ChimeraComponents.Button(SpeedLabel(sp), ChimeraComponents.ButtonVariant.Secondary,
                                                 ChimeraComponents.ButtonSize.Sm);
                b.ToggleMode   = true;
                b.ButtonGroup  = speedGroup;
                b.ButtonPressed = Mathf.IsEqualApprox(sp, 1f); // default 1×
                b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                b.Pressed += () => { if (!_online) OnSpeedChanged?.Invoke(sp); };
                _speedBtns[i] = b;
                _speedRow.AddChild(b);
            }
            vbox.AddChild(_speedRow);

            _pauseBtn = ChimeraComponents.Button("Pause Simulation", ChimeraComponents.ButtonVariant.Secondary,
                                                 ChimeraComponents.ButtonSize.Block);
            _pauseBtn.ToggleMode = true;
            _pauseBtn.Toggled += on => { if (!_online) OnPauseToggled?.Invoke(on); };
            vbox.AddChild(_pauseBtn);

            vbox.AddChild(new HSeparator());

            // ── Menu actions ──
            _resumeBtn = AddAction(vbox, "Resume", ChimeraComponents.ButtonVariant.Primary, () => OnResume?.Invoke());
            _settingsBtn = AddAction(vbox, "Settings", ChimeraComponents.ButtonVariant.Secondary, () => OnSettings?.Invoke());
            _saveBtn = AddAction(vbox, "Save (coming in 11.3)", ChimeraComponents.ButtonVariant.Ghost, null);
            _loadBtn = AddAction(vbox, "Load (coming in 11.3)", ChimeraComponents.ButtonVariant.Ghost, null);
            _saveBtn.Disabled = true;
            _loadBtn.Disabled = true;
            ChimeraTooltip.Attach(_saveBtn, "Save", "Mid-match save arrives in Story 11.3.", ChimeraTooltip.TooltipRole.Field);
            ChimeraTooltip.Attach(_loadBtn, "Load", "Mid-match load arrives in Story 11.3.", ChimeraTooltip.TooltipRole.Field);

            _concedeBtn = AddAction(vbox, "Concede", ChimeraComponents.ButtonVariant.Danger, ConfirmConcede);
            _quitBtn = AddAction(vbox, "Quit to Menu", ChimeraComponents.ButtonVariant.Danger, ConfirmQuit);
        }

        private Button AddAction(Control parent, string text, ChimeraComponents.ButtonVariant variant, Action? onPressed)
        {
            var b = ChimeraComponents.Button(text, variant, ChimeraComponents.ButtonSize.Block);
            if (onPressed != null) b.Pressed += onPressed;
            parent.AddChild(b);
            return b;
        }

        private static string SpeedLabel(float s) => Mathf.IsEqualApprox(s, 0.5f) ? "0.5×" : $"{(int)s}×";

        /// <summary>Configure the menu for the current match's network state + current game speed, then show it. Online:
        /// no pause, disabled Speed + Save/Load; offline: everything enabled and (per the scene) the sim is paused while
        /// this is open. <paramref name="currentSpeed"/> re-syncs the speed toggle group so a reopened menu never shows a
        /// stale highlight after the scene reset the speed on match end / Quit / Play Again.</summary>
        public void Open(bool online, float currentSpeed = 1f)
        {
            SetOnline(online);
            // Re-sync the speed toggle group to the scene's CURRENT speed WITHOUT emitting (the group is built once and
            // never rebuilt across matches; the scene resets _gameSpeed to 1× on match-end/Quit/Play-Again without
            // touching this overlay). Match by the closest Speeds[] value so 1×/0.5×/2×/3× all re-highlight correctly.
            SyncSpeedSelection(currentSpeed);
            // Offline the sim is paused while the menu is open (the scene does the actual pause); reflect that on the
            // toggle WITHOUT emitting (SetPressedNoSignal) so opening the menu never fires a spurious OnPauseToggled.
            _pauseBtn.SetPressedNoSignal(!online);
            Visible = true;
            _resumeBtn.GrabFocus();
        }

        private void SyncSpeedSelection(float currentSpeed)
        {
            for (int i = 0; i < Speeds.Length; i++)
                _speedBtns[i].SetPressedNoSignal(Mathf.IsEqualApprox(Speeds[i], currentSpeed));
        }

        /// <summary>Hide the menu. Also dismisses any open confirm dialog and clears <see cref="_activeDialog"/>, so an
        /// external close (e.g. the match resolving while a Concede/Quit confirm is open) can never leave the one-confirm
        /// guard permanently latched — which would block every future confirm since the overlay persists across matches.</summary>
        public void Close()
        {
            if (_activeDialog != null)
            {
                if (GodotObject.IsInstanceValid(_activeDialog)) _activeDialog.QueueFree();
                _activeDialog = null;
            }
            Visible = false;
        }

        private void SetOnline(bool online)
        {
            _online = online;

            // Speed + pause are presentation-loop cadence controls — meaningless online (peers can't be paused/scaled).
            _speedLabel.Visible = !online;
            _speedRow.Visible   = !online;
            _pauseBtn.Visible   = !online;
            foreach (Button b in _speedBtns) b.Disabled = online;

            // Save/Load are always disabled in 11.2 (built in 11.3), but stay VISIBLE so the layout is stable.
            // Concede / Quit / Settings / Resume remain available in both modes.
        }

        private void ConfirmConcede()
        {
            OpenConfirm("Concede match?", "Your faction forfeits the match. This cannot be undone.",
                "Concede", () => { OnConcede?.Invoke(); OnResume?.Invoke(); });
        }

        private void ConfirmQuit()
        {
            OpenConfirm("Quit to menu?", "The current match will end and you will return to the main menu.",
                "Quit to Menu", () => OnQuitToMenu?.Invoke());
        }

        private void OpenConfirm(string title, string body, string confirmText, Action onConfirmed)
        {
            if (_activeDialog != null) return; // one confirm at a time
            var dlg = ChimeraDialog.Create(title, body);
            dlg.AddCancel("Cancel");
            dlg.AddConfirm(confirmText, danger: true);
            dlg.Confirmed += () => { _activeDialog = null; onConfirmed(); };
            dlg.Dismissed += () => { _activeDialog = null; };
            _activeDialog = dlg;
            dlg.Open(this);
        }

        // Use _Input (not _UnhandledInput) so Esc is consumed before MainScene's _UnhandledInput re-toggles the menu.
        // While a confirm dialog is open the dialog owns Esc (cancel); while the settings panel is open MainScene has
        // hidden this menu, so the !Visible guard already yields Esc to the settings panel.
        public override void _Input(InputEvent ev)
        {
            if (!Visible || _activeDialog != null) return;
            if (ev is InputEventKey { Pressed: true, Echo: false } key
                && (key.Keycode == Key.Escape || key.Keycode == Key.F10))
            {
                OnResume?.Invoke();
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Kit bootstrap (mirrors SettingsPanel.EnsureKitInitialized) ─────────
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
    }
}
