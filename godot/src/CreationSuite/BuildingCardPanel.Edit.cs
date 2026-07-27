#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using ProjectChimera.Core;                // UnitCategories (the shared archetype closed-set source of truth)
using ProjectChimera.Core.Definitions;   // BuildingDefinition, FactionDefinition, BuildingDefinitionValidator, UnitDefinitionValidator, FactionWriter, ModelAssignment
using ProjectChimera.UI;                  // MeshLoader (unused here — no preview), SettingsManager (AR-5 last-used folder)
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraDialog, ChimeraTooltip, ChimeraValidationBadge
using ProjectChimera.UI.Theme;             // ThemeTokens

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 4.5 — the Building Card Editor's EDIT surface (the partial that augments the <see cref="BuildingCardPanel"/>
    /// shell): editable fields bound to the live <see cref="BuildingDefinition"/>, a Simple/Advanced disclosure with a
    /// raw-JSON escape hatch, fail-closed inline validation with per-field located badges, undo/redo, a
    /// Save/New/Duplicate/Delete toolbar, and write-back to the faction JSON on disk. Mirrors
    /// <c>UnitCardPanel.Edit.cs</c>'s shape (read-only reference — not edited by this story), minus the
    /// abilities/behaviors/hero/shop/revives-heroes sections buildings don't author, plus the three building-only
    /// fields and the new sparse cost-map composite control.
    ///
    /// <para><b>Persistence model.</b> Field edits AND Create/Duplicate/Delete mutate the in-memory
    /// <c>_faction.Buildings</c> and push an in-memory undo entry — undo/redo never touch the file. <b>Save</b> is the
    /// one persistence action: it reconciles the whole in-memory building list back into the faction file via
    /// <see cref="FactionWriter.SyncFactionBuildings"/> (untouched buildings + units + faction keys stay byte-identical),
    /// atomically with a reload self-check — the identical sequence <c>UnitCardPanel.Edit.cs</c>'s <c>PersistSync</c> uses.</para>
    /// </summary>
    public partial class BuildingCardPanel
    {
        // The closed authorable sets the dropdowns offer (mirror UnitDefinitionValidator's sets). Categories is a COPY
        // of the shared archetype source of truth (UnitCategories.All) so it can never drift from the validator/enum yet
        // remains a private per-panel array — the dropdown cannot alias (and so cannot corrupt) the validators' set.
        private static readonly string[] Categories = UnitCategories.All.ToArray();
        private static readonly string[] DamageTypes = { "Normal", "Pierce", "Siege", "Magic", "Hero" };
        private static readonly string[] ArmorTypes = { "Unarmored", "Light", "Medium", "Heavy", "Fortified", "Hero" };
        private static readonly string[] SeparationPriorities = { "Yield", "Normal", "Push" };
        /// <summary>The only resource ids the cost-map "+ Add resource" select offers (Story 4.3's Design Notes
        /// fence) — the SAME shared set <see cref="ResourceCostValidator.KnownResourceIds"/> and
        /// <see cref="UnitDefinitionValidator"/>'s cost-map check enforce, so the UI's offered set and the
        /// validator's accepted set can never drift apart.</summary>
        private static readonly string[] CostResourceIds = ResourceCostValidator.KnownResourceIds;
        private const double StatMax = 32767;   // one below the 16.16 ceiling (the form's first line of defence)

        private bool _lastValid = true;         // last validation verdict (drives the Save button + F5 gate)

        // ── Input: undo/redo + the Edit→Play gate ─────────────────────────

        /// <inheritdoc/>
        public override void _Input(InputEvent @event)
        {
            if (_panel is null || !_panel.Visible) return;   // only own input while open (⇒ Edit mode)
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            // Ctrl+Z / Ctrl+Y route through THIS history; SetInputAsHandled so another open editor's Ctrl+Z (also
            // _Input) doesn't also fire (mirrors UnitCardPanel.Edit.cs's guard).
            if (key.CtrlPressed && key.Keycode == Key.Z) { _history.Undo(); GetViewport().SetInputAsHandled(); return; }
            if (key.CtrlPressed && key.Keycode == Key.Y) { _history.Redo(); GetViewport().SetInputAsHandled(); return; }

            // Edit→Play gate: F5 toggles the mode in GameState._UnhandledInput (which runs AFTER _Input). Block it
            // ONLY when the current building is invalid; a clean/valid card lets F5 through.
            if (key.Keycode == Key.F5 && _current != null && !RevalidateAndReflect())
            {
                ShowError("Fix the highlighted field(s) before playtesting.");
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Editable body — replaces the read-only readouts ───────────

        private void BuildEditableBody(BuildingDefinition def)
        {
            _building = true;   // suppress live handlers while we seed control values

            // ── Simple (always visible) ──
            AddSection(_bodyHost, "Identity");
            AddText(_bodyHost, "Id", "id", "Id",
                "The unique id ([a-z0-9_]). It is the render slot + scenario reference — must be unique in this faction.",
                () => def.Id, v => def.Id = v, def);
            AddText(_bodyHost, "Name", "display_name", "Display Name", "The human-readable name shown in menus.",
                () => def.DisplayName, v => def.DisplayName = v, def);
            AddSelect(_bodyHost, "Archetype", "category", "Archetype", "The building's category (usually Structure).",
                Categories, () => def.Category, v => def.Category = v, def);
            AddModelRow(_bodyHost, def);   // LineEdit + Browse + Box placeholder (no live 3D render — Design Notes)

            AddSection(_bodyHost, "Combat");
            AddSelect(_bodyHost, "Damage type", "damage_type", "Damage Type",
                "The matrix row that picks the multiplier vs each armor class (0 attack_damage ⇒ irrelevant for most buildings).",
                DamageTypes, () => def.DamageType, v => def.DamageType = v, def);
            AddSelect(_bodyHost, "Armor type", "armor_type", "Armor Type",
                "The matrix column that scales incoming damage by type.",
                ArmorTypes, () => def.ArmorType, v => def.ArmorType = v, def);
            AddNumFloat(_bodyHost, "Attack", "attack_damage", "Attack Damage", "Base damage per hit (0 = non-defensive building).",
                0.5, () => def.AttackDamage, v => def.AttackDamage = v, def);
            AddNumFloat(_bodyHost, "Range", "attack_range", "Attack Range", "Distance the building can strike from (0 = no attack).",
                0.5, () => def.AttackRange, v => def.AttackRange = v, def);
            AddNumFloat(_bodyHost, "Atk interval", "attack_speed", "Attack Interval", "Seconds between attacks — lower is faster.",
                0.05, () => def.AttackSpeed, v => def.AttackSpeed = v, def);

            AddSection(_bodyHost, "Stats");
            AddNumFloat(_bodyHost, "HP", "hp", "Hit Points", "The building's health pool. Must be authored above zero.",
                1, () => def.Hp, v => def.Hp = v, def);
            AddNumInt(_bodyHost, "Supply", "supply", "Supply", "Population this building itself draws from supply (usually 0).",
                () => def.Supply, v => def.Supply = v, def);
            AddNumFloat(_bodyHost, "Vision", "vision_range", "Vision Range", "How far the building reveals the map.",
                0.5, () => def.VisionRange, v => def.VisionRange = v, def);

            AddSection(_bodyHost, "Economy");
            AddNumInt(_bodyHost, "Ore cost", "cost_ore", "Ore Cost", "Ore spent to construct this building (legacy field — the Cost map below wins when authored).",
                () => def.CostOre, v => def.CostOre = v, def);
            AddNumInt(_bodyHost, "Crystal cost", "cost_crystal", "Crystal Cost", "Crystal spent to construct this building (legacy field — the Cost map below wins when authored).",
                () => def.CostCrystal, v => def.CostCrystal = v, def);
            AddCostMapRow(_bodyHost, () => def.Cost, v => def.Cost = v, def);

            AddSection(_bodyHost, "Construction");
            AddRequiredNumFloat(_bodyHost, "Construction time", "construction_time", "Construction Time",
                "Seconds to build this building. Required — author a value (0 is valid) to clear the missing-field badge.",
                0.5, () => def.ConstructionTime, v => def.ConstructionTime = v, def);
            AddRequiredNumInt(_bodyHost, "Supply bonus", "supply_bonus", "Supply Bonus",
                "Amount this building adds to its faction's supply cap while alive. Required — author 0 for none.",
                () => def.SupplyBonus, v => def.SupplyBonus = v, def);
            AddProducesRow(_bodyHost, def);

            // ── Advanced (toggled by the Segment) ──
            _advancedHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = _segment.Active == 1 };
            _advancedHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _bodyHost.AddChild(_advancedHost);

            AddSection(_advancedHost, "Advanced stats");
            AddNumFloat(_advancedHost, "Flat armor", "armor", "Flat Armor", "Subtracted from post-matrix damage (0 = none).",
                1, () => def.Armor, v => def.Armor = v, def);
            AddNumFloat(_advancedHost, "Speed", "speed", "Move Speed", "World units per second (0 for a stationary building).",
                0.1, () => def.Speed, v => def.Speed = v, def);
            AddNumFloat(_advancedHost, "Train time", "train_time", "Train Time", "Seconds to train a unit at this building (distinct from Construction Time above).",
                0.5, () => def.TrainTime, v => def.TrainTime = v, def);
            AddNumFloat(_advancedHost, "Splash radius", "splash_radius", "Splash Radius", "AoE splash on projectile hit (0 = none).",
                0.5, () => def.SplashRadius, v => def.SplashRadius = v, def);
            AddNumFloat(_advancedHost, "Collision radius", "collision_radius", "Collision Radius", "Per-building separation radius (default 1).",
                0.1, () => def.CollisionRadius, v => def.CollisionRadius = v, def);
            AddNumFloat(_advancedHost, "Mesh scale", "mesh_scale", "Mesh Scale", "Visual scale of the model.",
                0.05, () => def.MeshScale, v => def.MeshScale = v, def);
            AddNumFloat(_advancedHost, "Max energy", "max_energy", "Max Energy", "Ability-resource pool (0 = cannot cast energy abilities).",
                1, () => def.MaxEnergy, v => def.MaxEnergy = v, def);
            AddSelect(_advancedHost, "Separation", "separation_priority", "Separation Priority",
                "Crowd-steering precedence vs a unit standing where this building is being placed.",
                SeparationPriorities, () => def.SeparationPriority, v => def.SeparationPriority = v, def);

            AddSection(_advancedHost, "Lists (comma-separated)");
            AddCommaList(_advancedHost, "Prerequisites", "prerequisites", "Prerequisites", "Building ids required before this building can be placed (Story 4.2 tech tree).",
                () => def.Prerequisites, v => def.Prerequisites = v ?? Array.Empty<string>(), nullable: false, def);
            // Story 4.11: authors the building→research "offers" linkage from the BUILDING side (Design Notes) —
            // the field already round-trips via FactionWriter.SyncFactionBuildings:503 (landed silently in 4.8/4.5),
            // so this is a one-line reuse of the exact AddCommaList pattern the Prerequisites row just above uses.
            AddCommaList(_advancedHost, "Available research", "available_research", "Available Research",
                "Research ids a creator can start from this building's command card (Story 4.11).",
                () => def.AvailableResearch, v => def.AvailableResearch = v ?? Array.Empty<string>(), nullable: false, def);

            AddSection(_advancedHost, "Raw JSON (this building)");
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

        /// <summary>Attach a field-role hover-and-focus tooltip to an interactive control.</summary>
        private static void AttachFieldTip(Control target, string term, string body)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            ChimeraTooltip.Attach(target, term, body, ChimeraTooltip.TooltipRole.Field);
        }

        // ── Field builders: live writeback (no rebuild) + a focus-session / per-select undo entry ──

        private void AddText(Control parent, string label, string key, string term, string body,
                             Func<string> get, Action<string> set, BuildingDefinition def)
        {
            LineEdit input = ChimeraComponents.Input(text: get());
            string snap = get();
            input.FocusEntered += () => snap = get();
            input.TextChanged += t => { if (_building) return; set(t); OnLiveChanged(key); };
            input.TextSubmitted += _ => { CommitStr(oldVal: snap, newVal: get(), set: set, def: def); snap = get(); };
            input.FocusExited += () => { CommitStr(oldVal: snap, newVal: get(), set: set, def: def); snap = get(); };
            AttachFieldTip(input, term, body);
            AddFieldRow(parent, label, input, MakeBadge(key));
        }

        // ── Model row: LineEdit + Browse (res:// *.glb dialog) + Box placeholder button ──

        /// <summary>
        /// The Model field: a typed LineEdit plus a <b>Browse</b> button (opens a <c>res://</c>-rooted <c>*.glb</c>
        /// FileDialog) and a <b>Box placeholder</b> button. Mirrors <c>UnitCardPanel.Edit.cs</c>'s <c>AddModelRow</c>
        /// EXCEPT there is no live preview to re-render (Design Notes — no <c>SubViewport</c>) so Browse/Box only
        /// set→undo→badge, they never call an <c>UpdatePreview</c>.
        /// </summary>
        private void AddModelRow(Control parent, BuildingDefinition def)
        {
            const string key = "mesh_path";
            Func<string> get = () => def.MeshPath ?? "";
            Action<string> set = v => def.MeshPath = ModelAssignment.NormalizeMeshPath(v);   // blank → null (box)

            LineEdit input = ChimeraComponents.Input(placeholder: "res://…/model.glb", text: get());
            _meshPathInput = input;
            string snap = get();
            input.FocusEntered += () => snap = get();
            input.TextChanged += t => { if (_building) return; set(t); OnLiveChanged(key); };
            input.TextSubmitted += _ => { CommitStr(oldVal: snap, newVal: get(), set: set, def: def); snap = get(); };
            input.FocusExited += () => { CommitStr(oldVal: snap, newVal: get(), set: set, def: def); snap = get(); };
            AttachFieldTip(input, "Model",
                "res:// path to the building's GLB. Blank = box placeholder. Use Browse to pick a GLB, or Box for the placeholder.");

            var browse = ChimeraComponents.Button("Browse", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            browse.Pressed += () => OpenMeshBrowseDialog(def);
            AttachFieldTip(browse, "Browse models", "Pick a GLB from the project (res://). Sets this building's model.");

            var box = ChimeraComponents.Button("Box", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            box.Pressed += () => AssignMeshPath(null, def);
            AttachFieldTip(box, "Box placeholder", "Clear the model and use the box placeholder (undoable).");

            var composite = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            composite.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            input.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            composite.AddChild(input);
            composite.AddChild(browse);
            composite.AddChild(box);

            AddFieldRow(parent, "Model", composite, MakeBadge(key));
        }

        private void AssignMeshPath(string? newPath, BuildingDefinition def)
        {
            if (_current == null || !ReferenceEquals(def, _current)) return;
            Action<string> set = v => def.MeshPath = ModelAssignment.NormalizeMeshPath(v);
            string oldVal = def.MeshPath ?? "";
            set(ModelAssignment.NormalizeMeshPath(newPath) ?? "");   // apply now
            string newVal = def.MeshPath ?? "";
            if (_meshPathInput != null) _meshPathInput.Text = newVal;   // reflect in the field (no TextChanged)
            CommitStr(oldVal: oldVal, newVal: newVal, set: set, def: def);   // undo (no-op if unchanged)
            OnLiveChanged("mesh_path");
            RevalidateAndReflect();
        }

        /// <summary>Open a <c>res://</c>-rooted <c>*.glb</c> FileDialog under the panel's CanvasLayer; on select,
        /// assign the path and remember its folder (AR-5). The dialog frees itself on select or cancel — no leak.</summary>
        private void OpenMeshBrowseDialog(BuildingDefinition def)
        {
            var dlg = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access   = FileDialog.AccessEnum.Resources,   // res://-rooted — no arbitrary-filesystem ingest
                Title    = "Select a GLB model",
                Filters  = new[] { "*.glb ; GLTF Binary" },
            };

            string lastFolder = SettingsManager.Instance?.Current.LastUsedAssetFolder ?? "";
            if (!string.IsNullOrEmpty(lastFolder) && DirAccess.DirExistsAbsolute(lastFolder))
                dlg.CurrentDir = lastFolder;

            dlg.FileSelected += path =>
            {
                AssignMeshPath(path, def);
                SaveLastUsedFolder(path);
                dlg.QueueFree();
            };
            dlg.Canceled += () => dlg.QueueFree();
            _canvas.AddChild(dlg);   // gate under the panel's CanvasLayer
            dlg.PopupCentered(new Vector2I(900, 600));
        }

        private static void SaveLastUsedFolder(string resPath)
        {
            SettingsManager? mgr = SettingsManager.Instance;
            if (mgr == null) return;   // absent in the standalone verify harness — best-effort only
            mgr.Current.LastUsedAssetFolder = ModelAssignment.FolderOf(resPath);
            mgr.Save();
        }

        private void AddNumFloat(Control parent, string label, string key, string term, string body,
                                 double step, Func<float> get, Action<float> set, BuildingDefinition def)
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
                               Func<int> get, Action<int> set, BuildingDefinition def)
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

        /// <summary>
        /// A required-nullable float row (Story 4.5 — <c>construction_time</c>): displays the fallback 0 while
        /// unauthored, but — UNLIKE <see cref="AddNumFloat"/> — a focus-exit on a STILL-null field authors it at
        /// its displayed value even when the SpinBox's numeric value never changed (a plain <c>ValueChanged</c>
        /// commit, mirroring <see cref="AddNumFloat"/>, would never fire for a same-value confirm — e.g. a creator
        /// deliberately confirming "0" when 0 is already the unauthored-fallback display — which would otherwise
        /// make that value permanently unreachable through the Simple UI). Any genuine value change still authors
        /// normally through <c>ValueChanged</c>.
        /// </summary>
        private void AddRequiredNumFloat(Control parent, string label, string key, string term, string body,
                                         double step, Func<float?> get, Action<float?> set, BuildingDefinition def)
        {
            SpinBox spin = ChimeraComponents.NumInput(value: get() ?? 0f, min: 0, max: StatMax, step: step);
            LineEdit le = spin.GetLineEdit();
            float? snap = get();
            le.FocusEntered += () => snap = get();
            spin.ValueChanged += v => { if (_building) return; set((float)v); OnLiveChanged(key); };
            le.FocusExited += () =>
            {
                float? now = get();
                if (now == null) { set((float)spin.Value); now = get(); }   // still-unauthored confirm → author at the displayed value
                if (!Nullable.Equals(now, snap))
                {
                    float? oldVal = snap, newVal = now;
                    BuildingDefinition t = def;
                    PushHistory(() => { set(newVal); GoToBuilding(t); }, () => { set(oldVal); GoToBuilding(t); });
                    OnLiveChanged(key);
                }
                snap = now;
            };
            AttachFieldTip(le, term, body);
            AddFieldRow(parent, label, spin, MakeBadge(key));
        }

        /// <summary>The int counterpart of <see cref="AddRequiredNumFloat"/> (Story 4.5 — <c>supply_bonus</c>).</summary>
        private void AddRequiredNumInt(Control parent, string label, string key, string term, string body,
                                       Func<int?> get, Action<int?> set, BuildingDefinition def)
        {
            SpinBox spin = ChimeraComponents.NumInput(value: get() ?? 0, min: 0, max: StatMax, step: 1);
            LineEdit le = spin.GetLineEdit();
            int? snap = get();
            le.FocusEntered += () => snap = get();
            spin.ValueChanged += v => { if (_building) return; set((int)Math.Round(v)); OnLiveChanged(key); };
            le.FocusExited += () =>
            {
                int? now = get();
                if (now == null) { set((int)Math.Round(spin.Value)); now = get(); }
                if (!Nullable.Equals(now, snap))
                {
                    int? oldVal = snap, newVal = now;
                    BuildingDefinition t = def;
                    PushHistory(() => { set(newVal); GoToBuilding(t); }, () => { set(oldVal); GoToBuilding(t); });
                    OnLiveChanged(key);
                }
                snap = now;
            };
            AttachFieldTip(le, term, body);
            AddFieldRow(parent, label, spin, MakeBadge(key));
        }

        private void AddSelect(Control parent, string label, string key, string term, string body,
                               string[] items, Func<string> get, Action<string> set, BuildingDefinition def)
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
                BuildingDefinition t = def;
                PushHistory(() => { set(nu); GoToBuilding(t); }, () => { set(old); GoToBuilding(t); });
                OnLiveChanged(key);
            };
            AttachFieldTip(sel, term, body);
            AddFieldRow(parent, label, sel, MakeBadge(key));
        }

        /// <summary>
        /// The Story 4.5 "Produces" dropdown: a leading <c>"(unauthored)"</c> entry mapped to <c>null</c> (distinct
        /// from the explicit <c>"None"</c> non-producer value — mirrors the Unit Card Editor's <c>AddHeroAbilityRow</c>
        /// "(none)" nullable-select shape), then every category, then <c>"None"</c>. Any explicit
        /// pick (including re-confirming "(unauthored)", a deliberate no-op) authors <c>produces_category</c> through
        /// the undo/validate/save path.
        /// </summary>
        private void AddProducesRow(Control parent, BuildingDefinition def)
        {
            const string key = "produces_category";
            var items = new List<string> { "(unauthored)" };
            var ids = new List<string?> { null };
            foreach (string c in Categories) { items.Add(c); ids.Add(c); }
            items.Add("None"); ids.Add("None");

            OptionButton sel = ChimeraComponents.Select(items.ToArray());
            string? current = string.IsNullOrEmpty(def.ProducesCategory) ? null : def.ProducesCategory;
            int cur = ids.IndexOf(current);
            sel.Selected = cur >= 0 ? cur : 0;
            sel.ItemSelected += idx =>
            {
                if (_building) return;
                int i = (int)idx;
                if (i < 0 || i >= ids.Count) return;
                string? old = string.IsNullOrEmpty(def.ProducesCategory) ? null : def.ProducesCategory;
                string? nu = ids[i];
                if (old == nu) return;
                def.ProducesCategory = nu;
                BuildingDefinition t = def;
                PushHistory(() => { def.ProducesCategory = nu; GoToBuilding(t); }, () => { def.ProducesCategory = old; GoToBuilding(t); });
                OnLiveChanged(key);
            };
            AttachFieldTip(sel, "Produces",
                "The unit category this building produces. Required — pick a category, or None for a non-producer.");
            AddFieldRow(parent, "Produces", sel, MakeBadge(key));
        }

        private void AddCommaList(Control parent, string label, string key, string term, string body,
                                  Func<string[]?> get, Action<string[]?> set, bool nullable, BuildingDefinition def)
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
                    BuildingDefinition t = def;
                    PushHistory(() => { set(newArr); GoToBuilding(t); }, () => { set(oldArr); GoToBuilding(t); });
                }
                snap = now;
            };
            AttachFieldTip(input, term, body);
            AddFieldRow(parent, label, input, MakeBadge(key));
        }

        // ── Cost map (Story 4.5): a structured resource-cost composite mirroring UnitCardPanel.Edit.cs's
        //    AddComponentPicker chip+add-select shape (UnitCardPanel.Edit.cs:493-594), keyed by resource id with a
        //    numeric SpinBox value instead of a bare id list. ──

        private void AddCostMapRow(Control parent, Func<Dictionary<string, int>?> get, Action<Dictionary<string, int>?> set, BuildingDefinition def)
        {
            const string key = "cost";
            Dictionary<string, int> attached = get() ?? new Dictionary<string, int>();

            var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            col.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));

            if (attached.Count > 0)
            {
                var chips = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                chips.AddThemeConstantOverride("h_separation", ChimeraComponents.Const(ThemeTokens.S1));
                chips.AddThemeConstantOverride("v_separation", ChimeraComponents.Const(ThemeTokens.S1));
                foreach (string resId in attached.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    chips.AddChild(MakeCostChip(resId, attached[resId], get, set, def));
                col.AddChild(chips);
            }
            else
            {
                col.AddChild(Body("(none — free, or the legacy Ore/Crystal cost fields above apply)", ThemeTokens.TextLo));
            }

            // "+ Add resource" select: a leading no-op prompt at index 0, then every known resource id not already
            // attached — restricted to {"ore","crystal"} (Story 4.3's Design Notes fence).
            var items = new List<string> { "+ Add resource…" };
            var ids = new List<string?> { null };
            foreach (string resId in CostResourceIds)
            {
                if (attached.ContainsKey(resId)) continue;
                items.Add(resId);
                ids.Add(resId);
            }
            OptionButton add = ChimeraComponents.Select(items.ToArray());
            add.Selected = 0;
            add.Disabled = ids.Count <= 1;   // nothing left to add
            add.ItemSelected += idx =>
            {
                if (_building) return;
                int i = (int)idx;
                if (i <= 0 || i >= ids.Count) return;
                string? nid = ids[i];
                if (nid == null) return;
                Dictionary<string, int> oldMap = CloneCostMap(get());
                Dictionary<string, int> newMap = CloneCostMap(get());
                newMap[nid] = 0;
                CommitCostMap(set, oldMap, newMap, def, rebuildNow: true);
            };
            AttachFieldTip(add, "Cost",
                "Add a resource this building costs to construct — restricted to ore/crystal (the only resources with runtime backing today).");
            col.AddChild(add);

            AddFieldRow(parent, "Cost", col, MakeBadge(key));
        }

        /// <summary>An attached cost-map chip: a resource-id <see cref="ChimeraComponents.Tag"/> + an amount
        /// <see cref="SpinBox"/> + an ✕ <see cref="ChimeraComponents.IconButton"/> that detaches it.</summary>
        private Control MakeCostChip(string resId, int amount, Func<Dictionary<string, int>?> get, Action<Dictionary<string, int>?> set, BuildingDefinition def)
        {
            var chip = new HBoxContainer();
            chip.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));

            var tag = ChimeraComponents.Tag(resId);
            tag.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            chip.AddChild(tag);

            SpinBox spin = ChimeraComponents.NumInput(value: amount, min: 0, max: StatMax, step: 1);
            spin.CustomMinimumSize = new Vector2(80, 0);
            LineEdit le = spin.GetLineEdit();
            int snap = amount;
            le.FocusEntered += () => snap = (int)Math.Round(spin.Value);
            spin.ValueChanged += v =>
            {
                if (_building) return;
                Dictionary<string, int> live = CloneCostMap(get());
                live[resId] = (int)Math.Round(v);
                set(live);
                OnLiveChanged("cost");
            };
            le.FocusExited += () =>
            {
                int now = (int)Math.Round(spin.Value);
                if (now != snap)
                {
                    Dictionary<string, int> oldMap = CloneCostMap(get());
                    oldMap[resId] = snap;
                    Dictionary<string, int> newMap = CloneCostMap(get());
                    newMap[resId] = now;
                    CommitCostMap(set, oldMap, newMap, def, rebuildNow: false);
                }
                snap = now;
            };
            AttachFieldTip(le, resId, $"Amount of '{resId}' this building costs to construct.");
            chip.AddChild(spin);

            var x = ChimeraComponents.IconButton("✕");
            x.Pressed += () =>
            {
                Dictionary<string, int> oldMap = CloneCostMap(get());
                Dictionary<string, int> newMap = CloneCostMap(get());
                newMap.Remove(resId);
                CommitCostMap(set, oldMap, newMap, def, rebuildNow: true);
            };
            AttachFieldTip(x, "Remove", $"Remove '{resId}' from this building's cost map (undoable).");
            chip.AddChild(x);
            return chip;
        }

        private static Dictionary<string, int> CloneCostMap(Dictionary<string, int>? src) =>
            src == null ? new Dictionary<string, int>() : new Dictionary<string, int>(src);

        /// <summary>Commit a cost-map change: set the map, push one undo entry (whole-map old/new snapshots —
        /// undo/redo always rebuilds via <see cref="GoToBuilding"/>, mirroring every other field's undo shape),
        /// revalidate, and (for a shape change — attach/detach) rebuild NOW so the chips/add-select reflect the new
        /// key set immediately (mirrors <c>AddComponentPicker</c>'s <c>ApplyComponentList</c>). An amount-only edit
        /// (committed on focus-exit) does NOT rebuild immediately — the SpinBox already shows the right value and a
        /// rebuild would drop focus mid-edit.</summary>
        private void CommitCostMap(Action<Dictionary<string, int>?> set,
                                   Dictionary<string, int> oldMap, Dictionary<string, int> newMap, BuildingDefinition def, bool rebuildNow)
        {
            set(CloneCostMap(newMap));
            BuildingDefinition t = def;
            PushHistory(
                () => { set(CloneCostMap(newMap)); GoToBuilding(t); },
                () => { set(CloneCostMap(oldMap)); GoToBuilding(t); });
            OnLiveChanged("cost");
            if (rebuildNow) Refresh();
        }

        // Commit a string-field change to the undo stack (focus-session granularity).
        private void CommitStr(string oldVal, string newVal, Action<string> set, BuildingDefinition def)
        {
            if (oldVal == newVal) return;
            BuildingDefinition t = def;
            PushHistory(() => { set(newVal); GoToBuilding(t); }, () => { set(oldVal); GoToBuilding(t); });
        }

        // Commit a numeric change (old/new carried as double; the setter re-narrows).
        private void PushValue(BuildingDefinition def, Action<double> set, double oldVal, double newVal)
        {
            BuildingDefinition t = def;
            PushHistory(() => { set(newVal); GoToBuilding(t); }, () => { set(oldVal); GoToBuilding(t); });
        }

        private void OnLiveChanged(string key)
        {
            if (_current == null) return;
            if (key is "display_name" or "id" or "category" or "produces_category") RefreshHeader();
            RevalidateAndReflect();   // proactive badges + Save-button state (no form rebuild — controls keep focus)
        }

        private void RefreshHeader()
        {
            if (_current == null) return;
            foreach (Node c in _headerHost.GetChildren()) { _headerHost.RemoveChild(c); c.QueueFree(); }
            BuildHeader(_current);
        }

        // ── Raw-JSON escape hatch ──────────────────────────────────────────────

        private void BuildRawPane(Control parent, BuildingDefinition def)
        {
            var hint = Body("Edit this building's JSON directly. On Save a dirty pane wins — validated, then folded back.", ThemeTokens.TextLo);
            hint.AutowrapMode = TextServer.AutowrapMode.Word;
            hint.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txs, ThemeTokens.Type));
            parent.AddChild(hint);

            _jsonPane = MakeJsonPane();
            _jsonPane.TextChanged += () => { if (!_suppressPaneDirty) _paneDirty = true; };
            parent.AddChild(_jsonPane);
            SetPaneText(FactionWriter.SerializeBuildingClean(def));   // seed programmatically (not dirty)

            var sync = ChimeraComponents.Button("Sync JSON from form", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            sync.Pressed += () => { if (_current != null) SetPaneText(FactionWriter.SerializeBuildingClean(_current)); };
            AttachFieldTip(sync, "Sync JSON", "Re-render this building's JSON from the current form values (discards manual pane edits).");
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

        // ── Validation + located badges ───────────────────────────────

        /// <summary>Re-run the validator (+ the presentation mesh check), paint badges, update the status line + Save
        /// button. Does NOT rebuild the form (controls keep focus). Returns true when the current building is fully valid.</summary>
        private bool RevalidateAndReflect()
        {
            foreach (ChimeraValidationBadge b in _badges.Values) b.Clear();

            if (_current == null) { ClearStatus(); _lastValid = true; UpdateToolbarEnabled(); return true; }

            BuildingValidationResult res = BuildingDefinitionValidator.Validate(_current, _faction?.Buildings);
            string? meshErr = MeshError(_current);

            // Group by field path before badging: the cost-map control can raise MULTIPLE simultaneous "cost"-keyed
            // errors (one per bad resource entry) onto the ONE composite control's ONE badge — a plain per-error
            // ShowBadge loop would have the last error silently overwrite every earlier one on the same key.
            foreach (var group in res.Errors.GroupBy(e => e.FieldPath))
                ShowBadge(group.Key, string.Join("  ", group.Select(e => e.Message)));
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

        /// <summary>
        /// The one validation rule that needs Godot: the mesh path must resolve to a real mesh, or be blank (box
        /// placeholder). Unlike <c>UnitCardPanel.Edit.cs</c>'s <c>MeshError</c> this panel has no live preview to
        /// trust (Design Notes — no <c>SubViewport</c>), so every def (including the currently-bound one) uses the
        /// plain <c>ResourceLoader.Exists</c> path-existence check.
        /// </summary>
        private string? MeshError(BuildingDefinition def)
        {
            string mp = def.MeshPath ?? "";
            if (mp.Length == 0) return null;   // blank = box placeholder — always valid
            if (!ResourceLoader.Exists(mp))
                return $"building '{def.Id}'.mesh_path: '{mp}' didn't load a mesh (leave blank for a box placeholder).";
            return null;
        }

        // ── Undo/redo ──────────────────────────────────────────────────

        private void PushHistory(Action redo, Action undo) => _history.Push(redo, undo);

        /// <summary>Navigate the browse cursor to <paramref name="b"/> and rebuild the form (undo/redo re-render).</summary>
        private void GoToBuilding(BuildingDefinition b)
        {
            if (_faction == null) return;
            int i = _faction.Buildings.IndexOf(b);
            if (i >= 0) _index = i;
            Refresh();
        }

        /// <summary>
        /// Story 4.6 hook: open this inspector already bound to <paramref name="building"/> — the Tech Tree Editor's
        /// <c>node_selected</c> handler calls this instead of duplicating <see cref="GoToBuilding"/>'s index lookup.
        /// </summary>
        public void SelectAndShow(BuildingDefinition building)
        {
            _panel.Visible = true;
            GoToBuilding(building);
        }

        // ── Toolbar + list ops ────────────────────────────────────────

        private Control BuildToolbar()
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            _saveBtn = ChimeraComponents.Button("Save", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Sm);
            _saveBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _saveBtn.Pressed += () => DoSave();
            AttachFieldTip(_saveBtn, "Save", "Validate, then write ALL edits to the faction file. Applies on the next playtest/match.");
            row.AddChild(_saveBtn);

            _newBtn = ChimeraComponents.Button("New", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            _newBtn.Pressed += DoCreate;
            AttachFieldTip(_newBtn, "New building", "Add a new building with default stats and a unique id.");
            row.AddChild(_newBtn);

            _dupBtn = ChimeraComponents.Button("Duplicate", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            _dupBtn.Pressed += DoDuplicate;
            AttachFieldTip(_dupBtn, "Duplicate", "Clone the current building with a new id.");
            row.AddChild(_dupBtn);

            _deleteBtn = ChimeraComponents.Button("Delete", ChimeraComponents.ButtonVariant.Danger, ChimeraComponents.ButtonSize.Sm);
            _deleteBtn.Pressed += DoDelete;
            AttachFieldTip(_deleteBtn, "Delete", "Remove the current building (asks to confirm first).");
            row.AddChild(_deleteBtn);

            return row;
        }

        private void UpdateToolbarEnabled()
        {
            bool hasBuilding = _current != null && _faction != null && _faction.Buildings.Count > 0;
            if (_saveBtn != null!) _saveBtn.Disabled = !hasBuilding || !_lastValid;
            if (_dupBtn != null!) _dupBtn.Disabled = !hasBuilding;
            if (_deleteBtn != null!) _deleteBtn.Disabled = !hasBuilding;
            if (_newBtn != null!) _newBtn.Disabled = _faction == null;
        }

        private void DoCreate()
        {
            if (_faction == null) return;
            // ConstructionTime/SupplyBonus/ProducesCategory intentionally left unauthored (null) — Save stays
            // disabled until the creator fills them in (the I/O-matrix "Missing required field" row): unlike a
            // unit's cost_ore (defaults to 50), these building-only required-nullable fields have no sensible
            // non-authored default (BuildingDefinitionValidator rejects a still-null value either way).
            // DW-55: author Hp explicitly so a freshly-created building is hp-authored (HpAuthored=true) and is not
            // falsely flagged "hp required but missing" by BuildingDefinitionValidator. The SpinBox already
            // seeds/edits from def.Hp; 100 mirrors UnitDefinition's default.
            var def = new BuildingDefinition { Id = UniqueId("new_building"), DisplayName = "New Building", Category = "Structure", Hp = 100f };
            _faction.Buildings.Add(def);
            _index = _faction.Buildings.Count - 1;
            BuildingDefinition captured = def;
            PushHistory(
                redo: () => { if (!_faction.Buildings.Contains(captured)) _faction.Buildings.Add(captured); GoToBuilding(captured); },
                undo: () => RemoveFromList(captured));
            Refresh();
            ShowOk($"Added '{def.Id}' — author construction time / supply bonus / produces, then Save.");
        }

        private void DoDuplicate()
        {
            if (_faction == null || _current == null) return;
            BuildingDefinition clone = CloneBuilding(_current, UniqueId(_current.Id + "_copy"));
            _faction.Buildings.Add(clone);
            _index = _faction.Buildings.Count - 1;
            BuildingDefinition captured = clone;
            PushHistory(
                redo: () => { if (!_faction.Buildings.Contains(captured)) _faction.Buildings.Add(captured); GoToBuilding(captured); },
                undo: () => RemoveFromList(captured));
            Refresh();
            ShowOk($"Duplicated as '{clone.Id}' — Save to write it to the file.");
        }

        private void DoDelete()
        {
            if (_faction == null || _current == null || _faction.Buildings.Count == 0) return;
            BuildingDefinition victim = _current;
            string id = victim.Id;
            var dlg = ChimeraDialog.Create("Delete building?",
                $"Remove '{id}' from this faction? Undoable until you close the editor; Save writes it to the file.");
            dlg.AddConfirm("Delete", danger: true);
            dlg.AddCancel("Cancel");
            dlg.Confirmed += () =>
            {
                int at = _faction.Buildings.IndexOf(victim);
                if (at < 0) return;
                _faction.Buildings.RemoveAt(at);
                if (_index >= _faction.Buildings.Count) _index = Math.Max(0, _faction.Buildings.Count - 1);
                BuildingDefinition captured = victim;
                int atI = at;
                PushHistory(
                    redo: () => RemoveFromList(captured),
                    undo: () => { _faction.Buildings.Insert(Math.Min(atI, _faction.Buildings.Count), captured); GoToBuilding(captured); });
                Refresh();
                ShowOk($"Deleted '{id}' — Save to write the change to the file.");
            };
            dlg.Open(this);
        }

        private void RemoveFromList(BuildingDefinition b)
        {
            if (_faction == null) return;
            _faction.Buildings.Remove(b);
            if (_index >= _faction.Buildings.Count) _index = Math.Max(0, _faction.Buildings.Count - 1);
            Refresh();
        }

        private string UniqueId(string baseId)
        {
            string id = UnitDefinitionValidator.SanitizeId(baseId);
            if (id.Length == 0) id = "new_building";
            if (!IdExists(id)) return id;
            for (int i = 2; i < 100000; i++)
            {
                string candidate = $"{id}_{i}";
                if (!IdExists(candidate)) return candidate;
            }
            return id;   // pathological fallback (validator will still reject a dup on Save)
        }

        private bool IdExists(string id) => _faction != null && _faction.Buildings.Exists(b => b.Id == id);

        private static BuildingDefinition CloneBuilding(BuildingDefinition s, string newId) => new()
        {
            Id = newId,
            DisplayName = s.DisplayName,
            Category = s.Category,
            MeshPath = s.MeshPath,
            Hp = s.Hp, Speed = s.Speed, AttackDamage = s.AttackDamage, AttackRange = s.AttackRange, AttackSpeed = s.AttackSpeed,
            DamageType = s.DamageType, ArmorType = s.ArmorType, Armor = s.Armor,
            CostOre = s.CostOre, CostCrystal = s.CostCrystal, Supply = s.Supply,
            // Story 4.5: clone the sparse cost map too (Cost is a Story 4.3 field UnitCardPanel.Edit.cs's CloneUnit
            // predates and does not yet clone — a pre-existing gap out of this story's read-only-reference scope for
            // units, but not one this NEW building clone should inherit).
            Cost = s.Cost == null ? null : new Dictionary<string, int>(s.Cost),
            MeshScale = s.MeshScale, TrainTime = s.TrainTime, VisionRange = s.VisionRange,
            SplashRadius = s.SplashRadius, CollisionRadius = s.CollisionRadius, MaxEnergy = s.MaxEnergy,
            SeparationPriority = s.SeparationPriority,
            Delivery = s.Delivery, ProjectileSpeed = s.ProjectileSpeed,
            XpBounty = s.XpBounty,
            Prerequisites = s.Prerequisites is null ? Array.Empty<string>() : (string[])s.Prerequisites.Clone(),
            // Story 4.8 review-pass fix: AvailableResearch was omitted from this hand-enumerated field list,
            // silently stripping it on Duplicate — the same defect class the RevivesHeroes/Hero/ShopStock comment
            // below already documents having been fixed once. Same null-safe .Clone() pattern as Prerequisites.
            AvailableResearch = s.AvailableResearch is null ? Array.Empty<string>() : (string[])s.AvailableResearch.Clone(),
            Abilities = s.Abilities is null ? Array.Empty<string>() : (string[])s.Abilities.Clone(),
            Behaviors = s.Behaviors is null ? Array.Empty<string>() : (string[])s.Behaviors.Clone(),
            AttackDomains = s.AttackDomains?.Clone() as string[],
            Tags = s.Tags?.Clone() as string[],
            IsHero = s.IsHero,
            // A Structure building CAN legitimately carry these (Story 3.14 hero-revival / Story 3.16 shop) —
            // review-pass fix: the initial cut only copied stats + the three building-only fields, silently
            // stripping an existing revive/shop building's capability on Duplicate.
            RevivesHeroes = s.RevivesHeroes,
            Hero = s.Hero?.Clone(),
            SellsItems = s.SellsItems,
            ShopStock = s.ShopStock is null ? null : (string[])s.ShopStock.Clone(),
            ShopRadius = s.ShopRadius,
            CombatFeedback = s.CombatFeedback,
            ConstructionTime = s.ConstructionTime,
            SupplyBonus = s.SupplyBonus,
            ProducesCategory = s.ProducesCategory,
        };

        // ── Save = persist the in-memory list to the file ─────────

        private void DoSave()
        {
            if (_current == null || _faction == null) return;
            _ = (_jsonPane != null && _paneDirty) ? SaveFromRawPane() : SaveFromForm();
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
            BuildingDefinition? parsed;
            try { parsed = JsonSerializer.Deserialize<BuildingDefinition>(_jsonPane.Text, FactionDefinition.JsonOptions); }
            catch (Exception ex) { ShowError($"Raw JSON didn't parse: {ex.Message}"); return false; }
            if (parsed == null) { ShowError("Raw JSON is empty."); return false; }

            // Validate the parsed building against the OTHER buildings (exclude the one it replaces) + the mesh check.
            var others = _faction.Buildings.Where(b => !ReferenceEquals(b, _current)).ToList();
            BuildingValidationResult res = BuildingDefinitionValidator.Validate(parsed, others);
            string? meshErr = MeshError(parsed);
            if (!res.Ok || meshErr != null)
            {
                string first = meshErr ?? (res.Errors.Count > 0 ? res.Errors[0].Message : "invalid");
                ShowError($"Raw JSON invalid — {first}");
                return false;
            }

            _faction.Buildings[_index] = parsed;   // fold the pane into the in-memory model
            _current = parsed;
            if (!PersistSync()) return false;
            _paneDirty = false;
            Refresh();   // rebuild the form from the folded model (re-seeds the pane, not dirty)
            ShowOk("Saved (raw JSON) — applies on next playtest/match.");
            return true;
        }

        /// <summary>Reconcile the whole in-memory building list into the faction file, atomically, with a reload
        /// self-check — the identical read-current/write-.tmp/self-check-LoadFromFile/atomic-File.Move sequence
        /// <c>UnitCardPanel.Edit.cs:1148-1171</c>'s <c>PersistSync</c> uses.</summary>
        private bool PersistSync()
        {
            if (_faction == null) return false;
            if (string.IsNullOrEmpty(_factionJsonPath)) { ShowError("No faction file is bound — cannot save."); return false; }

            string abs = ProjectSettings.GlobalizePath(_factionJsonPath);
            string tmp = abs + ".tmp";
            try
            {
                string current = File.ReadAllText(abs);
                string patched = FactionWriter.SyncFactionBuildings(current, _faction.Buildings);
                File.WriteAllText(tmp, patched);
                _ = FactionDefinition.LoadFromFile(tmp);   // self-check: refuse to report "Saved" for a file that won't reload
                File.Move(tmp, abs, overwrite: true);
                GD.Print($"[BuildingCard] Saved {abs} ({_faction.Buildings.Count} buildings).");
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
