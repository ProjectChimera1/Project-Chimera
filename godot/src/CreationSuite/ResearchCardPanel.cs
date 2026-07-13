#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using ProjectChimera.Core.Definitions;   // ResearchDefinition, FactionDefinition, ResearchValidator, FactionWriter
using ProjectChimera.UI;                  // GameState, GameMode
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraTabs, ChimeraTooltip, ChimeraValidationBadge, ChimeraDialog
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 4.11 — the Research Card Editor: a right-dock inspector over the current faction's
    /// <see cref="FactionDefinition.Research"/> list, sibling to <see cref="BuildingCardPanel"/> (Story 4.5) and
    /// built to the SAME shell shape (browse cursor, Simple/Advanced <see cref="ChimeraTabs"/> disclosure, per-field
    /// <see cref="ChimeraValidationBadge"/>, raw-JSON escape hatch, <see cref="EditorHistory"/> undo, Save/New/
    /// Duplicate/Delete toolbar) — but owned directly by <see cref="TechTreePanel"/> (constructed in its
    /// <c>BuildUi</c>, bound in its <c>Initialize</c>), not published by a separate bootstrap phase, since nothing
    /// else in the app needs to reach it. Opens on research-node selection via <see cref="SelectAndShow"/>, mirroring
    /// <c>TechTreePanel.OnNodeSelected</c> → <c>BuildingCardPanel.SelectAndShow</c>.
    ///
    /// <para><b>The repeatable Levels list.</b> No generalized reusable list-editor component exists in this
    /// codebase (Design Notes) — the <see cref="ResearchLevel"/> row UI below (cost map + time + the four modifier-
    /// delta fields, with its own add/remove-row affordances) is scoped to this panel's own needs, not a shared
    /// abstraction. Every level edit (field tweak, add, remove) snapshots the WHOLE <c>Levels</c> list for undo —
    /// simpler and safer than per-field undo granularity for a dynamically-sized nested list.</para>
    ///
    /// <para><b>Determinism posture — PURE AUTHORING-TIME, zero fold.</b> Editing a content POCO and rewriting a JSON
    /// file touches no <c>EntityWorld</c>/store/sim array and moves no checksum or golden — mirrors
    /// <see cref="BuildingCardPanel"/>'s posture exactly. The only <c>src/Core</c> touches are the Godot-free
    /// <see cref="ResearchValidator"/> + <see cref="FactionWriter"/>.</para>
    /// </summary>
    public partial class ResearchCardPanel : Node
    {
        // ── Layout constants ──
        private const float PANEL_W = 480f;
        private const float PANEL_H = 700f;
        // Story 4.11: offset further left than BuildingCardPanel's own CenterRight slot so the two inspectors never
        // overlap if both happen to be open at once (a building-node selection and a research-node selection each
        // open their own inspector independently; neither hides the other).
        private const float MARGIN  = 12f;
        private const float LEFT_OFFSET = PANEL_W + MARGIN + PANEL_W + MARGIN;

        private const double StatMax = 32767;   // one below the 16.16 ceiling (mirrors BuildingCardPanel.Edit.cs)

        // ── Kit context (self-owned; _accent only created when this panel is the first consumer) ──
        private GodotTheme        _theme  = null!;
        private AccentController? _accent;

        // ── Deps (wired by TechTreePanel after AddChild) ──
        private FactionDefinition? _faction;               // the research source (Research only — never Units/Buildings)
        private GameState?         _gameState;
        private int                _index;                 // browse cursor into _faction.Research
        private string             _factionJsonPath = "";  // res:// path of the faction file to write edits back to

        // ── Edit state ──
        private ResearchDefinition?    _current;
        private readonly EditorHistory _history = new();
        private readonly Dictionary<string, ChimeraValidationBadge> _badges = new();
        private bool _building;   // guard: suppress live handlers while (re)building controls
        private bool _lastValid = true;

        // ── Shell ──
        private CanvasLayer    _canvas = null!;
        private PanelContainer _panel  = null!;
        private VBoxContainer  _headerHost = null!;
        private VBoxContainer  _bodyHost   = null!;
        private Label          _counterLabel = null!;
        private Godot.Button   _prevBtn = null!;
        private Godot.Button   _nextBtn = null!;

        private ChimeraTabs    _segment = null!;
        private VBoxContainer?  _advancedHost;
        private TextEdit?       _jsonPane;
        private bool            _paneDirty;
        private bool            _suppressPaneDirty;
        private Label           _statusLabel = null!;
        private Godot.Button    _saveBtn   = null!;
        private Godot.Button    _newBtn    = null!;
        private Godot.Button    _dupBtn    = null!;
        private Godot.Button    _deleteBtn = null!;
        private VBoxContainer?  _levelsHost;   // the repeatable Levels rows (rebuilt on add/remove)

        /// <summary>The only resource ids the level cost-map "+ Add resource" select offers — the SAME shared set
        /// <see cref="ResourceCostValidator.KnownResourceIds"/> / <see cref="ResearchValidator"/> enforce.</summary>
        private static readonly string[] CostResourceIds = ResourceCostValidator.KnownResourceIds;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        public override void _Ready()
        {
            EnsureKitInitialized();   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>Bind the panel to the current scenario's faction + game state + the faction file <c>res://</c>
        /// path to persist edits to. Called by <c>TechTreePanel</c> AFTER <c>AddChild</c>, mirroring
        /// <c>BuildingCardPanel.Initialize</c>. Starts hidden; shown by <see cref="SelectAndShow"/>.</summary>
        public void Initialize(FactionDefinition? faction, GameState gameState, string factionJsonPath = "")
        {
            _faction         = faction;
            _gameState       = gameState;
            _factionJsonPath = factionJsonPath ?? "";
            _index           = 0;

            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;
        }

        /// <summary>Hide the panel.</summary>
        public void Close() => _panel.Visible = false;

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();
        }

        /// <summary>Story 4.11: open this inspector already bound to <paramref name="research"/> — the Tech Tree
        /// Editor's <c>node_selected</c> handler calls this, mirroring <c>BuildingCardPanel.SelectAndShow</c>.</summary>
        public void SelectAndShow(ResearchDefinition research)
        {
            _panel.Visible = true;
            GoToResearch(research);
        }

        // ── Input: undo/redo ─────────────────────────────────────────────

        public override void _Input(InputEvent @event)
        {
            if (_panel is null || !_panel.Visible) return;
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            if (key.CtrlPressed && key.Keycode == Key.Z) { _history.Undo(); GetViewport().SetInputAsHandled(); return; }
            if (key.CtrlPressed && key.Keycode == Key.Y) { _history.Redo(); GetViewport().SetInputAsHandled(); return; }
        }

        // ── Kit bootstrap (mirrors BuildingCardPanel.EnsureKitInitialized) ──────────

        private void EnsureKitInitialized()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 13 };
            AddChild(_canvas);

            // Right-docked but shifted one panel-width further left (LEFT_OFFSET) so it sits BESIDE the building/unit
            // card, not on top of it. Explicit offsets + GrowHorizontal.Begin keep it on-screen and grow over-wide
            // content leftward instead of off the right edge (see BuildingCardPanel).
            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.GrowHorizontal = Control.GrowDirection.Begin;
            _panel.GrowVertical   = Control.GrowDirection.Both;
            _panel.OffsetRight  = -(LEFT_OFFSET - PANEL_W);
            _panel.OffsetLeft   = -LEFT_OFFSET;
            _panel.OffsetTop    = -PANEL_H * 0.5f;
            _panel.OffsetBottom =  PANEL_H * 0.5f;
            _panel.Theme = _theme;
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var titleLbl = Heading("Research Editor", ThemeTokens.Tlg);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(titleLbl);

            _prevBtn = ChimeraComponents.IconButton("◀");
            _prevBtn.Pressed += () => Browse(-1);
            titleRow.AddChild(_prevBtn);

            _counterLabel = Body("—", ThemeTokens.TextMid);
            _counterLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _counterLabel.CustomMinimumSize = new Vector2(88, 0);
            _counterLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(_counterLabel);

            _nextBtn = ChimeraComponents.IconButton("▶");
            _nextBtn.Pressed += () => Browse(1);
            titleRow.AddChild(_nextBtn);

            var closeBtn = ChimeraComponents.Button("Close", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += Close;
            closeBtn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(closeBtn);

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

            _headerHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _headerHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            contentCol.AddChild(_headerHost);

            _segment = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced");
            _segment.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _segment.TabChanged += OnSegmentChanged;
            contentCol.AddChild(_segment);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            contentCol.AddChild(_bodyHost);

            _statusLabel = Body("", ThemeTokens.TextLo);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            root.AddChild(BuildToolbar());

            _panel.Visible = false;
        }

        // ── Per-research binding ─────────────────────────────────────────────────

        private void Bind(ResearchDefinition def)
        {
            _current = def;
            ClearHosts();
            BuildHeader(def);
            BuildEditableBody(def);
            RevalidateAndReflect();
        }

        private void Refresh()
        {
            if (_faction is null || _faction.Research is null || _faction.Research.Count == 0)
            {
                _current = null;
                ClearHosts();
                BuildEmptyState();
                UpdateCounter(0, 0);
                UpdateToolbarEnabled();
                return;
            }
            if (_index < 0 || _index >= _faction.Research.Count) _index = 0;
            Bind(_faction.Research[_index]);
            UpdateCounter(_index + 1, _faction.Research.Count);
        }

        private void Browse(int dir)
        {
            if (_faction is null || _faction.Research is null || _faction.Research.Count == 0) return;
            int n = _faction.Research.Count;
            _index = ((_index + dir) % n + n) % n;
            Refresh();
        }

        private void UpdateCounter(int i, int n)
        {
            _counterLabel.Text = n == 0 ? "—" : $"RESEARCH {i} / {n}";
            _prevBtn.Disabled = n <= 1;
            _nextBtn.Disabled = n <= 1;
        }

        private void ClearHosts()
        {
            foreach (Node c in _headerHost.GetChildren()) { _headerHost.RemoveChild(c); c.QueueFree(); }
            foreach (Node c in _bodyHost.GetChildren())   { _bodyHost.RemoveChild(c);   c.QueueFree(); }
            _badges.Clear();
            _advancedHost = null;
            _jsonPane = null;
            _levelsHost = null;
            _paneDirty = false;
        }

        private void BuildEmptyState()
        {
            _segment.Visible = false;
            _headerHost.AddChild(Heading("Research Editor", ThemeTokens.Txl));
            _bodyHost.AddChild(Body(_faction is null ? "No faction bound." : "This faction has no research — press New to add one.", ThemeTokens.TextLo));
        }

        private void BuildHeader(ResearchDefinition def)
        {
            _segment.Visible = true;
            var title = Heading(string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName, ThemeTokens.T2xl);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.AutowrapMode = TextServer.AutowrapMode.Word;
            _headerHost.AddChild(title);

            var id = Body(def.Id, ThemeTokens.TextLo);
            id.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txs, ThemeTokens.Type));
            _headerHost.AddChild(id);

            var tag = ChimeraComponents.Tag($"{def.Levels?.Count ?? 0} level(s)", ChimeraComponents.TagVariant.Accent);
            _headerHost.AddChild(tag);
        }

        // ── Editable body ────────────────────────────────────────────────────────

        private void BuildEditableBody(ResearchDefinition def)
        {
            _building = true;

            AddSection(_bodyHost, "Identity");
            AddText(_bodyHost, "Id", "id", "Id",
                "The unique id ([a-z0-9_]). Referenced by other research's Prerequisites and by a building's Available research field.",
                () => def.Id, v => def.Id = v, def);
            AddText(_bodyHost, "Name", "display_name", "Display Name", "The human-readable name shown on the command card.",
                () => def.DisplayName, v => def.DisplayName = v, def);

            AddSection(_bodyHost, "Levels");
            _levelsHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _levelsHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _bodyHost.AddChild(_levelsHost);
            RebuildLevelsHost(def);

            var addLevel = ChimeraComponents.Button("+ Add level", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            addLevel.Pressed += () => DoAddLevel(def);
            _bodyHost.AddChild(addLevel);

            AddSection(_bodyHost, "Cancellation");
            AddNumFloat01(_bodyHost, "Cancel refund", "cancel_refund_fraction", "Cancel Refund Fraction",
                "Fraction (0-1) of the current level's cost refunded when an in-progress order is cancelled.",
                () => def.CancelRefundFraction, v => def.CancelRefundFraction = v, def);

            _advancedHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = _segment.Active == 1 };
            _advancedHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _bodyHost.AddChild(_advancedHost);

            AddSection(_advancedHost, "Lists (comma-separated)");
            AddCommaList(_advancedHost, "Prerequisites", "prerequisites", "Prerequisites",
                "Building ids OR other research ids required before this research can be started (Story 4.11).",
                () => def.Prerequisites, v => def.Prerequisites = v ?? Array.Empty<string>(), def);

            AddSection(_advancedHost, "Raw JSON (this research)");
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
            if (_badges.TryGetValue(key, out ChimeraValidationBadge? existing)) return existing;
            var b = ChimeraValidationBadge.Create();
            _badges[key] = b;
            return b;
        }

        private static void AttachFieldTip(Control target, string term, string body)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            ChimeraTooltip.Attach(target, term, body, ChimeraTooltip.TooltipRole.Field);
        }

        private void AddText(Control parent, string label, string key, string term, string body,
                             Func<string> get, Action<string> set, ResearchDefinition def)
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

        /// <summary>A [0,1]-clamped float row (cancel_refund_fraction).</summary>
        private void AddNumFloat01(Control parent, string label, string key, string term, string body,
                                   Func<float> get, Action<float> set, ResearchDefinition def)
        {
            SpinBox spin = ChimeraComponents.NumInput(value: get(), min: 0, max: 1, step: 0.05);
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

        private void AddCommaList(Control parent, string label, string key, string term, string body,
                                  Func<string[]?> get, Action<string[]?> set, ResearchDefinition def)
        {
            string Join() { string[]? a = get(); return a == null ? "" : string.Join(", ", a); }
            string[] Parse(string s) => s.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

            LineEdit input = ChimeraComponents.Input(placeholder: "comma, separated", text: Join());
            string snap = Join();
            input.FocusEntered += () => snap = Join();
            input.TextChanged += t => { if (_building) return; set(Parse(t)); OnLiveChanged(key); };
            input.FocusExited += () =>
            {
                string now = Join();
                if (now != snap)
                {
                    string[] oldArr = Parse(snap), newArr = Parse(now);
                    ResearchDefinition t = def;
                    PushHistory(() => { set(newArr); GoToResearch(t); }, () => { set(oldArr); GoToResearch(t); });
                }
                snap = now;
            };
            AttachFieldTip(input, term, body);
            AddFieldRow(parent, label, input, MakeBadge(key));
        }

        private void OnLiveChanged(string key)
        {
            if (_current == null) return;
            if (key is "display_name" or "id") RefreshHeader();
            RevalidateAndReflect();
        }

        private void RefreshHeader()
        {
            if (_current == null) return;
            foreach (Node c in _headerHost.GetChildren()) { _headerHost.RemoveChild(c); c.QueueFree(); }
            BuildHeader(_current);
        }

        private void CommitStr(string oldVal, string newVal, Action<string> set, ResearchDefinition def)
        {
            if (oldVal == newVal) return;
            ResearchDefinition t = def;
            PushHistory(() => { set(newVal); GoToResearch(t); }, () => { set(oldVal); GoToResearch(t); });
        }

        private void PushValue(ResearchDefinition def, Action<double> set, double oldVal, double newVal)
        {
            ResearchDefinition t = def;
            PushHistory(() => { set(newVal); GoToResearch(t); }, () => { set(oldVal); GoToResearch(t); });
        }

        // ── Levels list (repeatable) — the minimal, non-generalized row UI (Design Notes) ──────────────────────

        private void RebuildLevelsHost(ResearchDefinition def)
        {
            if (_levelsHost == null) return;
            foreach (Node c in _levelsHost.GetChildren()) { _levelsHost.RemoveChild(c); c.QueueFree(); }

            List<ResearchLevel> levels = def.Levels ?? new List<ResearchLevel>();
            if (levels.Count == 0)
            {
                _levelsHost.AddChild(Body("(no levels — press + Add level below)", ThemeTokens.TextLo));
                return;
            }
            for (int i = 0; i < levels.Count; i++)
                _levelsHost.AddChild(BuildLevelRow(def, i));
        }

        private Control BuildLevelRow(ResearchDefinition def, int levelIndex)
        {
            ResearchLevel level = def.Levels[levelIndex];

            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));

            var headerRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            headerRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            var lvlTag = ChimeraComponents.Tag($"Level {levelIndex + 1}");
            headerRow.AddChild(lvlTag);
            var removeBtn = ChimeraComponents.IconButton("✕");
            removeBtn.Pressed += () => DoRemoveLevel(def, levelIndex);
            headerRow.AddChild(removeBtn);
            box.AddChild(headerRow);

            // Time (ticks — 30 ticks = 1 second).
            SpinBox timeSpin = ChimeraComponents.NumInput(value: level.TimeTicks, min: 1, max: 100000, step: 1);
            LineEdit timeLe = timeSpin.GetLineEdit();
            int timeSnap = level.TimeTicks;
            timeLe.FocusEntered += () => timeSnap = level.TimeTicks;
            timeSpin.ValueChanged += v => { if (_building) return; level.TimeTicks = (int)Math.Round(v); OnLevelFieldLiveChanged(); };
            timeLe.FocusExited += () =>
            {
                int now = level.TimeTicks;
                if (now != timeSnap) CommitLevelsSnapshot(def, () => level.TimeTicks = now, () => level.TimeTicks = timeSnap);
                timeSnap = now;
            };
            AttachFieldTip(timeLe, "Time (ticks)", "Duration of this level's research order, in sim ticks (30 ticks = 1 second).");
            AddFieldRow(box, "Time (ticks)", timeSpin, MakeBadge($"levels[{levelIndex}].time_ticks"));

            // Cost map (mirrors BuildingCardPanel.Edit.cs's AddCostMapRow chip pattern, scoped to this level).
            AddLevelCostRow(box, def, level, levelIndex);

            // The four modifier-delta fields (may be negative — symmetric ±StatMax bound).
            level.ModifierDelta ??= new ResearchModifierDelta();
            AddModifierDeltaField(box, "Max HP", levelIndex, "max_health_delta", () => level.ModifierDelta!.MaxHealthDelta, v => level.ModifierDelta!.MaxHealthDelta = v, def);
            AddModifierDeltaField(box, "Attack dmg", levelIndex, "attack_damage_delta", () => level.ModifierDelta!.AttackDamageDelta, v => level.ModifierDelta!.AttackDamageDelta = v, def);
            AddModifierDeltaField(box, "Move speed", levelIndex, "move_speed_delta", () => level.ModifierDelta!.MoveSpeedDelta, v => level.ModifierDelta!.MoveSpeedDelta = v, def);
            AddModifierDeltaField(box, "Armor", levelIndex, "armor_delta", () => level.ModifierDelta!.ArmorDelta, v => level.ModifierDelta!.ArmorDelta = v, def);

            var sep = new HSeparator();
            box.AddChild(sep);
            return box;
        }

        private void AddLevelCostRow(Control parent, ResearchDefinition def, ResearchLevel level, int levelIndex)
        {
            level.Cost ??= new Dictionary<string, int>();
            Dictionary<string, int> attached = level.Cost;

            var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            col.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));

            if (attached.Count > 0)
            {
                var chips = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                chips.AddThemeConstantOverride("h_separation", ChimeraComponents.Const(ThemeTokens.S1));
                chips.AddThemeConstantOverride("v_separation", ChimeraComponents.Const(ThemeTokens.S1));
                foreach (string resId in attached.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    chips.AddChild(MakeLevelCostChip(resId, attached[resId], def, level));
                col.AddChild(chips);
            }
            else
            {
                col.AddChild(Body("(free)", ThemeTokens.TextLo));
            }

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
            add.Disabled = ids.Count <= 1;
            add.ItemSelected += idx =>
            {
                if (_building) return;
                int i = (int)idx;
                if (i <= 0 || i >= ids.Count) return;
                string? nid = ids[i];
                if (nid == null) return;
                Dictionary<string, int> oldMap = new(level.Cost!);
                Dictionary<string, int> newMap = new(level.Cost!) { [nid] = 0 };
                CommitLevelCost(def, level, oldMap, newMap);
            };
            AttachFieldTip(add, "Cost", "Add a resource this level costs — restricted to ore/crystal (the only resources with runtime backing today).");
            col.AddChild(add);

            AddFieldRow(parent, "Cost", col, MakeBadge($"levels[{levelIndex}].cost"));
        }

        private Control MakeLevelCostChip(string resId, int amount, ResearchDefinition def, ResearchLevel level)
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
                level.Cost![resId] = (int)Math.Round(v);
                OnLevelFieldLiveChanged();
            };
            le.FocusExited += () =>
            {
                int now = (int)Math.Round(spin.Value);
                if (now != snap)
                {
                    Dictionary<string, int> oldMap = new(level.Cost!) { [resId] = snap };
                    Dictionary<string, int> newMap = new(level.Cost!) { [resId] = now };
                    CommitLevelCost(def, level, oldMap, newMap, rebuildNow: false);
                }
                snap = now;
            };
            AttachFieldTip(le, resId, $"Amount of '{resId}' this level costs.");
            chip.AddChild(spin);

            var x = ChimeraComponents.IconButton("✕");
            x.Pressed += () =>
            {
                Dictionary<string, int> oldMap = new(level.Cost!);
                Dictionary<string, int> newMap = new(level.Cost!);
                newMap.Remove(resId);
                CommitLevelCost(def, level, oldMap, newMap);
            };
            AttachFieldTip(x, "Remove", $"Remove '{resId}' from this level's cost.");
            chip.AddChild(x);
            return chip;
        }

        private void CommitLevelCost(ResearchDefinition def, ResearchLevel level,
            Dictionary<string, int> oldMap, Dictionary<string, int> newMap, bool rebuildNow = true)
        {
            level.Cost = new Dictionary<string, int>(newMap);
            CommitLevelsSnapshot(def,
                () => level.Cost = new Dictionary<string, int>(newMap),
                () => level.Cost = new Dictionary<string, int>(oldMap),
                rebuildNow);
        }

        private void AddModifierDeltaField(Control parent, string label, int levelIndex, string fieldKey, Func<float> get, Action<float> set, ResearchDefinition def)
        {
            SpinBox spin = ChimeraComponents.NumInput(value: get(), min: -StatMax, max: StatMax, step: 0.1);
            LineEdit le = spin.GetLineEdit();
            float snap = get();
            le.FocusEntered += () => snap = get();
            spin.ValueChanged += v => { if (_building) return; set((float)v); OnLevelFieldLiveChanged(); };
            le.FocusExited += () =>
            {
                float now = get();
                if (now != snap) CommitLevelsSnapshot(def, () => set(now), () => set(snap));
                snap = now;
            };
            AttachFieldTip(le, label, $"Permanent {label} delta this level applies to every current AND future faction unit on completion. May be negative.");
            AddFieldRow(parent, label, spin, MakeBadge($"levels[{levelIndex}].modifier_delta.{fieldKey}"));
        }

        private void OnLevelFieldLiveChanged()
        {
            if (_current == null) return;
            RevalidateAndReflect();
        }

        /// <summary>Push ONE undo entry for a Levels-list edit: snapshot-apply via the given redo/undo delegates,
        /// each of which mutates the live model then re-renders. Simpler than per-field granularity for a
        /// dynamically-sized nested list (Design Notes — no generalized list-editor abstraction).</summary>
        private void CommitLevelsSnapshot(ResearchDefinition def, Action redo, Action undo, bool rebuildNow = true)
        {
            redo();
            ResearchDefinition t = def;
            PushHistory(
                () => { redo(); GoToResearch(t); },
                () => { undo(); GoToResearch(t); });
            RevalidateAndReflect();
            if (rebuildNow) RebuildLevelsHost(t);
        }

        private void DoAddLevel(ResearchDefinition def)
        {
            var newLevel = new ResearchLevel { TimeTicks = 300, Cost = new Dictionary<string, int>() };
            List<ResearchLevel> before = new(def.Levels ?? new List<ResearchLevel>());
            def.Levels ??= new List<ResearchLevel>();
            def.Levels.Add(newLevel);
            List<ResearchLevel> after = new(def.Levels);
            ResearchDefinition t = def;
            PushHistory(
                () => { t.Levels = new List<ResearchLevel>(after); GoToResearch(t); },
                () => { t.Levels = new List<ResearchLevel>(before); GoToResearch(t); });
            RevalidateAndReflect();
            RebuildLevelsHost(def);
        }

        private void DoRemoveLevel(ResearchDefinition def, int levelIndex)
        {
            if (def.Levels == null || levelIndex < 0 || levelIndex >= def.Levels.Count) return;
            List<ResearchLevel> before = new(def.Levels);
            var after = new List<ResearchLevel>(def.Levels);
            after.RemoveAt(levelIndex);
            ResearchDefinition t = def;
            def.Levels = after;
            PushHistory(
                () => { t.Levels = new List<ResearchLevel>(after); GoToResearch(t); },
                () => { t.Levels = new List<ResearchLevel>(before); GoToResearch(t); });
            RevalidateAndReflect();
            RebuildLevelsHost(def);
        }

        // ── Raw-JSON escape hatch ──────────────────────────────────────────────

        private void BuildRawPane(Control parent, ResearchDefinition def)
        {
            var hint = Body("Edit this research's JSON directly. On Save a dirty pane wins — validated, then folded back.", ThemeTokens.TextLo);
            hint.AutowrapMode = TextServer.AutowrapMode.Word;
            hint.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txs, ThemeTokens.Type));
            parent.AddChild(hint);

            _jsonPane = MakeJsonPane();
            _jsonPane.TextChanged += () => { if (!_suppressPaneDirty) _paneDirty = true; };
            parent.AddChild(_jsonPane);
            SetPaneText(FactionWriter.SerializeResearchClean(def));

            var sync = ChimeraComponents.Button("Sync JSON from form", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            sync.Pressed += () => { if (_current != null) SetPaneText(FactionWriter.SerializeResearchClean(_current)); };
            AttachFieldTip(sync, "Sync JSON", "Re-render this research's JSON from the current form values (discards manual pane edits).");
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

        /// <summary>Re-run <see cref="ResearchValidator.Validate"/> over the whole faction, filter to the errors
        /// naming the CURRENT research entry, route each to a field badge by keyword (ResearchValidator emits flat
        /// strings, not (fieldPath, message) tuples like BuildingDefinitionValidator — a graph-level error, e.g. a
        /// cycle or the over-cap count, is not attributable to one field here; the Save-time self-check reload is
        /// the safety net for those). Does not rebuild the form.</summary>
        private bool RevalidateAndReflect()
        {
            foreach (ChimeraValidationBadge b in _badges.Values) b.Clear();

            if (_current == null || _faction == null) { ClearStatus(); _lastValid = true; UpdateToolbarEnabled(); return true; }

            IReadOnlyList<string> allErrors = ResearchValidator.Validate(_faction);
            string prefix = $"research '{_current.Id}'";
            var mine = allErrors.Where(e => e.StartsWith(prefix, StringComparison.Ordinal)).ToList();

            foreach (string e in mine)
                ShowBadge(RouteErrorToKey(e), e);

            bool ok = mine.Count == 0;
            _lastValid = ok;
            if (ok) ShowOk("Valid — Save to write to the faction file.");
            else ShowError($"{mine.Count} field(s) need attention before saving.");
            UpdateToolbarEnabled();
            return ok;
        }

        /// <summary>Routes a flat <see cref="ResearchValidator"/> error string to the specific per-level, per-field
        /// badge it names (e.g. <c>.levels[2].modifier_delta.armor_delta</c> → <c>levels[2].modifier_delta.armor_delta</c>),
        /// falling back to the whole-level bucket or the id field when the error has no addressable per-field home
        /// (e.g. the empty-ladder or duplicate-id-with-no-current-field cases) — never back to a single shared
        /// "levels" key, since every level row now owns its own set of badges (Review-pass fix: the shared key
        /// caused every field past the first in a level to fail <c>AddChild</c> on an already-parented badge).</summary>
        private static string RouteErrorToKey(string e)
        {
            if (e.Contains("duplicate research id")) return "id";
            if (e.Contains(".id:")) return "id";
            int levelsAt = e.IndexOf(".levels[", StringComparison.Ordinal);
            if (levelsAt >= 0)
            {
                int close = e.IndexOf(']', levelsAt);
                if (close > levelsAt)
                {
                    string indexPart = e.Substring(levelsAt + 1, close - levelsAt); // "levels[N]"
                    if (e.IndexOf(".time_ticks", close, StringComparison.Ordinal) is int ti && ti == close + 1)
                        return $"{indexPart}.time_ticks";
                    if (e.IndexOf(".cost", close, StringComparison.Ordinal) is int ci && ci == close + 1)
                        return $"{indexPart}.cost";
                    if (e.IndexOf(".modifier_delta.", close, StringComparison.Ordinal) is int mi && mi == close + 1)
                    {
                        int fieldStart = mi + ".modifier_delta.".Length;
                        int fieldEnd = e.IndexOfAny(new[] { '=', ' ' }, fieldStart);
                        string field = fieldEnd > fieldStart ? e.Substring(fieldStart, fieldEnd - fieldStart) : "";
                        if (field.Length > 0) return $"{indexPart}.modifier_delta.{field}";
                    }
                    return indexPart; // whole-level fallback — no badge exists at this key, ShowBadge no-ops safely
                }
            }
            if (e.Contains(".levels:")) return "id"; // empty-ladder — no level row to attach to
            if (e.Contains("cancel_refund_fraction")) return "cancel_refund_fraction";
            if (e.Contains(".prerequisites:")) return "prerequisites";
            return "id";
        }

        private void ShowBadge(string key, string message)
        {
            if (_badges.TryGetValue(key, out ChimeraValidationBadge? b)) b.ShowError(message);
        }

        // ── Undo/redo ──────────────────────────────────────────────────

        private void PushHistory(Action redo, Action undo) => _history.Push(redo, undo);

        private void GoToResearch(ResearchDefinition r)
        {
            if (_faction == null || _faction.Research == null) return;
            int i = _faction.Research.IndexOf(r);
            if (i >= 0) _index = i;
            Refresh();
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
            AttachFieldTip(_newBtn, "New research", "Add a new research entry with one default level and a unique id.");
            row.AddChild(_newBtn);

            _dupBtn = ChimeraComponents.Button("Duplicate", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            _dupBtn.Pressed += DoDuplicate;
            AttachFieldTip(_dupBtn, "Duplicate", "Clone the current research with a new id.");
            row.AddChild(_dupBtn);

            _deleteBtn = ChimeraComponents.Button("Delete", ChimeraComponents.ButtonVariant.Danger, ChimeraComponents.ButtonSize.Sm);
            _deleteBtn.Pressed += DoDelete;
            AttachFieldTip(_deleteBtn, "Delete", "Remove the current research (asks to confirm first).");
            row.AddChild(_deleteBtn);

            return row;
        }

        private void UpdateToolbarEnabled()
        {
            bool hasResearch = _current != null && _faction != null && (_faction.Research?.Count ?? 0) > 0;
            if (_saveBtn != null!) _saveBtn.Disabled = !hasResearch || !_lastValid;
            if (_dupBtn != null!) _dupBtn.Disabled = !hasResearch;
            if (_deleteBtn != null!) _deleteBtn.Disabled = !hasResearch;
            if (_newBtn != null!) _newBtn.Disabled = _faction == null;
        }

        private void DoCreate()
        {
            if (_faction == null) return;
            _faction.Research ??= new List<ResearchDefinition>();
            var def = new ResearchDefinition
            {
                Id = UniqueId("new_research"),
                DisplayName = "New Research",
                CancelRefundFraction = 0f,
                Prerequisites = Array.Empty<string>(),
                Levels = new List<ResearchLevel> { new ResearchLevel { TimeTicks = 300, Cost = new Dictionary<string, int>() } },
            };
            _faction.Research.Add(def);
            _index = _faction.Research.Count - 1;
            ResearchDefinition captured = def;
            PushHistory(
                redo: () => { if (!_faction.Research.Contains(captured)) _faction.Research.Add(captured); GoToResearch(captured); },
                undo: () => RemoveFromList(captured));
            Refresh();
            ShowOk($"Added '{def.Id}' — Save to write it to the file.");
        }

        private void DoDuplicate()
        {
            if (_faction == null || _current == null) return;
            ResearchDefinition clone = CloneResearch(_current, UniqueId(_current.Id + "_copy"));
            _faction.Research ??= new List<ResearchDefinition>();
            _faction.Research.Add(clone);
            _index = _faction.Research.Count - 1;
            ResearchDefinition captured = clone;
            PushHistory(
                redo: () => { if (!_faction.Research.Contains(captured)) _faction.Research.Add(captured); GoToResearch(captured); },
                undo: () => RemoveFromList(captured));
            Refresh();
            ShowOk($"Duplicated as '{clone.Id}' — Save to write it to the file.");
        }

        private void DoDelete()
        {
            if (_faction == null || _current == null || (_faction.Research?.Count ?? 0) == 0) return;
            ResearchDefinition victim = _current;
            string id = victim.Id;
            var dlg = ChimeraDialog.Create("Delete research?",
                $"Remove '{id}' from this faction? Undoable until you close the editor; Save writes it to the file.");
            dlg.AddConfirm("Delete", danger: true);
            dlg.AddCancel("Cancel");
            dlg.Confirmed += () =>
            {
                int at = _faction.Research!.IndexOf(victim);
                if (at < 0) return;
                _faction.Research.RemoveAt(at);
                if (_index >= _faction.Research.Count) _index = Math.Max(0, _faction.Research.Count - 1);
                ResearchDefinition captured = victim;
                int atI = at;
                PushHistory(
                    redo: () => RemoveFromList(captured),
                    undo: () => { _faction.Research.Insert(Math.Min(atI, _faction.Research.Count), captured); GoToResearch(captured); });
                Refresh();
                ShowOk($"Deleted '{id}' — Save to write the change to the file.");
            };
            dlg.Open(this);
        }

        private void RemoveFromList(ResearchDefinition r)
        {
            if (_faction == null || _faction.Research == null) return;
            _faction.Research.Remove(r);
            if (_index >= _faction.Research.Count) _index = Math.Max(0, _faction.Research.Count - 1);
            Refresh();
        }

        private string UniqueId(string baseId)
        {
            string id = UnitDefinitionValidator.SanitizeId(baseId);
            if (id.Length == 0) id = "new_research";
            if (!IdExists(id)) return id;
            for (int i = 2; i < 100000; i++)
            {
                string candidate = $"{id}_{i}";
                if (!IdExists(candidate)) return candidate;
            }
            return id;
        }

        private bool IdExists(string id) => _faction?.Research != null && _faction.Research.Exists(r => r.Id == id);

        private static ResearchDefinition CloneResearch(ResearchDefinition s, string newId) => new()
        {
            Id = newId,
            DisplayName = s.DisplayName,
            CancelRefundFraction = s.CancelRefundFraction,
            Prerequisites = s.Prerequisites is null ? Array.Empty<string>() : (string[])s.Prerequisites.Clone(),
            Levels = s.Levels?.Select(CloneLevel).ToList() ?? new List<ResearchLevel>(),
        };

        private static ResearchLevel CloneLevel(ResearchLevel l) => new()
        {
            Cost = l.Cost == null ? null : new Dictionary<string, int>(l.Cost),
            TimeTicks = l.TimeTicks,
            ModifierDelta = l.ModifierDelta == null ? null : new ResearchModifierDelta
            {
                MaxHealthDelta = l.ModifierDelta.MaxHealthDelta,
                AttackDamageDelta = l.ModifierDelta.AttackDamageDelta,
                MoveSpeedDelta = l.ModifierDelta.MoveSpeedDelta,
                ArmorDelta = l.ModifierDelta.ArmorDelta,
            },
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
            ResearchDefinition? parsed;
            try { parsed = JsonSerializer.Deserialize<ResearchDefinition>(_jsonPane.Text, FactionDefinition.JsonOptions); }
            catch (Exception ex) { ShowError($"Raw JSON didn't parse: {ex.Message}"); return false; }
            if (parsed == null) { ShowError("Raw JSON is empty."); return false; }

            _faction.Research![_index] = parsed;   // fold the pane into the in-memory model
            _current = parsed;

            // Validate the folded-in whole faction (mirrors BuildingCardPanel.Edit.cs's SaveFromRawPane — the
            // per-instance check ResearchValidator offers is whole-faction, so this call also doubles as the
            // "against the OTHER research entries" check BuildingDefinitionValidator gets for free per-instance).
            IReadOnlyList<string> errors = ResearchValidator.Validate(_faction);
            string prefix = $"research '{_current.Id}'";
            var mine = errors.Where(e => e.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (mine.Count > 0)
            {
                ShowError($"Raw JSON invalid — {mine[0]}");
                return false;
            }

            if (!PersistSync()) return false;
            _paneDirty = false;
            Refresh();
            ShowOk("Saved (raw JSON) — applies on next playtest/match.");
            return true;
        }

        /// <summary>Reconcile the whole in-memory research list into the faction file, atomically, with a reload
        /// self-check — the identical read-current/write-.tmp/self-check-LoadFromFile/atomic-File.Move sequence
        /// <c>BuildingCardPanel.Edit.cs</c>'s <c>PersistSync</c> uses.</summary>
        private bool PersistSync()
        {
            if (_faction == null) return false;
            if (string.IsNullOrEmpty(_factionJsonPath)) { ShowError("No faction file is bound — cannot save."); return false; }

            string abs = ProjectSettings.GlobalizePath(_factionJsonPath);
            string tmp = abs + ".tmp";
            try
            {
                string current = File.ReadAllText(abs);
                string patched = FactionWriter.SyncFactionResearch(current, _faction.Research ?? new List<ResearchDefinition>());
                File.WriteAllText(tmp, patched);
                _ = FactionDefinition.LoadFromFile(tmp);   // self-check: refuse to report "Saved" for a file that won't reload
                File.Move(tmp, abs, overwrite: true);
                GD.Print($"[ResearchCard] Saved {abs} ({_faction.Research?.Count ?? 0} research).");
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave no stray .tmp */ }
                ShowError($"Save failed: {ex.Message}");
                return false;
            }
        }

        // ── Small shared builders (mirror BuildingCardPanel's) ───────────────────

        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", Tok(ThemeTokens.TextHi));
            return l;
        }

        private Label Body(string text, StringName colorToken)
        {
            var l = new Label { Text = text };
            l.AddThemeColorOverride("font_color", Tok(colorToken));
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return l;
        }

        private Color Tok(StringName token) => _theme.GetColor(token, ThemeTokens.Type);

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
