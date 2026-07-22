#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Godot;
using ProjectChimera.AI;                // LLMService, AbilityDraftContext (Story 8.4)
using ProjectChimera.AI.Providers;      // AiAvailabilityEvaluator/Messages (four-state)
using ProjectChimera.Core;              // Fixed, ScenarioData
using ProjectChimera.Core.Definitions;  // AbilityDefinition, AbilityPresets, AbilityPresetMatcher, AbilityValidator, AbilityLoader, ContentJson, AbilityRegistry, ISecretStore
using ProjectChimera.UI;                // GameState, GameMode
using ProjectChimera.UI.Components;     // ChimeraSpinner (Story 8.4 "Transmuting…")

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// The Ability Editor (Story 2.5a) — a right-docked, Edit-mode-only authoring panel that produces validated
    /// <see cref="AbilityDefinition"/> JSON in <c>resources/data/abilities/</c>. Two layered modes (progressive
    /// disclosure): a <b>Simple</b> preset form (pick an <see cref="AbilityPresets.Kind"/>, tune numbers — never edit
    /// JSON) and an <b>Advanced</b> raw-JSON escape hatch (round-trips via <see cref="AbilityLoader"/>). Save is
    /// fail-closed: the file is written ONLY on a passing <see cref="AbilityValidator"/> gate, and a reject surfaces
    /// the validator's located error inline (AC3). Presentation-only — it reads the loaded <see cref="AbilityRegistry"/>
    /// and writes JSON files; it never touches a sim array (the sacred boundary). Clones the
    /// <see cref="TriggerEditorPanel"/> shell/lifecycle; styled from the SettingsPanel/ContentBrowserPanel house
    /// palette (no Theme resource exists yet — Epic 3 builds it).
    /// </summary>
    public partial class AbilityEditorPanel : Node
    {
        private const float PANEL_W = 480f;
        private const float PANEL_H = 660f;
        private const float MARGIN  = 12f;
        // SpinBox upper bound for Fixed-backed fields = the 16.16 integer ceiling, so any value representable in the
        // Fixed range reflects into the form WITHOUT a silent display-vs-model clamp (a loaded value can't exceed it).
        private const double FixedSpinMax = 32767;

        // ── House palette (verbatim from SettingsPanel / ContentBrowserPanel; the design kit is Epic 3) ──
        private static readonly Color PanelBg    = new(0.10f, 0.11f, 0.16f, 0.98f);
        private static readonly Color CardBg     = new(0.13f, 0.14f, 0.20f, 1f);
        private static readonly Color CardBorder = new(0.30f, 0.35f, 0.50f, 0.7f);
        private static readonly Color FieldBg    = new(0.08f, 0.09f, 0.12f, 1f);
        private static readonly Color HeaderBlue = new(0.5f, 0.6f, 0.8f);
        private static readonly Color BodyText   = new(0.85f, 0.85f, 0.85f);
        private static readonly Color DimText    = new(0.6f, 0.6f, 0.6f);
        private static readonly Color HintText   = new(0.55f, 0.55f, 0.6f);
        private static readonly Color OkGreen    = new(0.4f, 0.8f, 0.45f);
        private static readonly Color ErrRed     = new(0.92f, 0.4f, 0.4f);

        // ── Deps (create-only editor; scenario is accepted for phase-signature parity, unused today) ──
        private GameState?      _gameState;
        private AbilityRegistry _registry = AbilityRegistry.Empty;

        // ── Story 8.4 — AI draft affordance (provider-backed editable draft; null deps hide the row) ──
        private LLMService?              _llm;
        private AiAvailabilityEvaluator? _aiEvaluator;
        private ISecretStore?            _aiSecretStore;
        private VBoxContainer  _aiCard        = null!;
        private TextEdit       _aiPromptInput = null!;
        private Button         _aiGenBtn      = null!;
        private Label          _aiAvailLabel  = null!;
        private Label          _aiStatusLabel = null!;
        private ChimeraSpinner _aiSpinner     = null!;
        private Label          _aiSpinnerText = null!;

        // ── Shell ──
        private CanvasLayer    _canvas = null!;
        private PanelContainer _panel  = null!;

        // ── Mode ──
        private bool   _simpleMode = true;
        private Button _simpleBtn  = null!;
        private Button _advancedBtn = null!;
        private Control _simplePane   = null!;
        private Control _advancedPane = null!;

        // ── Header (both modes) ──
        private LineEdit     _idEdit        = null!;
        private LineEdit     _nameEdit      = null!;
        private OptionButton _targetingBtn  = null!;
        private Label        _targetingHint = null!;
        private string       _targetingName = "Self";

        // ── Activation (Story 2.6 — the closed passive model: active | aura | on_hit | while_alive) ──
        private OptionButton _activationBtn  = null!;
        private Label        _activationHint = null!;
        private string       _activationName = "active";
        /// <summary>A passive activation (anything but <c>active</c>) — drives the passive-authoring affordances.</summary>
        private bool IsPassive => _activationName != "active";

        // ── Simple body ──
        private OptionButton      _presetBtn = null!;
        private VBoxContainer     _simpleRows = null!;
        private AbilityPresets.Kind _presetKind = AbilityPresets.Kind.TargetedDamage;
        private AbilityPresets.Params _params = AbilityPresets.Defaults(AbilityPresets.Kind.TargetedDamage);

        // ── Advanced body ──
        private TextEdit _jsonPane = null!;

        // ── Status + list ──
        private Label         _statusLabel = null!;
        private VBoxContainer _listBox     = null!;

        // ── Authoring-only serialize options: the canonical converters + human-readable indentation. ──
        // Parse/validate ALWAYS goes through ContentJson.Options/AbilityLoader; only the on-disk + preview text is
        // indented (whitespace doesn't affect round-trip identity, which is judged on Fixed.Raw + structure).
        private static readonly JsonSerializerOptions IndentedOptions = new(ContentJson.Options) { WriteIndented = true };

        // ── Lifecycle (mirrors TriggerEditorPanel: _Ready builds, the phase calls Initialize after AddChild) ──

        public override void _Ready() => BuildUi();

        /// <summary>Wire the editor to the live game state + loaded ability registry. Called by AbilityEditorPhase
        /// AFTER AddChild (so the UI built in _Ready exists). Starts hidden; shown by the K toggle in Edit mode.</summary>
        public void Initialize(ScenarioData? scenario, GameState gameState, AbilityRegistry registry,
            LLMService? llm = null, AiAvailabilityEvaluator? aiEvaluator = null, ISecretStore? aiSecretStore = null)
        {
            _ = scenario;                       // create-only editor: scenario reserved for parity, unused today
            _gameState = gameState;
            _registry  = registry ?? AbilityRegistry.Empty;
            _llm           = llm;               // Story 8.4 — provider-backed draft framework (null hides the AI row)
            _aiEvaluator   = aiEvaluator;
            _aiSecretStore = aiSecretStore;

            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false;

            SelectPreset(AbilityPresets.Kind.TargetedDamage, seedHeader: true);
            SwitchMode(simple: true);
            RefreshList();
            RefreshAvailability();
        }

        /// <summary>Story 8.4 — drain LLM callbacks each frame (marshals draft results to the main thread).</summary>
        public override void _Process(double delta) => _llm?.DrainEvents();

        /// <summary>Toggle visibility (K key, Edit mode only). Refreshes the existing-ability list on open.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) { RefreshList(); RefreshAvailability(); }
        }

        private void Close() => _panel.Visible = false;

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) _panel.Visible = false;   // hide in Play (authoring is Edit-only)
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 14 };
            AddChild(_canvas);

            _panel = new PanelContainer();
            // Right-docked with a MARGIN gutter; explicit offsets + GrowHorizontal.Begin keep the right edge on-screen
            // and grow any over-wide content leftward instead of off the right edge (see BuildingCardPanel).
            _panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.GrowHorizontal = Control.GrowDirection.Begin;
            _panel.GrowVertical   = Control.GrowDirection.Both;
            _panel.OffsetRight  = -MARGIN;
            _panel.OffsetLeft   = -(PANEL_W + MARGIN);
            _panel.OffsetTop    = -PANEL_H * 0.5f;
            _panel.OffsetBottom =  PANEL_H * 0.5f;
            _panel.AddThemeStyleboxOverride("panel", Card(PanelBg, CardBorder, 8, 14, 12));
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 8);
            _panel.AddChild(root);

            // Title row + close.
            var titleRow = new HBoxContainer();
            root.AddChild(titleRow);
            var title = new Label { Text = "Ability Editor", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            title.AddThemeFontSizeOverride("font_size", 24);
            title.AddThemeColorOverride("font_color", Colors.White);
            titleRow.AddChild(title);
            var closeBtn = new Button { Text = "Close  [K]", CustomMinimumSize = new Vector2(96, 30) };
            closeBtn.AddThemeFontSizeOverride("font_size", 12);
            closeBtn.Pressed += Close;
            titleRow.AddChild(closeBtn);

            root.AddChild(new HSeparator());

            // Mode pill.
            BuildModePill(root);

            // Scrollable content (form is taller than the panel).
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);
            var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            content.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(content);

            // Story 8.4 — AI draft card (hidden until Initialize wires a provider). A prompt + Generate produces an
            // editable draft that lands in the SAME form as a hand-authored ability (LoadFromRegistry seam).
            BuildAiCard(content);

            // Common header.
            AddSectionHeader(content, "Ability");
            _idEdit = AddLineEditRow(content, "Id", "lowercase_id");
            _idEdit.TextChanged += _ => ClearStatus();
            _nameEdit = AddLineEditRow(content, "Display Name", "Human-readable name");
            _targetingBtn = AddDropdownRow(content, "Targeting", new[]
            {
                ("None", 0), ("Self", 1), ("Target Unit", 2), ("Ground Point", 3),
            }, selectedId: 1, OnTargetingSelected);
            _targetingHint = new Label
            {
                Text = "Authorable, but cast support is pending (Story 2.4 deferral) — not castable yet.",
                AutowrapMode = TextServer.AutowrapMode.Word, Visible = false,
            };
            _targetingHint.AddThemeFontSizeOverride("font_size", 10);
            _targetingHint.AddThemeColorOverride("font_color", HintText);
            content.AddChild(_targetingHint);

            // Activation selector (Story 2.6) — the CLOSED passive set, exactly these four and nothing else (AC5). A
            // passive choice reveals the passive affordances, fixes targeting, hides cost/cooldown, and routes the
            // effect graph through the Advanced structured composer (no Simple preset form for passives).
            _activationBtn = AddDropdownRow(content, "Activation", new[]
            {
                ("Active (player-cast)", 0), ("Aura (while-alive)", 1), ("On-hit", 2), ("While-alive (self)", 3),
            }, selectedId: 0, OnActivationSelected);
            _activationHint = new Label { AutowrapMode = TextServer.AutowrapMode.Word, Visible = false };
            _activationHint.AddThemeFontSizeOverride("font_size", 10);
            _activationHint.AddThemeColorOverride("font_color", HintText);
            content.AddChild(_activationHint);

            // Simple pane.
            _simplePane = BuildSimplePane();
            content.AddChild(_simplePane);

            // Advanced pane.
            _advancedPane = BuildAdvancedPane();
            content.AddChild(_advancedPane);

            // Status + save.
            content.AddChild(new HSeparator());
            _statusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.Word, Visible = false };
            _statusLabel.AddThemeFontSizeOverride("font_size", 12);
            content.AddChild(_statusLabel);

            var saveRow = new HBoxContainer();
            saveRow.AddThemeConstantOverride("separation", 8);
            content.AddChild(saveRow);
            var saveBtn = new Button { Text = "Save", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Validate, then write the ability file. Available in the NEXT match." };
            saveBtn.Pressed += () => DoSave(reloadAfter: false);
            saveRow.AddChild(saveBtn);
            var saveReloadBtn = new Button { Text = "Save & Reload", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Save, then reload the scene so the ability enters the registry (NOT hot-reloaded into a running match)." };
            saveReloadBtn.Pressed += () => DoSave(reloadAfter: true);
            saveRow.AddChild(saveReloadBtn);

            // Existing abilities.
            content.AddChild(new HSeparator());
            AddSectionHeader(content, "Existing abilities (loaded snapshot)");
            _listBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _listBox.AddThemeConstantOverride("separation", 4);
            content.AddChild(_listBox);
        }

        // ── Story 8.4 — AI draft affordance ────────────────────────────────────

        private void BuildAiCard(Control parent)
        {
            _aiCard = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _aiCard.AddThemeConstantOverride("separation", 4);
            parent.AddChild(_aiCard);

            AddSectionHeader(_aiCard, "AI Draft, Commander");

            _aiAvailLabel = new Label { AutowrapMode = TextServer.AutowrapMode.Word, Visible = false };
            _aiAvailLabel.AddThemeFontSizeOverride("font_size", 11);
            _aiCard.AddChild(_aiAvailLabel);

            _aiPromptInput = new TextEdit
            {
                PlaceholderText = "e.g. \"a short-cooldown self heal for a frontline bruiser\"",
                CustomMinimumSize = new Vector2(0, 60f),
                WrapMode = TextEdit.LineWrappingMode.Boundary,
            };
            _aiCard.AddChild(_aiPromptInput);

            var genRow = new HBoxContainer();
            genRow.AddThemeConstantOverride("separation", 8);
            _aiCard.AddChild(genRow);

            _aiGenBtn = new Button { Text = "Generate ✦", TooltipText = "Draft an editable ability from your prompt — you own what you make." };
            _aiGenBtn.Pressed += OnAiGeneratePressed;
            genRow.AddChild(_aiGenBtn);

            _aiSpinner = ChimeraSpinner.Create(20);
            _aiSpinner.Visible = false;
            genRow.AddChild(_aiSpinner);
            _aiSpinnerText = new Label { Text = "Transmuting…", Visible = false };
            _aiSpinnerText.AddThemeColorOverride("font_color", HeaderBlue);
            genRow.AddChild(_aiSpinnerText);

            _aiStatusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.Word, Visible = false };
            _aiStatusLabel.AddThemeFontSizeOverride("font_size", 11);
            _aiCard.AddChild(_aiStatusLabel);

            _aiCard.AddChild(new HSeparator());
        }

        /// <summary>Story 8.4 — drive the four-state AI-availability line + Generate gating from the config-derived
        /// evaluator (mirrors MapGeneratorPanel). A null evaluator (older wiring) hides the whole AI row; manual
        /// authoring is unaffected in every state.</summary>
        private void RefreshAvailability()
        {
            if (_aiCard == null!) return;
            if (_llm == null || _aiEvaluator == null || _aiSecretStore == null)
            {
                _aiCard.Visible = false;
                return;
            }

            _aiCard.Visible = true;
            var settings = SettingsManager.Instance?.Current ?? new SettingsData();
            AiAvailability state = _aiEvaluator.EvaluateConfig(settings, _aiSecretStore);
            bool available = state == AiAvailability.Healthy;

            _aiAvailLabel.Visible = true;
            _aiAvailLabel.Text = available
                ? "AI: ready (config OK — Test connection in Settings to confirm)."
                : AiAvailabilityMessages.Describe(state);
            _aiAvailLabel.AddThemeColorOverride("font_color", available ? OkGreen : new Color(0.95f, 0.8f, 0.45f));
            _aiGenBtn.Disabled = !available;
        }

        private void OnAiGeneratePressed()
        {
            if (_llm == null) return;
            string prompt = _aiPromptInput.Text.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                ShowAiStatus("Describe the ability first, Commander.", error: true);
                return;
            }

            SetAiBusy(true);

            // Existing ability ids are prompt hints (avoid id collisions); validation is self-contained.
            var ids = new string[_registry.Count];
            for (int i = 0; i < _registry.Count; i++) ids[i] = _registry.Get(i).Id;

            _llm.GenerateAbilityDraftAsync(prompt, new AbilityDraftContext { ExistingAbilityIds = ids }, OnAiDraftComplete);
        }

        private void OnAiDraftComplete(AbilityDefinition? def, string? error)
        {
            SetAiBusy(false);
            if (def == null)
            {
                ShowAiStatus(error ?? "Generation failed.", error: true);
                return;
            }

            // Land the validated draft in the SAME editable form a hand-authored ability uses (reopenable, unlocked).
            LoadFromRegistry(def);
            ShowAiStatus($"Draft '{def.Id}' ready — edit and Save.", error: false);
        }

        private void SetAiBusy(bool busy)
        {
            _aiGenBtn.Disabled = busy;
            _aiSpinner.Visible = busy;
            _aiSpinnerText.Visible = busy;
            if (busy) { _aiStatusLabel.Visible = false; }
        }

        private void ShowAiStatus(string message, bool error)
        {
            _aiStatusLabel.Visible = true;
            _aiStatusLabel.Text = message;
            _aiStatusLabel.AddThemeColorOverride("font_color", error ? ErrRed : OkGreen);
        }

        private void BuildModePill(Control parent)
        {
            var pillRow = new HBoxContainer();
            pillRow.AddThemeConstantOverride("separation", 4);
            parent.AddChild(pillRow);

            var group = new ButtonGroup();
            Button MakePill(string text, bool pressed) => new()
            {
                Text = text, ToggleMode = true, ButtonGroup = group, ButtonPressed = pressed,
                CustomMinimumSize = new Vector2(0, 32), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            _simpleBtn   = MakePill("Simple", true);
            _advancedBtn = MakePill("Advanced", false);
            _simpleBtn.AddThemeFontSizeOverride("font_size", 13);
            _advancedBtn.AddThemeFontSizeOverride("font_size", 13);
            _simpleBtn.Pressed   += SwitchToSimpleFromAdvanced;
            // Re-entry guard (mirrors SwitchToSimpleFromAdvanced): re-clicking the ALREADY-active Advanced pill must
            // NOT re-seed/re-serialize — that would rebuild the structured tree from the Simple model and clobber
            // in-progress Advanced edits. Story 2.5b: entering Advanced seeds the structured composer from the
            // current Simple model (EnterAdvancedFromSimple), then renders the tree + serializes the raw-JSON pane.
            _advancedBtn.Pressed += () => { if (!_simpleMode) return; SwitchMode(simple: false); EnterAdvancedFromSimple(); };
            pillRow.AddChild(_simpleBtn);
            pillRow.AddChild(_advancedBtn);
        }

        private Control BuildSimplePane()
        {
            var pane = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            pane.AddThemeConstantOverride("separation", 6);

            AddSectionHeader(pane, "Preset");
            _presetBtn = AddDropdownRow(pane, "Preset", PresetItems(), selectedId: 0, OnPresetSelected);

            _simpleRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _simpleRows.AddThemeConstantOverride("separation", 6);
            pane.AddChild(_simpleRows);
            return pane;
        }

        // BuildAdvancedPane() now lives in the partial AbilityEditorPanel.Advanced.cs (Story 2.5b): the structured
        // effect-tree composer + the kept (collapsible) raw-JSON escape hatch. It still assigns _jsonPane + wires
        // Show/Apply, so the raw-JSON round-trip path below is unchanged.

        // ── Mode + preset handlers ──────────────────────────────────────────────

        private void SwitchMode(bool simple)
        {
            _simpleMode = simple;
            _simplePane.Visible   = simple;
            _advancedPane.Visible = !simple;
            _simpleBtn.AddThemeColorOverride("font_color", simple ? Colors.White : DimText);
            _advancedBtn.AddThemeColorOverride("font_color", simple ? DimText : Colors.White);
        }

        /// <summary>
        /// Switching Advanced→Simple RECONCILES the raw-JSON pane into the form first, so the two model sources can
        /// never silently diverge (the data-loss footgun): the JSON is parsed + validated and, only if it maps
        /// LOSSLESSLY onto a preset, reflected into the Simple form. An invalid or non-preset graph is REFUSED — the
        /// pill snaps back to Advanced (preserving the raw text) with an inline note — so Simple mode only ever holds
        /// a model it can faithfully re-save. (Simple→Advanced is the reverse: BuildModePill's Advanced handler
        /// re-serialises the form via ShowJson, which is always in sync because we got here through this gate.)
        /// </summary>
        private void SwitchToSimpleFromAdvanced()
        {
            if (_simpleMode) return;   // defensive: the grouped pill won't re-fire Pressed when already Simple

            // Story 2.5b — reconcile from the CANONICAL Advanced model (the structured tree; a dirty raw-JSON pane
            // wins and re-seeds the tree), not just the raw pane, so the tree/JSON/header can never silently diverge.
            AbilityDefinition? resolved = ResolveAdvancedDef();
            if (resolved is null) { _advancedBtn.ButtonPressed = true; return; }   // invalid/incomplete: stay in Advanced (error shown)

            if (!AbilityPresetMatcher.TryDetectPreset(resolved, out _, out _))
            {
                ShowError("This effect graph has no Simple preset form — keep editing it in Advanced (structured composer or raw JSON).");
                _advancedBtn.ButtonPressed = true;   // revert the pill; stay in Advanced (programmatic set ≠ Pressed)
                return;
            }
            ReflectModelIntoForm(resolved);   // sets header + reflects the detected preset into the Simple body
            SwitchMode(simple: true);
            ShowValid("Loaded into the Simple form.");
        }

        private void OnPresetSelected(int id) => SelectPreset((AbilityPresets.Kind)id, seedHeader: false);

        private void SelectPreset(AbilityPresets.Kind kind, bool seedHeader)
        {
            _presetKind = kind;
            AbilityPresets.Params d = AbilityPresets.Defaults(kind);

            // Reset tunable numerics to the preset defaults; preserve the user's id/name unless empty (or seeding).
            _params.CostEnergy = d.CostEnergy; _params.CostOre = d.CostOre; _params.CostCrystal = d.CostCrystal;
            _params.Cooldown = d.Cooldown; _params.Amount = d.Amount; _params.Radius = d.Radius;
            _params.DurationTicks = d.DurationTicks;

            if (seedHeader || string.IsNullOrWhiteSpace(_idEdit.Text)) _idEdit.Text = d.Id;
            if (seedHeader || string.IsNullOrWhiteSpace(_nameEdit.Text)) _nameEdit.Text = d.DisplayName;

            // Sync the targeting dropdown to the preset's natural targeting (the creator may still override it).
            SetTargeting(AbilityPresets.Build(kind, d).Targeting);
            SelectDropdownId(_presetBtn, (int)kind);
            RebuildSimpleRows();
            ClearStatus();
        }

        private void RebuildSimpleRows()
        {
            foreach (Node c in _simpleRows.GetChildren()) { _simpleRows.RemoveChild(c); c.QueueFree(); }

            string amountLabel = _presetKind switch
            {
                AbilityPresets.Kind.TargetedDamage => "Damage",
                AbilityPresets.Kind.Heal           => "Heal Amount",
                AbilityPresets.Kind.SelfBuff       => "Attack Damage Bonus",
                AbilityPresets.Kind.AoeNuke        => "Damage (per target)",
                _ => "Amount",
            };
            AddSpinRow(_simpleRows, amountLabel, 0, FixedSpinMax, 1, FixedToDouble(_params.Amount), v => _params.Amount = ToFixed(v));

            if (_presetKind == AbilityPresets.Kind.AoeNuke)
                AddSpinRow(_simpleRows, "Radius", 0, FixedSpinMax, 0.5, FixedToDouble(_params.Radius), v => _params.Radius = ToFixed(v));

            if (_presetKind == AbilityPresets.Kind.SelfBuff)
                AddSpinRow(_simpleRows, "Duration (ticks)", 0, 99999, 1, _params.DurationTicks, v => _params.DurationTicks = (int)v);

            AddSpinRow(_simpleRows, "Cooldown (s)", 0, FixedSpinMax, 0.5, FixedToDouble(_params.Cooldown), v => _params.Cooldown = ToFixed(v));
            AddSpinRow(_simpleRows, "Cost: Energy", 0, FixedSpinMax, 1, FixedToDouble(_params.CostEnergy), v => _params.CostEnergy = ToFixed(v));
            AddSpinRow(_simpleRows, "Cost: Ore", 0, 99999, 1, _params.CostOre, v => _params.CostOre = (int)v);
            AddSpinRow(_simpleRows, "Cost: Crystal", 0, 99999, 1, _params.CostCrystal, v => _params.CostCrystal = (int)v);
        }

        private void OnTargetingSelected(int id)
        {
            _targetingName = id switch { 0 => "None", 1 => "Self", 2 => "TargetUnit", 3 => "GroundPoint", _ => "Self" };
            _targetingHint.Visible = _targetingName == "GroundPoint";
            ClearStatus();
        }

        private void SetTargeting(string name)
        {
            _targetingName = name;
            int id = name switch { "None" => 0, "Self" => 1, "TargetUnit" => 2, "GroundPoint" => 3, _ => 1 };
            SelectDropdownId(_targetingBtn, id);
            _targetingHint.Visible = name == "GroundPoint";
        }

        // ── Activation (Story 2.6 passive mode) ─────────────────────────────────

        private void OnActivationSelected(int id)
        {
            string name = id switch { 0 => "active", 1 => "aura", 2 => "on_hit", 3 => "while_alive", _ => "active" };
            SetActivation(name, userInitiated: true);
            ClearStatus();
        }

        /// <summary>
        /// Apply an activation choice. A passive (aura/on_hit/while_alive) fixes targeting to its shape rule
        /// (aura/on_hit ⇒ None, while_alive ⇒ Self), hides cost/cooldown (Decision #4) and zeroes them on the draft,
        /// shows the shape hint, and routes authoring into the Advanced structured composer (a passive has no Simple
        /// preset form). <paramref name="userInitiated"/> is false on the load path (<see cref="ReflectModelIntoForm"/>),
        /// where the mode is set by the caller — so loading a passive does not yank the user into Advanced twice.
        /// </summary>
        private void SetActivation(string name, bool userInitiated)
        {
            _activationName = name;
            int id = name switch { "active" => 0, "aura" => 1, "on_hit" => 2, "while_alive" => 3, _ => 0 };
            SelectDropdownId(_activationBtn, id);

            bool passive = IsPassive;
            ApplyPassiveAffordances(passive);   // hide + zero cost/cooldown (Decision #4)

            if (passive)
            {
                // Shape-rule targeting (AC5): aura/on_hit ⇒ None; while_alive ⇒ Self. Lock the dropdown so the
                // creator can't compose an invalid passive targeting.
                SetTargeting(name == "while_alive" ? "Self" : "None");
                _targetingBtn.Disabled = true;

                _activationHint.Text = name switch
                {
                    "aura"        => "Aura: every tick, grant a Modifier to allies in range. Build a Search Area (filter Ally) → Apply Modifier. Targeting None; no cost/cooldown.",
                    "on_hit"      => "On-hit: runs your effect graph when this unit's melee attack lands — and not otherwise. Targeting None; no cost/cooldown.",
                    "while_alive" => "While-alive (self): a permanent Apply Modifier (Duration < 0) OR a Persistent (period effect), installed at spawn. Targeting Self; no cost/cooldown.",
                    _             => "",
                };
                _activationHint.Visible = true;

                // No Simple preset form for passives — author the graph in the structured composer.
                if (userInitiated && _simpleMode) { SwitchMode(simple: false); EnterAdvancedFromSimple(); }
                _simpleBtn.Disabled = true;
            }
            else
            {
                _targetingBtn.Disabled  = false;
                _simpleBtn.Disabled     = false;
                _activationHint.Visible = false;
            }
        }

        /// <summary>Show/hide the Advanced cost &amp; cooldown section and, for a passive, zero those values on the
        /// draft (Decision #4 — a passive carries no cost/cooldown; the validator rejects a non-zero one).</summary>
        private void ApplyPassiveAffordances(bool passive)
        {
            if (_advCostSection != null!) _advCostSection.Visible = !passive;
            if (passive)
            {
                _draft.CostEnergy = Fixed.Zero; _draft.CostOre = 0; _draft.CostCrystal = 0; _draft.Cooldown = Fixed.Zero;
            }
        }

        // ── Model build ─────────────────────────────────────────────────────────

        /// <summary>Build the in-memory ability from the Simple form: preset effect + costs + the header overrides.</summary>
        private AbilityDefinition BuildSimpleModel()
        {
            _params.Id = SanitizeId(_idEdit.Text);
            _params.DisplayName = _nameEdit.Text;
            AbilityDefinition def = AbilityPresets.Build(_presetKind, _params);
            def.Targeting  = _targetingName;     // header override (the creator's explicit choice wins)
            def.Activation = _activationName;    // Story 2.6 (always "active" in Simple — passives author in Advanced)
            return def;
        }

        // ── Raw-JSON escape hatch ───────────────────────────────────────────────

        private void ShowJson()
        {
            try
            {
                // Story 2.5b: in Advanced, serialize the COMPOSED graph (the structured tree + header) — NOT
                // BuildSimpleModel(); that was the #1 round-trip trap (2.5a deferred item 1) that silently overwrote a
                // composed graph with the Simple preset. In Simple mode, unchanged.
                AbilityDefinition model = _simpleMode ? BuildSimpleModel() : BuildAdvancedModel();
                SetPaneText(JsonSerializer.Serialize(model, IndentedOptions));
                ClearStatus();
            }
            catch (InvalidOperationException ex)   // a structurally-incomplete composed tree (e.g. a Search Area with no child yet)
            {
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                ShowError($"Could not render JSON: {ex.Message}");
            }
        }

        private void ApplyJson()
        {
            AbilityValidationResult r = AbilityLoader.Load(_jsonPane.Text, CurrentEditorId());
            if (!r.Ok) { ShowError(r.Error); return; }   // do NOT clobber the model on a bad edit
            ReflectModelIntoForm(r.Value.Value);          // seeds header + Simple form + the structured tree (Story 2.5b, Task 2.2)
            _paneDirty = false;                            // the pane now matches the applied model
            ShowValid("Valid — applied to the form.");
        }

        /// <summary>Reflect a parsed/validated ability back into the header fields, and into the Simple-mode preset
        /// fields when the graph still matches a known preset shape (best-effort round-trip per UX-DR54).</summary>
        private void ReflectModelIntoForm(AbilityDefinition def)
        {
            _idEdit.Text   = def.Id;
            _nameEdit.Text = def.DisplayName;
            SetTargeting(def.Targeting);
            // Story 2.6 — reflect the activation (drives passive affordances). Load-path: the caller owns the mode
            // switch (LoadFromRegistry opens non-preset passives in Advanced), so do not double-switch here.
            SetActivation(def.Activation, userInitiated: false);

            if (AbilityPresetMatcher.TryDetectPreset(def, out AbilityPresets.Kind kind, out AbilityPresets.Params p))
            {
                _presetKind = kind;
                _params = p;
                SelectDropdownId(_presetBtn, (int)kind);
                RebuildSimpleRows();
            }
            // else: a non-preset advanced graph (e.g. a Sequence) — the Simple form is left as-is; the structured
            // composer (seeded just below) is its editable home (Story 2.5b), with the raw-JSON pane as the escape hatch.

            // Story 2.5b — hook the structured-tree rebuild into the ONE shared load path (Task 2.4): clicking an
            // existing multi-effect ability, Apply-JSON, and Advanced→Simple reconciliation all seed the composer here.
            SeedDraftFromDef(def);
        }

        // Preset detection (TryDetectPreset / IsSimpleSelfBuff — the LOSSLESS data-loss guard) lives in the Godot-free
        // AbilityPresetMatcher (src/Core/Definitions) so it is Tier-1-testable; the panel + Story 2.5b share it.

        // ── Validate-gated save (AC1, AC3) ──────────────────────────────────────

        private void DoSave(bool reloadAfter)
        {
            AbilityDefinition def;
            if (_simpleMode)
            {
                def = BuildSimpleModel();
                AbilityValidationResult r = new AbilityValidator().Validate(def);
                if (!r.Ok) { ShowError(r.Error); return; }   // AC3: blocked, located error shown, NO file written
            }
            else
            {
                // Story 2.5b — the structured tree is canonical; a manually-edited (dirty) raw-JSON pane wins and is
                // folded back into the tree (ResolveAdvancedDef), so the three sources never silently diverge.
                AbilityDefinition? resolved = ResolveAdvancedDef();
                if (resolved is null) return;                // AC3: inline error already shown, NOTHING written
                def = resolved;
                // Decision #8 — block an un-sanitised content id in Advanced (filename would diverge from the id, and
                // distinct ids could collide on one file). Panel-side; the validator only checks IsNullOrEmpty.
                if (SanitizeId(def.Id) != def.Id)
                {
                    ShowError($"ability '{def.Id}'.id: contains characters outside [a-z0-9_]; rename before saving.");
                    return;
                }
            }

            string fileId = SanitizeId(def.Id);
            if (string.IsNullOrEmpty(fileId)) { ShowError("ability id must contain at least one [a-z0-9_] character."); return; }

            string abs = ProjectSettings.GlobalizePath($"res://resources/data/abilities/{fileId}.json");
            if (File.Exists(abs)) ConfirmOverwrite(abs, def, reloadAfter);
            else WriteFile(abs, def, reloadAfter);
        }

        private void WriteFile(string abs, AbilityDefinition def, bool reloadAfter)
        {
            string tmp = abs + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(def, IndentedOptions);
                // Save-time round-trip self-check: re-parse the EXACT bytes about to hit disk. Guards the rare case
                // where a Fixed near the 16.16 ceiling serializes (via the converter's 32-bit ToFloat) to a magnitude
                // AbilityLoader rejects on reload — so the editor never reports "Saved" for a file that won't load
                // next match (honours the story's fail-closed "no invalid ability ever reaches a game" promise).
                AbilityValidationResult roundTrip = AbilityLoader.Load(json, CurrentEditorId());
                if (!roundTrip.Ok)
                {
                    ShowError($"Could not save — the ability did not round-trip and would not reload: {roundTrip.Error}");
                    return;   // nothing written: no temp file, no move
                }
                File.WriteAllText(tmp, json);   // atomic: write to temp, then replace
                File.Move(tmp, abs, overwrite: true);
                GD.Print($"[AbilityEditor] Saved {abs}");
                ShowValid($"Saved {Path.GetFileName(abs)} — available in the next match.");
                if (reloadAfter) GetTree().ReloadCurrentScene();
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort: leave no stray .tmp on failure */ }
                ShowError($"Save failed: {ex.Message}");
            }
        }

        private void ConfirmOverwrite(string abs, AbilityDefinition def, bool reloadAfter)
        {
            var dlg = new ConfirmationDialog
            {
                Title = "Overwrite ability?",
                DialogText = $"{Path.GetFileName(abs)} already exists. Overwrite it?",
                Exclusive = true,   // modal: blocks repeat-Save behind it so confirm dialogs can't stack
            };
            _canvas.AddChild(dlg);
            dlg.Confirmed += () => { WriteFile(abs, def, reloadAfter); dlg.QueueFree(); };
            dlg.Canceled  += () => dlg.QueueFree();
            dlg.PopupCentered();
        }

        // ── Existing-ability list (AC: 5; the loaded registry snapshot) ─────────

        private void RefreshList()
        {
            if (_listBox == null!) return;
            foreach (Node c in _listBox.GetChildren()) { _listBox.RemoveChild(c); c.QueueFree(); }

            if (_registry.Count == 0)
            {
                _listBox.AddChild(new Label { Text = "(none loaded — saved abilities appear after a scene reload)", Modulate = DimText });
                return;
            }

            for (int i = 0; i < _registry.Count; i++)
            {
                AbilityDefinition def = _registry.Get(i);
                var row = new HBoxContainer();
                _listBox.AddChild(row);
                var lbl = new Label { Text = def.Id, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
                lbl.AddThemeColorOverride("font_color", BodyText);
                row.AddChild(lbl);
                var editBtn = new Button { Text = "Edit" };
                AbilityDefinition captured = def;   // capture for the closure
                editBtn.Pressed += () => LoadFromRegistry(captured);
                row.AddChild(editBtn);
            }
        }

        /// <summary>Load an existing ability into the editor for edit/duplicate. Reflects into the form; if the graph
        /// matches a preset it opens in Simple, otherwise it opens the raw-JSON pane (single reusable load path —
        /// Story 2.5b reuses this to seed its structured tree).</summary>
        private void LoadFromRegistry(AbilityDefinition def)
        {
            ReflectModelIntoForm(def);                                       // seeds header + Simple form + structured tree
            SetPaneText(JsonSerializer.Serialize(def, IndentedOptions));     // raw-JSON view (programmatic → not a manual edit, not dirty)
            bool isPreset = AbilityPresetMatcher.TryDetectPreset(def, out _, out _);
            SwitchMode(simple: isPreset);
            ShowValid($"Loaded '{def.Id}' for editing.");
        }

        // ── Status surface (AC3 inline located error / valid badge) ─────────────

        private void ShowError(string? message)
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = message ?? "Invalid ability.";
            _statusLabel.AddThemeColorOverride("font_color", ErrRed);
        }

        private void ShowValid(string message = "Valid.")
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = message;
            _statusLabel.AddThemeColorOverride("font_color", OkGreen);
        }

        private void ClearStatus()
        {
            if (_statusLabel != null!) _statusLabel.Visible = false;
        }

        private string CurrentEditorId()
        {
            string id = SanitizeId(_idEdit.Text);
            return string.IsNullOrEmpty(id) ? "<editor>" : id;
        }

        // ── Conversions (authoring boundary — deterministic, no Fixed.FromFloat) ─

        private static double FixedToDouble(Fixed f) => f.Raw / (double)Fixed.ONE;
        // Clamp to the 16.16 integer range so the int Raw can never overflow even if a SpinBox bound is later widened
        // (defensive; realistic Simple values sit far inside). FixedJsonConverter.Read independently rejects out-of-range.
        private static Fixed ToFixed(double v) => Fixed.FromRaw((int)Math.Round(Math.Clamp(v, -FixedSpinMax, FixedSpinMax) * Fixed.ONE));

        /// <summary>Filename/id sanitiser: lowercase, keep [a-z0-9_], collapse the rest to '_'.</summary>
        private static string SanitizeId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char ch in raw.Trim().ToLowerInvariant())
                sb.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? ch : '_');
            return sb.ToString();
        }

        private static (string, int)[] PresetItems()
        {
            var items = new (string, int)[AbilityPresets.All.Length];
            for (int i = 0; i < AbilityPresets.All.Length; i++)
                items[i] = (AbilityPresets.All[i].Label, (int)AbilityPresets.All[i].Kind);
            return items;
        }

        // ── Styled control builders (house palette; no reusable wrapper exists — Epic 3 builds the kit) ──

        private static StyleBoxFlat Card(Color bg, Color border, int radius, float marginX, float marginY) => new()
        {
            BgColor = bg, BorderColor = border,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = marginX, ContentMarginRight = marginX,
            ContentMarginTop = marginY, ContentMarginBottom = marginY,
        };

        private static void AddSectionHeader(Control parent, string text)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", 11);
            lbl.AddThemeColorOverride("font_color", HeaderBlue);
            parent.AddChild(lbl);
        }

        private static HBoxContainer MakeLabeledRow(Control parent, string label)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            parent.AddChild(row);
            var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) };
            lbl.AddThemeFontSizeOverride("font_size", 13);
            lbl.AddThemeColorOverride("font_color", BodyText);
            row.AddChild(lbl);
            return row;
        }

        private static LineEdit AddLineEditRow(Control parent, string label, string placeholder)
        {
            HBoxContainer row = MakeLabeledRow(parent, label);
            var edit = new LineEdit
            {
                PlaceholderText = placeholder,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 28),
            };
            edit.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(edit);
            return edit;
        }

        private static SpinBox AddSpinRow(Control parent, string label, double min, double max, double step, double value, Action<double> onChanged)
        {
            HBoxContainer row = MakeLabeledRow(parent, label);
            var spin = new SpinBox
            {
                MinValue = min, MaxValue = max, Step = step, Value = value,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 28),
            };
            spin.AddThemeFontSizeOverride("font_size", 13);
            spin.ValueChanged += v => onChanged(v);
            row.AddChild(spin);
            return spin;
        }

        private OptionButton AddDropdownRow(Control parent, string label, (string Label, int Id)[] items, int selectedId, Action<int> onSelect)
        {
            HBoxContainer row = MakeLabeledRow(parent, label);
            OptionButton dropdown = MakeStyledDropdown(items, selectedId, onSelect);
            row.AddChild(dropdown);
            return dropdown;
        }

        /// <summary>The house-styled <see cref="OptionButton"/> half of <see cref="AddDropdownRow"/>, without the label
        /// row — so the Story 2.5b structured composer can use the same palette for its bare per-node kind dropdown.</summary>
        private OptionButton MakeStyledDropdown((string Label, int Id)[] items, int selectedId, Action<int> onSelect)
        {
            var dropdown = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 30) };
            dropdown.AddThemeFontSizeOverride("font_size", 13);
            dropdown.AddThemeColorOverride("font_color", BodyText);
            StyleBoxFlat Face(Color bg) => Card(bg, CardBorder, 6, 10, 4);
            dropdown.AddThemeStyleboxOverride("normal", Face(CardBg));
            dropdown.AddThemeStyleboxOverride("hover", Face(new Color(0.18f, 0.20f, 0.28f, 1f)));
            dropdown.AddThemeStyleboxOverride("pressed", Face(new Color(0.18f, 0.20f, 0.28f, 1f)));

            foreach (var (itemLabel, id) in items) dropdown.AddItem(itemLabel, id);
            SelectDropdownId(dropdown, selectedId);
            dropdown.ItemSelected += index => onSelect(dropdown.GetItemId((int)index));
            return dropdown;
        }

        private static void SelectDropdownId(OptionButton dropdown, int id)
        {
            int idx = dropdown.GetItemIndex(id);
            if (idx >= 0) dropdown.Selected = idx;
        }

        private TextEdit MakeJsonPane()
        {
            var edit = new TextEdit
            {
                PlaceholderText = "{ }",
                WrapMode = TextEdit.LineWrappingMode.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 260),
            };
            edit.AddThemeFontSizeOverride("font_size", 13);
            edit.AddThemeStyleboxOverride("normal", Card(FieldBg, CardBorder, 6, 10, 8));
            edit.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.92f));
            return edit;
        }
    }
}
