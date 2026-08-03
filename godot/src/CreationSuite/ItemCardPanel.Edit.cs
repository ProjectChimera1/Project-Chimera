#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Godot;
using ProjectChimera.Core;                // Fixed
using ProjectChimera.Core.Definitions;    // ItemDefinition, ItemWriter, ItemLoader, ContentJson
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraTooltip, ChimeraValidationBadge, ChimeraDialog

namespace ProjectChimera.CreationSuite
{
    /// <summary>Story 3.16 — the Item Card Editor's edit surface (fields, validation, per-item persistence, toolbar,
    /// undo, F5 gate). Partner file of <see cref="ItemCardPanel"/> (shell + browse). Mirrors <c>UnitCardPanel.Edit.cs</c>.</summary>
    public partial class ItemCardPanel
    {
        // Spinner bounds mirror the fail-closed validator caps so the editor can never dial in a value the
        // Validated<T> gate will reject: stat deltas clamp to ItemDefinitionValidator.MAX_ITEM_STAT_DELTA and costs
        // clamp to the 16.16 integer ceiling (short.MaxValue) that CheckCost enforces.
        private static readonly int DeltaCap = ItemDefinitionValidator.MAX_ITEM_STAT_DELTA.ToInt();
        // move_speed_delta clamps to its own much tighter cap (DW-42) so the Speed spinner can never dial in a value the
        // fail-closed MAX_MOVE_SPEED_DELTA gate rejects. DW-452: the range is READ FROM the Godot-free
        // ItemDefinitionValidator.MoveSpeedSpinnerRange() helper that Tier-1 pins to ±MAX_MOVE_SPEED_DELTA, so this
        // clamp can no longer silently decouple from the validator cap with every test still green.
        private static readonly (int Min, int Max) MoveSpeedRange = ItemDefinitionValidator.MoveSpeedSpinnerRange();
        private const int CostCap = short.MaxValue;

        // ── Form construction ────────────────────────────────────────────────────

