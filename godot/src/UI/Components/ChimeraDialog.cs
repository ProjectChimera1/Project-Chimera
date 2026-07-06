#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// dialog (UX-DR27/45) — a modal over a scrim (D-3). A custom <see cref="CanvasLayer"/> + full-rect
    /// <see cref="ColorRect"/> scrim + centered chamfered <see cref="PanelContainer"/>, matching the in-repo
    /// overlay precedent (MainMenuOverlay / SettingsPanel are all CanvasLayer). The scrim <b>eats input</b>
    /// (MouseFilter Stop) and the dialog <b>traps focus</b> + closes on Esc; a destructive action uses a
    /// danger primary and requires an explicit button press (scrim-click / Esc take the safe cancel).
    ///
    /// SPEC-SANCTIONED APPROXIMATIONS (documented, like 3.1b's gradient→solid fills): the CSS
    /// <c>backdrop-filter: blur(2px)</c> is not cheap for a ColorRect, so the scrim is a slightly-more-opaque
    /// solid dim (rgba(6,8,11,~0.82)); the panel's <c>surface-2→surface-1</c> gradient ports to a solid
    /// surface_2; and the masked <c>edge-light→line</c> two-layer border ports to a single edge_light
    /// hairline. Head / body / foot use the mock's paddings; foot buttons are right-aligned (gap s3).
    ///
    /// Presentation layer.
    /// </summary>
    public partial class ChimeraDialog : CanvasLayer
    {
        /// <summary>Emitted when a confirm action is chosen.</summary>
        [Signal] public delegate void ConfirmedEventHandler();
        /// <summary>Emitted when the dialog is dismissed (cancel button / scrim / Esc).</summary>
        [Signal] public delegate void DismissedEventHandler();

        // Blur→solid scrim approximation (D-3): the mock's rgba(6,8,11,0.72) + blur, nudged more opaque.
        private static readonly Color ScrimColor = new(6f / 255f, 8f / 255f, 11f / 255f, 0.82f);

        private ColorRect _scrim = null!;
        private PanelContainer _panel = null!;
        private HBoxContainer _foot = null!;
        private bool _destructive;
        private bool _closing;

        /// <summary>Build a dialog with a title + body sentence. Add actions with <see cref="AddConfirm"/> /
        /// <see cref="AddCancel"/>, then <see cref="Open"/> it under a parent node.</summary>
        public static ChimeraDialog Create(string title, string body)
        {
            var d = new ChimeraDialog { Layer = ChimeraComponents.OverlayLayerDialog };
            d.Build(title, body);
            return d;
        }

        private void Build(string title, string body)
        {
            // Scrim: full-rect solid dim that eats input; clicking it cancels a non-destructive dialog.
            _scrim = new ColorRect { Color = ScrimColor, MouseFilter = Control.MouseFilterEnum.Stop };
            _scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _scrim.GuiInput += OnScrimInput;
            AddChild(_scrim);

            // Centering layer over the scrim (transparent to mouse so clicks fall through to the scrim).
            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(center);

            // Panel: chamfered cut_lg, solid surface_2 (gradient→solid), edge_light hairline, shadow_pop.
            var box = ChimeraStyleBox.Chamfer(ChimeraComponents.Const(ThemeTokens.CutLg),
                ChimeraComponents.Col(ThemeTokens.Surface2), ChimeraComponents.Col(ThemeTokens.EdgeLight), 1);
            box.WithShadow(ThemeTokens.GetShadow(ThemeTokens.ShadowPop));
            _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
            _panel.AddThemeStyleboxOverride("panel", box);
            center.AddChild(_panel);

            var vb = new VBoxContainer();
            vb.AddThemeConstantOverride("separation", 0);
            _panel.AddChild(vb);

            int s5 = ChimeraComponents.Const(ThemeTokens.S5); // 24
            int s4 = ChimeraComponents.Const(ThemeTokens.S4); // 16
            int s3 = ChimeraComponents.Const(ThemeTokens.S3); // 12

            // ── Head: title + Esc kbd, pad 20/24, bottom line ──
            var head = new PanelContainer();
            head.AddThemeStyleboxOverride("panel", Edged(bottom: 1, padH: s5, padV: ComponentMetrics.DialogHeadPadV));
            var headRow = new HBoxContainer();
            var titleLbl = new Label { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            titleLbl.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontDisplay));
            titleLbl.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tlg));
            titleLbl.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextHi));
            headRow.AddChild(titleLbl);
            var esc = ChimeraComponents.Kbd("Esc");
            esc.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            headRow.AddChild(esc);
            head.AddChild(headRow);
            vb.AddChild(head);

            // ── Body: pad 24 ──
            var bodyWrap = new MarginContainer();
            foreach (var s in new[] { "left", "right", "top", "bottom" })
                bodyWrap.AddThemeConstantOverride($"margin_{s}", s5);
            var bodyLbl = new Label { Text = body, AutowrapMode = TextServer.AutowrapMode.Word };
            bodyLbl.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontUi));
            bodyLbl.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tmd));
            bodyLbl.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextMid));
            bodyWrap.AddChild(bodyLbl);
            vb.AddChild(bodyWrap);

            // ── Foot: right-aligned buttons, gap s3, pad 16/24, top line ──
            var footWrap = new PanelContainer();
            footWrap.AddThemeStyleboxOverride("panel", Edged(top: 1, padH: s5, padV: s4));
            _foot = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            _foot.AddThemeConstantOverride("separation", s3);
            footWrap.AddChild(_foot);
            vb.AddChild(footWrap);
        }

        // A borderless-fill stylebox carrying just a top OR bottom hairline + content margins (head/foot rules).
        private static StyleBoxFlat Edged(int top = 0, int bottom = 0, int padH = 0, int padV = 0)
        {
            var b = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0),
                BorderColor = ChimeraComponents.Col(ThemeTokens.Line),
                BorderWidthTop = top,
                BorderWidthBottom = bottom,
                ContentMarginLeft = padH,
                ContentMarginRight = padH,
                ContentMarginTop = padV,
                ContentMarginBottom = padV,
            };
            return b;
        }

        /// <summary>Add a confirm action (right-aligned). <paramref name="danger"/> makes it a danger primary
        /// and marks the dialog destructive (scrim-click no longer closes; only an explicit button does).</summary>
        public Button AddConfirm(string text, bool danger = false)
        {
            if (danger) _destructive = true;
            var btn = ChimeraComponents.Button(text,
                danger ? ChimeraComponents.ButtonVariant.Danger : ChimeraComponents.ButtonVariant.Primary);
            btn.Pressed += () => CloseWith(confirmed: true);
            _foot.AddChild(btn);
            return btn;
        }

        /// <summary>Add a cancel/ghost action (right-aligned) that dismisses the dialog.</summary>
        public Button AddCancel(string text)
        {
            var btn = ChimeraComponents.Button(text, ChimeraComponents.ButtonVariant.Ghost);
            btn.Pressed += () => CloseWith(confirmed: false);
            _foot.AddChild(btn);
            return btn;
        }

        /// <summary>Add the dialog to <paramref name="parent"/>, size it, fade the scrim in, and trap focus.</summary>
        public void Open(Node parent)
        {
            parent.AddChild(this);

            // Width = min(560, 90% of the viewport). Panel height is content-driven; CenterContainer centers it.
            float vpX = _panel.GetViewportRect().Size.X;
            float width = Mathf.Min(ComponentMetrics.DialogMaxWidth, vpX * ComponentMetrics.DialogWidthPct);
            _panel.CustomMinimumSize = new Vector2(width, 0);

            // Focus trap: focus the last foot button (usually the primary) + wrap neighbors so Tab cycles.
            WireFocusTrap();

            // Fade scrim + panel in over the speed token (gated on reduced-motion).
            double dur = ChimeraMotion.SpeedSeconds();
            if (dur <= 0.0) return;
            _scrim.Color = new Color(ScrimColor.R, ScrimColor.G, ScrimColor.B, 0);
            _panel.Modulate = new Color(1, 1, 1, 0);
            var tw = CreateTween().SetParallel(true);
            tw.TweenProperty(_scrim, "color", ScrimColor, dur);
            tw.TweenProperty(_panel, "modulate", Colors.White, dur);
        }

        private void WireFocusTrap()
        {
            int n = _foot.GetChildCount();
            if (n == 0) return;
            var buttons = new Control[n];
            for (int i = 0; i < n; i++) buttons[i] = (Control)_foot.GetChild(i);
            for (int i = 0; i < n; i++)
            {
                var next = buttons[(i + 1) % n].GetPath();
                var prev = buttons[(i - 1 + n) % n].GetPath();
                buttons[i].FocusNext = next;
                buttons[i].FocusNeighborRight = next;
                buttons[i].FocusPrevious = prev;
                buttons[i].FocusNeighborLeft = prev;
            }
            buttons[n - 1].GrabFocus(); // default focus on the primary/right-most action
        }

        /// <inheritdoc/>
        public override void _Input(InputEvent @event)
        {
            // Esc = the safe cancel (dismiss), even for a destructive dialog.
            if (!_closing && @event.IsActionPressed("ui_cancel"))
            {
                CloseWith(confirmed: false);
                GetViewport().SetInputAsHandled();
            }
        }

        private void OnScrimInput(InputEvent @event)
        {
            // Scrim-click cancels ONLY a non-destructive dialog (destructive requires an explicit button).
            if (_destructive) return;
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                CloseWith(confirmed: false);
        }

        private void CloseWith(bool confirmed)
        {
            if (_closing) return;
            _closing = true;
            EmitSignal(confirmed ? SignalName.Confirmed : SignalName.Dismissed);

            double dur = ChimeraMotion.SpeedSeconds();
            if (dur <= 0.0) { QueueFree(); return; }
            var tw = CreateTween().SetParallel(true);
            tw.TweenProperty(_scrim, "color", new Color(ScrimColor.R, ScrimColor.G, ScrimColor.B, 0), dur);
            tw.TweenProperty(_panel, "modulate", new Color(1, 1, 1, 0), dur);
            tw.Chain().TweenCallback(Callable.From(QueueFree));
        }
    }
}
