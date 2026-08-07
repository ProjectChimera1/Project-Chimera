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

        // Story 8.2 — availability status: the AI Generate affordance is gated on the four-state evaluator; manual
        // ECA / variables / events authoring stays usable in every state. Config-derived (synchronous) on open.
        private ProjectChimera.AI.Providers.AiAvailabilityEvaluator? _aiEvaluator;
        private ProjectChimera.Core.Definitions.ISecretStore?        _aiSecretStore;
        private Label  _aiAvailLabel = null!;
        private Button _newAiBtn     = null!;

        // Story 7.10 — the T3 node-graph editor this panel's read-only "edit in graph view" fallback rows open
        // (wired by DslGraphEditorPhase after both panels are constructed). Null until wired.
        private DslGraphEditorPanel? _graphEditor;

        // ── UI nodes ──────────────────────────────────────────────────────────

        private CanvasLayer    _canvas      = null!;
        private PanelContainer _panel       = null!;
        private ScrollContainer _listScroll = null!;   // Story 7.15 — held so FocusTrigger can scroll a row into view
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
        // Story 7.5: "custom_event" is a manual EVENT option (declared-event picker) and "raise_event" a manual
        // ACTION option (event picker + next-tick toggle + per-param arg expression fields) — both route the
        // trigger through the graph channel (custom events are graph-channel-only; ToFlat fails closed on them).
        private static readonly string[] EventKinds     = { "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold", "custom_event" };
        private static readonly string[] ConditionKinds = { "(none)", "always", "building_exists", "resource_comparison", "unit_count", "variable_comparison", "unit_in_region", "expression" };
        private static readonly string[] ActionKinds    = { "display_message", "spawn_unit", "victory", "defeat", "create_timer", "add_resources", "set_variable", "play_sound", "run_effect", "raise_event" };
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

        // ── Story 7.5 — custom-events declaration section + manual raise/subscribe controls ──
        private VBoxContainer _eventsSection = null!;
        private VBoxContainer _eventsList    = null!;
        private LineEdit      _evtName       = null!;
        private LineEdit      _evtParams     = null!;   // compact "name:Type, name:Type" line
        private LineEdit      _evtRaisers    = null!;   // compact "0,1" allowed-raiser slots line
        private Label         _evtStatus     = null!;
        private OptionButton  _manualEventName  = null!; // declared-event picker (event kind custom_event)
        private OptionButton  _manualRaiseEvent = null!; // declared-event picker (action kind raise_event)
        private CheckBox      _manualRaiseNextTick = null!;
        private VBoxContainer _raiseArgsBox  = null!;    // per-declared-param arg expression fields
        private readonly List<LineEdit> _raiseArgEdits = new();

        // ── Public init ───────────────────────────────────────────────────────

        public void Initialize(
            ScenarioData? scenario,
            GameState gameState,
            LLMService llm,
            ScenarioContext context,
            ProjectChimera.AI.Providers.AiAvailabilityEvaluator? aiEvaluator = null,
            ProjectChimera.Core.Definitions.ISecretStore? aiSecretStore = null)
        {
            _scenario  = scenario;
            _gameState = gameState;
            _llm       = llm;
            _context   = context;
            _aiEvaluator   = aiEvaluator;
            _aiSecretStore = aiSecretStore;

            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false; // start hidden; shown by L key toggle
            RefreshAvailability();
        }

        /// <summary>Called when the scenario is reloaded (e.g. after Import or scene restart).</summary>
        public void SetScenario(ScenarioData? scenario)
        {
            // DW-10: SetScenario means "the scenario object CHANGED". A same-reference re-bind (the in-place
            // Edit↔Play re-apply) is a no-op so it can never discard unsaved editor state; only an actual object
            // swap rebinds + refreshes. Captures the same-null case too.
            if (ReferenceEquals(scenario, _scenario)) return;
            _scenario = scenario;
            RefreshList();
        }

        /// <summary>Story 7.10 — wire the T3 node-graph editor this panel's graph-only read-only fallback rows
        /// open (called by <c>DslGraphEditorPhase</c>).</summary>
        public void SetGraphEditor(DslGraphEditorPanel graphEditor) => _graphEditor = graphEditor;

        /// <summary>Called each _Process frame by MainScene to drain LLM callbacks.</summary>
        public void Update()
        {
            _llm?.DrainEvents();
        }

        /// <summary>Toggle panel visibility. Called from MainScene on L key.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) { RefreshList(); RefreshAvailability(); }
        }

        /// <summary>Story 8.2 — drive the AI-availability status line + Generate gating from the config-derived
        /// (synchronous) evaluator. In every unavailable state the four-state message shows and the AI Generate
        /// affordances disable, while manual ECA / variables / events authoring stays fully usable. No network call
        /// here (Test-connection lives in Settings); a null evaluator (older wiring) leaves AI enabled as before.</summary>
        private void RefreshAvailability()
        {
            if (_aiAvailLabel == null) return; // UI not built yet
            if (_aiEvaluator == null || _aiSecretStore == null)
            {
                _aiAvailLabel.Visible = false;
                return;
            }

            var settings = ProjectChimera.UI.SettingsManager.Instance?.Current
                           ?? new ProjectChimera.Core.Definitions.SettingsData();
            var state = _aiEvaluator.EvaluateConfig(settings, _aiSecretStore);
            bool available = state == ProjectChimera.AI.Providers.AiAvailability.Healthy;

            _aiAvailLabel.Visible = true;
            _aiAvailLabel.Text = available
                ? "AI: ready (config OK — Test connection in Settings to confirm)."
                : ProjectChimera.AI.Providers.AiAvailabilityMessages.Describe(state);
            _aiAvailLabel.Modulate = available ? new Color(0.6f, 0.9f, 0.6f) : new Color(0.95f, 0.8f, 0.45f);

            // Gate ONLY the AI affordances; manual authoring stays enabled.
            if (_newAiBtn != null) _newAiBtn.Disabled = !available;
            if (_genBtn != null)   _genBtn.Disabled   = !available;
        }

        /// <summary>
        /// Story 7.15 — click-to-navigate landing for the trigger-debug overlay. Ensure the panel is open + its list
        /// refreshed, then scroll to + tint the authored <c>Triggers[]</c> row at <paramref name="triggerIndex"/>.
        /// A map-miss (index out of range, or a graph-only / empty list) opens the editor UNFOCUSED rather than
        /// crashing. Presentation-only — no scenario mutation.
        /// </summary>
        public void FocusTrigger(int triggerIndex)
        {
            // Ensure the panel is open and its rows are freshly built (so child index i == Triggers[i]).
            if (!_panel.Visible) _panel.Visible = true;
            RefreshList();

            TriggerDefinition[]? triggers = _scenario?.Triggers;
            if (triggers == null || (uint)triggerIndex >= (uint)triggers.Length) return; // map-miss → unfocused, no crash
            if (triggerIndex >= _triggerList.GetChildCount()) return;                     // defensive (row not present)

            if (_triggerList.GetChild(triggerIndex) is not Control row) return;

            // Tint the target row (RefreshList rebuilt every row at default modulate, so only this one is highlighted).
            row.Modulate = new Color(1.0f, 0.92f, 0.45f); // amber highlight

            // Scroll the row into view once layout has settled (positions are 0 until the next layout pass).
            _listScroll.CallDeferred(ScrollContainer.MethodName.EnsureControlVisible, row);
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

            // Story 8.2 — AI-availability status line (four-state); hidden until Initialize wires the evaluator.
            _aiAvailLabel = new Label
            {
                Text = "",
                Visible = false,
                AutowrapMode = TextServer.AutowrapMode.Word,
                CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 0),
            };
            root.AddChild(_aiAvailLabel);

            root.AddChild(new HSeparator());

            // ── Trigger list ──────────────────────────────────────────────────
            _listScroll = new ScrollContainer();
            _listScroll.CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 200f);
            root.AddChild(_listScroll);

            _triggerList = new VBoxContainer();
            _listScroll.AddChild(_triggerList);

            // Story 7.3 — layered-complexity entry points: manual preset form, variables, and the raw-IR hatch,
            // alongside the AI path. Each button toggles its section (all start collapsed).
            var manualBtn = new Button { Text = "+ New Trigger (Manual)" };
            manualBtn.Pressed += () => { RefreshVarPickers(); RefreshEventPickers(); ToggleSection(_manualSection); };
            AttachTip(manualBtn, "Manual Trigger", "Author an ECA trigger from closed-vocabulary dropdowns — no AI needed.");
            root.AddChild(manualBtn);

            _newAiBtn = new Button { Text = "+ New Trigger (via AI)" };
            _newAiBtn.Pressed += OnNewTriggerPressed;
            AttachTip(_newAiBtn, "New Trigger", "Open the AI trigger generator — describe a rule in plain English and review it before accepting.");
            root.AddChild(_newAiBtn);

            var varsBtn = new Button { Text = "Variables…" };
            varsBtn.Pressed += () => { RefreshVarsList(); RefreshVarPickers(); ToggleSection(_varsSection); };
            AttachTip(varsBtn, "Variables", "Declare typed, scoped DSL variables (Global / Per-player / Trigger-local).");
            root.AddChild(varsBtn);

            var eventsBtn = new Button { Text = "Custom Events…" };
            eventsBtn.Pressed += () => { RefreshEventsList(); RefreshEventPickers(); ToggleSection(_eventsSection); };
            AttachTip(eventsBtn, "Custom Events", "Declare named custom events (typed params + allowed raisers) that triggers can raise and subscribe to.");
            root.AddChild(eventsBtn);

            var rawBtn = new Button { Text = "Raw IR (advanced)…" };
            rawBtn.Pressed += OnRawIrPressed;
            AttachTip(rawBtn, "Raw IR", "Paste canonical graph-IR JSON (the escape hatch). Validated fail-closed on Accept.");
            root.AddChild(rawBtn);

            root.AddChild(new HSeparator());

            BuildManualSection(root);
            BuildVarsSection(root);
            BuildEventsSection(root);
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

            // DW-899 (same defect, second site): an unwrapped status Label's minimum width is its full text width, so a
            // long AI error widens this panel without bound. This panel has no ✕ to push off-screen (it closes on L),
            // so nothing becomes unreachable here — but the panel still swallows the screen. Same fix, and ExpandFill
            // rather than Expand for the same reason (see the MapGeneratorPanel comment).
            _statusLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.Word };
            _statusLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
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
            bool anyFlat = triggers != null && triggers.Length > 0;

            if (!anyFlat)
            {
                // Story 7.10 — even with no FLAT triggers, the graph channel may hold graph-only constructs; render
                // their read-only fallback rows before deciding the list is empty.
                int graphRows = RefreshGraphOnlyFallbackRows();
                if (graphRows == 0)
                    _triggerList.AddChild(new Label
                    {
                        Text = "(no triggers — click '+ New Trigger' to add one)",
                        AutowrapMode = TextServer.AutowrapMode.Word
                    });
                return;
            }

            for (int i = 0; i < triggers!.Length; i++)
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

            // Story 7.10 — graph-only constructs (raise_event / custom_event / loop / branch / event-param) live in
            // the graph channel and are NOT editable as flat sentences: surface them read-only after the flat rows.
            RefreshGraphOnlyFallbackRows();
        }

        /// <summary>
        /// Story 7.10 — render a non-destructive read-only "edit in graph view" row for each graph-channel trigger
        /// when the graph channel contains a graph-only construct (<see cref="TriggerGraph.ContainsGraphOnly"/> —
        /// the same classification <c>ToFlat</c> fails closed on). T2 NEVER mutates these; the button opens the T3
        /// panel. Returns the number of rows rendered (0 when the channel is absent/unparseable/flat-only).
        /// </summary>
        private int RefreshGraphOnlyFallbackRows()
        {
            string? json = _scenario?.TriggerGraphJson;
            if (string.IsNullOrWhiteSpace(json)) return 0;

            TriggerGraph graph;
            try { graph = TriggerGraph.FromJson(json!); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // The channel holds content, but it cannot be parsed: an invisible row here would let T2 claim
                // "(no triggers)" while unloadable trigger content exists. One honest row instead.
                _triggerList.AddChild(new HSeparator());
                AddGraphOnlyRow("graph channel present but unparseable — it will be rejected at load (open graph view for details)");
                return 1;
            }
            if (graph.Nodes.Count == 0) return 0;
            if (!graph.ContainsGraphOnly())
            {
                // Graph-channel triggers with no graph-only construct (e.g. authored in T3 from flat-capable
                // kinds) are still invisible in the flat list — surface one summary row so T2 never claims
                // "(no triggers)" while the graph channel holds live ones.
                _triggerList.AddChild(new HSeparator());
                AddGraphOnlyRow($"{graph.Nodes.Count} graph-channel node(s) (edit in graph view)");
                return 1;
            }

            _triggerList.AddChild(new HSeparator());
            _triggerList.AddChild(new Label
            {
                Text = "Graph-only constructs (read-only — edit in graph view):",
                AutowrapMode = TextServer.AutowrapMode.Word,
                Modulate = new Color(0.72f, 0.72f, 0.78f),
            });

            // If the channel holds exactly one trigger, name it alongside each graph-only node (cheap, unambiguous);
            // otherwise the row carries just the node's kind + id (a precise, non-destructive locator). Only nodes
            // that are ACTUALLY graph-only get a row — flat-representable TriggerNodes/actions are NOT mislabeled.
            string? soleTrigger = null;
            int triggerCount = 0;
            foreach (NodeBase n in graph.Nodes)
                if (n is TriggerNode tn) { triggerCount++; soleTrigger = string.IsNullOrEmpty(tn.Name) ? $"trigger {tn.Id}" : tn.Name; }
            if (triggerCount != 1) soleTrigger = null;

            int count = 0;
            foreach (NodeBase n in graph.Nodes)
            {
                if (!TriggerGraph.IsGraphOnly(n)) continue;
                string desc = DescribeGraphOnly(n);
                AddGraphOnlyRow(soleTrigger != null ? $"{desc}  (in '{soleTrigger}')" : desc);
                count++;
            }
            return count;
        }

        /// <summary>A concise, non-destructive description of a graph-only node: its closed-registry kind + id,
        /// with the declared event/raise name when the node carries one.</summary>
        private static string DescribeGraphOnly(NodeBase n)
        {
            string extra = n switch
            {
                EventNode e when !string.IsNullOrEmpty(e.EventName)  => $" '{e.EventName}'",
                RaiseEventNode r when !string.IsNullOrEmpty(r.Name)  => $" '{r.Name}'",
                _                                                    => "",
            };
            return $"{NodeKinds.KindOf(n)}{extra} (id {n.Id})";
        }

        /// <summary>Append one read-only graph-only fallback row (dimmed label + "Edit in graph view" button).</summary>
        private void AddGraphOnlyRow(string label)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"⬡ {label}",
                SizeFlagsHorizontal = Control.SizeFlags.Expand,
                ClipText = true,
                Modulate = new Color(0.72f, 0.72f, 0.78f),
            });
            var editBtn = new Button { Text = "Edit in graph view" };
            // Hide this (higher-layer) T2 panel first: the T3 canvas sits on a lower CanvasLayer, so opening it
            // underneath a still-visible T2 panel would occlude the graph editor the user just asked for.
            editBtn.Pressed += () => { _panel.Visible = false; _graphEditor?.Open(); };
            AttachTip(editBtn, "Graph view",
                "Open the T3 node-graph editor to view/edit this graph-only construct (raise_event / custom_event / loop / branch). T2 never edits it.");
            row.AddChild(editBtn);
            _triggerList.AddChild(row);
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
            // Story 7.5 — the declared-event picker, revealed when the "custom_event" kind is picked.
            _manualEventName = new OptionButton { Visible = false };
            AttachTip(_manualEventName, "Custom event",
                "The declared custom event this trigger subscribes to (declare events in the Custom Events section).",
                ChimeraTooltip.TooltipRole.Field);
            _manualSection.AddChild(_manualEventName);
            _manualEvent.ItemSelected += _ =>
                _manualEventName.Visible = EventKinds[Math.Max(0, _manualEvent.Selected)] == "custom_event";

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
            {
                string kind = ActionKinds[Math.Max(0, _manualAction.Selected)];
                _manualActValExpr.Visible = kind == "set_variable";
                bool raise = kind == "raise_event";
                _manualRaiseEvent.Visible = raise;
                _manualRaiseNextTick.Visible = raise;
                _raiseArgsBox.Visible = raise;
                if (raise) RefreshRaiseArgFields();
            };
            _manualActValExpr.TextChanged += _ => RefreshVarPickers(); // widen/narrow the set_variable picker live
            // Story 7.5 — the raise_event controls (event picker, next-tick toggle, per-param arg expressions),
            // revealed when the "raise_event" action is picked. Args are typed expressions compiled fail-closed on Add.
            _manualRaiseEvent = new OptionButton { Visible = false };
            AttachTip(_manualRaiseEvent, "Raise event",
                "The declared custom event to raise (subscribed handler triggers fire within the same tick).",
                ChimeraTooltip.TooltipRole.Field);
            _manualSection.AddChild(_manualRaiseEvent);
            _manualRaiseEvent.ItemSelected += _ => RefreshRaiseArgFields();
            _manualRaiseNextTick = new CheckBox { Text = "Raise next tick (feedback-safe)", Visible = false };
            AttachTip(_manualRaiseNextTick, "Next tick",
                "Defers the raise to the next tick through the bounded event queue — the sanctioned way to author A→B→A feedback (same-tick cycles are rejected at load).",
                ChimeraTooltip.TooltipRole.Field);
            _manualSection.AddChild(_manualRaiseNextTick);
            _raiseArgsBox = new VBoxContainer { Visible = false };
            _manualSection.AddChild(_raiseArgsBox);
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
                // Story 7.4: the picker widens to Int/Fixed/Bool when a value expression is present — say so.
                // DW-345: BOTH paths exclude PerPlayer targets (the form has no slot picker and the flat
                // TriggerAction.Faction defaults to 0 — it would silently write player slot 0); Raw IR carries
                // the slot, so both messages name that hatch.
                SetManualStatus(_manualActValExpr.Text.Trim().Length > 0
                    ? "✘ set_variable needs a declared Int/Fixed/Bool variable (Global or TriggerLocal; PerPlayer targets via Raw IR) — add one in the Variables section first."
                    : "✘ set_variable needs a declared Int variable (Global or TriggerLocal; PerPlayer targets via Raw IR) — add one in the Variables section first.");
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

            // ── Story 7.5 — custom-event subscribe (event kind custom_event) and/or raise (action raise_event):
            //    graph-channel-only (BuildCustomEventTrigger + accumulating Merge). Exotic combos the manual form
            //    cannot represent route to Raw IR fail-closed — nothing partial ever persists. ──
            if (ev == "custom_event" || act == "raise_event")
            {
                if (ev == "custom_event" && string.IsNullOrEmpty(SelectedText(_manualEventName)))
                {
                    SetManualStatus("✘ Pick the declared custom event to subscribe to (declare one in the Custom Events section first).");
                    return;
                }
                if (act == "raise_event" && string.IsNullOrEmpty(SelectedText(_manualRaiseEvent)))
                {
                    SetManualStatus("✘ Pick the declared custom event to raise (declare one in the Custom Events section first).");
                    return;
                }
                if (act == "run_effect" || (ev == "custom_event" && act != "set_variable" && act != "raise_event"))
                {
                    SetManualStatus("✘ A custom-event trigger currently pairs with the set_variable or raise_event action — author other combinations via Raw IR.");
                    return;
                }
                if (cond != "(none)" && cond != "expression")
                {
                    SetManualStatus("✘ A custom-event trigger takes '(none)' or an 'expression' condition — author other condition kinds via Raw IR.");
                    return;
                }
                string evCondText = _manualCondExpr.Text.Trim();
                if (cond == "expression" && evCondText.Length == 0)
                {
                    SetManualStatus("✘ The expression condition needs expression text.");
                    return;
                }
                PersistManualCustomEvent(name, ev, cond == "expression" ? evCondText : null, act);
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

        /// <summary>Story 7.5 — persist a manual custom-event trigger (subscribe via <c>custom_event</c> and/or
        /// raise via <c>raise_event</c>) through the Godot-free <see cref="TriggerGraph.BuildCustomEventTrigger"/>
        /// helper (parse + compile + arity/type/raiser checks fail-closed; located <see cref="JsonException"/> on
        /// any violation) and the same graph-channel ACCUMULATION as <see cref="PersistManualRunEffect"/> (merge,
        /// canonicalize, raw-IR resync). Nothing persists on rejection — the error lands on the status label.</summary>
        private void PersistManualCustomEvent(string name, string eventKind, string? conditionExprText, string act)
        {
            if (_scenario == null) return;
            try
            {
                var declMap = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
                if (_scenario.Variables != null)
                    foreach (var v in _scenario.Variables)
                        if (!string.IsNullOrWhiteSpace(v.Name))
                            declMap[v.Name] = (v.Type, v.Scope);

                string? raiseName = null;
                List<string>? raiseArgs = null;
                bool nextTick = false;
                if (act == "raise_event")
                {
                    raiseName = SelectedText(_manualRaiseEvent);
                    nextTick  = _manualRaiseNextTick.ButtonPressed;
                    raiseArgs = new List<string>();
                    foreach (LineEdit argEdit in _raiseArgEdits)
                        raiseArgs.Add(argEdit.Text.Trim());
                }

                string? setVarName   = act == "set_variable" ? SelectedText(_manualActVar) : null;
                string  valText      = act == "set_variable" ? _manualActValExpr.Text.Trim() : "";
                string? valueExpr    = valText.Length > 0 ? valText : null;

                TriggerGraph added = TriggerGraph.BuildCustomEventTrigger(
                    name,
                    eventKind, eventKind == "custom_event" ? SelectedText(_manualEventName) : null,
                    conditionExprText,
                    raiseName, raiseArgs, raiser: -1, nextTick,
                    setVarName, 0, valueExpr,
                    declMap, _scenario.CustomEvents,
                    _manualEnabled.ButtonPressed, _manualRunOnce.ButtonPressed, (int)_manualActVal.Value);

                TriggerGraph graph = string.IsNullOrWhiteSpace(_scenario.TriggerGraphJson)
                    ? added
                    : MergeInto(TriggerGraph.FromJson(_scenario.TriggerGraphJson!), added);
                _scenario.TriggerGraphJson = graph.ToCanonicalJson();  // canonicalize on persist
                if (_rawIrSection != null && _rawIrSection.Visible)
                    _rawIrInput.Text = _scenario.TriggerGraphJson;     // keep an open raw-IR section in sync
                SetManualStatus($"✔ Added custom-event trigger '{name}' to the graph channel.");
                RefreshList();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                SetManualStatus($"✘ Rejected (no trigger added): {ex.Message}");
            }
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

            // DW-343 (decision 2026-07-30: lint at declaration): names the expression grammar owns are refused
            // ('true'/'false' silently parse as the Bool literal — the silently-diverging class) or declared with
            // a warning (built-in/'length'/'event' collisions, non-identifier spellings — loud-failing or
            // position-only, still fully usable from pickers/flat triggers/Raw IR). The LOAD gate stays permissive
            // so legacy scenarios carrying such names keep loading; this lint is authoring-surface only.
            ExprNameLintVerdict lint = ExprNameLint.CheckVariableName(name, out string lintMsg);
            if (lint == ExprNameLintVerdict.Reject)
            {
                _varsStatus.Text = $"✘ {lintMsg}";
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
            // DW-343: a Warn-classified name declares fine but carries the lint note (the variable works from
            // pickers/flat triggers/Raw IR; only some expression-text positions are shadowed).
            _varsStatus.Text = lint == ExprNameLintVerdict.Warn
                ? $"✔ Declared '{name}'. ⚠ {lintMsg}"
                : $"✔ Declared '{name}'.";
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
        /// present (the properly-typed expression path); Int-only otherwise (the 7.3 literal path). DW-345: BOTH
        /// set_variable paths exclude PerPlayer targets — the form has no player-slot picker and both persisted
        /// shapes write slot 0, so a PerPlayer pick would silently assign the wrong player (Raw IR carries the
        /// slot). Eligibility lives in the Godot-free <see cref="TriggerVarPickerPolicy"/> (Tier-1 tested).</summary>
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
                    if (TriggerVarPickerPolicy.SetVariableTargetEligible(v.Type, v.Scope, widened))
                        _manualActVar.AddItem(v.Name);                // set_variable: TriggerLocal allowed (write-scratch)
                    if (TriggerVarPickerPolicy.ConditionVariableEligible(v.Type, v.Scope))
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

        // ── Story 7.5 — custom-events declaration section (the vars-section clone) ──

        private void BuildEventsSection(VBoxContainer root)
        {
            _eventsSection = new VBoxContainer { Visible = false };
            root.AddChild(_eventsSection);
            _eventsSection.AddChild(new Label { Text = "Declared custom events:" });

            _eventsList = new VBoxContainer();
            _eventsSection.AddChild(_eventsList);

            _evtName = new LineEdit { PlaceholderText = "event name (e.g. wave_start)" };
            _eventsSection.AddChild(_evtName);
            _evtParams = new LineEdit { PlaceholderText = "params (e.g. count:Int, rate:Fixed) — empty = none" };
            AttachTip(_evtParams, "Event params",
                "Comma-separated name:Type pairs (Int / Fixed / Bool). Handlers read them as event.<name>.",
                ChimeraTooltip.TooltipRole.Field);
            _eventsSection.AddChild(_evtParams);
            _evtRaisers = new LineEdit { PlaceholderText = "allowed raiser slots (e.g. 0,1) — empty = system only" };
            AttachTip(_evtRaisers, "Allowed raisers",
                "Faction slots allowed to raise this event. A trigger's authored raiser must be -1 (system) or one of these.",
                ChimeraTooltip.TooltipRole.Field);
            _eventsSection.AddChild(_evtRaisers);

            var addBtn = new Button { Text = "Add event" };
            addBtn.Pressed += OnEventAddPressed;
            _eventsSection.AddChild(addBtn);

            _evtStatus = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.Word };
            _eventsSection.AddChild(_evtStatus);
        }

        private void OnEventAddPressed()
        {
            if (_scenario == null) return;

            // Fail-closed at authoring time (the pre-tick gate re-checks everything): dup names, built-in
            // shadowing, malformed param/raiser text — refuse with feedback, never a partial persist. All of those
            // rules (and the raiser-slot ceiling, which must equal the load-time gate's FactionRegistry.PLAYER_COUNT
            // — DW-384: this used to hardcode (int)Faction.Player4 and refuse slots 4–7 the engine accepts) live in
            // the Godot-free, Tier-1-tested CustomEventAuthoringGate; the panel only reads text and reports.
            if (!CustomEventAuthoringGate.TryDeclare(
                    _scenario.CustomEvents, _evtName.Text, _evtParams.Text, _evtRaisers.Text,
                    out ScenarioCustomEvent[]? candidate, out string? err))
            {
                _evtStatus.Text = $"✘ {err}";
                return;
            }

            _scenario.CustomEvents = candidate;
            string name = _evtName.Text.Trim();
            _evtName.Text = "";
            _evtStatus.Text = $"✔ Declared '{name}'.";
            RefreshEventsList();
            RefreshEventPickers();
        }

        private void RefreshEventsList()
        {
            if (_eventsList == null) return;
            foreach (Node child in _eventsList.GetChildren()) { _eventsList.RemoveChild(child); child.QueueFree(); }
            var events = _scenario?.CustomEvents;
            if (events == null || events.Length == 0)
            {
                _eventsList.AddChild(new Label { Text = "(none)" });
                return;
            }
            for (int i = 0; i < events.Length; i++)
            {
                int idx = i;
                var ev = events[i];
                string paramsDesc = ev.Params is { Length: > 0 }
                    ? string.Join(", ", Array.ConvertAll(ev.Params, p => $"{p.Name}:{p.Type}"))
                    : "no params";
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = $"{ev.Name} ({paramsDesc})", SizeFlagsHorizontal = Control.SizeFlags.Expand, ClipText = true });
                var del = new Button { Text = "✘" };
                del.Pressed += () =>
                {
                    if (_scenario?.CustomEvents == null) return;
                    var list = new List<ScenarioCustomEvent>(_scenario.CustomEvents);
                    if (idx < list.Count) list.RemoveAt(idx);
                    _scenario.CustomEvents = list.Count == 0 ? null : list.ToArray();
                    RefreshEventsList();
                    RefreshEventPickers();
                };
                row.AddChild(del);
                _eventsList.AddChild(row);
            }
        }

        /// <summary>Repopulate the declared-event pickers (subscribe + raise) from the scenario's registry,
        /// preserving the current picks by name (the RefreshVarPickers discipline).</summary>
        private void RefreshEventPickers()
        {
            if (_manualEventName == null || _manualRaiseEvent == null) return;
            string prevSub   = SelectedText(_manualEventName);
            string prevRaise = SelectedText(_manualRaiseEvent);
            _manualEventName.Clear();
            _manualRaiseEvent.Clear();
            var events = _scenario?.CustomEvents;
            if (events != null)
                foreach (var ev in events)
                {
                    if (string.IsNullOrEmpty(ev?.Name)) continue;
                    _manualEventName.AddItem(ev.Name);
                    _manualRaiseEvent.AddItem(ev.Name);
                }
            SelectItemByText(_manualEventName, prevSub);
            SelectItemByText(_manualRaiseEvent, prevRaise);
            RefreshRaiseArgFields();
        }

        /// <summary>Rebuild the per-declared-param raise-argument expression fields for the currently picked raise
        /// event (one labeled LineEdit per param, preserving text by param name across refreshes).</summary>
        private void RefreshRaiseArgFields()
        {
            if (_raiseArgsBox == null) return;
            var prevText = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (LineEdit edit in _raiseArgEdits)
                if (edit.HasMeta("param")) prevText[(string)edit.GetMeta("param")] = edit.Text;
            foreach (Node child in _raiseArgsBox.GetChildren()) { _raiseArgsBox.RemoveChild(child); child.QueueFree(); }
            _raiseArgEdits.Clear();

            string evName = SelectedText(_manualRaiseEvent);
            if (string.IsNullOrEmpty(evName) || _scenario?.CustomEvents == null) return;
            ScenarioCustomEvent? ev = null;
            foreach (var candidate in _scenario.CustomEvents)
                if (candidate?.Name == evName) { ev = candidate; break; }
            if (ev?.Params == null || ev.Params.Length == 0) return;

            foreach (ScenarioEventParam p in ev.Params)
            {
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = $"{p.Name} ({p.Type}):" });
                var edit = new LineEdit
                {
                    PlaceholderText = $"expression, e.g. gold + 1",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                edit.SetMeta("param", p.Name);
                if (prevText.TryGetValue(p.Name, out string? kept)) edit.Text = kept;
                row.AddChild(edit);
                _raiseArgsBox.AddChild(row);
                _raiseArgEdits.Add(edit);
            }
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
