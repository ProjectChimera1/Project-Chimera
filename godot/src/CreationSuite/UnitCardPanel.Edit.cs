#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using ProjectChimera.Core.Definitions;   // UnitDefinition, FactionDefinition, UnitDefinitionValidator, FactionWriter, UnitValidationResult, ModelAssignment
using ProjectChimera.UI;                   // MeshLoader, SettingsManager (Story 3.5 — AR-5 last-used folder)
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
        // Story 3.12 — the closed attack-delivery set the dropdown offers (mirrors UnitDefinitionValidator._deliveries).
        private static readonly string[] Deliveries = { "Hitscan", "Projectile" };
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
            AddModelRow(_bodyHost, def);   // Story 3.5 — LineEdit + Browse + Box placeholder (replaces the 3.4 AddText stub)

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
            // Story 3.12: authorable delivery (Hitscan/Projectile), decoupled from range. Changing it rebuilds the body
            // (GoToUnit) so the dependent projectile_speed row toggles live. projectile_speed shows only when the
            // effective delivery is Projectile.
            AddDeliveryRow(_bodyHost, def);
            if (def.EffectiveDeliveryString() == "Projectile")
                AddNumFloat(_bodyHost, "Projectile speed", "projectile_speed", "Projectile Speed",
                    "World units/second the projectile travels (default 18). Only used by a Projectile-delivery unit.",
                    0.5, () => def.ProjectileSpeed, v => def.ProjectileSpeed = v, def);

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

            // Composition (Simple, UX-DR54): a preset role bundle fills the unit's abilities; Advanced holds the granular
            // ability + behavior pickers. Story 3.6 (composition = archetype + abilities + behaviors, no subclass).
            AddSection(_bodyHost, "Composition");
            AddCompositionRow(_bodyHost, def);

            // Promote to Hero (Simple, UX-DR54): a switch drives the EXISTING IsHero flag and reveals hero fields.
            // On = Standard leveling preset; the Simple leveling dropdown shows once promoted. Advanced holds every
            // raw hero field. Story 3.7 (a hero is a unit + XP component + leveling table + hero abilities — orthogonal
            // to the archetype, no subclass).
            AddSection(_bodyHost, "Promote to Hero");
            AddHeroPromoteRow(_bodyHost, def);
            if (def.IsHero && def.Hero != null)
                AddHeroLevelingRow(_bodyHost, def);   // Simple: a leveling-curve preset dropdown

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
            AddNumFloat(_advancedHost, "Mesh scale", "mesh_scale", "Mesh Scale", "Visual scale of the model — the preview re-renders live as you change it.",
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

            AddSection(_advancedHost, "Components");
            AddComponentPicker(_advancedHost, "Abilities", "abilities", "+ Add ability…",
                () => def.Abilities, v => def.Abilities = v ?? Array.Empty<string>(),
                AbilityCatalog(), "Abilities",
                "The active/passive abilities this unit can use. An undefined id blocks Save. Add or remove them here (undoable).",
                def);
            AddComponentPicker(_advancedHost, "Behaviors", "behaviors", "+ Add behavior…",
                () => def.Behaviors, v => def.Behaviors = v ?? Array.Empty<string>(),
                BehaviorCatalog(), "Behaviors",
                "The role/AI behaviors this unit composes (e.g. support). An undefined or archetype-incompatible behavior blocks Save.",
                def);

            // Advanced hero fields — only when this unit is a hero (revealed via the GoToUnit rebuild, D-4).
            if (def.IsHero && def.Hero != null)
                BuildHeroAdvanced(_advancedHost, def);

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

        // ── Model row (Story 3.5): LineEdit + Browse (res:// *.glb dialog) + Box placeholder button ──

        /// <summary>
        /// The Model field: the 3.4 typed LineEdit (kept, same commit path) plus a <b>Browse</b> button (opens a
        /// <c>res://</c>-rooted <c>*.glb</c> FileDialog) and a <b>Box placeholder</b> button. Browse/Box both route
        /// through <see cref="AssignMeshPath"/> — one path to set→preview→undo→badge — so a picked model re-renders
        /// the in-panel preview immediately, is undoable, and persists on Save. "Box" = the explicit cleared state
        /// (<c>MeshPath = null</c>), which <c>MeshLoader</c> renders as the box.
        /// </summary>
        private void AddModelRow(Control parent, UnitDefinition def)
        {
            const string key = "mesh_path";
            Func<string> get = () => def.MeshPath ?? "";
            Action<string> set = v => def.MeshPath = ModelAssignment.NormalizeMeshPath(v);   // blank → null (box)

            LineEdit input = ChimeraComponents.Input(placeholder: "res://…/model.glb", text: get());
            _meshPathInput = input;
            string snap = get();
            input.FocusEntered += () => snap = get();
            input.TextChanged += t => { if (_building) return; set(t); OnLiveChanged(key); };
            input.TextSubmitted += _ => { CommitStr(key, snap, get(), set, def); snap = get(); };
            input.FocusExited += () => { CommitStr(key, snap, get(), set, def); snap = get(); };
            AttachFieldTip(input, "Model",
                "res:// path to the unit's GLB. Blank = box placeholder. Use Browse to pick a GLB, or Box for the placeholder.");

            var browse = ChimeraComponents.Button("Browse", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            browse.Pressed += () => OpenMeshBrowseDialog(def);
            AttachFieldTip(browse, "Browse models", "Pick a GLB from the project (res://). Sets this unit's model and re-renders the preview.");

            var box = ChimeraComponents.Button("Box", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            box.Pressed += () => AssignMeshPath(null, def);
            AttachFieldTip(box, "Box placeholder", "Clear the model and use the box placeholder (undoable).");

            // Composite control: the LineEdit expands, the two buttons keep their natural size.
            var composite = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            composite.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            input.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            composite.AddChild(input);
            composite.AddChild(browse);
            composite.AddChild(box);

            AddFieldRow(parent, "Model", composite, MakeBadge(key));
        }

        /// <summary>
        /// Set <c>mesh_path</c> to <paramref name="newPath"/> (null/blank = box placeholder) through the field's
        /// existing set→live-preview→undo→badge path. Setting <see cref="LineEdit.Text"/> in code does NOT fire
        /// <c>TextChanged</c> in Godot, so there is no double-commit (D-2). Called by the Browse and Box buttons.
        /// </summary>
        private void AssignMeshPath(string? newPath, UnitDefinition def)
        {
            if (_current == null || !ReferenceEquals(def, _current)) return;
            Action<string> set = v => def.MeshPath = ModelAssignment.NormalizeMeshPath(v);
            string oldVal = def.MeshPath ?? "";
            set(ModelAssignment.NormalizeMeshPath(newPath) ?? "");   // apply now
            string newVal = def.MeshPath ?? "";
            if (_meshPathInput != null) _meshPathInput.Text = newVal;   // reflect in the field (no TextChanged)
            CommitStr(key: "mesh_path", oldVal: oldVal, newVal: newVal, set: set, def: def);   // undo (no-op if unchanged)
            OnLiveChanged("mesh_path");   // live: re-render preview + revalidate badge
            RevalidateAndReflect();
        }

        /// <summary>Open a <c>res://</c>-rooted <c>*.glb</c> FileDialog under the panel's CanvasLayer; on select, assign
        /// the path and remember its folder (AR-5). The dialog frees itself on select or cancel — no leak.</summary>
        private void OpenMeshBrowseDialog(UnitDefinition def)
        {
            var dlg = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access   = FileDialog.AccessEnum.Resources,   // res://-rooted — no arbitrary-filesystem ingest (Block-If)
                Title    = "Select a GLB model",
                Filters  = new[] { "*.glb ; GLTF Binary" },
            };

            // AR-5: reopen at the last-used folder when we have a valid one.
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

        /// <summary>
        /// Story 3.12 — the attack-delivery dropdown. Displays the EFFECTIVE delivery (so an unauthored ranged unit
        /// reads "Projectile") via <see cref="UnitDefinition.EffectiveDeliveryString"/>, and on pick sets the EXPLICIT
        /// <see cref="UnitDefinition.Delivery"/> string then rebuilds the body (<see cref="GoToUnit"/>, the promote-to-hero
        /// precedent) so the dependent <c>projectile_speed</c> row toggles live — one undoable step (Ctrl+Z reverses a
        /// Projectile↔Hitscan flip). The badge key matches the validator's <c>delivery</c> field path.
        /// </summary>
        private void AddDeliveryRow(Control parent, UnitDefinition def)
        {
            OptionButton sel = ChimeraComponents.Select(Deliveries);
            int cur = Array.IndexOf(Deliveries, def.EffectiveDeliveryString());
            if (cur >= 0) sel.Selected = cur;
            sel.ItemSelected += idx =>
            {
                if (_building) return;
                if (_current == null || !ReferenceEquals(def, _current)) return;
                string? old = def.Delivery;
                string nu = Deliveries[(int)idx];
                if (def.Delivery == nu) return;            // already explicitly this value — no-op
                Action apply  = () => def.Delivery = nu;
                Action revert = () => def.Delivery = old;
                apply();
                UnitDefinition t = def;
                PushHistory(() => { apply(); GoToUnit(t); }, () => { revert(); GoToUnit(t); });
                OnLiveChanged("delivery");
                GoToUnit(t);   // rebuild: the projectile_speed row appears (Projectile) or disappears (Hitscan)
            };
            AttachFieldTip(sel, "Delivery",
                "How the attack lands: Hitscan (instant damage, no projectile) or Projectile (a travelling shot). Independent of range.");
            AddFieldRow(parent, "Delivery", sel, MakeBadge("delivery"));
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

        // ── Structured component pickers (Story 3.6, D-3): chips + an "Add" Select over a registry catalog ──

        /// <summary>One catalog entry the picker offers: the ref <paramref name="Id"/>, its display <paramref name="Label"/>,
        /// and an optional <paramref name="Desc"/> for the remove-chip tooltip. Aligns abilities and behaviors on one shape.</summary>
        private readonly record struct ComponentEntry(string Id, string Label, string Desc);

        /// <summary>The ability catalog from the loaded <see cref="AbilityRegistry"/> (id + display name).</summary>
        private System.Collections.Generic.List<ComponentEntry> AbilityCatalog()
        {
            var list = new System.Collections.Generic.List<ComponentEntry>(_registry.Count);
            foreach (AbilityDefinition a in _registry.All)
                list.Add(new ComponentEntry(a.Id, string.IsNullOrEmpty(a.DisplayName) ? a.Id : a.DisplayName, ""));
            return list;
        }

        /// <summary>The behavior catalog from the loaded <see cref="BehaviorRegistry"/> (id + display name + description).</summary>
        private System.Collections.Generic.List<ComponentEntry> BehaviorCatalog()
        {
            var list = new System.Collections.Generic.List<ComponentEntry>(_behaviorRegistry.Count);
            foreach (BehaviorDefinition b in _behaviorRegistry.All)
                list.Add(new ComponentEntry(b.Id, string.IsNullOrEmpty(b.DisplayName) ? b.Id : b.DisplayName, b.Description));
            return list;
        }

        /// <summary>
        /// A structured multi-select over a registry: renders the attached ids as removable chips plus an "Add" Select of
        /// not-yet-attached catalog entries. Each add/remove writes the whole <c>string[]</c> back through the field's
        /// setter, pushes one undo entry, and routes through the live validate/save path — so undo, located badges, the raw
        /// pane, and Save all work with no new plumbing (D-3). Replaces the 3.4 read-only abilities row.
        /// </summary>
        private void AddComponentPicker(Control parent, string label, string key, string addPrompt,
                                        Func<string[]> get, Action<string[]?> set,
                                        System.Collections.Generic.IReadOnlyList<ComponentEntry> catalog,
                                        string tipTerm, string tipBody, UnitDefinition def)
        {
            string[] attached = get() ?? Array.Empty<string>();

            var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            col.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));

            // Attached ids → chips (Tag + ✕). Empty → a muted "(none)".
            if (attached.Length > 0)
            {
                var chips = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                chips.AddThemeConstantOverride("h_separation", ChimeraComponents.Const(ThemeTokens.S1));
                chips.AddThemeConstantOverride("v_separation", ChimeraComponents.Const(ThemeTokens.S1));
                foreach (string id in attached)
                {
                    string cid = id;
                    // Single null-safe lookup: FirstOrDefault yields default(ComponentEntry) (Id == null) on no match,
                    // so a null/unknown cid falls through to the raw id instead of crashing on a second .First().
                    ComponentEntry match = catalog.FirstOrDefault(c => c.Id == cid);
                    bool known = cid != null && match.Id == cid;
                    string disp = known ? match.Label : (cid ?? "");   // unknown/null id → raw id (still badged by the validator)
                    string desc = known ? match.Desc : "";
                    chips.AddChild(MakeComponentChip(cid, disp, desc, () => DetachComponent(key, get, set, cid, def)));
                }
                col.AddChild(chips);
            }
            else
            {
                col.AddChild(Body("(none)", ThemeTokens.TextLo));
            }

            // "Add" select: a leading no-op prompt at index 0, then every catalog entry not already attached.
            var items = new System.Collections.Generic.List<string> { addPrompt };
            var ids = new System.Collections.Generic.List<string?> { null };
            foreach (ComponentEntry e in catalog)
            {
                if (Array.IndexOf(attached, e.Id) >= 0) continue;   // already attached — omit
                items.Add(string.IsNullOrEmpty(e.Label) || e.Label == e.Id ? e.Id : $"{e.Label} ({e.Id})");
                ids.Add(e.Id);
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
                if (nid != null) AttachComponent(key, get, set, nid, def);
            };
            AttachFieldTip(add, tipTerm, tipBody);
            col.AddChild(add);

            AddFieldRow(parent, label, col, MakeBadge(key));
        }

        /// <summary>An attached-component chip: a <see cref="ChimeraComponents.Tag"/> plus an ✕ <see cref="ChimeraComponents.IconButton"/> that detaches it.</summary>
        private Control MakeComponentChip(string id, string display, string desc, Action onRemove)
        {
            var chip = new HBoxContainer();
            chip.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            var tag = ChimeraComponents.Tag(display);
            tag.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            chip.AddChild(tag);
            var x = ChimeraComponents.IconButton("✕");
            x.Pressed += onRemove;
            string tip = string.IsNullOrEmpty(desc)
                ? $"Detach '{id}' from this unit (undoable)."
                : $"{desc}\nDetach '{id}' from this unit (undoable).";
            AttachFieldTip(x, "Remove", tip);
            chip.AddChild(x);
            return chip;
        }

        private void AttachComponent(string key, Func<string[]> get, Action<string[]?> set, string id, UnitDefinition def)
        {
            string[] old = get() ?? Array.Empty<string>();
            if (Array.IndexOf(old, id) >= 0) return;
            string[] nu = old.Append(id).ToArray();
            ApplyComponentList(key, set, old, nu, def);
        }

        private void DetachComponent(string key, Func<string[]> get, Action<string[]?> set, string id, UnitDefinition def)
        {
            string[] old = get() ?? Array.Empty<string>();
            string[] nu = old.Where(x => x != id).ToArray();
            if (nu.Length == old.Length) return;
            ApplyComponentList(key, set, old, nu, def);
        }

        /// <summary>Commit a component-list change: set the array, push one undo entry, revalidate + rebuild so chips reflect it.</summary>
        private void ApplyComponentList(string key, Action<string[]?> set, string[] oldArr, string[] newArr, UnitDefinition def)
        {
            set(newArr);
            UnitDefinition t = def;
            PushHistory(() => { set(newArr); GoToUnit(t); }, () => { set(oldArr); GoToUnit(t); });
            OnLiveChanged(key);
            Refresh();   // re-render the chips + the "Add" list (the newly attached id drops out of it)
        }

        // ── Simple-mode composition preset (Story 3.6): a role bundle that fills def.Abilities ──

        /// <summary>The Simple-mode "Composition" dropdown: preselected via <see cref="UnitCompositionPresets.Detect"/>
        /// (so a hand-composed set reads back as <c>Custom</c>), and on pick applies the bundle's ids — filtered to what
        /// the live <see cref="AbilityRegistry"/> actually has (lenient, D-3) — to <c>def.Abilities</c> through the
        /// undo/validate/save path. Selecting <c>Custom</c> is a deliberate no-op (never wipes an authored set).</summary>
        private void AddCompositionRow(Control parent, UnitDefinition def)
        {
            string[] labels = UnitCompositionPresets.All.Select(a => a.Label).ToArray();
            OptionButton sel = ChimeraComponents.Select(labels);
            UnitCompositionPresets.Kind detected = UnitCompositionPresets.Detect(def.Abilities);
            int cur = Array.FindIndex(UnitCompositionPresets.All, a => a.Kind == detected);
            if (cur >= 0) sel.Selected = cur;
            sel.ItemSelected += idx =>
            {
                if (_building) return;
                UnitCompositionPresets.Kind chosen = UnitCompositionPresets.All[(int)idx].Kind;
                if (chosen == UnitCompositionPresets.Kind.Custom) return;   // no-op — keep the current set
                string[] old = def.Abilities ?? Array.Empty<string>();
                string[] bundle = UnitCompositionPresets.Bundle(chosen)
                    .Where(id => _registry.IndexOf(id) >= 0).ToArray();   // drop ids absent from the live registry
                if (bundle.Length == 0) return;   // every preset id filtered out (missing from this registry) — never wipe the authored set
                if (old.SequenceEqual(bundle)) return;
                def.Abilities = bundle;
                UnitDefinition t = def;
                PushHistory(() => { def.Abilities = bundle; GoToUnit(t); }, () => { def.Abilities = old; GoToUnit(t); });
                OnLiveChanged("abilities");
                Refresh();
            };
            AttachFieldTip(sel, "Composition",
                "Pick a role preset (Healer / Bruiser / Caster) to fill this unit's abilities. Switch to Advanced for the individual ability & behavior pickers; Custom keeps your own set.");
            AddFieldRow(parent, "Composition", sel, MakeBadge("composition"));
        }

        // ── Promote to Hero (Story 3.7): a switch drives the existing IsHero + reveals hero fields ──

        /// <summary>The Standard-preset default hero block instantiated when the creator flips the switch on.</summary>
        private static HeroDefinition MakeDefaultHero()
        {
            HeroLevelingPresets.Curve c = HeroLevelingPresets.Bundle(HeroLevelingPresets.Kind.Standard);
            return new HeroDefinition { MaxLevel = c.MaxLevel, BaseXp = c.BaseXp, XpGrowth = c.XpGrowth, XpPerKill = c.XpPerKill };
        }

        /// <summary>The Promote-to-Hero switch row (Simple, UX-DR54): a <see cref="ChimeraSwitch"/> bound to the EXISTING
        /// <see cref="UnitDefinition.IsHero"/>. Toggling it mutates <c>IsHero</c>+<c>Hero</c> then rebuilds the body via
        /// <see cref="GoToUnit"/> (the composition-preset precedent, D-4) so hero rows appear seeded / vanish cleared — one
        /// undoable step. The <c>is_hero</c> coherence error badges on this row.</summary>
        private void AddHeroPromoteRow(Control parent, UnitDefinition def)
        {
            ChimeraSwitch sw = ChimeraSwitch.Create(def.IsHero);
            sw.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            sw.Toggled += on => { if (!_building) OnPromoteToggled(def, on); };
            AttachFieldTip(sw, "Promote to Hero",
                "Make this unit a hero: adds a leveling curve, an XP-gain rule, and signature/ultimate ability slots. Off clears them.");

            // The switch would stretch under AddFieldRow's ExpandFill, so wrap it so it keeps its natural size at the left.
            var holder = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            sw.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            holder.AddChild(sw);
            AddFieldRow(parent, "Hero", holder, MakeBadge("is_hero"));
        }

        /// <summary>Apply a Promote-to-Hero toggle: set <c>IsHero</c>+<c>Hero</c> together (on ⇒ Standard preset, off ⇒
        /// null), push ONE undo entry, and rebuild so hero rows reveal/hide. Toggling off leaves a valid non-hero unit.</summary>
        private void OnPromoteToggled(UnitDefinition def, bool on)
        {
            if (_current == null || !ReferenceEquals(def, _current)) return;
            bool oldFlag = def.IsHero;
            HeroDefinition? oldHero = def.Hero;
            HeroDefinition? newHero = on ? (oldHero ?? MakeDefaultHero()) : null;
            if (oldFlag == on && ReferenceEquals(oldHero, newHero)) return;   // already consistent — nothing to do

            Action apply = () => { def.IsHero = on; def.Hero = newHero; };
            Action revert = () => { def.IsHero = oldFlag; def.Hero = oldHero; };
            apply();
            UnitDefinition t = def;
            PushHistory(() => { apply(); GoToUnit(t); }, () => { revert(); GoToUnit(t); });
            GoToUnit(t);   // rebuild the body: hero rows appear (seeded) or disappear (cleared)
        }

        /// <summary>The Simple-mode "Leveling" dropdown: preselected via <see cref="HeroLevelingPresets.Detect"/> (a
        /// hand-tuned curve reads back as <c>Custom</c>), applying the picked preset's four curve fields through the
        /// undo/validate/save path. Custom is a deliberate no-op (never wipes an authored curve).</summary>
        private void AddHeroLevelingRow(Control parent, UnitDefinition def)
        {
            HeroDefinition h = def.Hero!;
            string[] labels = HeroLevelingPresets.All.Select(a => a.Label).ToArray();
            OptionButton sel = ChimeraComponents.Select(labels);
            HeroLevelingPresets.Kind detected = HeroLevelingPresets.Detect(h);
            int cur = Array.FindIndex(HeroLevelingPresets.All, a => a.Kind == detected);
            if (cur >= 0) sel.Selected = cur;
            sel.ItemSelected += idx =>
            {
                if (_building) return;
                HeroLevelingPresets.Kind chosen = HeroLevelingPresets.All[(int)idx].Kind;
                if (chosen == HeroLevelingPresets.Kind.Custom) return;   // no-op — keep the current curve
                HeroLevelingPresets.Curve c = HeroLevelingPresets.Bundle(chosen);
                HeroLevelingPresets.Curve old = new(h.MaxLevel, h.BaseXp, h.XpGrowth, h.XpPerKill);
                if (old == c) return;
                Action applyNew = () => { h.MaxLevel = c.MaxLevel; h.BaseXp = c.BaseXp; h.XpGrowth = c.XpGrowth; h.XpPerKill = c.XpPerKill; };
                Action applyOld = () => { h.MaxLevel = old.MaxLevel; h.BaseXp = old.BaseXp; h.XpGrowth = old.XpGrowth; h.XpPerKill = old.XpPerKill; };
                applyNew();
                UnitDefinition t = def;
                PushHistory(() => { applyNew(); GoToUnit(t); }, () => { applyOld(); GoToUnit(t); });
                OnLiveChanged("hero.leveling");
                Refresh();   // reflect the applied curve in the Advanced hero fields
            };
            AttachFieldTip(sel, "Leveling",
                "Pick a leveling-curve preset (Standard / Fast / Slow). Switch to Advanced for the individual max level / XP fields; Custom keeps your own curve.");
            AddFieldRow(parent, "Leveling", sel, MakeBadge("hero.leveling"));
        }

        /// <summary>The Advanced "Hero" section: one row per raw hero field (numbers via <see cref="AddNumInt"/>/
        /// <see cref="AddNumFloat"/>; signature/ultimate via an ability Select + "(none)"). Every row carries a
        /// hover/focus tooltip + a located validation badge whose key matches the validator's <c>hero.*</c> keys.</summary>
        private void BuildHeroAdvanced(Control parent, UnitDefinition def)
        {
            HeroDefinition h = def.Hero!;
            AddSection(parent, "Hero");
            AddNumInt(parent, "Max level", "hero.max_level", "Max Level", "The highest level this hero can reach (2–100).",
                () => h.MaxLevel, v => h.MaxLevel = v, def);
            AddNumFloat(parent, "Base XP", "hero.base_xp", "Base XP", "XP required for the first level-up (must be > 0).",
                1, () => h.BaseXp, v => h.BaseXp = v, def);
            AddNumFloat(parent, "XP growth", "hero.xp_growth", "XP Growth", "Per-level multiplier on the XP requirement (≥ 1).",
                0.05, () => h.XpGrowth, v => h.XpGrowth = v, def);
            AddNumFloat(parent, "XP per kill", "hero.xp_per_kill", "XP Per Kill", "XP granted per enemy kill credited to this hero.",
                1, () => h.XpPerKill, v => h.XpPerKill = v, def);
            AddHeroAbilityRow(parent, "Signature", "hero.signature_ability", "Signature Ability",
                "The hero's signature ability (unlocked on level-up). Pick a defined ability or (none).",
                () => h.SignatureAbility, v => h.SignatureAbility = v, def);
            AddHeroAbilityRow(parent, "Ultimate", "hero.ultimate_ability", "Ultimate Ability",
                "The hero's ultimate ability — must differ from the signature. Pick a defined ability or (none).",
                () => h.UltimateAbility, v => h.UltimateAbility = v, def);
        }

        /// <summary>A hero ability-slot Select: a leading "(none)" (⇒ null, the unauthored/valid state) then every ability
        /// in the catalog, storing the ability id (NOT its display label). An out-of-catalog current id (an unknown ref
        /// from a raw-JSON edit) is appended so it stays selectable — the validator still badges it. On pick, sets the
        /// slot, pushes one undo entry, and routes through the live validate/save path.</summary>
        private void AddHeroAbilityRow(Control parent, string label, string key, string term, string body,
                                       Func<string?> get, Action<string?> set, UnitDefinition def)
        {
            var items = new System.Collections.Generic.List<string> { "(none)" };
            var ids = new System.Collections.Generic.List<string?> { null };
            foreach (ComponentEntry e in AbilityCatalog())
            {
                items.Add(string.IsNullOrEmpty(e.Label) || e.Label == e.Id ? e.Id : $"{e.Label} ({e.Id})");
                ids.Add(e.Id);
            }
            string? currentId = string.IsNullOrEmpty(get()) ? null : get();
            if (currentId != null && !ids.Contains(currentId)) { items.Add(currentId); ids.Add(currentId); }

            OptionButton sel = ChimeraComponents.Select(items.ToArray());
            int curIdx = ids.IndexOf(currentId);
            sel.Selected = curIdx >= 0 ? curIdx : 0;
            sel.ItemSelected += idx =>
            {
                if (_building) return;
                int i = (int)idx;
                if (i < 0 || i >= ids.Count) return;
                string? old = string.IsNullOrEmpty(get()) ? null : get();
                string? nu = ids[i];
                if (old == nu) return;
                set(nu);
                UnitDefinition t = def;
                PushHistory(() => { set(nu); GoToUnit(t); }, () => { set(old); GoToUnit(t); });
                OnLiveChanged(key);
            };
            AttachFieldTip(sel, term, body);
            AddFieldRow(parent, label, sel, MakeBadge(key));
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

            UnitValidationResult res = _validator.Validate(_current, _registry, _behaviorRegistry, _faction?.Units);
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

        /// <summary>The one validation rule that needs Godot (D-9): the mesh path must resolve to a real mesh, or be
        /// blank (box placeholder). For the live-previewed unit we trust the actual render outcome (<see cref="_lastMeshMissing"/>),
        /// so this flags both a MISSING path and an existing-but-corrupt GLB that yields no mesh (Story 3.5, D-3). For any
        /// other def (e.g. the raw-JSON pane's parsed candidate, which has no live preview) we fall back to a path-existence
        /// check.</summary>
        private string? MeshError(UnitDefinition def)
        {
            string mp = def.MeshPath ?? "";
            if (mp.Length == 0) return null;   // blank = box placeholder — always valid

            bool bad = ReferenceEquals(def, _current) ? _lastMeshMissing : !ResourceLoader.Exists(mp);
            if (bad)
                return $"unit '{def.Id}'.mesh_path: '{mp}' didn't load a mesh (leave blank for a box placeholder).";
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
            Delivery = s.Delivery, ProjectileSpeed = s.ProjectileSpeed,   // Story 3.12
            // Null-guard each array: the property defaults are Array.Empty, but a raw-JSON hatch edit of
            // "prerequisites"/"abilities"/"behaviors": null overrides the initializer with null, and an unguarded
            // .Clone() would then NullReference-crash the Duplicate action.
            Prerequisites = s.Prerequisites is null ? Array.Empty<string>() : (string[])s.Prerequisites.Clone(),
            Abilities = s.Abilities is null ? Array.Empty<string>() : (string[])s.Abilities.Clone(),
            Behaviors = s.Behaviors is null ? Array.Empty<string>() : (string[])s.Behaviors.Clone(),
            AttackDomains = s.AttackDomains?.Clone() as string[],
            Tags = s.Tags?.Clone() as string[],
            IsHero = s.IsHero,
            Hero = s.Hero?.Clone(),   // deep-copy the hero block so the clone validates independently (Story 3.7)
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
            UnitValidationResult res = _validator.Validate(parsed, _registry, _behaviorRegistry, others);
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
