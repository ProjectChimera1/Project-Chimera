#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
using ProjectChimera.UI.Components;   // ChimeraTooltip (Story 5.9 tooltip-gap closure)
using System;
using System.Text.Json;

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Edit-mode AI map generation panel (Phase 5).
    ///
    /// Toggle with the M key (in Edit mode). Three sections:
    ///   • Brief — natural language map description.
    ///   • Generate — calls LLMService.GenerateScenarioAsync; shows map stats on success.
    ///   • Load / Save — Load applies the scenario immediately (no disk write);
    ///     Save persists to res://resources/data/scenarios/ai_generated.json first.
    ///
    /// Fires OnLoadRequested(ScenarioData) which MainScene handles by calling
    /// LoadGeneratedScenario() to swap the active scenario without a disk write.
    /// </summary>
    public partial class MapGeneratorPanel : Node
    {
        // ── Panel dimensions ──────────────────────────────────────────────────

        private const float PANEL_W = 400f;
        private const float PANEL_H = 520f;
        private const float MARGIN  = 12f;

        // ── Dependencies ──────────────────────────────────────────────────────

        private GameState?           _gameState;
        private LLMService?          _llm;
        private MapGeneratorContext  _context = new();

        // Story 8.2 — availability status: the AI Generate affordance is gated on the four-state evaluator; the
        // surrounding editor stays usable in every state. Config-derived (synchronous) on open.
        private ProjectChimera.AI.Providers.AiAvailabilityEvaluator? _aiEvaluator;
        private ProjectChimera.Core.Definitions.ISecretStore?        _aiSecretStore;
        private Label _aiAvailLabel = null!;

        // ── UI nodes ──────────────────────────────────────────────────────────

        private CanvasLayer    _canvas     = null!;
        private PanelContainer _panel      = null!;
        private TextEdit       _briefInput = null!;
        private Button         _genBtn     = null!;
        private Label          _statusLabel = null!;
        private VBoxContainer  _statsBox   = null!;
        private Label          _statsLabel  = null!;
        private HBoxContainer  _actionRow  = null!;

        // ── State ─────────────────────────────────────────────────────────────

        private ScenarioData? _pendingScenario;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired when the user clicks Load. MainScene applies the scenario in memory.</summary>
        public event Action<ScenarioData>? OnLoadRequested;

        // ── Public init ───────────────────────────────────────────────────────

        public void Initialize(
            GameState gameState,
            LLMService llm,
            MapGeneratorContext context,
            ProjectChimera.AI.Providers.AiAvailabilityEvaluator? aiEvaluator = null,
            ProjectChimera.Core.Definitions.ISecretStore? aiSecretStore = null)
        {
            _gameState = gameState;
            _llm       = llm;
            _context   = context;
            _aiEvaluator   = aiEvaluator;
            _aiSecretStore = aiSecretStore;

            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false;
            RefreshAvailability();
        }

        /// <summary>Story 8.2 — drive the AI-availability status line + Generate gating from the config-derived
        /// (synchronous) evaluator. In every unavailable state the four-state message shows and Generate disables,
        /// while the surrounding editor stays usable. A null evaluator (older wiring) leaves AI enabled as before.</summary>
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

            if (_genBtn != null) _genBtn.Disabled = !available;
        }

        /// <summary>Called each _Process frame by MainScene to drain LLM callbacks.</summary>
        public void Update()
        {
            _llm?.DrainEvents();
        }

        /// <summary>Toggle panel visibility. Called from MainScene on M key.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) RefreshAvailability();
        }

        // ── _Ready ────────────────────────────────────────────────────────────

        public override void _Ready()
        {
            BuildUi();
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 13 };
            AddChild(_canvas);

            // Anchor panel to the left side, vertically centered.
            _panel = new PanelContainer();
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Position = new Vector2(MARGIN, -PANEL_H * 0.5f);
            _canvas.AddChild(_panel);

            var root = new VBoxContainer { Theme = new Theme() };
            root.AddThemeConstantOverride("separation", 8);
            _panel.AddChild(root);

            // ── Header ────────────────────────────────────────────────────────
            var header = new Label
            {
                Text = "Map Generator  (M to close)",
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

            // ── Brief input ───────────────────────────────────────────────────
            root.AddChild(new Label { Text = "Describe your map concept:" });

            _briefInput = new TextEdit
            {
                PlaceholderText =
                    "e.g. \"A narrow canyon with a rich ore node in the center. " +
                    "Both players start with extra resources and a pre-built Barracks.\"",
                CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 100f),
                WrapMode = TextEdit.LineWrappingMode.Boundary
            };
            AttachTip(_briefInput, "Map brief", "Describe the map in plain English — the AI turns this into a full scenario (terrain, resources, starting units).");
            root.AddChild(_briefInput);

            // ── Generate row ──────────────────────────────────────────────────
            var genRow = new HBoxContainer();
            root.AddChild(genRow);

            _genBtn = new Button { Text = "Generate ✦" };
            _genBtn.Pressed += OnGeneratePressed;
            AttachTip(_genBtn, "Generate", "Send the brief to the AI and generate a candidate scenario to preview below.");
            genRow.AddChild(_genBtn);

            _statusLabel = new Label { Text = "" };
            _statusLabel.SizeFlagsHorizontal = Control.SizeFlags.Expand;
            genRow.AddChild(_statusLabel);

            root.AddChild(new HSeparator());

            // ── Stats preview ─────────────────────────────────────────────────
            _statsBox = new VBoxContainer { Visible = false };
            root.AddChild(_statsBox);

            _statsBox.AddChild(new Label { Text = "Preview:" });

            _statsLabel = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.Word,
                CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 0)
            };
            _statsBox.AddChild(_statsLabel);

            _statsBox.AddChild(new HSeparator());

            // ── Action row ────────────────────────────────────────────────────
            _actionRow = new HBoxContainer { Visible = false };
            root.AddChild(_actionRow);

            var loadBtn = new Button { Text = "↗ Load (no save)" };
            loadBtn.Pressed += OnLoadPressed;
            AttachTip(loadBtn, "Load", "Apply the generated scenario immediately, without writing it to disk.");
            _actionRow.AddChild(loadBtn);

            var saveBtn = new Button { Text = "💾 Save & Load" };
            saveBtn.Pressed += OnSaveAndLoadPressed;
            AttachTip(saveBtn, "Save & Load", "Write the generated scenario to res://resources/data/scenarios/ai_generated.json, then apply it.");
            _actionRow.AddChild(saveBtn);

            var discardBtn = new Button { Text = "✘ Discard" };
            discardBtn.Pressed += OnDiscardPressed;
            AttachTip(discardBtn, "Discard", "Drop the generated scenario preview without applying it.");
            _actionRow.AddChild(discardBtn);
        }

        // ── Button callbacks ──────────────────────────────────────────────────

        private void OnGeneratePressed()
        {
            if (_llm == null)
            {
                _statusLabel.Text = "LLM service not configured.";
                return;
            }

            string desc = _briefInput.Text.Trim();
            if (string.IsNullOrEmpty(desc))
            {
                _statusLabel.Text = "Please describe your map first.";
                return;
            }

            _genBtn.Disabled = true;
            _statusLabel.Text = "Generating…";
            _statsBox.Visible = false;
            _actionRow.Visible = false;
            _pendingScenario = null;

            _llm.GenerateScenarioAsync(desc, _context, OnGenerationComplete);
        }

        private void OnGenerationComplete(ScenarioData? scenario, string? error)
        {
            _genBtn.Disabled = false;

            if (scenario == null)
            {
                _statusLabel.Text = $"✘ {error}";
                return;
            }

            _pendingScenario  = scenario;
            _statusLabel.Text = "✔ Review below, then Load or Save.";

            int unitCount     = scenario.Units.Length;
            int nodeCount     = scenario.ResourceNodes.Length;
            int buildingCount = scenario.Buildings.Length;

            _statsLabel.Text =
                $"Name:       {scenario.DisplayName}\n" +
                $"Win:        {scenario.WinCondition}\n" +
                $"Bounds:     ±{scenario.MapBounds}u\n" +
                $"Ore nodes:  {nodeCount}\n" +
                $"Buildings:  {buildingCount}\n" +
                $"Units:      {unitCount}  " +
                $"(P1: {CountSlot(scenario, 0)}, P2: {CountSlot(scenario, 1)})";

            _statsBox.Visible  = true;
            _actionRow.Visible = true;
        }

        private void OnLoadPressed()
        {
            if (_pendingScenario == null) return;
            OnLoadRequested?.Invoke(_pendingScenario);
        }

        private void OnSaveAndLoadPressed()
        {
            if (_pendingScenario == null) return;

            // Story 7.7 — the AI-generated save routes through the SAME hard MapWriteGate as Export/New-Map
            // (DW-164 pattern): a model-invalid generated map is never persisted; the located error surfaces here
            // and the load below is skipped (the boot gate would reject it anyway — this keeps bad bytes off disk).
            string? gateErr = MapWriteGate.Check(_pendingScenario);
            if (gateErr != null)
            {
                _statusLabel.Text = $"✘ Invalid map — not saved: {gateErr}";
                return;
            }

            try
            {
                string resPath = "res://resources/data/scenarios/ai_generated.json";
                string absPath = ProjectSettings.GlobalizePath(resPath);
                ScenarioSerializer.SaveToFile(_pendingScenario, absPath);
                GD.Print($"[MapGenerator] Saved to {absPath}");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Save failed: {ex.Message}";
                return;
            }

            OnLoadRequested?.Invoke(_pendingScenario);
        }

        private void OnDiscardPressed()
        {
            _pendingScenario   = null;
            _statsBox.Visible  = false;
            _actionRow.Visible = false;
            _statusLabel.Text  = "";
        }

        // ── Visibility handling ───────────────────────────────────────────────

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play)
                _panel.Visible = false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int CountSlot(ScenarioData s, int slot)
        {
            int n = 0;
            foreach (var u in s.Units)
                if (u.Slot == slot) n++;
            return n;
        }

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass).</summary>
        private static void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
