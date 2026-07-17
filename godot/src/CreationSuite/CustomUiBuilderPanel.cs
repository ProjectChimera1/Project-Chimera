#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions; // CustomUiTree / WidgetBase / AnchorPoint / ScenarioData
using ProjectChimera.Dsl;               // DslValueType

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 7.8 — the custom-UI authoring surface: a widget-palette + inspector builder with a live 16:9 preview
    /// that writes a valid persisted <see cref="CustomUiTree"/> into <see cref="ScenarioData.CustomUi"/>. Modeled on
    /// <c>TriggerEditorPanel</c> (a <see cref="Node"/> that owns a toggled <see cref="CanvasLayer"/> panel). A
    /// palette of the eight closed widget kinds, a 9-point anchor selector, integer offset/size, a <c>{variable}</c>
    /// bind field (a dropdown filtered by declared-variable type), a visibility bind, and a live preview inside the
    /// 16:9 safe area. The persisted tree is validated + cap-checked at load by <c>CustomUiGate</c> (this builder is
    /// convenience authoring; the load gate is authoritative).
    ///
    /// Read-only display only (Story 7.8): the palette has NO Button/interactive kind (that is the 7.9 write rail).
    /// </summary>
    public partial class CustomUiBuilderPanel : Node
    {
        private const float PANEL_W = 460f;
        private const float PANEL_H = 640f;
        private const float MARGIN = 12f;

        private static readonly WidgetKind[] PaletteKinds =
        {
            WidgetKind.Panel, WidgetKind.Label, WidgetKind.Counter, WidgetKind.ProgressBar,
            WidgetKind.Timer, WidgetKind.Leaderboard, WidgetKind.FloatingText, WidgetKind.ItemList,
        };

        private ScenarioData? _scenario;
        private CustomUiTree _tree = new();
        private int _nextId = 1;
        private WidgetBase? _selected;

        private CanvasLayer _canvas = null!;
        private Control _panel = null!;
        private VBoxContainer _list = null!;
        private Control _preview = null!;

        // Inspector controls
        private OptionButton _anchorOpt = null!;
        private SpinBox _x = null!, _y = null!, _w = null!, _h = null!, _rows = null!;
        private LineEdit _text = null!;
        private OptionButton _bindOpt = null!;
        private OptionButton _visibleOpt = null!;

        public override void _Ready() => BuildUi();

        /// <summary>Bind the live scenario; load its existing custom UI (or start a fresh tree).</summary>
        public void Initialize(ScenarioData scenario)
        {
            _scenario = scenario;
            _tree = scenario.CustomUi ?? new CustomUiTree();
            _nextId = 1;
            foreach (WidgetBase w in Flatten(_tree)) _nextId = Math.Max(_nextId, w.Id + 1);
            RefreshBindOptions();
            RefreshList();
            RefreshPreview();
        }

        /// <summary>Rebind after a scenario swap (New Map / load).</summary>
        public void SetScenario(ScenarioData scenario) => Initialize(scenario);

        /// <summary>Toggle the panel's visibility.</summary>
        public void Toggle() { if (_panel != null) _panel.Visible = !_panel.Visible; }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 13 };
            AddChild(_canvas);

            _panel = new PanelContainer { Visible = false };
            _panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
            _panel.GrowVertical = Control.GrowDirection.Both;
            _panel.OffsetLeft = MARGIN;
            _panel.OffsetRight = MARGIN + PANEL_W;
            _panel.OffsetTop = -PANEL_H * 0.5f;
            _panel.OffsetBottom = PANEL_H * 0.5f;
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 6);
            _panel.AddChild(root);

            root.AddChild(new Label { Text = "Custom UI Builder (Story 7.8)" });
            root.AddChild(new HSeparator());

            // ── Palette ──
            root.AddChild(new Label { Text = "Add widget:" });
            var palette = new HFlowContainer();
            foreach (WidgetKind kind in PaletteKinds)
            {
                var btn = new Button { Text = kind.ToString() };
                WidgetKind captured = kind;
                btn.Pressed += () => AddWidget(captured);
                palette.AddChild(btn);
            }
            root.AddChild(palette);
            root.AddChild(new HSeparator());

            // ── Widget list ──
            root.AddChild(new Label { Text = "Widgets:" });
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 140) };
            _list = new VBoxContainer();
            _list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(_list);
            root.AddChild(scroll);
            root.AddChild(new HSeparator());

            // ── Inspector ──
            root.AddChild(new Label { Text = "Inspector:" });
            _anchorOpt = new OptionButton();
            foreach (AnchorPoint a in Enum.GetValues<AnchorPoint>()) _anchorOpt.AddItem(a.ToString());
            _anchorOpt.ItemSelected += _ => ApplyInspector();
            root.AddChild(LabeledRow("Anchor", _anchorOpt));

            _x = MakeSpin(-4096, 4096); _y = MakeSpin(-4096, 4096);
            _w = MakeSpin(0, 4096); _h = MakeSpin(0, 4096);
            root.AddChild(LabeledRow("X", _x)); root.AddChild(LabeledRow("Y", _y));
            root.AddChild(LabeledRow("W", _w)); root.AddChild(LabeledRow("H", _h));

            _text = new LineEdit { PlaceholderText = "static text (Label/FloatingText)" };
            _text.TextChanged += _ => ApplyInspector();
            root.AddChild(LabeledRow("Text", _text));

            _bindOpt = new OptionButton();
            _bindOpt.ItemSelected += _ => ApplyInspector();
            root.AddChild(LabeledRow("Bind", _bindOpt));

            _visibleOpt = new OptionButton();
            _visibleOpt.ItemSelected += _ => ApplyInspector();
            root.AddChild(LabeledRow("Visible if", _visibleOpt));

            _rows = MakeSpin(1, DslBounds.MaxListRows);
            root.AddChild(LabeledRow("Rows (repeater)", _rows));

            var del = new Button { Text = "Delete selected" };
            del.Pressed += DeleteSelected;
            root.AddChild(del);
            root.AddChild(new HSeparator());

            // ── Live 16:9 preview ──
            root.AddChild(new Label { Text = "Preview (16:9):" });
            _preview = new Control { CustomMinimumSize = new Vector2(PANEL_W - 24, (PANEL_W - 24) * 9f / 16f) };
            _preview.ClipContents = true;
            var previewBg = new ColorRect { Color = new Color(0.08f, 0.09f, 0.12f) };
            previewBg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _preview.AddChild(previewBg);
            root.AddChild(_preview);
            root.AddChild(new HSeparator());

            // ── Save / Close ──
            var actions = new HBoxContainer();
            var save = new Button { Text = "Save to scenario" };
            save.Pressed += Save;
            var close = new Button { Text = "Close" };
            close.Pressed += Toggle;
            actions.AddChild(save); actions.AddChild(close);
            root.AddChild(actions);
        }

        private static SpinBox MakeSpin(double min, double max) =>
            new() { MinValue = min, MaxValue = max, Step = 1, AllowGreater = false, AllowLesser = false };

        private static HBoxContainer LabeledRow(string label, Control control)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(90, 0) });
            control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(control);
            return row;
        }

        // ── Model editing ─────────────────────────────────────────────────────

        private void AddWidget(WidgetKind kind)
        {
            WidgetBase w = NewWidget(kind, _nextId++);
            var list = new List<WidgetBase>(_tree.Widgets) { w };
            _tree.Widgets = list.ToArray();
            _selected = w;
            RefreshList();
            LoadInspector(w);
            RefreshPreview();
        }

        private static WidgetBase NewWidget(WidgetKind kind, int id) => kind switch
        {
            WidgetKind.Panel        => new PanelWidget { Id = id, W = 300, H = 120 },
            WidgetKind.Label        => new LabelWidget { Id = id, W = 200, H = 40, Text = "Label" },
            WidgetKind.Counter      => new CounterWidget { Id = id, W = 160, H = 40 },
            WidgetKind.ProgressBar  => new ProgressBarWidget { Id = id, W = 220, H = 24 },
            WidgetKind.Timer        => new TimerWidget { Id = id, W = 120, H = 40 },
            WidgetKind.Leaderboard  => new LeaderboardWidget { Id = id, W = 240, H = 200 },
            WidgetKind.FloatingText => new FloatingTextWidget { Id = id, W = 160, H = 40, Text = "!" },
            WidgetKind.ItemList     => new ItemListWidget { Id = id, W = 240, H = 200 },
            _                       => new PanelWidget { Id = id },
        };

        private void DeleteSelected()
        {
            if (_selected == null) return;
            var list = new List<WidgetBase>(_tree.Widgets);
            list.Remove(_selected);
            _tree.Widgets = list.ToArray();
            _selected = null;
            RefreshList();
            RefreshPreview();
        }

        private void LoadInspector(WidgetBase w)
        {
            _anchorOpt.Selected = (int)w.Anchor;
            _x.Value = w.X; _y.Value = w.Y; _w.Value = w.W; _h.Value = w.H;
            _text.Text = StaticText(w) ?? "";
            SelectOption(_bindOpt, w.ValueBind);
            SelectOption(_visibleOpt, w.VisibleBind);
            _rows.Value = w.MaxRows > 0 ? w.MaxRows : 8;
        }

        private void ApplyInspector()
        {
            if (_selected == null) return;
            _selected.Anchor = (AnchorPoint)_anchorOpt.Selected;
            _selected.X = (int)_x.Value; _selected.Y = (int)_y.Value;
            _selected.W = (int)_w.Value; _selected.H = (int)_h.Value;
            _selected.VisibleBind = OptionText(_visibleOpt);
            SetStaticText(_selected, _text.Text);
            SetBind(_selected, OptionText(_bindOpt));
            SetRows(_selected, (int)_rows.Value);
            RefreshPreview();
        }

        private static void SetBind(WidgetBase w, string? bind)
        {
            switch (w)
            {
                case LabelWidget l: l.Bind = bind; break;
                case CounterWidget c: c.Bind = bind; break;
                case ProgressBarWidget p: p.Bind = bind; break;
                case TimerWidget t: t.Bind = bind; break;
                case LeaderboardWidget lb: lb.Bind = bind; break;
                case FloatingTextWidget ft: ft.Bind = bind; break;
                case ItemListWidget il: il.Bind = bind; break;
            }
        }

        private static void SetRows(WidgetBase w, int rows)
        {
            if (w is LeaderboardWidget lb) lb.Rows = rows;
            else if (w is ItemListWidget il) il.Rows = rows;
        }

        private static void SetStaticText(WidgetBase w, string text)
        {
            string? t = string.IsNullOrEmpty(text) ? null : text;
            if (w is LabelWidget l) l.Text = t;
            else if (w is FloatingTextWidget ft) ft.Text = t;
        }

        private static string? StaticText(WidgetBase w) => w switch
        {
            LabelWidget l => l.Text,
            FloatingTextWidget f => f.Text,
            _ => null,
        };

        private void Save()
        {
            if (_scenario == null) return;
            _scenario.CustomUi = _tree.Widgets.Length == 0 ? null : _tree;
        }

        // ── List / bind option / preview refresh ──────────────────────────────

        private void RefreshList()
        {
            foreach (Node c in _list.GetChildren()) c.QueueFree();
            foreach (WidgetBase w in _tree.Widgets)
            {
                WidgetBase captured = w;
                var btn = new Button { Text = $"#{w.Id} {w.Kind}", ToggleMode = true, ButtonPressed = ReferenceEquals(w, _selected) };
                btn.Pressed += () => { _selected = captured; LoadInspector(captured); RefreshList(); };
                _list.AddChild(btn);
            }
        }

        private void RefreshBindOptions()
        {
            RefreshOption(_bindOpt);
            RefreshOption(_visibleOpt);
        }

        private void RefreshOption(OptionButton opt)
        {
            opt.Clear();
            opt.AddItem("(none)");
            if (_scenario?.Variables != null)
                foreach (ScenarioVariable v in _scenario.Variables)
                    opt.AddItem(v.Name);
        }

        private static void SelectOption(OptionButton opt, string? value)
        {
            if (string.IsNullOrEmpty(value)) { opt.Selected = 0; return; }
            for (int i = 0; i < opt.ItemCount; i++)
                if (opt.GetItemText(i) == value) { opt.Selected = i; return; }
            opt.Selected = 0;
        }

        private static string? OptionText(OptionButton opt)
        {
            if (opt.Selected <= 0) return null;
            return opt.GetItemText(opt.Selected);
        }

        private void RefreshPreview()
        {
            if (_preview == null) return;
            // Clear all but the background ColorRect (child 0).
            for (int i = _preview.GetChildCount() - 1; i >= 1; i--) _preview.GetChild(i).QueueFree();

            Vector2 size = _preview.CustomMinimumSize;
            float canvasW = _tree.CanvasWidth > 0 ? _tree.CanvasWidth : 1920;
            float scale = size.X / canvasW;
            foreach (WidgetBase w in _tree.Widgets)
            {
                var rect = new ColorRect
                {
                    Color = ReferenceEquals(w, _selected) ? new Color(0.3f, 0.6f, 0.9f, 0.6f) : new Color(0.4f, 0.4f, 0.5f, 0.5f),
                    Size = new Vector2(Math.Max(6, w.W * scale), Math.Max(6, w.H * scale)),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                rect.Position = PreviewPos(w, size, scale, rect.Size);
                _preview.AddChild(rect);
                var lbl = new Label { Text = w.Kind.ToString(), MouseFilter = Control.MouseFilterEnum.Ignore };
                lbl.Position = rect.Position;
                _preview.AddChild(lbl);
            }
        }

        private static Vector2 PreviewPos(WidgetBase w, Vector2 area, float scale, Vector2 size)
        {
            (float hx, float vy) = w.Anchor switch
            {
                AnchorPoint.TopLeft => (0f, 0f), AnchorPoint.TopCenter => (0.5f, 0f), AnchorPoint.TopRight => (1f, 0f),
                AnchorPoint.CenterLeft => (0f, 0.5f), AnchorPoint.Center => (0.5f, 0.5f), AnchorPoint.CenterRight => (1f, 0.5f),
                AnchorPoint.BottomLeft => (0f, 1f), AnchorPoint.BottomCenter => (0.5f, 1f), AnchorPoint.BottomRight => (1f, 1f),
                _ => (0f, 0f),
            };
            float x = hx * area.X - hx * size.X + w.X * scale;
            float y = vy * area.Y - vy * size.Y + w.Y * scale;
            return new Vector2(x, y);
        }

        private static IEnumerable<WidgetBase> Flatten(CustomUiTree tree)
        {
            var stack = new Stack<WidgetBase>(tree.Widgets);
            while (stack.Count > 0)
            {
                WidgetBase w = stack.Pop();
                yield return w;
                foreach (WidgetBase c in w.Children) stack.Push(c);
            }
        }
    }
}
