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
        private ScrollContainer _stepScroll = null!;   // wraps _bodyHost — hidden together with _stepTabs in Advanced
        private VBoxContainer  _bodyHost = null!;      // per-step content (refilled on step change)
        private Label          _statusLabel = null!;   // save/validation status line
        private Godot.Button   _backBtn   = null!;
        private Godot.Button   _nextBtn   = null!;
        private Godot.Button   _finishBtn = null!;

        // ── Simple/Advanced mode (Story 5.6, FR-18) ──
        private ChimeraTabs    _modeTabs     = null!;   // Segment: "Simple" / "Advanced"
        private VBoxContainer  _jsonPaneHost = null!;   // Advanced-only host: raw JSON pane + "Sync JSON from picks"
        private TextEdit       _jsonPane     = null!;
        private bool           _advancedMode;
        private bool           _paneDirty;
        private bool           _suppressPaneDirty;

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
            AttachTip(closeBtn, "Close", "Close the Faction Definer (X).");
            titleRow.AddChild(closeBtn);

            // Step indicator (Segment) — 5 steps, mirrors FactionDefinerStep's ordinal order.
            _stepTabs = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment,
                "Name & Color", "Roster", "Buildings & Tech", "Starting Conditions", "AI Preset");
            _stepTabs.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _stepTabs.TabChanged += _ => RefreshStepBody();
            // Per-tab tooltip (not AttachTip): AttachFocusable would mouse-ignore the tab buttons and make them
            // unclickable — see ChimeraTabs.AttachTabTooltip (Story 5.9 review pass 2).
            _stepTabs.AttachTabTooltip("Wizard steps", "Jump directly to any step — Name & Color, Roster, Buildings & Tech, Starting Conditions, or AI Preset.");
            root.AddChild(_stepTabs);

            // Simple/Advanced mode toggle (Story 5.6, FR-18) — mirrors the step-tabs Segment construction above.
            // Advanced swaps the whole guided step flow for one raw-JSON escape hatch (Back/Next disabled).
            _modeTabs = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced");
            _modeTabs.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _modeTabs.TabChanged += OnModeTabChanged;
            _modeTabs.AttachTabTooltip("Simple / Advanced", "Simple uses guided picks over existing content; Advanced swaps in a raw-JSON escape hatch that still runs through the same validator.");
            root.AddChild(_modeTabs);

            // Scrollable per-step body.
            _stepScroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(_stepScroll);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _stepScroll.AddChild(_bodyHost);

            // Advanced-mode raw JSON pane (mirrors UnitCardPanel.Edit.cs's MakeJsonPane/SetPaneText pattern).
            // Hidden until the mode toggle switches to Advanced.
            _jsonPaneHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _jsonPaneHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(_jsonPaneHost);

            _jsonPane = MakeJsonPane();
            _jsonPane.TextChanged += () => { if (!_suppressPaneDirty) _paneDirty = true; };
            AttachTip(_jsonPane, "Raw JSON", "Hand-edit the whole faction as JSON. Finish still runs it through the same validator as Simple mode.");
            _jsonPaneHost.AddChild(_jsonPane);

            var syncBtn = ChimeraComponents.Button("Sync JSON from picks", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            syncBtn.Pressed += () => SetJsonPaneText(FactionDefinerWizardCore.SerializeDraftClean(_draft));
            AttachTip(syncBtn, "Sync JSON from picks", "Overwrite the raw-JSON pane with your current Simple-mode picks.");
            _jsonPaneHost.AddChild(syncBtn);

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
            AttachTip(_backBtn, "Back", "Return to the previous wizard step.");
            row.AddChild(_backBtn);

            _nextBtn = ChimeraComponents.Button("Next", ChimeraComponents.ButtonVariant.Secondary);
            _nextBtn.Pressed += () => { if (_stepTabs.Active < LastStepIndex) _stepTabs.SetActive(_stepTabs.Active + 1); };
            AttachTip(_nextBtn, "Next", "Advance to the next wizard step.");
            row.AddChild(_nextBtn);

            var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddChild(spacer);

            _finishBtn = ChimeraComponents.Button("Finish", ChimeraComponents.ButtonVariant.Primary);
            _finishBtn.Pressed += OnFinishPressed;
            AttachTip(_finishBtn, "Finish", "Validate and write this faction to a new faction JSON file. Blocked until every required field passes.");
            row.AddChild(_finishBtn);

            return row;
        }

        private void UpdateFooterButtons()
        {
            // Advanced mode has no per-step navigation — Back/Next are disabled while it's active (spec Boundaries).
            _backBtn.Disabled = _advancedMode || _stepTabs.Active <= 0;
            _nextBtn.Disabled = _advancedMode || _stepTabs.Active >= LastStepIndex;
        }

        // ── Simple/Advanced mode toggle (Story 5.6, FR-18) ───────────────────────

        /// <summary>Handle a Simple↔Advanced switch. Simple→Advanced seeds the JSON pane fresh via
        /// <see cref="FactionDefinerWizardCore.SerializeDraftClean"/> every time (never stale). Advanced→Simple
        /// with unsaved pane edits shows an inline discard note — the Design Notes' one-way "whichever mode Finish
        /// is clicked from wins" model; a raw-JSON round-trip would desync the Roster/Buildings/Research
        /// reference-equality checkboxes (Story 5.5), so Advanced edits are NEVER folded back into <c>_draft</c>.</summary>
        private void OnModeTabChanged(int index)
        {
            bool goingAdvanced = index == 1;
            if (goingAdvanced)
            {
                SetJsonPaneText(FactionDefinerWizardCore.SerializeDraftClean(_draft));
                _advancedMode = true;
            }
            else
            {
                if (_advancedMode && _paneDirty)
                    ShowError("Advanced JSON edits were discarded — Simple mode never folds them back into your picks.");
                _advancedMode = false;
            }

            _stepTabs.Visible    = !_advancedMode;
            _stepScroll.Visible  = !_advancedMode;
            _jsonPaneHost.Visible = _advancedMode;
            UpdateFooterButtons();
        }

        private TextEdit MakeJsonPane()
        {
            var te = new TextEdit
            {
                PlaceholderText = "{ }",
                WrapMode = TextEdit.LineWrappingMode.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 300),
            };
            te.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontMono, ThemeTokens.Type));
            te.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Tsm, ThemeTokens.Type));
            te.AddThemeColorOverride("font_color", Tok(ThemeTokens.TextHi));
            return te;
        }

        private void SetJsonPaneText(string text)
        {
            _suppressPaneDirty = true;
            _jsonPane.Text = text;
            _suppressPaneDirty = false;
            _paneDirty = false;
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

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass). Shared by this file
        /// and <c>FactionDefinerPanel.Steps.cs</c> (same partial class).</summary>
        private void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);

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