        private void BuildBody(ItemDefinition def)
        {
            _building = true;

            AddSection(_bodyHost, "Identity");
            AddText(_bodyHost, "Id", "id", "Item id", "The stable id — also the JSON file name and reference key.",
                () => _current!.Id, v => _current!.Id = v);
            AddText(_bodyHost, "Name", "display_name", "Display name", "Shown in the shop + inventory UI.",
                () => _current!.DisplayName, v => _current!.DisplayName = v);

            AddSection(_bodyHost, "Charges");
            AddNumInt(_bodyHost, "Charges", "charges", "Charges",
                "0 = a carried STAT item; >0 = a consumable that fires its effect graph and deletes at zero.",
                () => _current!.Charges, v => _current!.Charges = v, 0, 999);

            AddSection(_bodyHost, "Carried stat deltas");
            AddNumFloat(_bodyHost, "Max HP", "max_health_delta", "Max-HP delta", "Flat max-health granted while carried.",
                () => _current!.MaxHealthDelta.ToFloat(), v => _current!.MaxHealthDelta = Fixed.FromFloat(v), -DeltaCap, DeltaCap);
            AddNumFloat(_bodyHost, "Attack", "attack_damage_delta", "Attack delta", "Flat attack-damage granted while carried.",
                () => _current!.AttackDamageDelta.ToFloat(), v => _current!.AttackDamageDelta = Fixed.FromFloat(v), -DeltaCap, DeltaCap);
            AddNumFloat(_bodyHost, "Speed", "move_speed_delta", "Move-speed delta", "Flat move-speed granted while carried.",
                () => _current!.MoveSpeedDelta.ToFloat(), v => _current!.MoveSpeedDelta = Fixed.FromFloat(v), MoveSpeedRange.Min, MoveSpeedRange.Max);
            AddNumFloat(_bodyHost, "Armor", "armor_delta", "Armor delta", "Flat armor granted while carried.",
                () => _current!.ArmorDelta.ToFloat(), v => _current!.ArmorDelta = Fixed.FromFloat(v), -DeltaCap, DeltaCap);

            // ── Advanced ──
            _advancedHost = new VBoxContainer { Visible = _segment.Active == 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _advancedHost.AddThemeConstantOverride("separation", 4);
            _bodyHost.AddChild(_advancedHost);

            AddSection(_advancedHost, "Presentation");
            AddText(_advancedHost, "Icon", "icon", "Icon path", "res:// path to the icon texture. Must exist if set.",
                () => _current!.Icon, v => _current!.Icon = v);

            AddSection(_advancedHost, "Shop cost");
            AddNumFloat(_advancedHost, "Cost ore", "cost_ore", "Ore cost", "Ore price when bought at a shop building.",
                () => _current!.CostOre.ToFloat(), v => _current!.CostOre = Fixed.FromFloat(v), 0, CostCap);
            AddNumFloat(_advancedHost, "Cost crystal", "cost_crystal", "Crystal cost", "Crystal price when bought at a shop.",
                () => _current!.CostCrystal.ToFloat(), v => _current!.CostCrystal = Fixed.FromFloat(v), 0, CostCap);

            AddSection(_advancedHost, "Raw JSON (whole item, incl. effect graph)");
            BuildRawPane(_advancedHost, def);

            _building = false;
        }

        private void AddSection(Control parent, string text) => parent.AddChild(ChimeraComponents.FieldLabel(text));

        private void AddFieldRow(Control parent, string label, string key, Control control)
        {
            var row = new HBoxContainer();
            var lbl = ChimeraComponents.FieldLabel(label);
            lbl.CustomMinimumSize = new Vector2(92, 0);
            row.AddChild(lbl);
            control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(control);
            var badge = ChimeraValidationBadge.Create();
            _badges[key] = badge;
            row.AddChild(badge);
            parent.AddChild(row);
        }

        private void AddText(Control parent, string label, string key, string term, string body,
                             Func<string> get, Action<string> set)
        {
            var input = ChimeraComponents.Input("", get());
            input.MouseFilter = Control.MouseFilterEnum.Stop;
            input.FocusMode   = Control.FocusModeEnum.All;
            input.TextChanged   += t => { if (_building) return; set(t); Revalidate(); };
            input.TextSubmitted += _ => CommitEdit();
            input.FocusExited   += CommitEdit;
            ChimeraTooltip.Attach(input, term, body, ChimeraTooltip.TooltipRole.Field);
            AddFieldRow(parent, label, key, input);
        }

        private void AddNumInt(Control parent, string label, string key, string term, string body,
                               Func<int> get, Action<int> set, double min, double max)
        {
            var spin = ChimeraComponents.NumInput(get(), min, max, 1);
            spin.ValueChanged += v => { if (_building) return; set((int)Math.Round(v)); Revalidate(); CommitEdit(); };
            ChimeraTooltip.Attach(spin, term, body, ChimeraTooltip.TooltipRole.Field);
            AddFieldRow(parent, label, key, spin);
        }

        private void AddNumFloat(Control parent, string label, string key, string term, string body,
                                 Func<float> get, Action<float> set, double min, double max)
        {
            var spin = ChimeraComponents.NumInput(get(), min, max, 1);
            spin.ValueChanged += v => { if (_building) return; set((float)v); Revalidate(); CommitEdit(); };
            ChimeraTooltip.Attach(spin, term, body, ChimeraTooltip.TooltipRole.Field);
            AddFieldRow(parent, label, key, spin);
        }

        private void BuildRawPane(Control parent, ItemDefinition def)
        {
            _jsonPane = new TextEdit { CustomMinimumSize = new Vector2(0, 180), Editable = true };
            _jsonPane.TextChanged += () => { if (!_suppressPaneDirty) _paneDirty = true; };
            parent.AddChild(_jsonPane);
            SetPaneText(ItemWriter.Serialize(def));
        }

        private void SetPaneText(string t)
        {
            if (_jsonPane == null) return;
            _suppressPaneDirty = true;
            _jsonPane.Text = t;
            _suppressPaneDirty = false;
            _paneDirty = false;
        }

        // ── Validation ───────────────────────────────────────────────────────────

        private bool Revalidate()
        {
            foreach (var b in _badges.Values) b.Clear();
            if (_current == null) { _lastValid = false; UpdateToolbarEnabled(); return false; }
            var res = _validator.ValidateFields(_current, p => ResourceLoader.Exists(p));
            foreach ((string key, string msg) in res.Errors)
                if (_badges.TryGetValue(key, out var b)) b.ShowError(msg);
            _lastValid = res.Ok;
            _statusLabel.Text = res.Ok ? "Valid." : $"{res.Errors.Count} field error(s).";
            UpdateToolbarEnabled();
            return res.Ok;
        }

        private void UpdateToolbarEnabled()
        {
            _saveBtn.Disabled   = _current == null || !_lastValid;
            _dupBtn.Disabled    = _current == null;
            _deleteBtn.Disabled = _items.Count == 0;
        }

        // ── Undo/redo (whole-item JSON snapshots) ──────────────────────────────────

        private void CommitEdit()
        {
            if (_current == null) return;
            string after = ItemWriter.Serialize(_current);
            if (after == _preEditJson) return;
            string before = _preEditJson;
            int idx = _index;
            _history.Push(redo: () => ApplyItemJson(idx, after), undo: () => ApplyItemJson(idx, before));
            _preEditJson = after;
        }

        private void ApplyItemJson(int idx, string json)
        {
            if (idx < 0 || idx >= _items.Count) return;
            try
            {
                var def = JsonSerializer.Deserialize<ItemDefinition>(json, ContentJson.Options);
                if (def == null) return;
                _items[idx] = def;
                _index = idx;
                Bind(def);
            }
            catch (JsonException) { /* a snapshot always round-trips; ignore defensively */ }
        }

        // ── Persistence ────────────────────────────────────────────────────────────

        private void DoSave()
        {
            if (_current == null) return;

            // Raw pane wins when dirty (fold it into the model first, fail-closed on a parse error).
            if (_jsonPane != null && _paneDirty)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<ItemDefinition>(_jsonPane.Text, ContentJson.Options);
                    if (parsed == null) { ShowError("Raw JSON parsed to null."); return; }
                    _items[_index] = parsed;
                    _current = parsed;
                }
                catch (JsonException ex) { ShowError("Raw JSON error: " + FirstLine(ex.Message)); return; }
                _paneDirty = false;
            }

