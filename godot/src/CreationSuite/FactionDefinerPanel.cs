#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;   // FactionDefinerStep, FactionPresetPool, FactionDefinerWizardCore
using ProjectChimera.UI;                  // GameState, GameMode
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraTabs
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 5.5 (FR-17, UX-DR40) — the Faction Definer guided wizard: a 5-step flow (Name &amp; Color, Roster,
    /// Buildings &amp; Tech, Starting Conditions, AI Preset) that assembles a brand-new
    /// <see cref="FactionDefinition"/> from creator picks over the existing on-disk faction content, blocks Finish
    /// until <see cref="FactionValidator.ValidateComplete"/> passes, and only then writes a NEW faction JSON.
    /// Follows the established code-only editor pattern exactly (<see cref="ChimeraComponents"/>/
    /// <see cref="ChimeraTabs"/>, <c>CanvasLayer</c>→<c>PanelContainer</c> shell, no <c>.tscn</c>) — the same
    /// construction method as every sibling editor (Unit Card, Building, Tech Tree, Ability).
    ///
    /// <para><b>This file</b> is the shell: kit bootstrap, the panel chrome (title/close, step tabs, scrollable
    /// step-content host, status line, Back/Next/Finish footer), and step navigation. The step content builders +
    /// the in-memory wizard state + the Finish/save handler live in the partial
    /// <see cref="FactionDefinerPanel"/> file <c>FactionDefinerPanel.Steps.cs</c>.</para>
    ///
    /// <para><b>Unlike every sibling editor</b> (which bind to and edit the CURRENTLY LOADED scenario faction), this
    /// panel never binds an existing <see cref="FactionDefinition"/> — it always assembles a fresh one from scratch
    /// and, on Finish, writes it to a brand-new <c>{id}_faction.json</c> (refusing if that file already exists;
    /// never patches/overwrites <c>alpha_faction.json</c>/<c>beta_faction.json</c> or any other existing faction).</para>
    /// </summary>
    public partial class FactionDefinerPanel : Node
    {
        // ── Layout constants (component-intrinsic dims; the spacing/color TOKENS are read from the theme) ──
        private const float PANEL_W = 560f;
        private const float PANEL_H = 680f;

        /// <summary>Ordinal index of the wizard's last step (<see cref="FactionDefinerStep.AiPreset"/>) — the
        /// Back/Next footer clamps against this instead of a hardcoded step count, so adding a 6th step later can't
        /// silently desync the Next button's bound.</summary>
        private const int LastStepIndex = (int)FactionDefinerStep.AiPreset;

        /// <summary>The on-disk faction files scanned for the Roster / Buildings &amp; Tech preset pools (Story
        /// 5.5's "Epics 2-4 content" pool) — the two shipped, fully-authored factions. Deliberately excludes
        /// <c>_unitcard_sample.json</c>/<c>_buildingcard_sample.json</c> (editor scratch fixtures, not real
        /// content).</summary>
        private static readonly string[] PresetSourceFiles = { "alpha_faction.json", "beta_faction.json" };

        private const string FACTIONS_DIR_RES = "res://resources/data/factions/";

        // ── Kit context (self-owned; _accent only created when this panel is the first consumer) ──
        private GodotTheme        _theme  = null!;
        private AccentController?  _accent;

        // ── Deps (wired by FactionDefinerPhase after AddChild) ──
        private GameState? _gameState;

        // ── Shell ──
        private CanvasLayer    _canvas = null!;
        private PanelContainer _panel  = null!;
        private ChimeraTabs    _stepTabs = null!;     // Segment: the 5 wizard steps
        private VBoxContainer  _bodyHost = null!;      // per-step content (refilled on step change)
        private Label          _statusLabel = null!;   // save/validation status line
        private Godot.Button   _backBtn   = null!;
        private Godot.Button   _nextBtn   = null!;
        private Godot.Button   _finishBtn = null!;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            EnsureKitInitialized();   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>Bind the panel to game state. Called by <c>FactionDefinerPhase</c> AFTER <c>AddChild</c>. Starts
        /// hidden; shown by the <c>X</c> toggle in Edit mode.</summary>
        public void Initialize(GameState gameState)
        {
            _gameState = gameState;
            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;
        }

        /// <summary>Toggle visibility (X key, Edit mode only). On open: reset the wizard to a fresh draft + re-scan
        /// the preset pools (every open starts a brand-new faction — no partial state carries across a close).</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) ResetWizard();
        }

        /// <summary>Hide the panel.</summary>
        public void Close()
        {
            _panel.Visible = false;
        }

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();   // hide in Play (authoring is Edit-only)
        }

        // ── Kit bootstrap ─────────────────────────────────────────────────────────

        private void EnsureKitInitialized()
        {
            // ALWAYS load the theme (the inner PanelContainer.Theme needs it regardless of factory state).
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            // Guard ONLY the one-time factory bootstrap so a future startup phase makes this a clean no-op.
            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 13 };
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Position = new Vector2(-PANEL_W * 0.5f, -PANEL_H * 0.5f);
            _panel.Theme = _theme;   // _panel is a Control (FactionDefinerPanel : Node has NO Theme) — propagates to the subtree
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // Title + close row.
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var titleLbl = Heading("Faction Definer", ThemeTokens.Tlg);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(titleLbl);

            var closeBtn = ChimeraComponents.Button("Close [X]", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += Close;
            closeBtn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(closeBtn);

            // Step indicator (Segment) — 5 steps, mirrors FactionDefinerStep's ordinal order.
            _stepTabs = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment,
                "Name & Color", "Roster", "Buildings & Tech", "Starting Conditions", "AI Preset");
            _stepTabs.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _stepTabs.TabChanged += _ => RefreshStepBody();
            root.AddChild(_stepTabs);

            // Scrollable per-step body.
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            scroll.AddChild(_bodyHost);

            // Status line + Back/Next/Finish footer.
            _statusLabel = Body("", ThemeTokens.TextLo);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            root.AddChild(BuildFooter());

            _panel.Visible = false;   // hidden until the first X toggle
        }

        private HBoxContainer BuildFooter()
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            _backBtn = ChimeraComponents.Button("Back", ChimeraComponents.ButtonVariant.Secondary);
            _backBtn.Pressed += () => { if (_stepTabs.Active > 0) _stepTabs.SetActive(_stepTabs.Active - 1); };
            row.AddChild(_backBtn);

            _nextBtn = ChimeraComponents.Button("Next", ChimeraComponents.ButtonVariant.Secondary);
            _nextBtn.Pressed += () => { if (_stepTabs.Active < LastStepIndex) _stepTabs.SetActive(_stepTabs.Active + 1); };
            row.AddChild(_nextBtn);

            var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddChild(spacer);

            _finishBtn = ChimeraComponents.Button("Finish", ChimeraComponents.ButtonVariant.Primary);
            _finishBtn.Pressed += OnFinishPressed;
            row.AddChild(_finishBtn);

            return row;
        }

        private void UpdateFooterButtons()
        {
            _backBtn.Disabled = _stepTabs.Active <= 0;
            _nextBtn.Disabled = _stepTabs.Active >= LastStepIndex;
        }

        // ── Small shared builders (mirror BuildingCardPanel's) ───────────────────

        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", Tok(ThemeTokens.TextHi));
            return l;
        }

        private Label Body(string text, StringName colorToken)
        {
            var l = new Label { Text = text };
            l.AddThemeColorOverride("font_color", Tok(colorToken));
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            l.AutowrapMode = TextServer.AutowrapMode.Word;
            return l;
        }

        private Color Tok(StringName token) => _theme.GetColor(token, ThemeTokens.Type);

        private void ShowOk(string msg)
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = msg;
            _statusLabel.AddThemeColorOverride("font_color", Tok(ThemeTokens.Ok));
        }

        private void ShowError(string msg)
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = msg;
            _statusLabel.AddThemeColorOverride("font_color", Tok(ThemeTokens.Danger));
        }

        private void ClearStatus()
        {
            if (_statusLabel != null!) _statusLabel.Visible = false;
        }
    }
}
