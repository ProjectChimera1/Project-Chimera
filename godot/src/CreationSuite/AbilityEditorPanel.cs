#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Godot;
using ProjectChimera.Core;              // Fixed, ScenarioData
using ProjectChimera.Core.Definitions;  // AbilityDefinition, AbilityPresets, AbilityValidator, AbilityLoader, ContentJson, AbilityRegistry
using ProjectChimera.Effects;           // EffectNode shapes (preset round-trip detection)
using ProjectChimera.UI;                // GameState, GameMode

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
        public void Initialize(ScenarioData? scenario, GameState gameState, AbilityRegistry registry)
        {
            _ = scenario;                       // create-only editor: scenario reserved for parity, unused today
            _gameState = gameState;
            _registry  = registry ?? AbilityRegistry.Empty;

            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false;

            SelectPreset(AbilityPresets.Kind.TargetedDamage, seedHeader: true);
            SwitchMode(simple: true);
            RefreshList();
        }

        /// <summary>Toggle visibility (K key, Edit mode only). Refreshes the existing-ability list on open.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) RefreshList();
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
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Position = new Vector2(-(PANEL_W + MARGIN), -PANEL_H * 0.5f);
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
            _advancedBtn = MakePill("Advanced (raw JSON)", false);
            _simpleBtn.AddThemeFontSizeOverride("font_size", 13);
            _advancedBtn.AddThemeFontSizeOverride("font_size", 13);
            _simpleBtn.Pressed   += () => SwitchMode(simple: true);
            _advancedBtn.Pressed += () => { SwitchMode(simple: false); ShowJson(); };
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

        private Control BuildAdvancedPane()
        {
            var pane = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            pane.AddThemeConstantOverride("separation", 6);

            AddSectionHeader(pane, "Raw JSON (escape hatch)");
            var hint = new Label
            {
                Text = "Edit the ability JSON directly. 'Apply' parses + validates and reflects back into the form; 'Show' re-renders from the form.",
                AutowrapMode = TextServer.AutowrapMode.Word,
            };
            hint.AddThemeFontSizeOverride("font_size", 10);
            hint.AddThemeColorOverride("font_color", HintText);
            pane.AddChild(hint);

            _jsonPane = MakeJsonPane();
            pane.AddChild(_jsonPane);

            var btnRow = new HBoxContainer();
            btnRow.AddThemeConstantOverride("separation", 8);
            pane.AddChild(btnRow);
            var showBtn = new Button { Text = "Show JSON", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            showBtn.Pressed += ShowJson;
            btnRow.AddChild(showBtn);
            var applyBtn = new Button { Text = "Apply JSON", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            applyBtn.Pressed += ApplyJson;
            btnRow.AddChild(applyBtn);
            return pane;
        }

        // ── Mode + preset handlers ──────────────────────────────────────────────

        private void SwitchMode(bool simple)
        {
            _simpleMode = simple;
            _simplePane.Visible   = simple;
            _advancedPane.Visible = !simple;
            _simpleBtn.AddThemeColorOverride("font_color", simple ? Colors.White : DimText);
            _advancedBtn.AddThemeColorOverride("font_color", simple ? DimText : Colors.White);
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
            AddSpinRow(_simpleRows, amountLabel, 0, 9999, 1, FixedToDouble(_params.Amount), v => _params.Amount = ToFixed(v));

            if (_presetKind == AbilityPresets.Kind.AoeNuke)
                AddSpinRow(_simpleRows, "Radius", 0, 100, 0.5, FixedToDouble(_params.Radius), v => _params.Radius = ToFixed(v));

            if (_presetKind == AbilityPresets.Kind.SelfBuff)
                AddSpinRow(_simpleRows, "Duration (ticks)", 0, 99999, 1, _params.DurationTicks, v => _params.DurationTicks = (int)v);

            AddSpinRow(_simpleRows, "Cooldown (s)", 0, 600, 0.5, FixedToDouble(_params.Cooldown), v => _params.Cooldown = ToFixed(v));
            AddSpinRow(_simpleRows, "Cost: Energy", 0, 9999, 1, FixedToDouble(_params.CostEnergy), v => _params.CostEnergy = ToFixed(v));
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

        // ── Model build ─────────────────────────────────────────────────────────

        /// <summary>Build the in-memory ability from the Simple form: preset effect + costs + the header overrides.</summary>
        private AbilityDefinition BuildSimpleModel()
        {
            _params.Id = SanitizeId(_idEdit.Text);
            _params.DisplayName = _nameEdit.Text;
            AbilityDefinition def = AbilityPresets.Build(_presetKind, _params);
            def.Targeting = _targetingName;     // header override (the creator's explicit choice wins)
            return def;
        }

        // ── Raw-JSON escape hatch ───────────────────────────────────────────────

        private void ShowJson()
        {
            try
            {
                _jsonPane.Text = JsonSerializer.Serialize(BuildSimpleModel(), IndentedOptions);
                ClearStatus();
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
            ReflectModelIntoForm(r.Value.Value);
            ShowValid("Valid — applied to the form.");
        }

        /// <summary>Reflect a parsed/validated ability back into the header fields, and into the Simple-mode preset
        /// fields when the graph still matches a known preset shape (best-effort round-trip per UX-DR54).</summary>
        private void ReflectModelIntoForm(AbilityDefinition def)
        {
            _idEdit.Text   = def.Id;
            _nameEdit.Text = def.DisplayName;
            SetTargeting(def.Targeting);

            if (TryDetectPreset(def, out AbilityPresets.Kind kind, out AbilityPresets.Params p))
            {
                _presetKind = kind;
                _params = p;
                SelectDropdownId(_presetBtn, (int)kind);
                RebuildSimpleRows();
            }
            // else: an advanced graph (e.g. a Sequence) with no preset shape — leave the Simple form as-is; the
            // graph remains fully editable through the raw-JSON pane (that IS the advanced path in 2.5a).
        }

        /// <summary>Best-effort: recognise a preset shape from a parsed graph so Advanced edits round-trip into Simple.</summary>
        private static bool TryDetectPreset(AbilityDefinition def, out AbilityPresets.Kind kind, out AbilityPresets.Params p)
        {
            p = new AbilityPresets.Params
            {
                Id = def.Id, DisplayName = def.DisplayName,
                CostEnergy = def.CostEnergy, CostOre = def.CostOre, CostCrystal = def.CostCrystal, Cooldown = def.Cooldown,
            };
            switch (def.EffectGraph)
            {
                case DamageEffect d:
                    kind = AbilityPresets.Kind.TargetedDamage; p.Amount = d.Amount; return true;
                case HealEffect h:
                    kind = AbilityPresets.Kind.Heal; p.Amount = h.Amount; return true;
                case ApplyModifierEffect am:
                    kind = AbilityPresets.Kind.SelfBuff; p.Amount = am.Modifier.AttackDamageDelta;
                    p.DurationTicks = am.Modifier.DurationTicks; return true;
                case SearchAreaEffect { Child: DamageEffect cd } sa:
                    kind = AbilityPresets.Kind.AoeNuke; p.Amount = cd.Amount; p.Radius = sa.Radius; return true;
                default:
                    kind = AbilityPresets.Kind.TargetedDamage; return false;
            }
        }

        // ── Validate-gated save (AC1, AC3) ──────────────────────────────────────

        private void DoSave(bool reloadAfter)
        {
            AbilityValidationResult r;
            AbilityDefinition def;
            if (_simpleMode)
            {
                def = BuildSimpleModel();
                r = new AbilityValidator().Validate(def);
            }
            else
            {
                r = AbilityLoader.Load(_jsonPane.Text, CurrentEditorId());
                def = r.Ok ? r.Value.Value : null!;
            }

            if (!r.Ok) { ShowError(r.Error); return; }   // AC3: blocked, located error shown, NO file written

            string fileId = SanitizeId(def.Id);
            if (string.IsNullOrEmpty(fileId)) { ShowError("ability id must contain at least one [a-z0-9_] character."); return; }

            string abs = ProjectSettings.GlobalizePath($"res://resources/data/abilities/{fileId}.json");
            if (File.Exists(abs)) ConfirmOverwrite(abs, def, reloadAfter);
            else WriteFile(abs, def, reloadAfter);
        }

        private void WriteFile(string abs, AbilityDefinition def, bool reloadAfter)
        {
            try
            {
                string json = JsonSerializer.Serialize(def, IndentedOptions);
                string tmp = abs + ".tmp";
                File.WriteAllText(tmp, json);   // atomic: write to temp, then replace
                File.Move(tmp, abs, overwrite: true);
                GD.Print($"[AbilityEditor] Saved {abs}");
                ShowValid($"Saved {Path.GetFileName(abs)} — available in the next match.");
                if (reloadAfter) GetTree().ReloadCurrentScene();
            }
            catch (Exception ex)
            {
                ShowError($"Save failed: {ex.Message}");
            }
        }

        private void ConfirmOverwrite(string abs, AbilityDefinition def, bool reloadAfter)
        {
            var dlg = new ConfirmationDialog
            {
                Title = "Overwrite ability?",
                DialogText = $"{Path.GetFileName(abs)} already exists. Overwrite it?",
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
            ReflectModelIntoForm(def);
            _jsonPane.Text = JsonSerializer.Serialize(def, IndentedOptions);
            bool isPreset = TryDetectPreset(def, out _, out _);
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
        private static Fixed ToFixed(double v) => Fixed.FromRaw((int)Math.Round(v * Fixed.ONE));

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
            row.AddChild(dropdown);
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