            if (!Revalidate()) { ShowError("Fix the highlighted field(s) before saving."); return; }
            if (!Persist()) return; // Persist reports its own error
            Bind(_current);
            ShowOk($"Saved '{_current.Id}'.");
        }

        private bool Persist()
        {
            if (_current == null) return false;
            string id = _current.Id;
            try
            {
                string absDir = ProjectSettings.GlobalizePath(_itemsDir);
                Directory.CreateDirectory(absDir);
                string abs = Path.Combine(absDir, id + ".json");
                string tmp = abs + ".tmp";
                File.WriteAllText(tmp, ItemWriter.Serialize(_current));
                // Reload self-check: refuse to report "Saved" if it will not reload through the fail-closed gate.
                var check = ItemLoader.LoadFromFile(tmp);
                if (!check.Ok) { try { File.Delete(tmp); } catch { } ShowError("Save self-check failed: " + check.Error); return false; }
                File.Move(tmp, abs, overwrite: true);
                // A rename (id changed) leaves the old file behind — remove it so the id is authoritative.
                // DW-456 (same sink class as DoDelete): _originalId comes from Bind() and can carry a HAND-AUTHORED
                // traversal id — LoadItemsFromDir deserializes raw JSON with NO gate, so a file whose id field is
                // "../../evil" binds, and fixing the id then saving would otherwise File.Delete OUTSIDE the items
                // directory. Same fail-closed rule: an unsafe id can never have been a file this editor wrote.
                if (ItemDefinitionValidator.IsFilenameSafeId(_originalId) && _originalId != id)
                {
                    string old = Path.Combine(absDir, _originalId + ".json");
                    if (File.Exists(old)) File.Delete(old);
                }
                _originalId = id;
                return true;
            }
            catch (Exception ex) { ShowError("Save failed: " + ex.Message); return false; }
        }

        // ── Toolbar ops ──────────────────────────────────────────────────────────

        private void DoCreate()
        {
            var def = new ItemDefinition { Id = UniqueId("new_item"), DisplayName = "New Item" };
            _items.Add(def);
            _index = _items.IndexOf(def);
            _history.Push(
                redo: () => { if (!_items.Contains(def)) _items.Add(def); _index = _items.IndexOf(def); Refresh(); },
                undo: () => { _items.Remove(def); if (_index >= _items.Count) _index = Math.Max(0, _items.Count - 1); Refresh(); });
            Refresh();
        }

