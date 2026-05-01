#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
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

            // Anchor panel to the right side, vertically centered.
            _panel = new PanelContainer();
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Position = new Vector2(-(PANEL_W + MARGIN), -PANEL_H * 0.5f);
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

            var newBtn = new Button { Text = "+ New Trigger (via AI)" };
            newBtn.Pressed += OnNewTriggerPressed;
            root.AddChild(newBtn);

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
            _genSection.AddChild(_nlInput);

            var genRow = new HBoxContainer();
            _genSection.AddChild(genRow);

            _genBtn = new Button { Text = "Generate ✦" };
            _genBtn.Pressed += OnGeneratePressed;
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
            acceptRow.AddChild(_acceptBtn);

            _discardBtn = new Button { Text = "✘ Discard" };
            _discardBtn.Pressed += OnDiscardPressed;
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

            string prettyJson = JsonSerializer.Serialize(trigger,
                new JsonSerializerOptions { WriteIndented = true });

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

        // ── Helpers ───────────────────────────────────────────────────────────

        private Control GetAcceptRow() =>
            (Control)_preview.GetMeta("acceptRow").AsGodotObject();

        private static string GodotEscape(string s) =>
            s.Replace("[", "[[").Replace("]", "]]");
    }
}
