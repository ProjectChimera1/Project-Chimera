#nullable enable
using System.Collections.Generic;
using System.Linq;
using Godot;
using ProjectChimera.AI;                  // LLMService, UnitDraftContext (Story 8.4)
using ProjectChimera.AI.Providers;         // AiAvailabilityEvaluator/Messages (four-state)
using ProjectChimera.Core.Definitions;   // UnitDefinition, FactionDefinition, AbilityRegistry, UnitCardText, UnitDefinitionValidator, ISecretStore
using ProjectChimera.UI;                  // GameState, GameMode, MeshLoader
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraTabs, ChimeraTooltip, ChimeraValidationBadge, ChimeraSpinner
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 3.3/3.4 — the Unit Card Editor (UX-DR77). Story 3.3 shipped the read-only card; <b>3.4 makes that same
    /// panel editable in place</b> (D-2): the readouts become <see cref="ChimeraComponents"/> inputs bound to the live
    /// <see cref="UnitDefinition"/>, and the panel gains Save / New / Duplicate / Delete, a Simple/Advanced disclosure
    /// with a raw-JSON escape hatch, fail-closed inline validation with per-field located badges (UX-DR55), undo/redo,
    /// and write-back to the faction JSON on disk. It reuses the 3.3 browse (<c>_faction.Units</c> + <c>_index</c>), the
    /// 3D preview, the kit bootstrap, and the read-only header — it does not clone them (see 3.4 story, D-2).
    ///
    /// <para><b>This file</b> is the shell + the preserved 3.3 surfaces (kit bootstrap, browse, 3D preview, read-only
    /// header, tooltips). The 3.4 edit surface — editable fields, disclosure, raw-JSON hatch, validation, persistence,
    /// undo/redo, toolbar, input — lives in the partial <see cref="UnitCardPanel"/> file <c>UnitCardPanel.Edit.cs</c>.</para>
    ///
    /// <para><b>Determinism posture — PURE AUTHORING-TIME, zero fold.</b> Editing a content POCO and rewriting a JSON
    /// file touches no <c>EntityWorld</c>/store/sim array and moves no checksum or golden (<c>CanonicalModelHash</c>
    /// folds ScenarioData by path-string + unit-id, never by unit stats). The only <c>src/Core</c> touches are the new
    /// Godot-free <see cref="UnitDefinitionValidator"/> + <see cref="FactionWriter"/>. Stamps stay 9/3/1/2 + StartStateHash 1.</para>
    /// </summary>
    public partial class UnitCardPanel : Node
    {
        // ── Layout constants (component-intrinsic dims; the spacing/color TOKENS are read from the theme) ──
        private const float PANEL_W = 480f;
        private const float PANEL_H = 700f;
        private const float MARGIN  = 12f;
        private const int   PREVIEW_W = 240;
        private const int   PREVIEW_H = 180;
        private const float TURNTABLE_SPEED = 30f; // deg/sec (AssetPreviewScene value, D-8)

        // ── Kit context ──
        private GodotTheme        _theme  = null!;

        // ── Deps (wired by UnitCardPhase after AddChild) ──
        private FactionDefinition? _faction;               // the unit source (Units only — D-10)
        private GameState?         _gameState;
        private AbilityRegistry    _registry = AbilityRegistry.Empty;
        private BehaviorRegistry   _behaviorRegistry = BehaviorRegistry.Empty;   // Story 3.6 — the behavior picker + compat source
        private int                _index;                 // browse cursor into _faction.Units

        // ── Story 8.4 — AI draft affordance (unit + hero via a toggle; null deps hide the row) ──
        private LLMService?              _llm;
        private AiAvailabilityEvaluator? _aiEvaluator;
        private ISecretStore?            _aiSecretStore;
        private VBoxContainer  _aiCard        = null!;
        private TextEdit       _aiPromptInput = null!;
        private CheckBox       _aiHeroToggle  = null!;
        private Godot.Button   _aiGenBtn      = null!;
        private Label          _aiAvailLabel  = null!;
        private Label          _aiStatusLabel = null!;
        private ChimeraSpinner _aiSpinner     = null!;
        private Label          _aiSpinnerText = null!;

        // ── Story 8.5 — AI balance-analysis affordance ──
        private VBoxContainer  _balanceCard        = null!;
        private TextEdit       _balancePromptInput = null!;
        private Godot.Button   _balanceGenBtn      = null!;
        private Label          _balanceAvailLabel  = null!;
        private Label          _balanceStatusLabel = null!;
        private ChimeraSpinner _balanceSpinner     = null!;
        private Label          _balanceSpinnerText = null!;
        private VBoxContainer  _balanceRowsHost    = null!;   // suggestion rows (cleared + repopulated per analysis)

        private string             _factionJsonPath = "";  // res:// path of the faction file to write edits back to (D-8)

        // ── Edit state (Story 3.4) ──
        private UnitDefinition?    _current;               // the unit currently bound/edited (== _faction.Units[_index])
        private string             _originalId = "";       // the bound unit's id at Bind time — the PatchFactionJson target (survives an id rename)
        private readonly EditorHistory            _history   = new();   // own instance (D-6); reused by Ctrl+Z/Y when visible
        private readonly UnitDefinitionValidator  _validator = new();   // the Godot-free AR-39 gate (D-9)
        // JSON key → located badge(s) (UX-DR55). A List, not a single badge, because Story 5.9 added a second
        // Simple-mode row for a field (Ultimate ability) that already had an Advanced-mode row under the SAME
        // key — a single-value map let the second MakeBadge call silently overwrite the first, so the Simple row
        // never reflected a validation error even while visible. ShowBadge now fans an error out to every badge
        // registered under that key.
        private readonly Dictionary<string, List<ChimeraValidationBadge>> _badges = new();
        private bool _building;                            // guard: suppress live handlers while (re)building controls
        private LineEdit? _meshPathInput;                  // the Model row's text field (Story 3.5 — Browse/Box write .Text here)
        private bool _lastMeshMissing;                     // last UpdatePreview fell back to the box for a NON-blank path (missing OR failed-to-load — D-3)

        // ── Shell ──
        private CanvasLayer    _canvas = null!;
        private PanelContainer _panel  = null!;
        private VBoxContainer  _headerHost = null!;        // read-only header (refilled per unit)
        private VBoxContainer  _bodyHost   = null!;        // editable fields (refilled per unit)
        private Label          _counterLabel = null!;
        private Godot.Button   _prevBtn = null!;
        private Godot.Button   _nextBtn = null!;

        // ── Disclosure + edit chrome (built once) ──
        private ChimeraTabs    _segment = null!;           // Simple / Advanced (UX-DR54, Segment — D-3)
        private VBoxContainer?  _advancedHost;             // advanced-fields + raw-JSON subtree (rebuilt per unit; visibility = segment)
        private TextEdit?       _jsonPane;                 // raw-JSON escape hatch over the single unit (D-5)
        private bool            _paneDirty;                // the raw-JSON pane has manual edits not yet folded back
        private bool            _suppressPaneDirty;        // a programmatic SetPaneText must not mark the pane dirty
        private Label           _statusLabel = null!;      // save/validation status line
        private Godot.Button    _saveBtn   = null!;
        private Godot.Button    _newBtn    = null!;
        private Godot.Button    _dupBtn    = null!;
        private Godot.Button    _deleteBtn = null!;

        // ── Preview (built ONCE; only the mesh swaps per unit) ──
        private SubViewport _subViewport = null!;
        private Camera3D    _camera      = null!;
        private Node3D      _turntable   = null!;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            _theme = ChimeraComponents.EnsureInitialized(this);   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>
        /// Bind the panel to the current scenario's faction + game state + validated ability registry + the faction
        /// file <c>res://</c> path to persist edits to (D-8). Called by <c>UnitCardPhase</c> AFTER <c>AddChild</c>.
        /// Starts hidden; shown by the <c>J</c> toggle in Edit mode.
        /// </summary>
        public void Initialize(FactionDefinition? faction, GameState gameState, AbilityRegistry registry,
                               BehaviorRegistry behaviorRegistry, string factionJsonPath = "",
                               LLMService? llm = null, AiAvailabilityEvaluator? aiEvaluator = null,
                               ISecretStore? aiSecretStore = null)
        {
            _faction          = faction;
            _gameState        = gameState;
            _registry         = registry ?? AbilityRegistry.Empty;
            _behaviorRegistry = behaviorRegistry ?? BehaviorRegistry.Empty;
            _factionJsonPath  = factionJsonPath ?? "";
            _index           = 0;
            _llm           = llm;               // Story 8.4 — provider-backed draft framework (null hides the AI row)
            _aiEvaluator   = aiEvaluator;
            _aiSecretStore = aiSecretStore;

            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;
            RefreshAvailability();
        }

        /// <summary>
        /// Standalone-harness entry point (D-1 / <c>/godot-verify</c>): load a faction JSON by <c>res://</c> path,
        /// rebind the card to it (Units only), and set it as the write-back target. Presentation-only.
        /// </summary>
        public void LoadFactionFromPath(string resPath)
        {
            string abs = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(abs))
            {
                GD.PrintErr($"[UnitCard] LoadFactionFromPath: '{abs}' not found.");
                return;
            }
            _faction         = FactionDefinition.LoadFromFile(abs);
            _factionJsonPath = resPath;   // persist edits back to the same file
            _index           = 0;
            _panel.Visible   = true;
            _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            Refresh();
        }

        /// <summary>Toggle visibility (J key, Edit mode only). On open: enable the preview render + (re)bind the current unit.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible)
            {
                _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                Refresh();
                RefreshAvailability();
            }
            else
            {
                _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
        }

        // ── Story 8.4 — AI draft affordance ────────────────────────────────────

        private void BuildAiCard(Control parent)
        {
            _aiCard = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _aiCard.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            parent.AddChild(_aiCard);

            _aiCard.AddChild(ChimeraComponents.Heading("AI Draft, Commander", ThemeTokens.Tmd));

            _aiAvailLabel = ChimeraComponents.Body("", ThemeTokens.TextLo);
            _aiAvailLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _aiAvailLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _aiAvailLabel.Visible = false;
            _aiCard.AddChild(_aiAvailLabel);

            _aiPromptInput = new TextEdit
            {
                PlaceholderText = "e.g. \"a fast ranged skirmisher with a poison arrow\"",
                CustomMinimumSize = new Vector2(0, 56f),
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            _aiCard.AddChild(_aiPromptInput);

            _aiHeroToggle = new CheckBox { Text = "Draft as Hero (adds leveling + ability slots)" };
            _aiCard.AddChild(_aiHeroToggle);

            var genRow = new HBoxContainer();
            genRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _aiCard.AddChild(genRow);

            _aiGenBtn = ChimeraComponents.Button("Generate ✦", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            _aiGenBtn.Pressed += OnAiGeneratePressed;
            genRow.AddChild(_aiGenBtn);

            _aiSpinner = ChimeraSpinner.Create(20);
            _aiSpinner.Visible = false;
            genRow.AddChild(_aiSpinner);
            _aiSpinnerText = ChimeraComponents.Body("Transmuting…", ThemeTokens.TextMid);
            _aiSpinnerText.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _aiSpinnerText.Visible = false;
            genRow.AddChild(_aiSpinnerText);

            _aiStatusLabel = ChimeraComponents.Body("", ThemeTokens.TextLo);
            _aiStatusLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _aiStatusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _aiStatusLabel.Visible = false;
            _aiCard.AddChild(_aiStatusLabel);

            _aiCard.AddChild(new HSeparator());
        }

        /// <summary>Story 8.4 — four-state AI-availability line + Generate gating (mirrors MapGeneratorPanel). A null
        /// evaluator (older wiring) hides the whole AI row; the manual editor is unaffected in every state.</summary>
        private void RefreshAvailability()
        {
            if (_aiCard == null!) return;
            if (_llm == null || _aiEvaluator == null || _aiSecretStore == null)
            {
                _aiCard.Visible = false;
                if (_balanceCard != null!) _balanceCard.Visible = false;
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
            _aiGenBtn.Disabled = !available;

            // Story 8.5 — the balance-analysis card degrades on the SAME four-state; manual balance editing stays usable.
            if (_balanceCard != null!)
            {
                _balanceCard.Visible = true;
                _balanceAvailLabel.Visible = true;
                _balanceAvailLabel.Text = available
                    ? "AI balance: ready (config OK — Test connection in Settings to confirm)."
                    : AiAvailabilityMessages.Describe(state);
                _balanceGenBtn.Disabled = !available;
            }
        }

        private void OnAiGeneratePressed()
        {
            if (_llm == null) return;
            if (_faction == null)
            {
                ShowAiStatus("No faction bound — open a faction first, Commander.");
                return;
            }
            string prompt = _aiPromptInput.Text.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                ShowAiStatus("Describe the unit first, Commander.");
                return;
            }

            SetAiBusy(true);
            // Self-contained validation (Siblings null); a colliding id is renamed on insert via UniqueId. The loaded
            // ability/behavior registries gate composition refs exactly as the manual editor does.
            var ctx = new UnitDraftContext { AbilityRegistry = _registry, BehaviorRegistry = _behaviorRegistry };
            if (_aiHeroToggle.ButtonPressed)
                _llm.GenerateHeroDraftAsync(prompt, ctx, OnAiDraftComplete);
            else
                _llm.GenerateUnitDraftAsync(prompt, ctx, OnAiDraftComplete);
        }

        private void OnAiDraftComplete(UnitDefinition? def, string? error)
        {
            SetAiBusy(false);
            if (def == null)
            {
                ShowAiStatus(error ?? "Generation failed.", error: true);
                return;
            }
            if (_faction == null) return;

            // Land the validated draft as an editable, reopenable unit in the SAME list Save/browse operate on (the
            // DoCreate seam). Nothing is locked. A colliding id is uniquified so the roster stays duplicate-free.
            if (_faction.Units.Any(u => u != null && u.Id == def.Id))
                def.Id = UniqueId(def.Id);
            _faction.Units.Add(def);
            _index = _faction.Units.Count - 1;
            UnitDefinition captured = def;
            PushHistory(
                redo: () => { if (!_faction.Units.Contains(captured)) _faction.Units.Add(captured); GoToUnit(captured); },
                undo: () => RemoveFromList(captured));
            Refresh();
            ShowAiStatus($"Draft '{def.Id}' added — edit its fields, then Save.");
        }

        private void SetAiBusy(bool busy)
        {
            _aiGenBtn.Disabled = busy;
            _aiSpinner.Visible = busy;
            _aiSpinnerText.Visible = busy;
            if (busy) _aiStatusLabel.Visible = false;
        }

        private void ShowAiStatus(string message, bool error = false)
        {
            _aiStatusLabel.Visible = true;
            _aiStatusLabel.Text = message;
            // Distinguish a failure from a ready/success message (an all-neutral line reads a failed generation as success).
            _aiStatusLabel.AddThemeColorOverride("font_color", Tok(error ? ThemeTokens.Danger : ThemeTokens.Ok));
        }

        // ── Story 8.5 — AI balance-analysis affordance ─────────────────────────

        private void BuildBalanceCard(Control parent)
        {
            _balanceCard = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            _balanceCard.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            parent.AddChild(_balanceCard);

            _balanceCard.AddChild(ChimeraComponents.Heading("AI Balance Analysis, Commander", ThemeTokens.Tmd));

            _balanceAvailLabel = ChimeraComponents.Body("", ThemeTokens.TextLo);
            _balanceAvailLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _balanceAvailLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _balanceAvailLabel.Visible = false;
            _balanceCard.AddChild(_balanceAvailLabel);

            _balancePromptInput = new TextEdit
            {
                PlaceholderText = "e.g. \"melee units feel too weak against ranged\"",
                CustomMinimumSize = new Vector2(0, 56f),
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            _balanceCard.AddChild(_balancePromptInput);

            var genRow = new HBoxContainer();
            genRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _balanceCard.AddChild(genRow);

            _balanceGenBtn = ChimeraComponents.Button("Analyze ✦", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            _balanceGenBtn.Pressed += OnBalanceAnalyzePressed;
            genRow.AddChild(_balanceGenBtn);

            _balanceSpinner = ChimeraSpinner.Create(20);
            _balanceSpinner.Visible = false;
            genRow.AddChild(_balanceSpinner);
            _balanceSpinnerText = ChimeraComponents.Body("Transmuting…", ThemeTokens.TextMid);
            _balanceSpinnerText.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _balanceSpinnerText.Visible = false;
            genRow.AddChild(_balanceSpinnerText);

            _balanceStatusLabel = ChimeraComponents.Body("", ThemeTokens.TextLo);
            _balanceStatusLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _balanceStatusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _balanceStatusLabel.Visible = false;
            _balanceCard.AddChild(_balanceStatusLabel);

            _balanceRowsHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _balanceRowsHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            _balanceCard.AddChild(_balanceRowsHost);

            _balanceCard.AddChild(new HSeparator());
        }

        private void OnBalanceAnalyzePressed()
        {
            if (_llm == null) return;
            if (_faction == null)
            {
                ShowBalanceStatus("No faction bound — open a faction first, Commander.", error: true);
                return;
            }
            string prompt = _balancePromptInput.Text.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                ShowBalanceStatus("Describe what feels off first, Commander.", error: true);
                return;
            }

            var ids = _faction.Units.Where(u => u != null && !string.IsNullOrEmpty(u.Id)).Select(u => u.Id).ToList();
            var ctx = new BalanceAnalysisContext { UnitIds = ids };

            SetBalanceBusy(true);
            ClearBalanceRows();
            _llm.GenerateBalanceAnalysisAsync(prompt, ctx, OnBalanceAnalysisComplete);
        }

        private void OnBalanceAnalysisComplete(BalanceReport? report, string? error)
        {
            SetBalanceBusy(false);
            if (report == null)
            {
                ShowBalanceStatus(error ?? "Analysis failed.", error: true);
                return;
            }
            if (report.Suggestions.Count == 0)
            {
                ShowBalanceStatus("No tuning suggestions returned, Commander.");
                return;
            }

            ClearBalanceRows();
            foreach (BalanceSuggestion s in report.Suggestions)
                _balanceRowsHost.AddChild(BuildSuggestionRow(s));
            ShowBalanceStatus($"{report.Suggestions.Count} suggestion(s) — review, edit the value, then Apply. Nothing is applied automatically.");
        }

        /// <summary>Build one editable suggestion row: a field chip + unit id, the rationale, an editable proposed-value
        /// input, and Apply / Discard. Apply gates the (possibly edited) value through
        /// <see cref="BalanceSuggestionApplier.TryApply"/> and lands a success through the existing undo/validate/Save seam.</summary>
        private Control BuildSuggestionRow(BalanceSuggestion s)
        {
            var row = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));

            // Show the unit's REAL current value (read from the bound roster), not the model's unverified `current`
            // claim — a hallucinated or omitted current is misleading in a balance tool. "—" when unreadable.
            string curText = "—";
            UnitDefinition? liveUnit = _faction?.Units?.FirstOrDefault(u => u != null && u.Id == s.UnitId);
            if (liveUnit != null && BalanceSuggestionApplier.TryReadField(liveUnit, s.Field, out double curVal))
                curText = curVal.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var suggestionLbl = ChimeraComponents.Body($"{s.UnitId} · {s.Field}   (current {curText})", ThemeTokens.TextMid);
            suggestionLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(suggestionLbl);

            var rationale = ChimeraComponents.Body(s.Rationale, ThemeTokens.TextLo);
            rationale.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            rationale.AutowrapMode = TextServer.AutowrapMode.Word;
            row.AddChild(rationale);

            var editRow = new HBoxContainer();
            editRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            row.AddChild(editRow);

            var valueInput = new LineEdit
            {
                Text = s.Proposed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                CustomMinimumSize = new Vector2(90f, 0),
            };
            editRow.AddChild(valueInput);

            var applyBtn = ChimeraComponents.Button("Apply", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Sm);
            editRow.AddChild(applyBtn);
            var discardBtn = ChimeraComponents.Button("Discard", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            editRow.AddChild(discardBtn);

            applyBtn.Pressed += () => OnApplySuggestion(s, valueInput.Text, row);
            discardBtn.Pressed += () => { _balanceRowsHost.RemoveChild(row); row.QueueFree(); };

            return row;
        }

        private void OnApplySuggestion(BalanceSuggestion s, string editedText, Control row)
        {
            if (_faction == null) return;
            if (!double.TryParse(editedText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double edited))
            {
                ShowBalanceStatus($"'{editedText}' is not a number, Commander.", error: true);
                return;
            }

            UnitDefinition? target = _faction.Units.FirstOrDefault(u => u != null && u.Id == s.UnitId);
            if (target == null)
            {
                ShowBalanceStatus($"Unit '{s.UnitId}' is no longer in the roster.", error: true);
                return;
            }

            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, s.Field, edited, _faction.Units);
            if (candidate == null)
            {
                // Located reject — nothing mutated; show the message and leave the row for another edit.
                ShowBalanceStatus(err ?? "Rejected.", error: true);
                return;
            }

            // Commit the validated clone into the SAME list Save/browse operate on, through the existing undo seam.
            // Reference-based restore (locate by reference at closure time, never a captured index) — the 8.4 draft-apply
            // precedent: a raw index goes stale if the roster is later reordered/deleted, so an undo would overwrite a
            // different slot (or throw). Idempotent guards keep undo/redo safe if the target was removed meanwhile.
            UnitDefinition old = target;
            UnitDefinition neu = candidate;
            int idx = _faction.Units.IndexOf(old);
            if (idx < 0) { ShowBalanceStatus($"Unit '{s.UnitId}' is no longer in the roster.", error: true); return; }
            _faction.Units[idx] = neu;
            PushHistory(
                redo: () => { int i = _faction.Units.IndexOf(old); if (i >= 0) { _faction.Units[i] = neu; GoToUnit(neu); } },
                undo: () => { int i = _faction.Units.IndexOf(neu); if (i >= 0) { _faction.Units[i] = old; GoToUnit(old); } });
            GoToUnit(neu);
            RevalidateAndReflect();
            if (!PersistSync())
                // PersistSync already surfaced its own error — don't also claim success. The in-memory edit + undo stand.
                return;

            _balanceRowsHost.RemoveChild(row);
            row.QueueFree();
            // Echo the value ACTUALLY applied (int fields round), read back from the committed clone — not the typed text.
            double appliedVal = BalanceSuggestionApplier.TryReadField(neu, s.Field, out double rb) ? rb : edited;
            ShowBalanceStatus($"Applied {s.Field}={appliedVal:0.###} to '{s.UnitId}' — re-validated and saved.");
        }

        private void ClearBalanceRows()
        {
            if (_balanceRowsHost == null!) return;
            foreach (Node child in _balanceRowsHost.GetChildren())
            {
                _balanceRowsHost.RemoveChild(child);
                child.QueueFree();
            }
        }

        private void SetBalanceBusy(bool busy)
        {
            _balanceGenBtn.Disabled = busy;
            _balanceSpinner.Visible = busy;
            _balanceSpinnerText.Visible = busy;
            if (busy) _balanceStatusLabel.Visible = false;
        }

        private void ShowBalanceStatus(string message, bool error = false)
        {
            _balanceStatusLabel.Visible = true;
            _balanceStatusLabel.Text = message;
            _balanceStatusLabel.AddThemeColorOverride("font_color", Tok(error ? ThemeTokens.Danger : ThemeTokens.Ok));
        }

        /// <summary>Hide the panel and stop rendering the preview.</summary>
        public void Close()
        {
            _panel.Visible = false;
            if (_subViewport != null!) _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }

        // ── Story 5.9 (onboarding) ────────────────────────────────────────────────

        /// <summary>The curated fixed list of existing unit ids offered as onboarding "starter templates" (spec
        /// Boundaries/Never: a small fixed list via the existing Duplicate path, not a gallery UI). Ids must exist
        /// in the bound faction's roster (both shipped factions carry all three) — an id that doesn't resolve
        /// still opens the panel (see <see cref="StartFromTemplate"/>), it just skips the duplicate.</summary>
        public static readonly (string Id, string Label)[] CuratedTemplateUnits =
        {
            ("worker",   "Worker (Economy)"),
            ("infantry", "Infantry (Melee)"),
            ("archer",   "Archer (Ranged)"),
        };

        /// <summary>Ensure the panel is visible without changing which unit is bound — a no-op if already open.
        /// Backs onboarding steps 2/3, which revisit the SAME unit <see cref="StartFromTemplate"/> created in step 1
        /// rather than duplicating again.</summary>
        public void EnsureVisible()
        {
            if (!_panel.Visible) Toggle();
        }

        /// <summary>Onboarding step 1 (Story 5.9): open the panel and duplicate a curated template unit, selected
        /// and ready for editing. Drives the SAME <c>Duplicate</c> path a manual toolbar click uses (D-2 — never
        /// re-implemented here). Returns whether the duplicate actually happened; a template id that isn't in the
        /// bound faction's roster still opens the panel on whatever unit is currently browsed (never a silent
        /// no-op window), but the caller can now tell the two cases apart instead of assuming success (Story 5.9
        /// review pass — was: the onboarding overlay always claimed "Created a copy…" even on this fallback).</summary>
        public bool StartFromTemplate(string templateUnitId)
        {
            _panel.Visible = true;
            _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

            int idx = _faction?.Units.FindIndex(u => u.Id == templateUnitId) ?? -1;
            if (idx >= 0)
            {
                _index = idx;
                Refresh();
                DoDuplicate();   // clones + selects + pushes undo, exactly like the manual "Duplicate" button
                return true;
            }

            Refresh();
            return false;
        }

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();   // hide in Play (authoring is Edit-only)
        }

        /// <inheritdoc/>
        public override void _Process(double delta)
        {
            _llm?.DrainEvents();   // Story 8.4 — marshal AI draft callbacks to the main thread each frame
            if (_panel is null || !_panel.Visible) return;
            _turntable.RotateY(Mathf.DegToRad(TURNTABLE_SPEED * (float)delta));   // slow live turntable (D-8)
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 11 };
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            // Right-docked with a MARGIN gutter. Explicit offsets pin all four edges (PANEL_W wide, ending MARGIN px
            // from the screen edge); GrowHorizontal.Begin makes any over-wide content grow LEFTWARD into the screen
            // rather than spilling off the right edge. See BuildingCardPanel for the shared rationale.
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.GrowHorizontal = Control.GrowDirection.Begin;
            _panel.GrowVertical   = Control.GrowDirection.Both;
            _panel.OffsetRight  = -MARGIN;
            _panel.OffsetLeft   = -(PANEL_W + MARGIN);
            _panel.OffsetTop    = -PANEL_H * 0.5f;
            _panel.OffsetBottom =  PANEL_H * 0.5f;
            _panel.Theme = _theme;   // _panel is a Control (UnitCardPanel : Node has NO Theme) — propagates to the subtree
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // Title + browse + close row.
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var titleLbl = ChimeraComponents.Heading("Unit Editor", ThemeTokens.Tlg);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(titleLbl);

            _prevBtn = ChimeraComponents.IconButton("◀");
            _prevBtn.Pressed += () => Browse(-1);
            titleRow.AddChild(_prevBtn);

            _counterLabel = ChimeraComponents.Body("—", ThemeTokens.TextMid);
            _counterLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _counterLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _counterLabel.CustomMinimumSize = new Vector2(88, 0);
            _counterLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(_counterLabel);

            _nextBtn = ChimeraComponents.IconButton("▶");
            _nextBtn.Pressed += () => Browse(1);
            titleRow.AddChild(_nextBtn);

            var closeBtn = ChimeraComponents.Button("Close [J]", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += Close;
            closeBtn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(closeBtn);

            // Scrollable body (the card can be taller than the panel).
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);

            var contentCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            contentCol.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            scroll.AddChild(contentCol);

            // Story 8.4 — AI draft card (hidden until Initialize wires a provider). A prompt + Generate (unit, or hero
            // via the toggle) adds an editable, reopenable unit to the faction — the SAME list Save/browse operate on.
            BuildAiCard(contentCol);

            // Story 8.5 — AI balance-analysis card (hidden until Initialize wires a provider). A focus prompt + Analyze
            // requests per-field tuning suggestions over the WHOLE roster; each is editable and applied one at a time
            // through the same validate/undo/Save seam manual edits use — nothing auto-applies.
            BuildBalanceCard(contentCol);

            // Read-only header → Preview (persistent) → disclosure Segment → editable body (refilled per unit).
            _headerHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _headerHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            contentCol.AddChild(_headerHost);

            contentCol.AddChild(BuildPreviewHost());

            // Simple / Advanced disclosure (UX-DR54 Segment — D-3), built once above the fields.
            _segment = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced");
            _segment.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _segment.TabChanged += OnSegmentChanged;
            contentCol.AddChild(_segment);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            contentCol.AddChild(_bodyHost);

            // Status line + toolbar (fixed below the scroll — the AbilityEditor save-row shape).
            _statusLabel = ChimeraComponents.Body("", ThemeTokens.TextLo);
            _statusLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            root.AddChild(BuildToolbar());

            _panel.Visible = false;   // hidden until the first J toggle
        }

        /// <summary>Build the isolated 3D preview host ONCE. The mesh inside swaps per unit; the host persists.</summary>
        private Control BuildPreviewHost()
        {
            _subViewport = new SubViewport
            {
                Size                   = new Vector2I(PREVIEW_W, PREVIEW_H),
                RenderTargetClearMode  = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, // starts hidden → renders nothing
                OwnWorld3D             = true, // ISOLATED world — do NOT share the game world
            };

            _camera = new Camera3D { Position = new Vector3(0f, 1.2f, 3.5f) };
            _subViewport.AddChild(_camera);

            var key = new DirectionalLight3D { LightEnergy = 1.4f };
            key.RotationDegrees = new Vector3(-45f, 45f, 0f);
            _subViewport.AddChild(key);

            var fill = new DirectionalLight3D { LightEnergy = 0.6f, LightSpecular = 0f };
            fill.RotationDegrees = new Vector3(-20f, -120f, 0f);
            _subViewport.AddChild(fill);

            var worldEnv = new WorldEnvironment();
            var env = new Godot.Environment
            {
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor  = new Color(0.35f, 0.35f, 0.4f),
                AmbientLightEnergy = 0.6f,
                BackgroundMode     = Godot.Environment.BGMode.Color,
                BackgroundColor    = Tok(ThemeTokens.Surface0),
            };
            worldEnv.Environment = env;
            _subViewport.AddChild(worldEnv);

            _turntable = new Node3D();
            _subViewport.AddChild(_turntable);

            var container = new SubViewportContainer
            {
                Stretch       = true,
                StretchShrink = 1,
                MouseFilter   = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            container.CustomMinimumSize = new Vector2(PREVIEW_W, PREVIEW_H);
            container.AddChild(_subViewport);
            return container;
        }

        // ── Per-unit binding ─────────────────────────────────────────────────────

        /// <summary>Rebuild every region for <paramref name="def"/>: read-only header, editable body, and the 3D preview.</summary>
        private void Bind(UnitDefinition def)
        {
            _current    = def;
            _originalId = def.Id;
            ClearHosts();
            BuildHeader(def);
            BuildEditableBody(def);   // Story 3.4 (UnitCardPanel.Edit.cs) — replaces the 3.3 read-only readouts
            UpdatePreview(def);
            RevalidateAndReflect();   // paint any badges + set the Save/Delete enabled state for the freshly-bound unit
        }

        /// <summary>Bind the unit at <see cref="_index"/>, or show an empty state if the faction has no units.</summary>
        private void Refresh()
        {
            if (_faction is null || _faction.Units.Count == 0)
            {
                _current = null;
                ClearHosts();
                BuildEmptyState();
                ClearPreview();
                UpdateCounter(0, 0);
                UpdateToolbarEnabled();
                return;
            }
            if (_index < 0 || _index >= _faction.Units.Count) _index = 0;
            Bind(_faction.Units[_index]);
            UpdateCounter(_index + 1, _faction.Units.Count);
        }

        /// <summary>Cycle the browse cursor over <c>_faction.Units</c> (Units only — D-10), wrapping both ways.</summary>
        private void Browse(int dir)
        {
            if (_faction is null || _faction.Units.Count == 0) return;
            int n = _faction.Units.Count;
            _index = ((_index + dir) % n + n) % n;
            Refresh();
        }

        private void UpdateCounter(int i, int n)
        {
            _counterLabel.Text = n == 0 ? "—" : $"UNIT {i} / {n}";
            _prevBtn.Disabled = n <= 1;
            _nextBtn.Disabled = n <= 1;
        }

        private void ClearHosts()
        {
            foreach (Node c in _headerHost.GetChildren()) { _headerHost.RemoveChild(c); c.QueueFree(); }
            foreach (Node c in _bodyHost.GetChildren())   { _bodyHost.RemoveChild(c);   c.QueueFree(); }
            // The badge/pane/advanced-host nodes lived under _bodyHost — they are freed above; drop the stale refs.
            _badges.Clear();
            _advancedHost = null;
            _jsonPane = null;
            _meshPathInput = null;   // the Model row's LineEdit was freed with the body subtree (Story 3.5)
            _paneDirty = false;
        }

        private void BuildEmptyState()
        {
            _segment.Visible = false;
            _headerHost.AddChild(ChimeraComponents.Heading("Unit Editor", ThemeTokens.Txl));
            var emptyLbl = ChimeraComponents.Body(_faction is null ? "No faction bound." : "This faction has no units — press New to add one.", ThemeTokens.TextLo);
            emptyLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _bodyHost.AddChild(emptyLbl);
        }

        // ── Read-only header (kept from 3.3; HERO tag stays read-only — Promote-to-Hero is 3.7) ──

        private void BuildHeader(UnitDefinition def)
        {
            _segment.Visible = true;

            var title = ChimeraComponents.Heading(string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName, ThemeTokens.T2xl);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.AutowrapMode = TextServer.AutowrapMode.Word;
            _headerHost.AddChild(title);

            var id = ChimeraComponents.Body(def.Id, ThemeTokens.TextLo);
            id.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            id.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txs, ThemeTokens.Type));
            _headerHost.AddChild(id);

            var tags = new HBoxContainer();
            tags.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            var arch = ChimeraComponents.Tag(def.Category);
            AttachTip(arch, "Archetype", "The unit's movement & role class — edit it in the Archetype field below.");
            tags.AddChild(arch);

            if (def.IsHero)   // passive, read-only HERO tag (added 3.2; the Promote-to-Hero switch is 3.7)
            {
                var hero = ChimeraComponents.Tag("HERO", ChimeraComponents.TagVariant.Accent);
                AttachTip(hero, "Hero", "A hero unit — mints a persistent, cross-match hero identity when it spawns.");
                tags.AddChild(hero);
            }
            _headerHost.AddChild(tags);
        }

        // ── 3D preview (kept from 3.3; D-8) ──────────────────────────────────────

        private void UpdatePreview(UnitDefinition def)
        {
            ClearPreview();
            Color tint = FactionTint();
            // Crash-proof: MeshLoader returns a box placeholder when the path is empty/missing/unloadable.
            // usedPlaceholder distinguishes a real load from a fallback so the badge can flag a model that
            // fails to load, not just a missing path (Story 3.5, D-3).
            Mesh mesh = MeshLoader.LoadFromGlb(def.MeshPath ?? "", new Vector3(0.8f, 1.6f, 0.8f), tint, out bool usedPlaceholder);
            _lastMeshMissing = usedPlaceholder && !string.IsNullOrEmpty(def.MeshPath);
            var mi = new MeshInstance3D { Mesh = mesh };
            mi.Scale = MeshLoader.ScaleFromDefinition(def.MeshScale);
            _turntable.AddChild(mi);
            FitCamera(mesh.GetAabb(), def.MeshScale);
        }

        private void ClearPreview()
        {
            if (_turntable is null) return;
            foreach (Node c in _turntable.GetChildren()) c.QueueFree();
            _turntable.Rotation = Vector3.Zero;
        }

        /// <summary>Frame the mesh with a minimal AABB-based camera fit so large meshes don't clip the fixed frame (D-8).</summary>
        private void FitCamera(Aabb aabb, float meshScale)
        {
            Vector3 size   = aabb.Size * meshScale;
            Vector3 center = (aabb.Position + aabb.Size * 0.5f) * meshScale;
            float radius   = Mathf.Max(0.1f, size.Length() * 0.5f);
            float fovRad   = Mathf.DegToRad(_camera.Fov);
            float dist     = radius / Mathf.Sin(fovRad * 0.5f) * 1.2f;
            Vector3 camPos = center + new Vector3(0f, size.Y * 0.15f, dist);
            _camera.LookAtFromPosition(camPos, center, Vector3.Up);
            _camera.Near = 0.05f;
            _camera.Far  = dist + radius * 4f + 10f;
        }

        private Color FactionTint()
        {
            if (_faction?.Color is { Length: >= 3 } c)
                return new Color(c[0], c[1], c[2]);
            return new Color(0.6f, 0.7f, 0.9f);
        }

        // ── Small shared builders (kept from 3.3) ────────────────────────────────

        private Color Tok(StringName token) => _theme.GetColor(token, ThemeTokens.Type);

        private static string FileNameOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass — kept as a local
        /// wrapper so existing unqualified call sites in this file don't need touching).</summary>
        private void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
