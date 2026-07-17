#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions; // CustomUiTree / WidgetBase / AnchorPoint
using ProjectChimera.Core.Sim;          // DslVarReadback
using ProjectChimera.Dsl;               // DslValueType / DslBounds

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 7.8 — the PRESENTATION renderer for the custom-UI read rail. Pulls the version-stamped
    /// <see cref="DslVarReadback"/> each frame and builds/updates a Godot <c>Control</c> tree on an overlay
    /// <c>CanvasLayer</c>, re-formatting a widget ONLY when its bound variable's version changes (all int→string /
    /// Fixed→mm:ss formatting is presentation-side, via <see cref="WidgetFormat"/> — no string ever enters the tick).
    ///
    /// Widgets render inside the fixed 16:9 safe area (letterboxed within the viewport). Caps are ASSERTED at build
    /// time (<see cref="DslBounds.MaxWidgetCount"/>/<see cref="DslBounds.MaxWidgetDepth"/>/
    /// <see cref="DslBounds.MaxListRows"/>) — an over-cap tree is REFUSED (not truncated); the load gate makes this
    /// unreachable on sanctioned content (belt-and-suspenders).
    ///
    /// The tree is late-bound via a getter so it survives the F5 Edit→Play re-apply (the scenario is re-applied,
    /// and the readback re-initialized from the same declarations by <c>ScenarioDirector.LoadScenario</c>).
    /// </summary>
    public partial class CustomUiBridge : CanvasLayer
    {
        private DslVarReadback? _readback;
        private Func<CustomUiTree?>? _treeGetter;
        private Func<int>? _localFactionGetter;
        private Godot.Theme? _theme; // `Theme` (bare) resolves to the ProjectChimera.UI.Theme namespace here

        private Control _root = null!;         // fills the viewport; children are laid into the 16:9 safe area
        private CustomUiTree? _builtTree;      // the tree reference currently rendered
        private Vector2 _builtViewport;        // the viewport size the current layout was built for
        private readonly List<Bound> _bound = new();

        /// <summary>A rendered widget + its live-bind bookkeeping (version cache so we re-format only on change).</summary>
        private sealed class Bound
        {
            public WidgetBase Model = null!;
            public Control Node = null!;       // the widget's own control (visibility target)
            public Label? Text;                // Label/Counter/Timer/FloatingText target
            public ProgressBar? Bar;           // ProgressBar target
            public VBoxContainer? Rows;         // repeater rows container (Leaderboard/ItemList)
            public uint LastVersion;
            public bool HasVersion;
        }

        /// <summary>
        /// Wire the read rail. <paramref name="treeGetter"/> returns the live <see cref="CustomUiTree"/> (or null);
        /// <paramref name="localFactionGetter"/> resolves the LIVE local player's 0-based DSL per-player slot (the
        /// value selecting a PerPlayer bind's slot) — resolved each frame (never a baked constant), so it follows the
        /// actual local player. The caller is responsible for the engine-faction→slot conversion (see
        /// <see cref="DslVarReadback.PlayerSlotForFaction"/>); this getter's int is used directly as the slot.
        /// <paramref name="theme"/> styles numeric labels (mono tabular figures) when available.
        /// </summary>
        public void Initialize(DslVarReadback readback, Func<CustomUiTree?> treeGetter, Func<int> localFactionGetter, Godot.Theme? theme)
        {
            _readback = readback;
            _treeGetter = treeGetter;
            _localFactionGetter = localFactionGetter;
            _theme = theme;
        }

        public override void _Ready()
        {
            Layer = 3; // above the 3D + HUD readouts, below the top authoring panels (TriggerEditor=12, Onboarding=14)
            _root = new Control { Name = "CustomUiRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);
        }

        /// <summary>Pull the read rail and refresh the widget tree. Pumped once per frame from <c>MainScene._Process</c>.</summary>
        public void Update()
        {
            if (_readback == null || _treeGetter == null || _root == null) return;

            CustomUiTree? tree = _treeGetter();
            Vector2 viewport = _root.Size;

            // Rebuild on a tree change OR a viewport resize (layout is in scaled canvas units).
            if (!ReferenceEquals(tree, _builtTree) || viewport != _builtViewport)
                Rebuild(tree, viewport);

            // Per-frame value + visibility refresh (re-format only on version change).
            for (int i = 0; i < _bound.Count; i++)
                RefreshBound(_bound[i]);
        }

        // ── Build ──────────────────────────────────────────────────────────────

        private void Rebuild(CustomUiTree? tree, Vector2 viewport)
        {
            _builtTree = tree;
            _builtViewport = viewport;
            foreach (Node c in _root.GetChildren()) c.QueueFree();
            _bound.Clear();

            WidgetBase[] widgets = tree?.Widgets ?? Array.Empty<WidgetBase>();
            if (tree == null || widgets.Length == 0) return;

            // Belt-and-suspenders cap assert — REFUSE to render an over-cap tree rather than truncating (the load
            // gate makes this unreachable on sanctioned content).
            if (!CapsOk(widgets))
            {
                GD.PushWarning("[CustomUiBridge] custom UI exceeds a widget/depth cap — refusing to render (should be unreachable past the load gate).");
                return;
            }

            // Compute the 16:9 safe area (letterboxed) and the canvas→pixel scale.
            float canvasW = tree.CanvasWidth > 0 ? tree.CanvasWidth : 1920;
            float canvasH = tree.CanvasHeight > 0 ? tree.CanvasHeight : 1080;
            Rect2 safe = SafeArea16By9(viewport, canvasW / canvasH);
            float scale = safe.Size.X / canvasW;

            foreach (WidgetBase w in widgets)
                BuildWidget(w, _root, safe.Position, safe.Size, scale);
        }

        private void BuildWidget(WidgetBase model, Control parent, Vector2 parentOrigin, Vector2 parentSize, float scale)
        {
            Vector2 size = new(model.W * scale, model.H * scale);
            Vector2 pos = AnchoredPosition(model.Anchor, parentOrigin, parentSize, size, model.X * scale, model.Y * scale);

            var node = new Control { Name = $"Widget{model.Id}", MouseFilter = Control.MouseFilterEnum.Ignore };
            node.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            node.Position = pos;
            node.Size = size;
            parent.AddChild(node);

            var bound = new Bound { Model = model, Node = node };
            AttachContent(model, node, bound);
            _bound.Add(bound);

            // Children are anchored WITHIN this widget's rect. Their Control.Position is relative to `node`'s local
            // space (origin 0,0) — pass Vector2.Zero, NOT node.Position, or every child is double-offset by the
            // parent's own position (visible whenever the parent is not at the safe-area origin).
            WidgetBase[] children = model.Children ?? Array.Empty<WidgetBase>();
            foreach (WidgetBase child in children)
                BuildWidget(child, node, Vector2.Zero, node.Size, scale);
        }

        /// <summary>Create the inner Godot control(s) for a widget kind and record the update targets on
        /// <paramref name="bound"/>.</summary>
        private void AttachContent(WidgetBase model, Control node, Bound bound)
        {
            switch (model)
            {
                case PanelWidget:
                {
                    var panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
                    panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    if (_theme != null) panel.Theme = _theme;
                    node.AddChild(panel);
                    break;
                }
                case ProgressBarWidget:
                {
                    var bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false };
                    bar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    if (_theme != null) bar.Theme = _theme;
                    node.AddChild(bar);
                    bound.Bar = bar;
                    break;
                }
                case LeaderboardWidget:
                case ItemListWidget:
                {
                    var rows = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                    rows.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    if (_theme != null) rows.Theme = _theme;
                    node.AddChild(rows);
                    bound.Rows = rows;
                    break;
                }
                default: // Label / Counter / Timer / FloatingText
                {
                    var label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
                    label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    ApplyNumberFont(label, model);
                    node.AddChild(label);
                    bound.Text = label;
                    // Seed static caption immediately (a Label/FloatingText caption is not version-gated).
                    string? caption = StaticCaption(model);
                    if (caption != null) label.Text = caption;
                    break;
                }
            }
        }

        // ── Per-frame refresh ─────────────────────────────────────────────────

        private void RefreshBound(Bound b)
        {
            // Resolve the local per-player slot LIVE each frame (mirrors the late-bound tree getter).
            int localFaction = _localFactionGetter?.Invoke() ?? 0;

            // Visibility bind (truthy) — applied every frame (cheap).
            if (b.Model.VisibleBind is string vb && _readback!.TryGetScalar(vb, localFaction, out _, out int vRaw, out _, out _))
                b.Node.Visible = vRaw != 0;

            string? valueBind = b.Model.ValueBind;
            if (valueBind == null) return;

            if (b.Model.ExpectsArrayBind)
            {
                if (!_readback!.TryGetArray(valueBind, out DslValueType elem, out int count, out uint aver)) return;
                if (b.HasVersion && aver == b.LastVersion) return; // unchanged → skip re-format
                b.LastVersion = aver; b.HasVersion = true;
                RebuildRows(b, valueBind, elem, count);
                return;
            }

            if (!_readback!.TryGetScalar(valueBind, localFaction, out DslValueType type, out int raw0, out _, out uint ver)) return;
            if (b.HasVersion && ver == b.LastVersion) return; // unchanged → skip re-format
            b.LastVersion = ver; b.HasVersion = true;
            ApplyScalar(b, type, raw0);
        }

        private void ApplyScalar(Bound b, DslValueType type, int raw0)
        {
            switch (b.Model)
            {
                case ProgressBarWidget pb when b.Bar != null:
                    b.Bar.Value = WidgetFormat.Fraction(type, raw0, pb.Max);
                    break;
                case TimerWidget when b.Text != null:
                    b.Text.Text = WidgetFormat.MmSs(type, raw0);
                    break;
                default:
                    if (b.Text != null)
                    {
                        string prefix = StaticCaption(b.Model) is string cap ? cap + " " : "";
                        b.Text.Text = prefix + WidgetFormat.Number(type, raw0);
                    }
                    break;
            }
        }

        private void RebuildRows(Bound b, string bindName, DslValueType elem, int count)
        {
            if (b.Rows == null) return;
            foreach (Node c in b.Rows.GetChildren()) c.QueueFree();
            int rowCap = b.Model.MaxRows > 0 ? b.Model.MaxRows : DslBounds.MaxListRows;
            int shown = Math.Min(count, Math.Min(rowCap, DslBounds.MaxListRows)); // runtime cap assert — never exceed MaxListRows
            for (int i = 0; i < shown; i++)
            {
                var row = new Label { Text = (i + 1) + ". " + WidgetFormat.Number(elem, _readback!.ArrayGet(bindName, i)) };
                ApplyNumberFont(row, b.Model);
                b.Rows.AddChild(row);
            }
        }

        // ── Layout helpers ────────────────────────────────────────────────────

        private static bool CapsOk(WidgetBase[] widgets)
        {
            int count = 0;
            foreach (WidgetBase w in widgets)
                if (!CountAndDepthOk(w, 1, ref count)) return false;
            return count <= DslBounds.MaxWidgetCount;
        }

        private static bool CountAndDepthOk(WidgetBase w, int depth, ref int count)
        {
            if (++count > DslBounds.MaxWidgetCount) return false;
            if (depth > DslBounds.MaxWidgetDepth) return false;
            WidgetBase[] children = w.Children ?? Array.Empty<WidgetBase>();
            foreach (WidgetBase c in children)
                if (!CountAndDepthOk(c, depth + 1, ref count)) return false;
            return true;
        }

        /// <summary>The 16:9 safe-area rect centered (letterboxed) in <paramref name="viewport"/>.</summary>
        private static Rect2 SafeArea16By9(Vector2 viewport, float aspect)
        {
            if (viewport.X <= 0 || viewport.Y <= 0) return new Rect2(Vector2.Zero, viewport);
            float w, h;
            if (viewport.X / viewport.Y > aspect) { h = viewport.Y; w = h * aspect; }
            else { w = viewport.X; h = w / aspect; }
            return new Rect2(new Vector2((viewport.X - w) * 0.5f, (viewport.Y - h) * 0.5f), new Vector2(w, h));
        }

        /// <summary>Top-left pixel of a widget whose anchor corner/edge aligns to the 9-point anchor within its
        /// parent, plus the scaled (x,y) offset.</summary>
        private static Vector2 AnchoredPosition(AnchorPoint anchor, Vector2 parentOrigin, Vector2 parentSize, Vector2 size, float offX, float offY)
        {
            (float hx, float vy) = anchor switch
            {
                AnchorPoint.TopLeft      => (0f, 0f),
                AnchorPoint.TopCenter    => (0.5f, 0f),
                AnchorPoint.TopRight     => (1f, 0f),
                AnchorPoint.CenterLeft   => (0f, 0.5f),
                AnchorPoint.Center       => (0.5f, 0.5f),
                AnchorPoint.CenterRight  => (1f, 0.5f),
                AnchorPoint.BottomLeft   => (0f, 1f),
                AnchorPoint.BottomCenter => (0.5f, 1f),
                AnchorPoint.BottomRight  => (1f, 1f),
                _                        => (0f, 0f),
            };
            float x = parentOrigin.X + hx * parentSize.X - hx * size.X + offX;
            float y = parentOrigin.Y + vy * parentSize.Y - vy * size.Y + offY;
            return new Vector2(x, y);
        }

        private void ApplyNumberFont(Label label, WidgetBase model)
        {
            if (_theme == null) return;
            // Tabular figures for live numeric readouts (jitter-free) — Counter/ProgressBar/Timer/repeater rows.
            if (model is CounterWidget or TimerWidget or LeaderboardWidget or ItemListWidget or ProgressBarWidget)
            {
                // `Theme` (bare) is Godot.Theme here (using Godot;), so fully-qualify the token holder namespace.
                Font tnum = _theme.GetFont(ProjectChimera.UI.Theme.ThemeTokens.MonoTnum, ProjectChimera.UI.Theme.ThemeTokens.Type);
                if (tnum != null) label.AddThemeFontOverride("font", tnum);
            }
        }

        private static string? StaticCaption(WidgetBase model) => model switch
        {
            LabelWidget l        => l.Text,
            FloatingTextWidget f => f.Text,
            _                    => null,
        };
    }
}
