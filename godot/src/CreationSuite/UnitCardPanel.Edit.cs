#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using ProjectChimera.Core.Definitions;   // UnitDefinition, FactionDefinition, UnitDefinitionValidator, FactionWriter, UnitValidationResult
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraDialog, ChimeraTooltip, ChimeraValidationBadge
using ProjectChimera.UI.Theme;             // ThemeTokens

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 3.4 — the Unit Card Editor's EDIT surface (the partial that augments the 3.3 <see cref="UnitCardPanel"/>
    /// shell): editable fields bound to the live <see cref="UnitDefinition"/>, a Simple/Advanced disclosure with a
    /// raw-JSON escape hatch, fail-closed inline validation with per-field located badges (UX-DR55), undo/redo, a
    /// Save/New/Duplicate/Delete toolbar, and write-back to the faction JSON on disk.
    ///
    /// <para><b>Persistence model (D-6/D-10).</b> Field edits AND Create/Duplicate/Delete mutate the in-memory
    /// <c>_faction.Units</c> and push an in-memory undo entry — undo/redo never touch the file. <b>Save</b> is the one
    /// persistence action: it reconciles the whole in-memory unit list back into the faction file via
    /// <see cref="FactionWriter.SyncFactionUnits"/> (untouched units + buildings + faction keys stay byte-identical),
    /// atomically with a reload self-check. Edits are "usable in playtest" on the next Play/match (the in-memory
    /// <see cref="FactionDefinition"/> the default path re-reads); the file is authoritative for a restart.</para>
    /// </summary>
    public partial class UnitCardPanel
    {
        // The closed authorable sets the dropdowns offer (mirror UnitDefinitionValidator's sets).
        private static readonly string[] Categories = { "Worker", "Melee", "Ranged", "Siege", "Air", "Structure" };
        private static readonly string[] DamageTypes = { "Normal", "Pierce", "Siege", "Magic", "Hero" };
        private static readonly string[] ArmorTypes = { "Unarmored", "Light", "Medium", "Heavy", "Fortified", "Hero" };
        private static readonly string[] SeparationPriorities = { "Yield", "Normal", "Push" };
        private const double StatMax = 32767;   // one below the 16.16 ceiling (the form's first line of defence)

        private bool _lastValid = true;         // last validation verdict (drives the Save button + F5 gate)

        // ── Input: undo/redo + the Edit→Play gate (D-6/D-11) ─────────────────────────

        /// <inheritdoc/>
        public override void _Input(InputEvent @event)
        {
            if (_panel is null || !_panel.Visible) return;   // only own input while open (⇒ Edit mode)
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            // Ctrl+Z / Ctrl+Y route through THIS history; SetInputAsHandled so EntityPlacer's Ctrl+Z (also _Input)
            // doesn't also fire. The panel is the last-added scene node (phase 24), so its _Input runs first (D-6).
            if (key.CtrlPressed && key.Keycode == Key.Z) { _history.Undo(); GetViewport().SetInputAsHandled(); return; }
            if (key.CtrlPressed && key.Keycode == Key.Y) { _history.Redo(); GetViewport().SetInputAsHandled(); return; }

            // D-11 Edit→Play gate: F5 toggles the mode in GameState._UnhandledInput (which runs AFTER _Input). Block
            // it ONLY when the current unit is invalid; a clean/valid card lets F5 through (and OnModeChanged closes it).
            if (key.Keycode == Key.F5 && _current != null && !RevalidateAndReflect())
            {
                ShowError("Fix the highlighted field(s) before playtesting.");
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Editable body (Task 2/3) — replaces the 3.3 read-only readouts ───────────

        private void BuildEditableBody(UnitDefinition def)
        {
            _building = true;   // suppress live handlers while we seed control values

            // ── Simple (always visible) ──
            AddSection(_bodyHost, "Identity");
            AddText(_bodyHost, "Id", "id", "Id",
                "The unique id ([a-z0-9_]). It is the render slot + scenario reference — must be unique in this faction.",
                () => def.Id, v => def.Id = v, def);
            AddText(_bodyHost, "Name", "display_name", "Display Name", "The human-readable name shown in menus.",
                () => def.DisplayName, v => def.DisplayName = v, def);
            AddSelect(_bodyHost, "Archetype", "category", "Archetype", "The movement & role class (one of six).",
                Categories, () => def.Category, v => def.Category = v, def);
            AddText(_bodyHost, "Model", "mesh_path", "Model",
                "res:// path to the unit's GLB. Blank = box placeholder. (A browse button arrives in Story 3.5.)",
                () => def.MeshPath ?? "", v => def.MeshPath = string.IsNullOrEmpty(v) ? null : v, def);

            AddSection(_bodyHost, "Combat");
            AddSelect(_bodyHost, "Damage type", "damage_type", "Damage Type",
                "The matrix row that picks the multiplier vs each armor class.",
                DamageTypes, () => def.DamageType, v => def.DamageType = v, def);
            AddSelect(_bodyHost, "Armor type", "armor_type", "Armor Type",
                "The matrix column that scales incoming damage by type.",
                ArmorTypes, () => def.ArmorType, v => def.ArmorType = v, def);
            AddNumFloat(_bodyHost, "Attack", "attack_damage", "Attack Damage", "Base damage per hit, before matrix + armor.",
                0.5, () => def.AttackDamage, v => def.AttackDamage = v, def);
            AddNumFloat(_bodyHost, "Range", "attack_range", "Attack Range", "0 ≈ melee. Distance the unit can strike from.",
                0.5, () => def.AttackRange, v => def.AttackRange = v, def);
            AddNumFloat(_bodyHost, "Atk interval", "attack_speed", "Attack Interval", "Seconds between attacks — lower is faster.",
                0.05, () => def.AttackSpeed, v => def.AttackSpeed = v, def);

            AddSection(_bodyHost, "Stats");
            AddNumFloat(_bodyHost, "HP", "hp", "Hit Points", "The unit's health pool. It dies at 0.",
                1, () => def.Hp, v => def.Hp = v, def);
            AddNumFloat(_bodyHost, "Speed", "speed", "Move Speed", "World units per second the unit travels.",
                0.1, () => def.Speed, v => def.Speed = v, def);
            AddNumInt(_bodyHost, "Supply", "supply", "Supply", "Population this unit draws from your supply cap.",
                () => def.Supply, v => def.Supply = v, def);
            AddNumFloat(_bodyHost, "Vision", "vision_range", "Vision Range", "How far the unit reveals the map.",
                0.5, () => def.VisionRange, v => def.VisionRange = v, def);

            AddSection(_bodyHost, "Economy");
            AddNumInt(_bodyHost, "Ore cost", "cost_ore", "Ore Cost", "Ore spent to train this unit.",
                () => def.CostOre, v => def.CostOre = v, def);
            AddNumInt(_bodyHost, "Crystal cost", "cost_crystal", "Crystal Cost", "Crystal (the scarce resource) spent to train this unit.",
                () => def.CostCrystal, v => def.CostCrystal = v, def);

            // ── Advanced (toggled by the Segment) ──
            _advancedHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = _segment.Active == 1 };
            _advancedHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _bodyHost.AddChild(_advancedHost);

            AddSection(_advancedHost, "Advanced stats");
            AddNumFloat(_advancedHost, "Flat armor", "armor", "Flat Armor", "Subtracted from post-matrix damage (0 = none).",
                1, () => def.Armor, v => def.Armor = v, def);
            AddNumFloat(_advancedHost, "Train time", "train_time", "Train Time", "Seconds to train this unit at a producing building.",
                0.5, () => def.TrainTime, v => def.TrainTime = v, def);
            AddNumFloat(_advancedHost, "Splash radius", "splash_radius", "Splash Radius", "AoE splash on projectile hit (0 = none).",
                0.5, () => def.SplashRadius, v => def.SplashRadius = v, def);
            AddNumFloat(_advancedHost, "Collision radius", "collision_radius", "Collision Radius", "Per-unit separation radius (default 1).",
                0.1, () => def.CollisionRadius, v => def.CollisionRadius = v, def);
            AddNumFloat(_advancedHost, "Mesh scale", "mesh_scale", "Mesh Scale", "Visual scale of the model. (Live re-render is Story 3.5.)",
                0.05, () => def.MeshScale, v => def.MeshScale = v, def);
            AddNumFloat(_advancedHost, "Max energy", "max_energy", "Max Energy", "Ability-resource pool (0 = cannot cast energy abilities).",
                1, () => def.MaxEnergy, v => def.MaxEnergy = v, def);
            AddSelect(_advancedHost, "Separation", "separation_priority", "Separation Priority",
                "Crowd-steering precedence: a Push unit holds ground vs a Yield neighbour.",
                SeparationPriorities, () => def.SeparationPriority, v => def.SeparationPriority = v, def);

            AddSection(_advancedHost, "Lists (comma-separated)");
            AddCommaList(_advancedHost, "Prerequisites", "prerequisites", "Prerequisites", "Building ids required before this unit can be trained.",
                () => def.Prerequisites, v => def.Prerequisites = v ?? Array.Empty<string>(), nullable: false, def);
            AddCommaList(_advancedHost, "Tags", "tags", "Tags", "Classification tags (Organic / Mechanical / Magical).",
                () => def.Tags, v => def.Tags = v, nullable: true, def);
            AddCommaList(_advancedHost, "Attack domains", "attack_domains", "Attack Domains", "Domains this unit can attack (Ground / Air / Structure). Blank = all.",
                () => def.AttackDomains, v => def.AttackDomains = v, nullable: true, def);
            AddAbilitiesRow(_advancedHost, def);

            AddSection(_advancedHost, "Raw JSON (this unit)");
            BuildRawPane(_advancedHost, def);

            _building = false;
        }

        private void OnSegmentChanged(int index)
        {
            if (_advancedHost != null) _advancedHost.Visible = index == 1;
        }

        private static void AddSection(Control parent, string text) => parent.AddChild(ChimeraComponents.FieldLabel(text));

        private void AddFieldRow(Control parent, string label, Control control, ChimeraValidationBadge badge)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            var lbl = ChimeraComponents.FieldLabel(label);
            lbl.CustomMinimumSize = new Vector2(108, 0);
            lbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(lbl);
            control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(control);
            row.AddChild(badge);
            parent.AddChild(row);
        }

        private ChimeraValidationBadge MakeBadge(string key)
        {
            var b = ChimeraValidationBadge.Create();
            _badges[key] = b;
            return b;
        }

        /// <summary>Attach a field-role hover-and-focus tooltip to an interactive control (AC3 / UX-DR53).</summary>
        private static void AttachFieldTip(Control target, string term, string body)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            ChimeraTooltip.Attach(target, term, body, ChimeraTooltip.TooltipRole.Field);
        }

        // ── Field builders: live writeback (no rebuild) + a focus-session / per-select undo entry ──

        private void AddText(Control parent, string label, string key, string term, string body,
                             Func<string> get, Action<string> set, UnitDefinition def)
        {
            LineEdit input = ChimeraComponents.Input(text: get());
            string snap = get();
            input.FocusEntered += () => snap = get();
            input.TextChanged += t => { if (_building) return; set(t); OnLiveChanged(key); };
            input.TextSubmitted += _ => { CommitStr(key, snap, get(), set, def); snap = get(); };
            input.FocusExited += () => { CommitStr(key, snap, get(), set, def); snap = get(); };
            AttachFieldTip(input, term, body);
            AddFieldRow(parent, label, input, MakeBadge(key));
        }

        private void AddNumFloat(Control parent, string label, string key, string term, string body,
                                 double step, Func<float> get, Action<float> set, UnitDefinition def)
        {
            SpinBox spin = ChimeraComponents.NumInput(value: get(), min: 0, max: StatMax, step: step);
            LineEdit le = spin.GetLineEdit();
            float snap = get();
            le.FocusEntered += () => snap = get();
            spin.ValueChanged += v => { if (_building) return; set((float)v); OnLiveChanged(key); };
            le.FocusExited += () =>
            {
                float now = get();
                if (now != snap) PushValue(def, s => set((float)s), snap, now);
                snap = now;
            };
            AttachFieldTip(le, term, body);
            AddFieldRow(parent, label, spin, MakeBadge(key));
        }

        private void AddNumInt(Control parent, string label, string key, string term, string body,
                               Func<int> get, Action<int> set, UnitDefinition def)
        {
            SpinBox spin = ChimeraComponents.NumInput(value: get(), min: 0, max: StatMax, step: 1);
            LineEdit le = spin.GetLineEdit();
            int snap = get();
            le.FocusEntered += () => snap = get();
            spin.ValueChanged += v => { if (_building) return; set((int)Math.Round(v)); OnLiveChanged(key); };
            le.FocusExited += () =>
            {
                int now = get();
                if (now != snap) PushValue(def, s => set((int)Math.Round(s)), snap, now);
                snap = now;
            };
            AttachFieldTip(le, term, body);
            AddFieldRow(parent, label, spin, MakeBadge(key));
        }

        private void AddSelect(Control parent, string label, string key, string term, string body,
                               string[] items, Func<string> get, Action<string> set, UnitDefinition def)
        {
            OptionButton sel = ChimeraComponents.Select(items);
            int cur = Array.IndexOf(items, get());
            if (cur >= 0) sel.Selected = cur;
            sel.ItemSelected += idx =>
            {
                if (_building) return;
                string old = get();
                string nu = items[(int)idx];
                if (old == nu) return;
                set(nu);
                UnitDefinition t = def;
                PushHistory(() => { set(nu); GoToUnit(t); }, () => { set(old); GoToUnit(t); });
                OnLiveChanged(key);
            };
            AttachFieldTip(sel, term, body);
            AddFieldRow(parent, label, sel, MakeBadge(key));
        }

        private void AddCommaList(Control parent, string label, string key, string term, string body,
                                  Func<string[]?> get, Action<string[]?> set, bool nullable, UnitDefinition def)
        {
            string Join() { string[]? a = get(); return a == null ? "" : string.Join(", ", a); }
            string[]? Parse(string s)
            {
                string[] parts = s.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                return parts.Length == 0 && nullable ? null : parts;
            }
            LineEdit input = ChimeraComponents.Input(placeholder: "comma, separated", text: Join());
            string snap = Join();
            input.FocusEntered += () => snap = Join();
            input.TextChanged += t => { if (_building) return; set(Parse(t)); OnLiveChanged(key); };
            input.FocusExited += () =>
            {
                string now = Join();
                if (now != snap)
                {
                    string[]? oldArr = Parse(snap), newArr = Parse(now);
                    UnitDefinition t = def;
                    PushHistory(() => { set(newArr); GoToUnit(t); }, () => { set(oldArr); GoToUnit(t); });
                }
                snap = now;
            };
            AttachFieldTip(input, term, body);
            AddFieldRow(parent, label, input, MakeBadge(key));
        }

        private void AddAbilitiesRow(Control parent, UnitDefinition def)
        {
            string txt = def.Abilities is { Length: > 0 } ? string.Join(", ", def.Abilities) : "(none)";
            LineEdit display = ChimeraComponents.Input(text: txt);
            display.Editable = false;   // read-only in 3.4 — the structured ability picker is Story 3.6; edit via Raw JSON
            AttachFieldTip(display, "Abilities",
                "The unit's abilities (read-only here — edit via Raw JSON; the ability picker is Story 3.6). An undefined id blocks Save.");
            AddFieldRow(parent, "Abilities", display, MakeBadge("abilities"));
        }

        // Commit a string-field change to the undo stack (focus-session granularity).
        private void CommitStr(string key, string oldVal, string newVal, Action<string> set, UnitDefinition def)
        {
            _ = key;
            if (oldVal == newVal) return;
            UnitDefinition t = def;
            PushHistory(() => { set(newVal); GoToUnit(t); }, () => { set(oldVal); GoToUnit(t); });
        }

        // Commit a numeric change (old/new carried as double; the setter re-narrows).
        private void PushValue(UnitDefinition def, Action<double> set, double oldVal, double newVal)
        {
            UnitDefinition t = def;
            PushHistory(() => { set(newVal); GoToUnit(t); }, () => { set(oldVal); GoToUnit(t); });
        }

        private void OnLiveChanged(string key)
        {
            if (_current == null) return;
            if (key is "mesh_path" or "mesh_scale") UpdatePreview(_current);
            if (key is "display_name" or "id" or "category") RefreshHeader();
            RevalidateAndReflect();   // proactive badges + Save-button state (no form rebuild — controls keep focus)
        }

        private void RefreshHeader()
        {
            if (_current == null) return;
            foreach (Node c in _headerHost.GetChildren()) { _headerHost.RemoveChild(c); c.QueueFree(); }
            BuildHeader(_current);
        }

        // ── Raw-JSON escape hatch (D-5) ──────────────────────────────────────────────

        private void BuildRawPane(Control parent, UnitDefinition def)
        {
            var hint = Body("Edit this unit's JSON directly (e.g. combat_feedback). On Save a dirty pane wins — validated, then folded back.", ThemeTokens.TextLo);
            hint.AutowrapMode = TextServer.AutowrapMode.Word;
            hint.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txs, ThemeTokens.Type));
            parent.AddChild(hint);

            _jsonPane = MakeJsonPane();
            _jsonPane.TextChanged += () => { if (!_suppressPaneDirty) _paneDirty = true; };
            parent.AddChild(_jsonPane);
            SetPaneText(FactionWriter.SerializeUnitClean(def));   // seed programmatically (not dirty)

            var sync = ChimeraComponents.Button("Sync JSON from form", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            sync.Pressed += () => { if (_current != null) SetPaneText(FactionWriter.SerializeUnitClean(_current)); };
            AttachFieldTip(sync, "Sync JSON", "Re-render this unit's JSON from the current form values (discards manual pane edits).");
            parent.AddChild(sync);
        }

        private TextEdit MakeJsonPane()
        {
            var te = new TextEdit
            {
                PlaceholderText = "{ }",
                WrapMode = TextEdit.LineWrappingMode.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 200),
            };
            te.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontMono, ThemeTokens.Type));
            te.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Tsm, ThemeTokens.Type));
            te.AddThemeColorOverride("font_color", Tok(ThemeTokens.TextHi));
            return te;
        }

        private void SetPaneText(string text)
        {
            if (_jsonPane == null) return;
            _suppressPaneDirty = true;
            _jsonPane.Text = text;
            _suppressPaneDirty = false;
            _paneDirty = false;
        }

        // ── Validation + located badges (Task 5; D-4/D-9/D-11) ───────────────────────

        /// <summary>Re-run the validator (+ the presentation mesh check), paint badges, update the status line + Save
        /// button. Does NOT rebuild the form (controls keep focus). Returns true when the current unit is fully valid.</summary>
        private bool RevalidateAndReflect()
        {
            foreach (ChimeraValidationBadge b in _badges.Values) b.Clear();

            if (_current == null) { ClearStatus(); _lastValid = true; UpdateToolbarEnabled(); return true; }

            UnitValidationResult res = _validator.Validate(_current, _registry, _faction?.Units);
            string? meshErr = MeshError(_current);

            foreach ((string key, string msg) in res.Errors) ShowBadge(key, msg);
            if (meshErr != null) ShowBadge("mesh_path", meshErr);

            bool ok = res.Ok && meshErr == null;
            _lastValid = ok;
            if (ok) ShowOk("Valid — Save to write to the faction file.");
            else ShowError($"{res.Errors.Count + (meshErr != null ? 1 : 0)} field(s) need attention before saving.");
            UpdateToolbarEnabled();
            return ok;
        }

        private void ShowBadge(string key, string message)
        {
            if (_badges.TryGetValue(key, out ChimeraValidationBadge? b)) b.ShowError(message);
            // else: no field control home for this key (should not happen); the status line still summarizes the count.
        }

        /// <summary>The one validation rule that needs Godot (D-9): the mesh path must resolve, or be blank (box placeholder).</summary>
        private static string? MeshError(UnitDefinition def)
        {
            string mp = def.MeshPath ?? "";
            if (mp.Length > 0 && !ResourceLoader.Exists(mp))
                return $"unit '{def.Id}'.mesh_path: '{mp}' isn't an imported resource (leave blank for a box placeholder).";
            return null;
        }

        // ── Undo/redo (Task 7; D-6) ──────────────────────────────────────────────────

        private void PushHistory(Action redo, Action undo) => _history.Push(redo, undo);

        /// <summary>Navigate the browse cursor to <paramref name="u"/> and rebuild the form (undo/redo re-render — D-6).</summary>
        private void GoToUnit(UnitDefinition u)
        {
            if (_faction == null) return;
            int i = _faction.Units.IndexOf(u);
            if (i >= 0) _index = i;
            Refresh();
        }

        // ── Toolbar + list ops (Task 6/8; D-7) ───────────────────────────────────────

        private Control BuildToolbar()
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            _saveBtn = ChimeraComponents.Button("Save", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Sm);
            _saveBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _saveBtn.Pressed += () => DoSave(reloadAfter: false);
            AttachFieldTip(_saveBtn, "Save", "Validate, then write ALL edits to the faction file. Applies on the next playtest/match.");
            row.AddChild(_saveBtn);

            _newBtn = ChimeraComponents.Button("New", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            _newBtn.Pressed += DoCreate;
            AttachFieldTip(_newBtn, "New unit", "Add a new unit with default stats and a unique id.");
            row.AddChild(_newBtn);

            _dupBtn = ChimeraComponents.Button("Duplicate", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            _dupBtn.Pressed += DoDuplicate;
            AttachFieldTip(_dupBtn, "Duplicate", "Clone the current unit with a new id (its abilities/feedback come along).");
            row.AddChild(_dupBtn);

            _deleteBtn = ChimeraComponents.Button("Delete", ChimeraComponents.ButtonVariant.Danger, ChimeraComponents.ButtonSize.Sm);
            _deleteBtn.Pressed += DoDelete;
            AttachFieldTip(_deleteBtn, "Delete", "Remove the current unit (asks to confirm first).");
            row.AddChild(_deleteBtn);

            return row;
        }

        private void UpdateToolbarEnabled()
        {
            bool hasUnit = _current != null && _faction != null && _faction.Units.Count > 0;
            if (_saveBtn != null!) _saveBtn.Disabled = !hasUnit || !_lastValid;
            if (_dupBtn != null!) _dupBtn.Disabled = !hasUnit;
            if (_deleteBtn != null!) _deleteBtn.Disabled = !hasUnit;
            if (_newBtn != null!) _newBtn.Disabled = _faction == null;
        }

        private void DoCreate()
        {
            if (_faction == null) return;
            var def = new UnitDefinition { Id = UniqueId("new_unit"), DisplayName = "New Unit", Category = "Melee" };
            _faction.Units.Add(def);
            _index = _faction.Units.Count - 1;
            UnitDefinition captured = def;
            PushHistory(
                redo: () => { if (!_faction.Units.Contains(captured)) _faction.Units.Add(captured); GoToUnit(captured); },
                undo: () => RemoveFromList(captured));
            Refresh();
            ShowOk($"Added '{def.Id}' — edit its fields, then Save to write it to the file.");
        }

        private void DoDuplicate()
        {
            if (_faction == null || _current == null) return;
            UnitDefinition clone = CloneUnit(_current, UniqueId(_current.Id + "_copy"));
            _faction.Units.Add(clone);
            _index = _faction.Units.Count - 1;
            UnitDefinition captured = clone;
            PushHistory(
                redo: () => { if (!_faction.Units.Contains(captured)) _faction.Units.Add(captured); GoToUnit(captured); },
                undo: () => RemoveFromList(captured));
            Refresh();
            ShowOk($"Duplicated as '{clone.Id}' — Save to write it to the file.");
        }

        private void DoDelete()
        {
            if (_faction == null || _current == null || _faction.Units.Count == 0) return;
            UnitDefinition victim = _current;
            string id = victim.Id;
            var dlg = ChimeraDialog.Create("Delete unit?",
                $"Remove '{id}' from this faction? Undoable until you close the editor; Save writes it to the file.");
            dlg.AddConfirm("Delete", danger: true);
            dlg.AddCancel("Cancel");
            dlg.Confirmed += () =>
            {
                int at = _faction.Units.IndexOf(victim);
                if (at < 0) return;
                _faction.Units.RemoveAt(at);
                if (_index >= _faction.Units.Count) _index = Math.Max(0, _faction.Units.Count - 1);
                UnitDefinition captured = victim;
                int atI = at;
                PushHistory(
                    redo: () => RemoveFromList(captured),
                    undo: () => { _faction.Units.Insert(Math.Min(atI, _faction.Units.Count), captured); GoToUnit(captured); });
                Refresh();
                ShowOk($"Deleted '{id}' — Save to write the change to the file.");
            };
            dlg.Open(this);
        }

        private void RemoveFromList(UnitDefinition u)
        {
            if (_faction == null) return;
            _faction.Units.Remove(u);
            if (_index >= _faction.Units.Count) _index = Math.Max(0, _faction.Units.Count - 1);
            Refresh();
        }

        private string UniqueId(string baseId)
        {
            string id = UnitDefinitionValidator.SanitizeId(baseId);
            if (id.Length == 0) id = "new_unit";
            if (!IdExists(id)) return id;
            for (int i = 2; i < 100000; i++)
            {
                string candidate = $"{id}_{i}";
                if (!IdExists(candidate)) return candidate;
            }
            return id;   // pathological fallback (validator will still reject a dup on Save)
        }

        private bool IdExists(string id) => _faction != null && _faction.Units.Exists(u => u.Id == id);

        private static UnitDefinition CloneUnit(UnitDefinition s, string newId) => new()
        {
            Id = newId,
            DisplayName = s.DisplayName,
            Category = s.Category,
            MeshPath = s.MeshPath,
            Hp = s.Hp, Speed = s.Speed, AttackDamage = s.AttackDamage, AttackRange = s.AttackRange, AttackSpeed = s.AttackSpeed,
            DamageType = s.DamageType, ArmorType = s.ArmorType, Armor = s.Armor,
            CostOre = s.CostOre, CostCrystal = s.CostCrystal, Supply = s.Supply,
            MeshScale = s.MeshScale, TrainTime = s.TrainTime, VisionRange = s.VisionRange,
            SplashRadius = s.SplashRadius, CollisionRadius = s.CollisionRadius, MaxEnergy = s.MaxEnergy,
            SeparationPriority = s.SeparationPriority,
            Prerequisites = (string[])s.Prerequisites.Clone(),
            Abilities = (string[])s.Abilities.Clone(),
            AttackDomains = s.AttackDomains?.Clone() as string[],
            Tags = s.Tags?.Clone() as string[],
            IsHero = s.IsHero,
            CombatFeedback = s.CombatFeedback,   // shared presentation-DTO ref (a raw-hatch edit re-parses a fresh POCO)
        };

        // ── Save = persist the in-memory list to the file (Task 6; D-1/D-10) ─────────

        private void DoSave(bool reloadAfter)
        {
            if (_current == null || _faction == null) return;
            bool saved = (_jsonPane != null && _paneDirty) ? SaveFromRawPane() : SaveFromForm();
            if (saved && reloadAfter) GetTree().ReloadCurrentScene();
        }

        private bool SaveFromForm()
        {
            if (!RevalidateAndReflect()) { ShowError("Fix the highlighted field(s) before saving."); return false; }
            if (!PersistSync()) return false;
            ShowOk("Saved — applies on next playtest/match.");
            return true;
        }

        private bool SaveFromRawPane()
        {
            if (_jsonPane == null || _faction == null || _current == null) return false;
            UnitDefinition? parsed;
            try { parsed = JsonSerializer.Deserialize<UnitDefinition>(_jsonPane.Text, FactionDefinition.JsonOptions); }
            catch (Exception ex) { ShowError($"Raw JSON didn't parse: {ex.Message}"); return false; }
            if (parsed == null) { ShowError("Raw JSON is empty."); return false; }

            // Validate the parsed unit against the OTHER units (exclude the one it replaces) + the mesh check.
            var others = _faction.Units.Where(u => !ReferenceEquals(u, _current)).ToList();
            UnitValidationResult res = _validator.Validate(parsed, _registry, others);
            string? meshErr = MeshError(parsed);
            if (!res.Ok || meshErr != null)
            {
                string first = meshErr ?? (res.Errors.Count > 0 ? res.Errors[0].Message : "invalid");
                ShowError($"Raw JSON invalid — {first}");
                return false;
            }

            _faction.Units[_index] = parsed;   // fold the pane into the in-memory model
            _current = parsed;
            if (!PersistSync()) return false;
            _paneDirty = false;
            Refresh();   // rebuild the form from the folded model (re-seeds the pane, not dirty)
            ShowOk("Saved (raw JSON) — applies on next playtest/match.");
            return true;
        }

        /// <summary>Reconcile the whole in-memory unit list into the faction file, atomically, with a reload self-check.</summary>
        private bool PersistSync()
        {
            if (_faction == null) return false;
            if (string.IsNullOrEmpty(_factionJsonPath)) { ShowError("No faction file is bound — cannot save."); return false; }

            string abs = ProjectSettings.GlobalizePath(_factionJsonPath);
            string tmp = abs + ".tmp";
            try
            {
                string current = File.ReadAllText(abs);
                string patched = FactionWriter.SyncFactionUnits(current, _faction.Units);
                File.WriteAllText(tmp, patched);
                _ = FactionDefinition.LoadFromFile(tmp);   // self-check: refuse to report "Saved" for a file that won't reload
                File.Move(tmp, abs, overwrite: true);
                GD.Print($"[UnitCard] Saved {abs} ({_faction.Units.Count} units).");
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave no stray .tmp */ }
                ShowError($"Save failed: {ex.Message}");
                return false;
            }
        }

        // ── Status line ──────────────────────────────────────────────────────────────

        private void ShowOk(string msg)
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = msg;
            _statusLabel.AddThemeColorOverride("font_color", Tok(ThemeTokens.Ok));
        }

        private void ShowError(string msg)
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = msg;
            _statusLabel.AddThemeColorOverride("font_color", Tok(ThemeTokens.Danger));
        }

        private void ClearStatus()
        {
            if (_statusLabel != null!) _statusLabel.Visible = false;
        }
    }
}
