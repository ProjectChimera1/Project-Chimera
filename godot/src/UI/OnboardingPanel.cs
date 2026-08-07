#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;     // ScenarioData, WinCondition
using ProjectChimera.UI.Components;         // ChimeraComponents, ChimeraTooltip
using ProjectChimera.UI.Theme;              // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;             // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 5.9 (NFR-2) — "Your First Scenario" guided onboarding: a presentation-only, skippable/replayable
    /// 6-step checklist overlay that coaches a first-time creator from opening the Creation Suite to a playable
    /// scenario in under 15 minutes, with no manual and no JSON editing. Auto-offered on first boot (see
    /// <c>OnboardingPhase</c>) via <see cref="SettingsData.HasSeenOnboarding"/>; always reachable afterward via the
    /// Settings panel's "Replay onboarding" action (never a new hotkey).
    ///
    /// <para>Steps 1-4 DRIVE existing, already-working panels/tools rather than re-implementing them (D-2): step 1
    /// duplicates a curated template unit via <see cref="Core.MainScene.OpenUnitCardPanel"/> →
    /// <see cref="CreationSuite.UnitCardPanel.StartFromTemplate"/>; steps 2-3 just re-open the same Unit Card
    /// Editor; step 4 is purely instructional over <c>EntityPlacer</c>'s always-on placement palette (nothing to
    /// drive — it never needs opening). Steps 5-6 are this story's own small additive UI: a win-condition picker
    /// that mutates the live <see cref="ScenarioData.WinCondition"/> directly (mirrors <c>WinConditionPhase</c>'s
    /// own button-group pattern) and an optional "Enter Play Mode" button over <see cref="Core.MainScene.EnterPlayMode"/>.
    /// Non-modal (no scrim) and never disables anything else — dismissible at any step, on top of but never
    /// blocking the rest of the Creation Suite.</para>
    /// </summary>
    public partial class OnboardingPanel : Node
    {
        // ── Layout constants ──
        private const float PANEL_W = 380f;
        private const float PANEL_H = 360f;
        private const float MARGIN  = 12f;

        private enum Step { Template = 0, TuneStats = 1, PromoteHero = 2, PlaceEntities = 3, WinCondition = 4, PlayTest = 5 }
        private const int LastStepIndex = (int)Step.PlayTest;

        private static readonly string[] StepTitles =
        {
            "Step 1 — Create a unit",
            "Step 2 — Tune its stats",
            "Step 3 — Promote to Hero",
            "Step 4 — Place a base + units",
            "Step 5 — Set the win condition",
            "Step 6 — Play it",
        };

        // ── Kit context ──
        private GodotTheme        _theme  = null!;

        // ── Deps (wired by OnboardingPhase after AddChild) ──
        private GameState?       _gameState;
        private SettingsManager? _settingsMgr;
        private Core.MainScene?  _mainScene;
        private ScenarioData?    _scenario;

        // ── Shell ──
        private CanvasLayer     _canvas         = null!;
        private PanelContainer  _panel          = null!;
        private Label           _stepTitleLabel = null!;
        private Label           _elapsedLabel   = null!;
        private Label           _noteLabel      = null!;
        private VBoxContainer   _bodyHost       = null!;
        private Godot.Button    _backBtn        = null!;
        private Godot.Button    _nextBtn        = null!;

        // ── State ──
        private int    _stepIndex;
        private ulong  _startMsec;
        private bool   _timing;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            _theme = ChimeraComponents.EnsureInitialized(this);   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>Bind the panel to game state / settings / the panel-navigation host / the live scenario.
        /// Called by <c>OnboardingPhase</c> AFTER <c>AddChild</c> — the LAST phase, so every panel/tool onboarding
        /// might drive already exists. Starts hidden.</summary>
        public void Initialize(GameState gameState, SettingsManager settingsMgr, Core.MainScene mainScene, ScenarioData? scenario)
        {
            _gameState   = gameState;
            _settingsMgr = settingsMgr;
            _mainScene   = mainScene;
            _scenario    = scenario;

            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false;
        }

        /// <summary>Show the overlay. <paramref name="forceRestart"/> (the Settings-panel replay action) always
        /// resets to step 1; a plain re-open (nothing else calls this today) resumes where it left off.</summary>
        public void Open(bool forceRestart = false)
        {
            if (forceRestart || !_panel.Visible)
            {
                _stepIndex = 0;
                _startMsec = Time.GetTicksMsec();
                _timing    = true;
            }
            _panel.Visible = true;
            ClearNote();
            RefreshStep();
        }

        /// <summary>Dismiss without marking onboarding seen (not currently wired to any button — Skip and Finish
        /// both go through <see cref="MarkSeenAndClose"/> so a dismissal never re-offers on next boot; kept for
        /// symmetry with sibling panels' Close()).</summary>
        public void Close()
        {
            _panel.Visible = false;
            _timing = false;
        }

        /// <summary>Persist <see cref="SettingsData.HasSeenOnboarding"/> = true and hide. The single exit path for
        /// Skip, Finish, AND entering Play mode (reaching Play at all is a reasonable "graduated" signal — AC5's
        /// round-trip only requires that onboarding stop auto-offering once completed OR skipped).</summary>
        private void MarkSeenAndClose()
        {
            if (_settingsMgr != null)
            {
                _settingsMgr.Current.HasSeenOnboarding = true;
                _settingsMgr.Save();
            }
            _panel.Visible = false;
            _timing = false;
        }

        private void OnModeChanged(int mode)
        {
            // Read the LIVE mode, not the signal's `mode` argument: WinConditionPhase (an earlier subscriber) can
            // synchronously veto Edit→Play (ResetToAuthoredStart failure) via GameState.SetMode(Edit) BEFORE this
            // handler runs its turn in the original Play emission's subscriber list — Godot re-delivers that
            // nested Edit emission to every subscriber first, then resumes the outer Play emission, so a naive
            // `mode == Play` check here still fires on a vetoed transition even though GameState.Mode has already
            // reverted to Edit (Story 5.9 review pass — was: marked onboarding permanently "seen" on a failed
            // Play attempt, with no recovery path except the Settings replay action).
            if (mode == (int)GameMode.Play && _gameState?.Mode == GameMode.Play && _panel.Visible) MarkSeenAndClose();
        }

        /// <inheritdoc/>
        public override void _Process(double delta)
        {
            if (_panel is null || !_panel.Visible || !_timing) return;
            ulong elapsedMs = Time.GetTicksMsec() - _startMsec;
            int totalSec = (int)(elapsedMs / 1000UL);
            _elapsedLabel.Text = $"Elapsed {totalSec / 60:00}:{totalSec % 60:00}";
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 14 };   // below Settings (15) so Escape's scrim can still cover it
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, 0f);
            _panel.Position = new Vector2(MARGIN, -(PANEL_H + MARGIN));   // bottom-left, clear of the HUD/TerrainBrush/palette
            _panel.Theme = _theme;   // _panel is a Control (OnboardingPanel : Node has NO Theme) — propagates to the subtree
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // ── Title + elapsed + Skip row ──
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var titleLbl = ChimeraComponents.Heading("Your First Scenario", ThemeTokens.Tlg);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(titleLbl);

            _elapsedLabel = ChimeraComponents.Body("Elapsed 00:00", ThemeTokens.TextLo);
            _elapsedLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _elapsedLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(_elapsedLabel);

            var skipBtn = ChimeraComponents.Button("Skip", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            skipBtn.Pressed += MarkSeenAndClose;
            AttachTip(skipBtn, "Skip", "Dismiss this walkthrough. Replay it anytime from Settings.");
            titleRow.AddChild(skipBtn);

            // ── Step title ──
            _stepTitleLabel = ChimeraComponents.Body(StepTitles[0], ThemeTokens.TextHi);
            _stepTitleLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _stepTitleLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            root.AddChild(_stepTitleLabel);

            // ── Scrollable per-step body ──
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                CustomMinimumSize    = new Vector2(0, 150),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            scroll.AddChild(_bodyHost);

            // ── Transient note (e.g. "Created from 'worker'…") ──
            _noteLabel = ChimeraComponents.Body("", ThemeTokens.Ok);
            _noteLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _noteLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _noteLabel.Visible = false;
            root.AddChild(_noteLabel);

            // ── Back / Next footer ──
            var footer = new HBoxContainer();
            footer.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(footer);

            _backBtn = ChimeraComponents.Button("Back", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            _backBtn.Pressed += () => { if (_stepIndex > 0) { _stepIndex--; ClearNote(); RefreshStep(); } };
            AttachTip(_backBtn, "Back", "Return to the previous onboarding step.");
            footer.AddChild(_backBtn);

            var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            footer.AddChild(spacer);

            _nextBtn = ChimeraComponents.Button("Next", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Sm);
            _nextBtn.Pressed += () =>
            {
                if (_stepIndex >= LastStepIndex) { MarkSeenAndClose(); return; }
                _stepIndex++;
                ClearNote();
                RefreshStep();
            };
            AttachTip(_nextBtn, "Next", "Advance to the next onboarding step (or finish, on the last step).");
            footer.AddChild(_nextBtn);

            _panel.Visible = false;   // hidden until Open() (auto-offer or Settings replay)
        }

        // ── Step body dispatch ──────────────────────────────────────────────────

        private void RefreshStep()
        {
            _stepTitleLabel.Text = StepTitles[_stepIndex];
            foreach (Node c in _bodyHost.GetChildren()) { _bodyHost.RemoveChild(c); c.QueueFree(); }

            switch ((Step)_stepIndex)
            {
                case Step.Template:      BuildTemplateStep();      break;
                case Step.TuneStats:     BuildTuneStatsStep();     break;
                case Step.PromoteHero:   BuildPromoteHeroStep();   break;
                case Step.PlaceEntities: BuildPlaceEntitiesStep(); break;
                case Step.WinCondition:  BuildWinConditionStep();  break;
                case Step.PlayTest:      BuildPlayTestStep();      break;
            }

            _backBtn.Disabled = _stepIndex <= 0;
            _nextBtn.Text = _stepIndex >= LastStepIndex ? "Finish" : "Next";
        }

        // ── Step 1: Template ─────────────────────────────────────────────────────

        private void BuildTemplateStep()
        {
            var templateBody = ChimeraComponents.Body(
                "Create your first unit from a starter template — this duplicates an existing unit so you can " +
                "customize it freely. No JSON required.", ThemeTokens.TextMid);
            templateBody.AutowrapMode = TextServer.AutowrapMode.Word;
            templateBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _bodyHost.AddChild(templateBody);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            foreach ((string id, string label) in CreationSuite.UnitCardPanel.CuratedTemplateUnits)
            {
                var btn = ChimeraComponents.Button(label, ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
                string capturedId = id;
                btn.Pressed += () =>
                {
                    bool created = _mainScene?.OpenUnitCardPanel(capturedId) ?? false;
                    if (created)
                        ShowNote($"Created a copy of '{capturedId}' — now open in the Unit Editor [{EditorHotkeys.ChordFor(EditorPanelId.UnitCard)}].", ThemeTokens.Ok);
                    else
                        ShowNote($"Couldn't find template '{capturedId}' in the current faction — opened the Unit Editor on whatever's currently selected instead.", ThemeTokens.Warn);
                };
                AttachTip(btn, label, $"Duplicate the existing '{id}' unit into a new one you can edit freely (opens the Unit Editor).");
                row.AddChild(btn);
            }
            _bodyHost.AddChild(row);
        }

        // ── Step 2: Tune stats ───────────────────────────────────────────────────

        private void BuildTuneStatsStep()
        {
            var tuneBody = ChimeraComponents.Body(
                "In the Unit Editor (still open from Step 1), tune one Combat stat (e.g. Attack Damage) and one " +
                "Economy stat (e.g. Ore Cost) — both live in the Simple tab, no Advanced mode needed.", ThemeTokens.TextMid);
            tuneBody.AutowrapMode = TextServer.AutowrapMode.Word;
            tuneBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _bodyHost.AddChild(tuneBody);

            var btn = ChimeraComponents.Button($"Open Unit Editor [{EditorHotkeys.ChordFor(EditorPanelId.UnitCard)}]", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            btn.Pressed += () => _mainScene?.OpenUnitCardPanel();
            AttachTip(btn, "Open Unit Editor", "Reopen the Unit Editor on the unit you're building.");
            _bodyHost.AddChild(btn);
        }

        // ── Step 3: Promote to Hero ──────────────────────────────────────────────

        private void BuildPromoteHeroStep()
        {
            var promoteBody = ChimeraComponents.Body(
                "Toggle 'Promote to Hero' in the Unit Editor's Simple tab, then pick an Ultimate ability from the " +
                "Ultimate row right there in Simple mode — no Advanced/raw-JSON panel needed.", ThemeTokens.TextMid);
            promoteBody.AutowrapMode = TextServer.AutowrapMode.Word;
            promoteBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _bodyHost.AddChild(promoteBody);

            var btn = ChimeraComponents.Button($"Open Unit Editor [{EditorHotkeys.ChordFor(EditorPanelId.UnitCard)}]", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            btn.Pressed += () => _mainScene?.OpenUnitCardPanel();
            AttachTip(btn, "Open Unit Editor", "Reopen the Unit Editor on the unit you're building.");
            _bodyHost.AddChild(btn);
        }

        // ── Step 4: Place entities ───────────────────────────────────────────────

        private void BuildPlaceEntitiesStep()
        {
            var placeBody = ChimeraComponents.Body(
                "Use the always-on placement palette (top-right of the screen) to place a base and a few units: " +
                "Tab cycles P1 Unit / P2 Unit / Ore Node, B cycles building type, click to place, Shift+click for " +
                "a worker.", ThemeTokens.TextMid);
            placeBody.AutowrapMode = TextServer.AutowrapMode.Word;
            placeBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _bodyHost.AddChild(placeBody);
        }

        // ── Step 5: Win condition ────────────────────────────────────────────────

        private void BuildWinConditionStep()
        {
            var winIntroBody = ChimeraComponents.Body("Choose how this scenario is won.", ThemeTokens.TextMid);
            winIntroBody.AutowrapMode = TextServer.AutowrapMode.Word;
            winIntroBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _bodyHost.AddChild(winIntroBody);

            if (_scenario == null)
            {
                var winNoScenarioBody = ChimeraComponents.Body(
                    "No editable scenario is loaded this session — the win condition can't be changed right now.",
                    ThemeTokens.TextLo);
                winNoScenarioBody.AutowrapMode = TextServer.AutowrapMode.Word;
                winNoScenarioBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                _bodyHost.AddChild(winNoScenarioBody);
                return;
            }

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            AddWinConditionButton(row, WinCondition.DestroyAllBuildings, "Destroy All Buildings",
                "Win by destroying every enemy building.");
            AddWinConditionButton(row, WinCondition.EliminateAllUnits, "Eliminate All Units",
                "Win by eliminating every enemy unit.");
            _bodyHost.AddChild(row);
        }

        /// <summary>One win-condition preset button — mirrors the FactionDefinerPanel color-swatch/AI-preset button
        /// idiom (active = Primary, inactive = Secondary). Mutates <see cref="ScenarioData.WinCondition"/> directly
        /// (the same authoring-time-mutation pattern <c>EntityPlacer</c> already uses), not a JSON round-trip.</summary>
        private void AddWinConditionButton(Control parent, WinCondition value, string label, string tip)
        {
            bool isActive = _scenario!.WinCondition == value;
            var btn = ChimeraComponents.Button(label,
                isActive ? ChimeraComponents.ButtonVariant.Primary : ChimeraComponents.ButtonVariant.Secondary,
                ChimeraComponents.ButtonSize.Sm);
            btn.Pressed += () =>
            {
                _scenario!.WinCondition = value;
                _mainScene?.RefreshWinConditionUi();   // keep the WinConditionUi corner panel's radios in sync
                RefreshStep();
            };
            AttachTip(btn, label, tip);
            parent.AddChild(btn);
        }

        // ── Step 6: Play ─────────────────────────────────────────────────────────

        private void BuildPlayTestStep()
        {
            var playBody = ChimeraComponents.Body(
                "You're ready. Press F5 (or click below) to enter Play mode and watch your authored unit fight.",
                ThemeTokens.TextMid);
            playBody.AutowrapMode = TextServer.AutowrapMode.Word;
            playBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _bodyHost.AddChild(playBody);

            var btn = ChimeraComponents.Button("Enter Play Mode [F5]", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Sm);
            btn.Pressed += () => _mainScene?.EnterPlayMode();
            AttachTip(btn, "Enter Play Mode", "Start the simulation with your authored scenario (same as pressing F5).");
            _bodyHost.AddChild(btn);
        }

        // ── Note ─────────────────────────────────────────────────────────────────

        private void ShowNote(string text, StringName? colorToken = null)
        {
            _noteLabel.Text = text;
            _noteLabel.AddThemeColorOverride("font_color", Tok(colorToken ?? ThemeTokens.Ok));
            _noteLabel.Visible = true;
        }

        private void ClearNote()
        {
            _noteLabel.Visible = false;
            _noteLabel.Text = "";
        }

        private Color Tok(StringName token) => _theme.GetColor(token, ThemeTokens.Type);

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass).</summary>
        private void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