        private void DoDuplicate()
        {
            if (_current == null) return;
            var clone = CloneItem(_current, UniqueId(_current.Id + "_copy"));
            _items.Add(clone);
            _index = _items.IndexOf(clone);
            _history.Push(
                redo: () => { if (!_items.Contains(clone)) _items.Add(clone); _index = _items.IndexOf(clone); Refresh(); },
                undo: () => { _items.Remove(clone); if (_index >= _items.Count) _index = Math.Max(0, _items.Count - 1); Refresh(); });
            Refresh();
        }

        private void DoDelete()
        {
            if (_current == null) return;
            var target = _current;
            int idx = _index;
            string id = target.Id;
            var dlg = ChimeraDialog.Create("Delete item?", $"Remove '{id}'? This deletes its JSON file — this cannot be undone.");
            dlg.AddConfirm("Delete", danger: true);
            dlg.AddCancel("Cancel");
            dlg.Confirmed += () =>
            {
                if (idx >= 0 && idx < _items.Count && _items[idx] == target) _items.RemoveAt(idx);
                // DW-47 (review): the id feeds File.Delete here just as it feeds Persist()'s Path.Combine/File.Move.
                // The Delete button is NOT validity-gated (unlike Save, which rides DoSave→Revalidate), so a hand-typed
                // traversal id (e.g. "../../foo") could otherwise escape the items directory on disk. Fail closed with
                // THE single shared "may this id touch the on-disk file?" decision (DW-456 — Tier-1 tested, also
                // load-bearing in both ValidateFields/Validate id gates): an out-of-charset, reserved-basename or empty
                // id can never have produced a legit on-disk file, so skip the filesystem delete and only drop the
                // in-memory row.
                if (ItemDefinitionValidator.IsFilenameSafeId(id))
                {
                    try
                    {
                        string abs = Path.Combine(ProjectSettings.GlobalizePath(_itemsDir), id + ".json");
                        if (File.Exists(abs)) File.Delete(abs);
                    }
                    catch (Exception ex) { ShowError("Delete failed: " + ex.Message); }
                }
                if (_index >= _items.Count) _index = Math.Max(0, _items.Count - 1);
                _history.Push(
                    redo: () => { int i = _items.IndexOf(target); if (i >= 0) _items.RemoveAt(i); Refresh(); },
                    undo: () => { _items.Insert(Math.Min(idx, _items.Count), target); _index = idx; Refresh(); });
                Refresh();
            };
            dlg.Open(this);
        }

        private static ItemDefinition CloneItem(ItemDefinition src, string newId)
        {
            var clone = JsonSerializer.Deserialize<ItemDefinition>(ItemWriter.Serialize(src), ContentJson.Options)
                        ?? new ItemDefinition();
            clone.Id = newId;
            return clone;
        }

        /// <summary>DW-453: mint Create/Duplicate ids through the SINGLE shared convention
        /// (<see cref="ItemDefinitionValidator.MakeUniqueItemId"/> → <c>UnitDefinitionValidator.SanitizeId</c> +
        /// <c>MakeUniqueId</c>'s dedup/reserved-basename avoidance) so a minted id ALWAYS satisfies the ValidateFields
        /// id gate. The old LOCAL sanitizer here was Unicode-aware (<c>char.IsLetterOrDigit</c>), so duplicating a base
        /// like "café" minted an id the DW-47 charset gate rejects — an un-saveable item needing a manual rename.</summary>
        private string UniqueId(string baseId) =>
            ItemDefinitionValidator.MakeUniqueItemId(_items.ConvertAll(i => i.Id), baseId);

        private static string FirstLine(string s)
        {
            int i = s.IndexOf(" Path:", StringComparison.Ordinal);
            return i >= 0 ? s.Substring(0, i) : s;
        }

        private void ShowOk(string msg)    => _statusLabel.Text = msg;
        private void ShowError(string msg) => _statusLabel.Text = msg;

        // ── Input: undo/redo + F5 fail-closed gate ─────────────────────────────────

        public override void _Input(InputEvent @event)
        {
            if (_panel is null || !_panel.Visible) return;
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            if (key.CtrlPressed && key.Keycode == Key.Z) { _history.Undo(); GetViewport().SetInputAsHandled(); return; }
            if (key.CtrlPressed && key.Keycode == Key.Y) { _history.Redo(); GetViewport().SetInputAsHandled(); return; }

            if (key.Keycode == Key.F5 && _current != null && !Revalidate())
            {
                ShowError("Fix the highlighted field(s) before playtesting.");
                GetViewport().SetInputAsHandled();
            }
        }
    }
}
