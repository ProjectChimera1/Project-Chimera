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
    /// Toggle with the M key (in Edit mode). Four sections:
    ///   • Scenario type — DW-371: the ScenarioTypeRegistry picker. Selecting a type writes that type's TRUSTED
    ///     map clamps (min player slots, max pre-placed combat units per slot, per-slot faction-path resolution)
    ///     onto the MapGeneratorContext, so non-RTS scenarios stop being wrongly rejected by the RTS gate.
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
        // DW-371 added the scenario-type row + its wrapping clamp hint above the brief; the panel grows to keep the
        // Load/Save action row on screen at the previous content height.
        private const float PANEL_H = 600f;
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
        // DW-371 — scenario-type picker + its clamp hint. Every decision behind them lives in the Godot-free
        // ScenarioTypeRegistry (Tier-1 tested); these two nodes are a thin shell over that table.
        private OptionButton   _typeOpt    = null!;
        private Label          _typeHint   = null!;
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
            SyncScenarioTypeUi();
            RefreshAvailability();
        }

        /// <summary>DW-371 — point the picker at the context's CURRENT scenario type without mutating the context.
        /// The boot phase seeds a fresh context (ScenarioType.Rts, RTS clamps); a caller that pre-applied a type,
        /// or hand-set clamps, keeps exactly what it configured — selecting an item in the picker is the only thing
        /// that rewrites the clamps.</summary>
        private void SyncScenarioTypeUi()
        {
            if (_typeOpt == null) return; // UI not built yet

            var type = _context.ScenarioType;
            for (int i = 0; i < _typeOpt.ItemCount; i++)
                if (_typeOpt.GetItemId(i) == (int)type)
                {
                    _typeOpt.Selected = i;
                    break;
                }

            _typeHint.Text = ScenarioTypeRegistry.TryGet(type, out _)
                ? ScenarioTypeRegistry.DescribeClamps(type)
                : "";
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

        /// <summary>Hide the panel. The ✕ button and Esc route here; the title screen observes
        /// <see cref="PanelRoot"/>'s visibility to re-show itself when this was opened from the menu.</summary>
        public void Close() => _panel.Visible = false;

        /// <summary>The panel root, so a caller can observe open/close via <c>Control.VisibilityChanged</c>
        /// (this class is a plain <see cref="Node"/>, so it has no visibility signal of its own).</summary>
        public Control PanelRoot => _panel;

        /// <summary>
        /// Esc closes the panel — and when the brief field has focus, the FIRST press just releases that focus
        /// (stop typing), the second closes. That two-step is what every OS dialog does, and it is what makes
        /// "click into the field, then leave" possible without reaching for the mouse. Lives in
        /// <c>_UnhandledInput</c> so a focused control always gets first refusal on the key.
        /// </summary>
        public override void _UnhandledInput(InputEvent @event)
        {
            if (_panel == null || !_panel.Visible) return;
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
            if (key.Keycode != Key.Escape) return;

            if (_briefInput != null && _briefInput.HasFocus()) _briefInput.ReleaseFocus();
            else Close();
            GetViewport().SetInputAsHandled();
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
            // A real close affordance, not just the M hint. M is an _UnhandledInput hotkey, so once the brief
            // field below has focus it correctly types "m" instead of toggling (TextFocusGuard's rule) — which
            // left this panel with NO reachable exit and its own header telling the player a lie. Reported by
            // Alec 2026-08-04: "I click inside the map generator text field, I can't click out of it and close
            // it and it stays in the way."
            var headerRow = new HBoxContainer();
            headerRow.AddChild(new Label
            {
                Text = "Map Generator",
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            });
            var closeBtn = new Button { Text = "✕", TooltipText = "Close (Esc)" };
            closeBtn.Pressed += Close;
            headerRow.AddChild(closeBtn);
            root.AddChild(headerRow);

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

            // ── Scenario type (DW-371) ────────────────────────────────────────
            // The picker populates the TRUSTED MapGeneratorContext clamps from ScenarioTypeRegistry — the seam
            // Story 8.3 parameterized but left without a caller, so every generated map was gated by RTS clamps.
            var typeRow = new HBoxContainer();
            root.AddChild(typeRow);
            typeRow.AddChild(new Label { Text = "Scenario type:" });

            _typeOpt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            foreach (var preset in ScenarioTypeRegistry.All)
                _typeOpt.AddItem(preset.DisplayName, (int)preset.Type);
            _typeOpt.Selected = 0; // RTS — the registry's declaration order puts the default first.
            _typeOpt.ItemSelected += OnScenarioTypeSelected;
            AttachTip(_typeOpt, "Scenario type",
                "Chooses which map rules the generated scenario is checked against — how many player slots it must " +
                "have, how many pre-placed combat units each slot may keep, and how slots map to factions. " +
                "RTS skirmish is the classic 1v1 gate.");
            typeRow.AddChild(_typeOpt);

            _typeHint = new Label
            {
                Text = ScenarioTypeRegistry.DescribeClamps(ScenarioTypeRegistry.DefaultType),
                AutowrapMode = TextServer.AutowrapMode.Word,
                CustomMinimumSize = new Vector2(PANEL_W - MARGIN * 2, 0),
            };
            _typeHint.Modulate = new Color(0.75f, 0.78f, 0.85f);
            root.AddChild(_typeHint);

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

            // DW-899: this label MUST wrap. Without AutowrapMode a Label's minimum width is its full single-line text
            // width, and a failed generation writes a 250-350 char error here (an "Invalid JSON: … — line N:" from
            // LLMService quotes up to 160 chars of the offending line). That minimum propagates genRow -> root ->
            // _panel, and because _panel is anchored CenterLeft with a pinned left edge, ALL the growth goes RIGHTWARD
            // until the header's ✕ — which sits flush against the right content edge — is rendered outside the 1920px
            // viewport and can no longer be clicked. Reported as "close button doesn't work after it fails to generate";
            // Ctrl+M still worked because that path is keyboard-only and geometry-independent. Nothing about the close
            // button, its handler, or any visibility flag differs before/after the failure — only this text does.
            //
            // ExpandFill, not Expand, is load-bearing: an autowrapping Label reports a minimum width of ~1, and under
            // SIZE_EXPAND WITHOUT SIZE_FILL an HBoxContainer lays a child out at its MINIMUM size, so autowrap alone
            // would collapse the label to a 1px column wrapping one character per line. Fill stretches it to the slack
            // it was allocated, which is what makes the wrap correct.
            _statusLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.Word };
            _statusLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
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

        /// <summary>DW-371 — the picker's only job: push the selected type's TRUSTED preset onto the live
        /// MapGeneratorContext (min player slots, max pre-placed combat units per slot, per-slot faction-path
        /// resolver) and echo the resulting clamps. The decision itself is the Godot-free
        /// <see cref="ScenarioTypeRegistry.Apply"/>; re-selecting RTS restores the historical clamps exactly, so
        /// toggling can never leave a stale relaxed clamp behind.</summary>
        private void OnScenarioTypeSelected(long index)
        {
            var type = (ScenarioType)_typeOpt.GetItemId((int)index);
            ScenarioTypeRegistry.Apply(_context, type);
            _typeHint.Text = ScenarioTypeRegistry.DescribeClamps(type);
        }

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
