#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
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
            MapGeneratorContext context)
        {
            _gameState = gameState;
            _llm       = llm;
            _context   = context;

            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false;
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
            root.AddChild(_briefInput);

            // ── Generate row ──────────────────────────────────────────────────
            var genRow = new HBoxContainer();
            root.AddChild(genRow);

            _genBtn = new Button { Text = "Generate ✦" };
            _genBtn.Pressed += OnGeneratePressed;
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
            _actionRow.AddChild(loadBtn);

            var saveBtn = new Button { Text = "💾 Save & Load" };
            saveBtn.Pressed += OnSaveAndLoadPressed;
            _actionRow.AddChild(saveBtn);

            var discardBtn = new Button { Text = "✘ Discard" };
            discardBtn.Pressed += OnDiscardPressed;
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
    }
}
