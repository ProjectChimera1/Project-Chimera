#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;             // Story 7.3 — DslValueType/VarScope, TriggerGraph (raw-IR hatch), DslJson
using ProjectChimera.Effects;         // Story 7.3 — EffectNode (run_effect embedded subgraph)
using ProjectChimera.UI;
using ProjectChimera.UI.Components;   // ChimeraTooltip (Story 5.9 tooltip-gap closure)
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Edit-mode trigger authoring UI (Phase 5).
    ///
    /// Toggle with the L key (in Edit mode). Shows two sections:
    ///   • Trigger list — all triggers in the current scenario; enable/disable, delete.
    ///   • Generator — natural language input → Claude API → JSON preview → Accept / Discard.
    ///
    /// Integrates with LLMService for AI-powered authoring and writes accepted
    /// triggers directly into ScenarioData.Triggers[].
    /// </summary>
    public partial class TriggerEditorPanel : Node
    {
        // ── Panel dimensions ──────────────────────────────────────────────────

        private const float PANEL_W      = 420f;
        private const float PANEL_H      = 580f;
        private const float MARGIN       = 12f;
        private const float ROW_H        = 32f;

        // ── Dependencies ──────────────────────────────────────────────────────

        private ScenarioData?   _scenario;
        private GameState?      _gameState;
        private LLMService?     _llm;
        private ScenarioContext _context = new();

        // ── UI nodes ──────────────────────────────────────────────────────────

        private CanvasLayer    _canvas      = null!;
        private PanelContainer _panel       = null!;
        private VBoxContainer  _triggerList = null!;
        private VBoxContainer  _genSection  = null!;
        private TextEdit       _nlInput     = null!;
        private Button         _genBtn      = null!;
        private Label          _statusLabel = null!;
        private RichTextLabel  _preview     = null!;
        private Button         _acceptBtn   = null!;
        private Button         _discardBtn  = null!;

        // ── State ─────────────────────────────────────────────────────────────

        private TriggerDefinition? _pendingTrigger;

        // ── Story 7.3 — manual ECA form / variables section / raw-IR hatch ─────

        // Closed-vocab dropdown sources (mirror the NodeKinds registry — since Story 7.7 the ONE vocabulary source
        // the validator also consumes; these UI lists stay hand-kept because they carry presentation-only extras
        // like "(none)"/"expression", and the pre-tick validator is the real gate). "run_effect" is a manual
        // ACTION option that routes the trigger through the graph channel (trigger_graph), not the flat array.
        // Story 7.4: "expression" is a manual CONDITION option that routes the trigger through the graph channel
        // (a CEL-shaped text expression compiled into the shared IR), like run_effect does for actions.
        private static readonly string[] EventKinds     = { "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold" };
        private static readonly string[] ConditionKinds = { "(none)", "always", "building_exists", "resource_comparison", "unit_count", "variable_comparison", "unit_in_region", "expression" };
        private static readonly string[] ActionKinds    = { "display_message", "spawn_unit", "victory", "defeat", "create_timer", "add_resources", "set_variable", "play_sound", "run_effect" };
        private static readonly string[] ValueTypeNames = Enum.GetNames(typeof(DslValueType));
        private static readonly string[] ScopeNames     = Enum.GetNames(typeof(VarScope));

        private VBoxContainer _manualSection = null!;
        private LineEdit      _manualName    = null!;
        private CheckBox      _manualEnabled = null!;
        private CheckBox      _manualRunOnce = null!;
        private OptionButton  _manualEvent   = null!;
        private OptionButton  _manualCond    = null!;
        private OptionButton  _manualCondVar = null!;   // declared-variable picker for variable_comparison
        private SpinBox       _manualCondVal = null!;
        private OptionButton  _manualAction  = null!;
        private OptionButton  _manualActVar  = null!;   // declared-variable picker for set_variable
        private SpinBox       _manualActVal  = null!;
        private LineEdit      _manualActText = null!;    // display_message text / effect JSON (run_effect)
        private LineEdit      _manualCondExpr   = null!; // Story 7.4 — condition expression text (kind "expression")
        private LineEdit      _manualActValExpr = null!; // Story 7.4 — optional set_variable value expression text
        private Label         _manualStatus  = null!;

        private VBoxContainer _varsSection   = null!;
        private VBoxContainer _varsList      = null!;
        private LineEdit      _varName       = null!;
        private OptionButton  _varType       = null!;
        private OptionButton  _varScope      = null!;
        private LineEdit      _varInitial    = null!;
        private OptionButton  _varElemType   = null!;   // Story 7.6 — array element type (visible when Array is selected)
        private LineEdit      _varCapacity   = null!;   // Story 7.6 — array capacity (1..DslBounds.MaxArrayCapacity)
        private HBoxContainer _varArrayRow   = null!;   // Story 7.6 — the Array-only input row (hidden for scalars)
        private Label         _varsStatus    = null!;   // duplicate-name / bad-initial feedback (review follow-up)

        private VBoxContainer _rawIrSection  = null!;
        private TextEdit      _rawIrInput    = null!;
        private Label         _rawIrStatus   = null!;

        // ── Public init ───────────────────────────────────────────────────────

        public void Initialize(
            ScenarioData? scenario,
            GameState gameState,
            LLMService llm,
            ScenarioContext context)
        {
            _scenario  = scenario;
            _gameState = gameState;
            _llm       = llm;
            _context   = context;

            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false; // start hidden; shown by L key toggle
        }

        /// <summary>Called when the scenario is reloaded (e.g. after Import or scene restart).</summary>
        public void SetScenario(ScenarioData? scenario)
        {
            _scenario = scenario;
            RefreshList();
        }

        /// <summary>Called each _Process frame by MainScene to drain LLM callbacks.</summary>
        public void Update()
        {
            _llm?.DrainEvents();
        }

        /// <summary>Toggle panel visibility. Called from MainScene on L key.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) RefreshList();
        }

        // ── _Ready ────────────────────────────────────────────────────────────

        public override void _Ready()
        {
            BuildUi();
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 12 };
            AddChild(_canvas);

            // Anchor panel to the right side, vertically centered. Explicit offsets + GrowHorizontal.Begin keep the
            // right edge on-screen (MARGIN gutter) and grow over-wide content leftward, never off-screen (see BuildingCardPanel).
            _panel = new PanelContainer();
            _panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.GrowHorizontal = Control.GrowDirection.Begin;
            _panel.GrowVertical   = Control.GrowDirection.Both;
            _panel.OffsetRight  = -MARGIN;
            _panel.OffsetLeft   = -(PANEL_W + MARGIN);
            _panel.OffsetTop    = -PANEL_H * 0.5f;
            _panel.OffsetBottom =  PANEL_H * 0.5f;
            _canvas.AddChild(_panel);

            var root = new VBoxContainer { Theme = new Theme() };
            root.AddThemeConstantOverride("separation", 8);
            _panel.AddChild(root);

            // ── Header ────────────────────────────────────────────────────────
            var header = new Label
            {
                Text = "Trigger Editor   (L to close)",
                HorizontalAlignment = HorizontalAlignment.Center
            };
            root.AddChild(header);

            root.AddChild(new HSeparator());

            // ── Trigger list ──────────────────────────────────────────────────
            var listScroll = new ScrollContainer();
            listScroll.CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 200f);
            root.AddChild(listScroll);

            _triggerList = new VBoxContainer();
            listScroll.AddChild(_triggerList);

            // Story 7.3 — layered-complexity entry points: manual preset form, variables, and the raw-IR hatch,
            // alongside the AI path. Each button toggles its section (all start collapsed).
            var manualBtn = new Button { Text = "+ New Trigger (Manual)" };
            manualBtn.Pressed += () => ToggleSection(_manualSection);
            AttachTip(manualBtn, "Manual Trigger", "Author an ECA trigger from closed-vocabulary dropdowns — no AI needed.");
            root.AddChild(manualBtn);

            var newBtn = new Button { Text = "+ New Trigger (via AI)" };
            newBtn.Pressed += OnNewTriggerPressed;
            AttachTip(newBtn, "New Trigger", "Open the AI trigger generator — describe a rule in plain English and review it before accepting.");
            root.AddChild(newBtn);

            var varsBtn = new Button { Text = "Variables…" };
            varsBtn.Pressed += () => { RefreshVarsList(); RefreshVarPickers(); ToggleSection(_varsSection); };
            AttachTip(varsBtn, "Variables", "Declare typed, scoped DSL variables (Global / Per-player / Trigger-local).");
            root.AddChild(varsBtn);

            var rawBtn = new Button { Text = "Raw IR (advanced)…" };
            rawBtn.Pressed += OnRawIrPressed;
            AttachTip(rawBtn, "Raw IR", "Paste canonical graph-IR JSON (the escape hatch). Validated fail-closed on Accept.");
            root.AddChild(rawBtn);

            root.AddChild(new HSeparator());

            BuildManualSection(root);
            BuildVarsSection(root);
            BuildRawIrSection(root);

            root.AddChild(new HSeparator());

            // ── Generator section (initially hidden) ──────────────────────────
            _genSection = new VBoxContainer();
            _genSection.Visible = false;
            root.AddChild(_genSection);

            _genSection.AddChild(new Label { Text = "Describe the trigger in plain English:" });

            _nlInput = new TextEdit
            {
                PlaceholderText =
                    "e.g. \"When Player 1 destroys the enemy Barracks, spawn 10 archers near their base and show a victory message.\"",
                CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 90f),
                WrapMode = TextEdit.LineWrappingMode.Boundary
            };
            AttachTip(_nlInput, "Trigger description", "Describe the trigger in plain English — the AI turns this into a condition/action rule you review before accepting.");
            _genSection.AddChild(_nlInput);

            var genRow = new HBoxContainer();
            _genSection.AddChild(genRow);

            _genBtn = new Button { Text = "Generate ✦" };
            _genBtn.Pressed += OnGeneratePressed;
            AttachTip(_genBtn, "Generate", "Send the description to the AI and preview the generated trigger below.");
            genRow.AddChild(_genBtn);

            _statusLabel = new Label { Text = "" };
            _statusLabel.SizeFlagsHorizontal = Control.SizeFlags.Expand;
            genRow.AddChild(_statusLabel);

            _genSection.AddChild(new Label { Text = "Preview (review before accepting):" });

            _preview = new RichTextLabel
            {
                BbcodeEnabled = true,
                CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 120f),
                ScrollActive = true,
                Visible = false
            };
            _genSection.AddChild(_preview);

            var acceptRow = new HBoxContainer { Visible = false };
            _genSection.AddChild(acceptRow);

            _acceptBtn = new Button { Text = "✔ Accept" };
            _acceptBtn.Pressed += OnAcceptPressed;
            AttachTip(_acceptBtn, "Accept", "Add the previewed trigger to this scenario.");
            acceptRow.AddChild(_acceptBtn);

            _discardBtn = new Button { Text = "✘ Discard" };
            _discardBtn.Pressed += OnDiscardPressed;
            AttachTip(_discardBtn, "Discard", "Drop the previewed trigger without adding it.");
            acceptRow.AddChild(_discardBtn);

            // Keep a reference so we can show/hide the accept row.
            _preview.SetMeta("acceptRow", acceptRow);
        }

        // ── Trigger list ──────────────────────────────────────────────────────

        private void RefreshList()
        {
            // Clear existing rows.
            foreach (Node child in _triggerList.GetChildren())
            {
                _triggerList.RemoveChild(child);
                child.QueueFree();
            }

            var triggers = _scenario?.Triggers;
            if (triggers == null || triggers.Length == 0)
            {
                _triggerList.AddChild(new Label
                {
                    Text = "(no triggers — click '+ New Trigger' to add one)",
                    AutowrapMode = TextServer.AutowrapMode.Word
                });
                return;
            }

            for (int i = 0; i < triggers.Length; i++)
            {
                var trigger = triggers[i];
                int idx     = i; // capture for closures

                var row = new HBoxContainer();
                _triggerList.AddChild(row);

                var enabledBox = new CheckBox { ButtonPressed = trigger.Enabled };
                enabledBox.Toggled += on =>
                {
                    if (_scenario != null && idx < _scenario.Triggers.Length)
                        _scenario.Triggers[idx].Enabled = on;
                };
                AttachTip(enabledBox, trigger.Name, "Enable or disable this trigger without deleting it.", ChimeraTooltip.TooltipRole.Field);
                row.AddChild(enabledBox);

                var nameLabel = new Label
                {
                    Text = trigger.Name,
                    SizeFlagsHorizontal = Control.SizeFlags.Expand,
                    ClipText = true
                };
                row.AddChild(nameLabel);

                var onceLabel = new Label
                {
                    Text = trigger.RunOnce ? "[once]" : "",
                    Modulate = new Color(0.7f, 0.7f, 0.7f)
                };
                row.AddChild(onceLabel);

                var delBtn = new Button { Text = "✘" };
                delBtn.Pressed += () => DeleteTrigger(idx);
                AttachTip(delBtn, "Delete", $"Remove trigger '{trigger.Name}' from this scenario.");
                row.AddChild(delBtn);
            }
        }

        private void DeleteTrigger(int idx)
        {
            if (_scenario == null) return;

            var list = new List<TriggerDefinition>(_scenario.Triggers);
            if (idx < 0 || idx >= list.Count) return;
            list.RemoveAt(idx);
            _scenario.Triggers = list.ToArray();
            RefreshList();
        }

        // ── Generator callbacks ───────────────────────────────────────────────

        private void OnNewTriggerPressed()
        {
            _genSection.Visible = true;
            _nlInput.Text = "";
            _statusLabel.Text = "";
            _preview.Visible = false;
            GetAcceptRow().Visible = false;
            _pendingTrigger = null;
        }

        private void OnGeneratePressed()
        {
            if (_llm == null)
            {
                _statusLabel.Text = "LLM service not configured.";
                return;
            }

            string desc = _nlInput.Text.Trim();
            if (string.IsNullOrEmpty(desc))
            {
                _statusLabel.Text = "Please describe the trigger first.";
                return;
            }

            _genBtn.Disabled = true;
            _statusLabel.Text = "Generating…";
            _preview.Visible  = false;
            GetAcceptRow().Visible = false;
            _pendingTrigger = null;

            _llm.GenerateTriggerAsync(desc, _context, OnGenerationComplete);
        }

        private void OnGenerationComplete(TriggerDefinition? trigger, string? error)
        {
            _genBtn.Disabled = false;

            if (trigger == null)
            {
                _statusLabel.Text = $"✘ {error}";
                return;
            }

            _pendingTrigger   = trigger;
            _statusLabel.Text = "✔ Review and accept below.";

            // Register the FixedJsonConverter so the Fixed fields (Amount/TimerSeconds/CooldownSeconds) render as
            // human-readable decimals in the preview, not as {} (Fixed.Raw is a field, which STJ omits by default).
            string prettyJson = JsonSerializer.Serialize(trigger,
                new JsonSerializerOptions { WriteIndented = true, Converters = { new FixedJsonConverter() } });

            _preview.Text = $"[code]{GodotEscape(prettyJson)}[/code]";
            _preview.Visible = true;
            GetAcceptRow().Visible = true;
        }

        private void OnAcceptPressed()
        {
            if (_pendingTrigger == null || _scenario == null) return;

            var list = new List<TriggerDefinition>(_scenario.Triggers) { _pendingTrigger };
            _scenario.Triggers = list.ToArray();
            _pendingTrigger = null;

            _genSection.Visible = false;
            RefreshList();
        }

        private void OnDiscardPressed()
        {
            _pendingTrigger = null;
            _genSection.Visible = false;
            _statusLabel.Text = "";
        }

        // ── Visibility handling ───────────────────────────────────────────────

        private void OnModeChanged(int mode)
        {
            // Hide panel when switching to Play mode.
            if (mode == (int)GameMode.Play)
                _panel.Visible = false;
        }

        // ── Story 7.3 — manual ECA preset form ─────────────────────────────────

        private void BuildManualSection(VBoxContainer root)
        {
            _manualSection = new VBoxContainer { Visible = false };
            root.AddChild(_manualSection);
            _manualSection.AddChild(new Label { Text = "Manual trigger (ECA):" });

            _manualName = new LineEdit { PlaceholderText = "Trigger name", Text = "New Trigger" };
            _manualSection.AddChild(_manualName);

            var flags = new HBoxContainer();
            _manualEnabled = new CheckBox { Text = "Enabled", ButtonPressed = true };
            _manualRunOnce = new CheckBox { Text = "Run once" };
            flags.AddChild(_manualEnabled);
            flags.AddChild(_manualRunOnce);
            _manualSection.AddChild(flags);

            _manualSection.AddChild(new Label { Text = "When (event):" });
            _manualEvent = MakeOption(EventKinds);
            _manualSection.AddChild(_manualEvent);

            _manualSection.AddChild(new Label { Text = "If (condition):" });
            _manualCond = MakeOption(ConditionKinds);
            _manualSection.AddChild(_manualCond);
            var condRow = new HBoxContainer();
            _manualCondVar = new OptionButton();  // populated with declared variables
            _manualCondVal = new SpinBox { MinValue = -1_000_000, MaxValue = 1_000_000, Value = 0 };
            condRow.AddChild(new Label { Text = "var" });
            condRow.AddChild(_manualCondVar);
            condRow.AddChild(new Label { Text = "== value" });
            condRow.AddChild(_manualCondVal);
            _manualSection.AddChild(condRow);
            // Story 7.4 — the condition-expression text field, revealed when the "expression" kind is picked.
            _manualCondExpr = new LineEdit
            {
                PlaceholderText = "condition expression, e.g. (a || b) && !c",
                Visible = false,
            };
            AttachTip(_manualCondExpr, "Condition expression",
                "A typed boolean expression over declared variables (CEL-shaped: + - * / %, comparisons, && || !, count/distance/min/max/abs). Validated fail-closed on Add.",
                ChimeraTooltip.TooltipRole.Field);
            _manualSection.AddChild(_manualCondExpr);
            _manualCond.ItemSelected += _ =>
                _manualCondExpr.Visible = ConditionKinds[Math.Max(0, _manualCond.Selected)] == "expression";

            _manualSection.AddChild(new Label { Text = "Then (action):" });
            _manualAction = MakeOption(ActionKinds);
            _manualSection.AddChild(_manualAction);
            var actRow = new HBoxContainer();
            _manualActVar = new OptionButton();   // declared-variable picker for set_variable
            _manualActVal = new SpinBox { MinValue = -1_000_000, MaxValue = 1_000_000, Value = 0 };
            actRow.AddChild(new Label { Text = "var" });
            actRow.AddChild(_manualActVar);
            actRow.AddChild(new Label { Text = "value" });
            actRow.AddChild(_manualActVal);
            _manualSection.AddChild(actRow);
            // Story 7.4 — the optional set_variable VALUE expression. When non-empty, the trigger persists via the
            // graph channel and the assignment is the computed, typed expression result (widening the variable
            // picker to Int/Fixed/Bool); when empty, the literal SpinBox value applies (Int-only picker).
            _manualActValExpr = new LineEdit
            {
                PlaceholderText = "value expression (optional), e.g. (gold + 5) * 2",
                Visible = false,
            };
            AttachTip(_manualActValExpr, "Value expression",
                "Computes the set_variable value from a typed expression (must match the target variable's type). Leave empty to use the literal value.",
                ChimeraTooltip.TooltipRole.Field);
            _manualSection.AddChild(_manualActValExpr);
            _manualAction.ItemSelected += _ =>
                _manualActValExpr.Visible = ActionKinds[Math.Max(0, _manualAction.Selected)] == "set_variable";
            _manualActValExpr.TextChanged += _ => RefreshVarPickers(); // widen/narrow the set_variable picker live
            _manualActText = new LineEdit { PlaceholderText = "message text, or run_effect JSON e.g. {\"kind\":\"direct_hp_delta\",\"delta\":-10}" };
            _manualSection.AddChild(_manualActText);

            var addBtn = new Button { Text = "Add trigger" };
            addBtn.Pressed += OnManualAddPressed;
            _manualSection.AddChild(addBtn);

            _manualStatus = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.Word };
            _manualSection.AddChild(_manualStatus);
        }

        private void OnManualAddPressed()
        {
            if (_scenario == null) { SetManualStatus("No scenario loaded."); return; }

            string name = string.IsNullOrWhiteSpace(_manualName.Text) ? "New Trigger" : _manualName.Text.Trim();
            string ev   = EventKinds[Math.Max(0, _manualEvent.Selected)];
            string cond = ConditionKinds[Math.Max(0, _manualCond.Selected)];
            string act  = ActionKinds[Math.Max(0, _manualAction.Selected)];

            // Review follow-up: an empty variable picker (no declared Int variable yet) used to persist a silently
            // inert trigger — Variable="" makes a variable_comparison never true and a set_variable a no-op. Refuse
            // with feedback instead of authoring a dead trigger.
            if (cond == "variable_comparison" && string.IsNullOrEmpty(SelectedText(_manualCondVar)))
            {
                SetManualStatus("✘ variable_comparison needs a declared Int variable — add one in the Variables section first.");
                return;
            }
            if (act == "set_variable" && string.IsNullOrEmpty(SelectedText(_manualActVar)))
            {
                // Story 7.4: the picker widens to Int/Fixed/Bool when a value expression is present — say so
                // (PerPlayer targets are excluded there: the form has no slot picker; Raw IR carries the slot).
                SetManualStatus(_manualActValExpr.Text.Trim().Length > 0
                    ? "✘ set_variable needs a declared Int/Fixed/Bool variable (Global or TriggerLocal; PerPlayer targets via Raw IR) — add one in the Variables section first."
                    : "✘ set_variable needs a declared Int variable — add one in the Variables section first.");
                return;
            }

            // Story 7.4 (review patch): the run_effect branch below maps "expression" to a NULL condition — an
            // authored expression condition would be silently discarded and the effect would persist firing
            // UNCONDITIONALLY (with a success status). Refuse fail-closed instead.
            if (act == "run_effect" && cond == "expression")
            {
                SetManualStatus("✘ An expression condition cannot pair with run_effect yet — author via Raw IR.");
                return;
            }

            // The run_effect action has no flat representation — route it through the graph channel (trigger_graph).
            // Review follow-up: the chosen condition rides along as a graph ConditionNode (it was silently discarded
            // before — the effect fired unconditionally against the authored logic).
            if (act == "run_effect")
            {
                PersistManualRunEffect(name, ev,
                    cond == "(none)" || cond == "expression" ? null : cond,
                    cond == "variable_comparison" ? SelectedText(_manualCondVar) : null,
                    (int)_manualCondVal.Value);
                return;
            }

            // Story 7.4 — expression condition and/or set_variable value expression: expressions live in the graph
            // IR only, so the whole trigger persists via the graph channel (BuildExpressionTrigger + Merge, the
            // PersistManualRunEffect accumulation pattern). Fail-closed: parse/compile errors land on the status
            // label and nothing is persisted.
            string condExprText = _manualCondExpr.Text.Trim();
            string valExprText  = act == "set_variable" ? _manualActValExpr.Text.Trim() : "";
            bool exprCondition  = cond == "expression";
            bool exprValue      = act == "set_variable" && valExprText.Length > 0;
            if (exprCondition || exprValue)
            {
                if (exprCondition && condExprText.Length == 0)
                {
                    SetManualStatus("✘ The expression condition needs expression text.");
                    return;
                }
                if (exprCondition && act != "set_variable")
                {
                    SetManualStatus("✘ An expression condition currently pairs with the set_variable action (author other combinations via Raw IR).");
                    return;
                }
                if (exprValue && !exprCondition && cond != "(none)")
                {
                    SetManualStatus("✘ A value expression pairs with the 'expression' condition or '(none)' (the graph channel would drop other condition kinds).");
                    return;
                }
                PersistManualExpression(name, ev,
                    exprCondition ? condExprText : null,
                    SelectedText(_manualActVar),
                    exprValue ? valExprText : null,
                    (int)_manualActVal.Value);
                return;
            }

            var conditions = new List<TriggerCondition>();
            if (cond != "(none)")
            {
                var c = new TriggerCondition { Type = cond };
                if (cond == "variable_comparison")
                {
                    c.Variable = SelectedText(_manualCondVar);
                    c.Operator = "==";
                    c.Value    = (int)_manualCondVal.Value;
                }
                conditions.Add(c);
            }

            var action = new TriggerAction { Type = act };
            switch (act)
            {
                case "set_variable":
                    action.Variable = SelectedText(_manualActVar);
                    action.Value    = (int)_manualActVal.Value;
                    break;
                case "display_message":
                    action.Text = _manualActText.Text;
                    break;
                // spawn_unit/create_timer/add_resources/etc. keep their defaults here (advanced fields are the raw-IR
                // hatch's job); the preset form covers the common variable/message leaves + run_effect.
            }

            var trigger = new TriggerDefinition
            {
                Name    = name,
                Enabled = _manualEnabled.ButtonPressed,
                RunOnce = _manualRunOnce.ButtonPressed,
                Events  = new[] { new TriggerEvent { Type = ev } },
                Conditions = conditions.ToArray(),
                Actions = new[] { action },
            };

            _scenario.Triggers = Append(_scenario.Triggers, trigger);
            SetManualStatus($"✔ Added '{name}'.");
            RefreshList();
        }

        /// <summary>Build a single-trigger graph (event → trigger → run_effect, optionally gated by a condition —
        /// review follow-up: the form's chosen condition used to be silently discarded here) from the effect JSON in
        /// the text field and ACCUMULATE it into <see cref="ScenarioData.TriggerGraphJson"/> — a second run_effect
        /// trigger is MERGED with the existing graph (ids offset past it), never overwriting the first. The effect
        /// JSON is parsed through the shared effect converter (fail-closed) into an <see cref="EffectNode"/>, then
        /// wired by the Godot-free, unit-testable <see cref="TriggerGraph.BuildRunEffectTrigger"/> helper.</summary>
        private void PersistManualRunEffect(string name, string ev,
            string? conditionKind, string? conditionVariable, int conditionValue)
        {
            if (_scenario == null) return;
            string effectJson = _manualActText.Text.Trim();
            if (string.IsNullOrEmpty(effectJson))
            {
                SetManualStatus("run_effect needs an effect JSON in the text field.");
                return;
            }
            try
            {
                // Parse the embedded effect subgraph fail-closed through the shared effect converter, then wire the
                // single-trigger graph via the extracted helper (ids 0=trigger, 1=event, 2=run_effect, 3=condition).
                EffectNode effect = JsonSerializer.Deserialize<EffectNode>(effectJson, DslJson.Options)
                    ?? throw new JsonException("effect JSON deserialized to null.");
                TriggerGraph added = TriggerGraph.BuildRunEffectTrigger(
                    name, ev, effect, _manualEnabled.ButtonPressed, _manualRunOnce.ButtonPressed,
                    conditionKind, conditionVariable, conditionValue);

                // ACCUMULATE: merge into the existing graph channel (offsetting ids) so an earlier run_effect/raw-IR
                // trigger survives. When the channel is empty, the new single-trigger graph stands alone.
                TriggerGraph graph = string.IsNullOrWhiteSpace(_scenario.TriggerGraphJson)
                    ? added
                    : MergeInto(TriggerGraph.FromJson(_scenario.TriggerGraphJson!), added);
                _scenario.TriggerGraphJson = graph.ToCanonicalJson();  // canonicalize on persist
                // Review follow-up: keep an OPEN raw-IR section in sync — its Apply would otherwise overwrite the
                // graph channel with the stale text it captured when it was opened, silently dropping this trigger.
                if (_rawIrSection != null && _rawIrSection.Visible)
                    _rawIrInput.Text = _scenario.TriggerGraphJson;
                SetManualStatus($"✔ Added run_effect trigger '{name}' to the graph channel.");
                RefreshList();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // NotSupportedException: System.Text.Json can surface it on hostile input; the hatch must stay
                // fail-closed with feedback rather than crash the editor.
                SetManualStatus($"✘ Rejected: {ex.Message}");
            }
        }

        /// <summary>Story 7.4 — persist a manual trigger whose condition and/or set_variable value is a CEL-shaped
        /// TEXT expression, via the Godot-free <see cref="TriggerGraph.BuildExpressionTrigger"/> helper (parse +
        /// compile fail-closed; located <see cref="JsonException"/> on any syntax/type/cap violation) and the same
        /// graph-channel ACCUMULATION as <see cref="PersistManualRunEffect"/> (merge, canonicalize, raw-IR resync).
        /// Nothing persists on rejection — the error lands on the status label.</summary>
        private void PersistManualExpression(string name, string ev, string? conditionExprText,
            string? setVarName, string? valueExprText, int literalValue)
        {
            if (_scenario == null) return;
            try
            {
                var declMap = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
                if (_scenario.Variables != null)
                    foreach (var v in _scenario.Variables)
                        if (!string.IsNullOrWhiteSpace(v.Name))
                            declMap[v.Name] = (v.Type, v.Scope);

                // Review P10 — the declared-array map (mirrors declMap): without it, an author who declares an
                // Array variable and types `length(arr) >= 2` in the manual condition field got a false "no
                // array declaration is available" reject (the helper defaulted arrayDecls to null).
                var arrayDecls = new Dictionary<string, (DslValueType Elem, int Capacity)>(StringComparer.Ordinal);
                if (_scenario.Variables != null)
                    foreach (var v in _scenario.Variables)
                        if (!string.IsNullOrWhiteSpace(v.Name) && v.Type == DslValueType.Array
                            && v.ElementType is DslValueType elem && v.Capacity is int cap)
                            arrayDecls[v.Name] = (elem, cap);

                TriggerGraph added = TriggerGraph.BuildExpressionTrigger(
                    name, ev, conditionExprText, setVarName, 0, valueExprText, declMap,
                    _manualEnabled.ButtonPressed, _manualRunOnce.ButtonPressed, literalValue, arrayDecls);

                TriggerGraph graph = string.IsNullOrWhiteSpace(_scenario.TriggerGraphJson)
                    ? added
                    : MergeInto(TriggerGraph.FromJson(_scenario.TriggerGraphJson!), added);
                _scenario.TriggerGraphJson = graph.ToCanonicalJson();  // canonicalize on persist
                if (_rawIrSection != null && _rawIrSection.Visible)
                    _rawIrInput.Text = _scenario.TriggerGraphJson;     // keep an open raw-IR section in sync
                SetManualStatus($"✔ Added expression trigger '{name}' to the graph channel.");
                RefreshList();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                SetManualStatus($"✘ Rejected (no trigger added): {ex.Message}");
            }
        }

        /// <summary>Merge <paramref name="added"/> into <paramref name="existing"/> (offsetting ids) and return the
        /// accumulated graph. A thin wrapper over <see cref="TriggerGraph.Merge"/> for readability at the call site.</summary>
        private static TriggerGraph MergeInto(TriggerGraph existing, TriggerGraph added)
        {
            existing.Merge(added);
            return existing;
        }

        // ── Story 7.3 — variables declaration section ──────────────────────────

        private void BuildVarsSection(VBoxContainer root)
        {
            _varsSection = new VBoxContainer { Visible = false };
            root.AddChild(_varsSection);
            _varsSection.AddChild(new Label { Text = "Declared variables:" });

            _varsList = new VBoxContainer();
            _varsSection.AddChild(_varsList);

            var row = new HBoxContainer();
            _varName    = new LineEdit { PlaceholderText = "name" };
            _varType    = MakeOption(ValueTypeNames);
            _varScope   = MakeOption(ScopeNames);
            _varInitial = new LineEdit { PlaceholderText = "initial (0)", CustomMinimumSize = new Vector2(70, 0) };
            row.AddChild(_varName);
            row.AddChild(_varType);
            row.AddChild(_varScope);
            row.AddChild(_varInitial);
            _varsSection.AddChild(row);

            // Story 7.6 — Array-only inputs: element type + capacity, shown ONLY while the Array type is
            // selected (layered complexity: scalar declarations keep the exact pre-7.6 row).
            _varArrayRow = new HBoxContainer { Visible = false };
            _varArrayRow.AddChild(new Label { Text = "element:" });
            _varElemType = MakeOption(new[] { "Int", "Fixed", "Bool" });
            _varArrayRow.AddChild(_varElemType);
            _varArrayRow.AddChild(new Label { Text = "capacity:" });
            _varCapacity = new LineEdit { PlaceholderText = $"1..{DslBounds.MaxArrayCapacity}", CustomMinimumSize = new Vector2(70, 0) };
            _varArrayRow.AddChild(_varCapacity);
            _varsSection.AddChild(_varArrayRow);
            _varType.ItemSelected += idx =>
                _varArrayRow.Visible = (DslValueType)Math.Max(0, (int)idx) == DslValueType.Array;

            var addBtn = new Button { Text = "Add variable" };
            addBtn.Pressed += OnVarAddPressed;
            _varsSection.AddChild(addBtn);

            _varsStatus = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.Word };
            _varsSection.AddChild(_varsStatus);
        }

        private void OnVarAddPressed()
        {
            if (_scenario == null) return;
            string name = _varName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { _varsStatus.Text = "✘ A variable needs a name."; return; }

            // Review follow-up: a duplicate declaration used to be silently accepted here and only rejected later by
            // the pre-tick validator (failing the WHOLE scenario with an error this panel never showed). Refuse at
            // authoring time with feedback instead.
            if (_scenario.Variables != null)
                foreach (var existing in _scenario.Variables)
                    if (existing.Name == name)
                    {
                        _varsStatus.Text = $"✘ '{name}' is already declared.";
                        return;
                    }

            var type  = (DslValueType)Math.Max(0, _varType.Selected);
            var scope = (VarScope)Math.Max(0, _varScope.Selected);
            // Review follow-up: unparseable initial text used to silently become 0 — refuse with feedback instead
            // (an EMPTY field still defaults to 0, matching the "initial (0)" placeholder).
            string initialText = _varInitial.Text.Trim();
            Fixed initial = Fixed.Zero;
            if (initialText.Length > 0)
            {
                if (!float.TryParse(initialText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f))
                {
                    _varsStatus.Text = $"✘ '{initialText}' is not a number.";
                    return;
                }
                initial = Fixed.FromFloat(f);
            }

            // Story 7.6 — Array declarations: validate the element type + capacity fail-closed on Add (the
            // same rules the ScenarioValidator gate applies), and force the Global-only scope rule with feedback.
            DslValueType? elemType = null;
            int? capacity = null;
            if (type == DslValueType.Array)
            {
                if (scope != VarScope.Global)
                {
                    _varsStatus.Text = "\u2718 Arrays are Global-scope only (this story).";
                    return;
                }
                if (initial != Fixed.Zero)
                {
                    _varsStatus.Text = "\u2718 Arrays seed empty \u2014 leave 'initial' blank/0.";
                    return;
                }
                elemType = (int)Math.Max(0, _varElemType.Selected) switch
                {
                    1 => DslValueType.Fixed,
                    2 => DslValueType.Bool,
                    _ => DslValueType.Int,
                };
                string capText = _varCapacity.Text.Trim();
                if (!int.TryParse(capText, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int cap)
                    || cap < 1 || cap > DslBounds.MaxArrayCapacity)
                {
                    _varsStatus.Text = $"\u2718 Capacity must be an integer in 1..{DslBounds.MaxArrayCapacity} (DslBounds.MaxArrayCapacity).";
                    return;
                }
                capacity = cap;
            }

            var decl = new ScenarioVariable { Name = name, Type = type, Scope = scope, Initial = initial, ElementType = elemType, Capacity = capacity };
            _scenario.Variables = Append(_scenario.Variables ?? Array.Empty<ScenarioVariable>(), decl);
            _varName.Text = "";
            _varsStatus.Text = $"✔ Declared '{name}'.";
            RefreshVarsList();
            RefreshVarPickers();
        }

        private void RefreshVarsList()
        {
            if (_varsList == null) return;
            foreach (Node child in _varsList.GetChildren()) { _varsList.RemoveChild(child); child.QueueFree(); }
            var vars = _scenario?.Variables;
            if (vars == null || vars.Length == 0)
            {
                _varsList.AddChild(new Label { Text = "(none)" });
                return;
            }
            for (int i = 0; i < vars.Length; i++)
            {
                int idx = i;
                var v = vars[i];
                var row = new HBoxContainer();
                string typeText = v.Type == DslValueType.Array && v.ElementType is DslValueType et
                    ? $"Array<{et}>[{v.Capacity ?? 0}]"
                    : v.Type.ToString();
                row.AddChild(new Label { Text = $"{v.Name} : {typeText} / {v.Scope}", SizeFlagsHorizontal = Control.SizeFlags.Expand });
                var del = new Button { Text = "✘" };
                del.Pressed += () =>
                {
                    if (_scenario?.Variables == null) return;
                    var list = new List<ScenarioVariable>(_scenario.Variables);
                    if (idx < list.Count) list.RemoveAt(idx);
                    _scenario.Variables = list.Count == 0 ? null : list.ToArray();
                    RefreshVarsList();
                    RefreshVarPickers();
                };
                row.AddChild(del);
                _varsList.AddChild(row);
            }
        }

        /// <summary>Repopulate the variable-picker dropdowns in the manual form from the declared variables. The
        /// CONDITION picker lists Int-typed variables (variable_comparison stays Int-only) and EXCLUDES
        /// <see cref="VarScope.TriggerLocal"/> ones — a condition reads before the trigger-local scope is entered
        /// (it would read 0), so TriggerLocal is write-scratch only and stays available to the action picker.
        /// Story 7.4: the set_variable picker WIDENS to Int/Fixed/Bool-typed variables when a value expression is
        /// present (the properly-typed expression path); Int-only otherwise (the 7.3 literal path).</summary>
        private void RefreshVarPickers()
        {
            if (_manualCondVar == null || _manualActVar == null) return;
            // Review patch: this runs on EVERY value-expression keystroke (TextChanged), so preserve the user's
            // current picks across the repopulate and re-select by name when the item survives the refilter.
            string prevCond = SelectedText(_manualCondVar);
            string prevAct  = SelectedText(_manualActVar);
            _manualCondVar.Clear();
            _manualActVar.Clear();
            bool widened = _manualActValExpr != null && _manualActValExpr.Text.Trim().Length > 0;
            var vars = _scenario?.Variables;
            if (vars != null)
                foreach (var v in vars)
                {
                    // Review (7.4 pass 2): the WIDENED (expression) path excludes PerPlayer-scoped targets — the
                    // manual form has no player-slot picker and PersistManualExpression writes slot 0, so offering
                    // a PerPlayer variable would silently assign the wrong player. Per-player expression targets
                    // are authored via Raw IR (which carries the slot). The literal path keeps its legacy filter.
                    bool actEligible = widened
                        ? (v.Type is DslValueType.Int or DslValueType.Fixed or DslValueType.Bool)
                          && v.Scope != VarScope.PerPlayer
                        : v.Type == DslValueType.Int;
                    if (actEligible)
                        _manualActVar.AddItem(v.Name);                // set_variable: TriggerLocal allowed (write-scratch)
                    if (v.Type == DslValueType.Int && v.Scope != VarScope.TriggerLocal)
                        _manualCondVar.AddItem(v.Name);               // variable_comparison: exclude TriggerLocal (read)
                }
            SelectItemByText(_manualCondVar, prevCond);
            SelectItemByText(_manualActVar, prevAct);
        }

        /// <summary>Re-select the item whose text equals <paramref name="text"/>, if still present (no-op otherwise).</summary>
        private static void SelectItemByText(OptionButton opt, string text)
        {
            if (text.Length == 0) return;
            for (int i = 0; i < opt.ItemCount; i++)
                if (opt.GetItemText(i) == text) { opt.Select(i); return; }
        }

        // ── Story 7.3 — raw-IR escape hatch ────────────────────────────────────

        private void BuildRawIrSection(VBoxContainer root)
        {
            _rawIrSection = new VBoxContainer { Visible = false };
            root.AddChild(_rawIrSection);
            _rawIrSection.AddChild(new Label { Text = "Raw graph-IR JSON (canonical):" });
            // Story 7.6 — layered-complexity hint: loops, branches, and array actions are authored HERE this
            // story (T2/T3 sugar is Stories 7.10/7.13).
            _rawIrSection.AddChild(new Label
            {
                Text = "Loops (for_each / for_each_batched), branches, and array actions are authored via this raw-IR hatch for now.",
                AutowrapMode = TextServer.AutowrapMode.Word,
            });

            _rawIrInput = new TextEdit
            {
                CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 120f),
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                PlaceholderText = "{ \"nodes\": [...], \"exec_edges\": [...], \"data_edges\": [] }",
            };
            _rawIrSection.AddChild(_rawIrInput);

            var acceptBtn = new Button { Text = "✔ Validate & Apply IR" };
            acceptBtn.Pressed += OnRawIrAccept;
            _rawIrSection.AddChild(acceptBtn);

            _rawIrStatus = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.Word };
            _rawIrSection.AddChild(_rawIrStatus);
        }

        private void OnRawIrPressed()
        {
            if (_rawIrSection != null && !_rawIrSection.Visible && _scenario?.TriggerGraphJson != null)
                _rawIrInput.Text = _scenario.TriggerGraphJson; // show the current graph IR for editing
            ToggleSection(_rawIrSection!);
        }

        private void OnRawIrAccept()
        {
            if (_scenario == null) { _rawIrStatus.Text = "No scenario loaded."; return; }
            string json = _rawIrInput.Text.Trim();
            if (string.IsNullOrEmpty(json)) { _rawIrStatus.Text = "Paste graph-IR JSON first."; return; }
            try
            {
                // Fail-closed parse through the closed-registry converter (unknown kind / stray / duplicate property →
                // a LOCATED JsonException). Nothing is persisted unless the whole graph parses.
                TriggerGraph parsed = TriggerGraph.FromJson(json);
                _scenario.TriggerGraphJson = parsed.ToCanonicalJson();
                _rawIrStatus.Text = "✔ Applied. Graph-only triggers now run from trigger_graph.";
                RefreshList();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // NotSupportedException: System.Text.Json can surface it on hostile input; the hatch must stay
                // fail-closed with a located message rather than crash the editor.
                _rawIrStatus.Text = $"✘ Rejected (no trigger added): {ex.Message}";
            }
        }

        // ── Story 7.3 — small UI helpers ───────────────────────────────────────

        private static OptionButton MakeOption(string[] items)
        {
            var opt = new OptionButton();
            foreach (string s in items) opt.AddItem(s);
            return opt;
        }

        private static string SelectedText(OptionButton opt) =>
            opt.Selected >= 0 ? opt.GetItemText(opt.Selected) : "";

        private static void ToggleSection(VBoxContainer section) => section.Visible = !section.Visible;

        private void SetManualStatus(string text) { if (_manualStatus != null) _manualStatus.Text = text; }

        private static T[] Append<T>(T[] arr, T item)
        {
            var list = new List<T>(arr) { item };
            return list.ToArray();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Control GetAcceptRow() =>
            (Control)_preview.GetMeta("acceptRow").AsGodotObject();

        private static string GodotEscape(string s) =>
            s.Replace("[", "[[").Replace("]", "]]");

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass).</summary>
        private static void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
