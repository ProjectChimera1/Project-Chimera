#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;   // ScenarioData, PersistenceManifest, PersistableAttributes, PersistenceManifestValidator, ScenarioSerializer
using ProjectChimera.UI;                  // GameState, GameMode
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraSwitch, ChimeraListRow, ChimeraValidationBadge, ChimeraTooltip
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 3.8 — the Persistence Manifest editor (FR-7a / FR-7b / AR-12). A creator opens it (V in Edit mode) to
    /// declare WHICH hero progression carries forward between their custom games: a master <see cref="ChimeraSwitch"/>
    /// enables persistence for the scenario, and a scope-grouped checklist of <see cref="PersistableAttributes.Eligible"/>
    /// attributes (today Hero Level + Accumulated XP) selects the ones that persist. Save routes through the fail-closed
    /// <see cref="PersistenceManifestValidator"/> then writes the scenario via <see cref="ScenarioSerializer.SaveToFile"/>
    /// (the <c>WinConditionPhase</c> persistence precedent).
    ///
    /// <para><b>Only eligible attributes are offerable (AR-12).</b> The checklist is built FROM the catalog, so a creator
    /// can never select a mid-game attribute through the UI — the validator is the fail-closed backstop for hand-edited
    /// scenario JSON, surfaced here as a section-level <see cref="ChimeraValidationBadge"/> (D-4).</para>
    ///
    /// <para><b>Determinism posture — PURE AUTHORING-TIME, zero fold.</b> Editing the manifest POCO + rewriting the
    /// scenario JSON touches no <c>EntityWorld</c>/store/sim array and moves no checksum or golden (a null manifest is
    /// omitted-when-null; <c>CanonicalModelHash</c> does not walk it). The load/apply rail is Story 3.9.</para>
    /// </summary>
    public partial class PersistenceManifestPanel : Node
    {
        // ── Layout constants (component-intrinsic dims; spacing/color TOKENS come from the theme) ──
        private const float PANEL_W = 420f;
        private const float PANEL_H = 560f;
        private const float MARGIN  = 12f;

        // ── Kit context ──
        private GodotTheme        _theme  = null!;

        // ── Deps (wired by PersistenceManifestPhase after AddChild) ──
        private ScenarioData? _scenario;
        private GameState?    _gameState;
        private string        _scenarioPath = "";   // res:// path of the scenario file to write back to (D-8 precedent)

        // ── Nodes ──
        private CanvasLayer    _canvas        = null!;
        private PanelContainer _panel         = null!;
        private ChimeraSwitch  _masterSwitch  = null!;
        private VBoxContainer  _checklistHost = null!;   // BindReveal target — the scope-grouped attribute checklist
        private ChimeraValidationBadge _badge = null!;   // section-level located badge (hand-edit errors, D-4)
        private Label          _statusLabel   = null!;
        private Godot.Button   _saveBtn       = null!;

        private readonly PersistenceManifestValidator _validator = new();
        private readonly List<(string Key, ChimeraListRow Row)> _rows = new();
        private bool _building;   // suppress live handlers while (re)building controls / setting programmatic state

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            _theme = ChimeraComponents.EnsureInitialized(this);   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>Bind the panel to the live scenario + game state + the scenario file <c>res://</c> path to persist to.
        /// Called by <c>PersistenceManifestPhase</c> AFTER <c>AddChild</c>. Starts hidden; shown by the V toggle in Edit.</summary>
        public void Initialize(ScenarioData? scenario, GameState gameState, string scenarioPath)
        {
            _scenario     = scenario;
            _gameState    = gameState;
            _scenarioPath = scenarioPath ?? "";

            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;
        }

        /// <summary>Rebind after the scenario is reloaded (Import / scene restart) — mirrors <c>TriggerEditorPanel.SetScenario</c>.</summary>
        public void SetScenario(ScenarioData? scenario)
        {
            // DW-10: same-reference re-bind (the in-place Edit↔Play re-apply) is a no-op — only an actual object
            // swap (Import / scene restart) rebinds + refreshes. Captures the same-null case too.
            if (ReferenceEquals(scenario, _scenario)) return;
            _scenario = scenario;
            if (_panel.Visible) Refresh();
        }

        /// <summary>Toggle visibility (V key, Edit mode only). On open: (re)reflect the current manifest.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) Refresh();
        }

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) _panel.Visible = false;   // hide in Play (authoring is Edit-only)
        }

        // ── UI construction ────────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 11 };
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
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
            _panel.Theme = _theme;   // _panel is a Control (this Node has no Theme) — propagates to the subtree
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // ── Title + close row ──
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var titleLbl = ChimeraComponents.Heading("Hero Persistence", ThemeTokens.Tlg);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(titleLbl);

            var closeBtn = ChimeraComponents.Button("Close [V]", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += () => _panel.Visible = false;
            closeBtn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            AttachFieldTip(closeBtn, "Close", "Close the Hero Persistence editor (also toggled with V in Edit mode).");
            titleRow.AddChild(closeBtn);

            // ── Master switch row (UX-DR54 disclosure) ──
            var switchRow = new HBoxContainer();
            switchRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(switchRow);

            _masterSwitch = ChimeraSwitch.Create(false);
            _masterSwitch.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _masterSwitch.Toggled += OnMasterToggled;
            AttachFieldTip(_masterSwitch, "Enable hero persistence",
                "When on, this scenario carries the selected hero progression forward between matches. When off, nothing persists (your selection is kept).");
            switchRow.AddChild(_masterSwitch);

            var switchLbl = ChimeraComponents.Body("Enable hero persistence for this scenario", ThemeTokens.TextMid);
            switchLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            switchLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            switchLbl.AutowrapMode = TextServer.AutowrapMode.Word;
            switchRow.AddChild(switchLbl);

            // ── Checklist host (the reveal target) ──
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);

            _checklistHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _checklistHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            scroll.AddChild(_checklistHost);

            // The switch reveals/hides the checklist inline (the Promote-to-Hero pattern). BindReveal syncs the host's
            // visibility to the switch's current state.
            _masterSwitch.BindReveal(_checklistHost);

            // ── Section validation badge + status line ──
            _badge = ChimeraValidationBadge.Create();
            root.AddChild(_badge);

            _statusLabel = ChimeraComponents.Body("", ThemeTokens.TextLo);
            _statusLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            // ── Save ──
            _saveBtn = ChimeraComponents.Button("Save", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Block);
            _saveBtn.Pressed += OnSavePressed;
            AttachFieldTip(_saveBtn, "Save",
                "Validate the manifest (fail-closed) and write it to the scenario file. Saving is blocked if an attribute is unknown or mid-game-only.");
            root.AddChild(_saveBtn);

            _panel.Visible = false;   // hidden until the first V toggle
        }

        // ── Reflect the bound scenario's manifest into the controls ─────────────────

        /// <summary>Rebuild the checklist + reflect the switch state from <see cref="_scenario"/>'s current manifest.</summary>
        private void Refresh()
        {
            _building = true;

            // Master switch reflects "configured AND enabled". A null manifest ⇒ off (not configured).
            bool enabled = _scenario?.PersistenceManifest is { Enabled: true };
            _masterSwitch.SetOn(enabled, animate: false);   // no signal — SetOn uses SetPressedNoSignal

            BuildChecklist();

            _badge.Clear();
            _statusLabel.Visible = false;
            _statusLabel.Text = "";
            if (_scenario == null)
            {
                _statusLabel.Visible = true;
                _statusLabel.Text = "No scenario loaded.";
            }

            _building = false;
        }

        /// <summary>(Re)build the scope-grouped eligible-attribute checklist. Only scopes with ≥1 eligible attribute get
        /// a section (D-1) — no empty section advertises an unbuilt system. Each row's initial selected state reflects
        /// whether its key is in the current manifest.</summary>
        private void BuildChecklist()
        {
            foreach (Node c in _checklistHost.GetChildren()) { _checklistHost.RemoveChild(c); c.QueueFree(); }
            _rows.Clear();

            PersistenceManifest? m = _scenario?.PersistenceManifest;

            // Iterate the scopes in enum order; render only those the catalog populates.
            foreach (AttributeScope scope in Enum.GetValues<AttributeScope>())
            {
                PersistableAttribute[] inScope = PersistableAttributes.ByScope(scope);
                if (inScope.Length == 0) continue;

                var header = ChimeraComponents.FieldLabel(scope.ToString());
                _checklistHost.AddChild(header);

                foreach (PersistableAttribute attr in inScope)
                {
                    ChimeraListRow row = ChimeraListRow.Create(attr.Label);
                    bool selected = m != null && m.Attributes.Contains(attr.Key);
                    row.SetSelected(selected);   // does NOT emit Selected (only _GuiInput does) — safe during build

                    string key = attr.Key;
                    ChimeraListRow captured = row;
                    row.Selected += () => OnRowToggled(key, captured);

                    AttachFieldTip(row, attr.Label, attr.Tip);
                    _checklistHost.AddChild(row);
                    _rows.Add((key, row));
                }
            }
        }

        // ── Interaction ─────────────────────────────────────────────────────────────

        private void OnMasterToggled(bool on)
        {
            if (_building || _scenario == null) return;

            // The pure state transition (create/enable, or disable-retaining-selection) lives in the Godot-free helper so
            // it is Tier-1-tested; the panel only wires it. BindReveal already syncs the checklist host's visibility.
            _scenario.PersistenceManifest = PersistenceManifestEditing.ApplyMasterToggle(_scenario.PersistenceManifest, on);
        }

        private void OnRowToggled(string key, ChimeraListRow row)
        {
            if (_building || _scenario == null) return;

            _scenario.PersistenceManifest =
                PersistenceManifestEditing.ApplyAttributeToggle(_scenario.PersistenceManifest, key, row.IsSelected);

            // Selecting an attribute implies persistence is on — reflect that on the master switch (defensive: the
            // checklist is only interactable while the switch is on via BindReveal).
            if (!_masterSwitch.On) _masterSwitch.SetOn(true, animate: true);

            // A fresh valid selection clears any prior hand-edit badge/status.
            _badge.Clear();
            _statusLabel.Visible = false;
        }

        // ── Save (validate fail-closed → persist) ───────────────────────────────────

        private void OnSavePressed()
        {
            if (_scenario == null)
            {
                ShowStatus("No scenario loaded.");
                return;
            }

            // No write-back target ⇒ nothing to save to. GlobalizePath("") would resolve to the project root and hand
            // SaveToFile a directory-ish path; guard it with a clear message instead of a generic "Save failed".
            if (string.IsNullOrEmpty(_scenarioPath))
            {
                ShowStatus("No scenario file path to save to.");
                return;
            }

            // Fail-closed gate: the checklist offers only eligible attributes, so any error here comes from a hand-edited
            // scenario JSON carrying an unknown / mid-game / duplicate key. Block the Save and locate it on the badge.
            ManifestValidationResult result = _validator.Validate(_scenario.PersistenceManifest);
            if (!result.Ok)
            {
                _badge.ShowError(result.Errors[0].Message);
                ShowStatus($"Save blocked — {result.Errors.Count} invalid attribute(s):\n" +
                           string.Join("\n", System.Linq.Enumerable.Select(result.Errors, e => e.Message)));
                return;
            }

            _badge.Clear();

            try
            {
                string abs = ProjectSettings.GlobalizePath(_scenarioPath);
                // Story 14.5 — absent-stays-absent contract. This writes the ENTIRE shared ScenarioData, not just the
                // manifest. A null PersistenceManifest is omitted by [JsonIgnore(WhenWritingNull)] on ScenarioData, so a
                // manifest-less map saves with no persistence_manifest key. enabled:true originates ONLY from the explicit
                // master/checklist toggles in this panel (PersistenceManifestEditing.ApplyMasterToggle /
                // ApplyAttributeToggle) — a routine map-save must never inject a default manifest. Backstop: the Tier-1
                // AllShippedScenarios_HaveNoManifest_ExceptOptInWhitelist guard fails RED if any future in-memory
                // default-manifest injection reaches a shipped file through a save.
                ScenarioSerializer.SaveToFile(_scenario, abs);
                ShowStatus(_scenario.PersistenceManifest == null
                    ? "Saved (persistence not configured — no manifest written)."
                    : $"Saved. Persistence {(_scenario.PersistenceManifest.Enabled ? "enabled" : "disabled")}, " +
                      $"{_scenario.PersistenceManifest.Attributes.Count} attribute(s).");
                GD.Print($"[PersistenceManifest] Saved scenario to {abs}.");
            }
            catch (Exception ex)
            {
                ShowStatus($"Save failed: {ex.Message}");
                GD.PrintErr($"[PersistenceManifest] Save error: {ex}");
            }
        }

        private void ShowStatus(string text)
        {
            _statusLabel.Text = text;
            _statusLabel.Visible = true;
        }


        /// <summary>Attach a hover-AND-keyboard-focus tooltip (UX-DR53 / NFR-2). The keyboard half needs
        /// <c>FocusMode.All</c>; descendants are made mouse-transparent so the composite is the unambiguous hover target.</summary>
        private void AttachFieldTip(Control target, string term, string body)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            MakeChildrenMouseIgnore(target);
            ChimeraTooltip.Attach(target, term, body, ChimeraTooltip.TooltipRole.Field);
        }

        private static void MakeChildrenMouseIgnore(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
                MakeChildrenMouseIgnore(child);
            }
        }
    }
}
