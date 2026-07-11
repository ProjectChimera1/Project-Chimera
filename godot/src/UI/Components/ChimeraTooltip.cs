#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// tooltip (UX-DR26/53/45, NFR-2) — attachable to ANY control, revealing on short-hover AND keyboard
    /// focus. A per-attachment manager <see cref="Node"/> (parented to the target, so it and its signal
    /// connections are freed with it). <see cref="Attach"/> wires <c>MouseEntered</c> (+ a short hover
    /// <see cref="Timer"/>) / <c>MouseExited</c> AND <c>FocusEntered</c> / <c>FocusExited</c> to one
    /// show/hide path.
    ///
    /// WHY NOT the built-in (D-4): Godot's <c>Control._MakeCustomTooltip</c> is mouse-hover-only and needs
    /// subclassing the target — it cannot serve "any control" and cannot satisfy UX-DR45's keyboard-focus
    /// reveal. The shown tooltip is a plain <see cref="PanelContainer"/> on the high tooltip
    /// <see cref="CanvasLayer"/> (NOT a <see cref="Popup"/>, which would grab focus and break the focus
    /// trigger), positioned above the target, so it appears WITHOUT stealing focus.
    ///
    /// Presentation layer.
    /// </summary>
    public partial class ChimeraTooltip : Node
    {
        /// <summary>Tooltip roles from the mock: a centered pop (<c>.tip__pop</c>) and a left field hint (<c>.f-tip</c>).</summary>
        public enum TooltipRole { Pop, Field }

        private const string OverlayName = "ChimeraTooltipLayer";

        private Control _target = null!;
        private string _term = "";
        private string _body = "";
        private TooltipRole _role;

        private Timer _hoverTimer = null!;
        private PanelContainer? _current;
        private Tween? _tween; // the in-flight fade-in for _current, killed before a fade-out (3.1c review)

        /// <summary>
        /// Attach a hover-and-focus tooltip to <paramref name="ctrl"/>. <paramref name="term"/> renders bold
        /// in accent_bright + the display font; <paramref name="body"/> is a plain "teach never scold"
        /// sentence (UX-DR65). Returns the manager node (usually ignored).
        /// </summary>
        public static ChimeraTooltip Attach(Control ctrl, string term, string body, TooltipRole role = TooltipRole.Pop)
        {
            var t = new ChimeraTooltip { _target = ctrl, _term = term, _body = body, _role = role };
            ctrl.AddChild(t);
            return t;
        }

        /// <summary>Attach a hover-AND-keyboard-focus tooltip AND correctly configure <paramref name="target"/>
        /// for it: <c>Stop</c> mouse filter, <c>All</c> focus mode, and descendant <see cref="Control"/>s made
        /// mouse-transparent so the composite itself is the unambiguous hover/focus target (the 3.3 lesson).
        /// Centralizes what several Creation Suite panels previously hand-rolled as identical private per-file
        /// wrappers (Story 5.9 review pass) — new call sites should use this instead of re-deriving it.</summary>
        public static void AttachFocusable(Control target, string term, string body, TooltipRole role = TooltipRole.Pop)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            MakeChildrenMouseIgnore(target);
            Attach(target, term, body, role);
        }

        private static void MakeChildrenMouseIgnore(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
                MakeChildrenMouseIgnore(child);
            }
        }

        /// <inheritdoc/>
        public override void _Ready()
        {
            _hoverTimer = new Timer
            {
                OneShot = true,
                WaitTime = ComponentMetrics.TooltipHoverDelayMs / 1000.0,
            };
            AddChild(_hoverTimer);
            _hoverTimer.Timeout += Show;

            // Hover: short delay before reveal; leave cancels + hides.
            _target.MouseEntered += () => _hoverTimer.Start();
            _target.MouseExited += () => { _hoverTimer.Stop(); Hide(); };
            // Keyboard focus: reveal immediately (the UX-DR45 requirement the built-in tooltip misses).
            _target.FocusEntered += Show;
            _target.FocusExited += Hide;
        }

        /// <inheritdoc/>
        public override void _ExitTree()
        {
            // The shown tooltip lives on the shared overlay (not under the target), so free it explicitly
            // when this manager leaves the tree (e.g. the target was freed).
            KillShowTween();
            if (_current != null && GodotObject.IsInstanceValid(_current)) _current.QueueFree();
            _current = null;
        }

        // Build + reveal the tooltip above the target (idempotent while already shown).
        private void Show()
        {
            if (!GodotObject.IsInstanceValid(_target)) return;
            if (_current != null && GodotObject.IsInstanceValid(_current)) return;
            if (!ChimeraComponents.IsInitialized) return;

            KillShowTween(); // drop any stale fade-in before revealing a fresh tip

            var layer = ChimeraComponents.GetOverlayLayer(_target, OverlayName, ChimeraComponents.OverlayLayerTooltip);
            var tip = BuildTooltip();
            layer.AddChild(tip);
            _current = tip;

            // Size to content, then position above the target (clamped on-screen), on the overlay's coords.
            tip.ResetSize();
            Vector2 size = tip.Size;
            Rect2 r = _target.GetGlobalRect();
            Vector2 vp = _target.GetViewportRect().Size;

            float x = _role == TooltipRole.Field
                ? r.Position.X                                   // .f-tip: left-aligned to the field
                : r.Position.X + (r.Size.X - size.X) / 2f;       // .tip__pop: centered over the target
            float yTarget = r.Position.Y - size.Y - ComponentMetrics.TooltipGap; // above the target
            x = Mathf.Clamp(x, 4f, Mathf.Max(4f, vp.X - size.X - 4f));
            yTarget = Mathf.Max(4f, yTarget);

            // Fade + rise-in over the motion budget (pop reads speed; field is the faster .f-tip 120ms).
            double dur = _role == TooltipRole.Field
                ? ChimeraMotion.Seconds(ComponentMetrics.TooltipFieldFadeMs)
                : ChimeraMotion.SpeedSeconds();
            if (dur <= 0.0)
            {
                tip.Position = new Vector2(x, yTarget);
                tip.Modulate = Colors.White;
            }
            else
            {
                tip.Position = new Vector2(x, yTarget + ComponentMetrics.TooltipRiseY);
                tip.Modulate = new Color(1, 1, 1, 0);
                var tw = tip.CreateTween().SetParallel(true)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                tw.TweenProperty(tip, "modulate", Colors.White, dur);
                tw.TweenProperty(tip, "position", new Vector2(x, yTarget), dur);
                _tween = tw;
            }
        }

        private void Hide()
        {
            _hoverTimer.Stop();
            if (_current == null || !GodotObject.IsInstanceValid(_current)) { _current = null; return; }
            var tip = _current;
            _current = null; // clear first so a re-show can't reuse the fading node
            KillShowTween();  // stop the in-flight fade-in so it can't fight the fade-out (3.1c review)

            double dur = ChimeraMotion.SpeedSeconds();
            if (dur <= 0.0) { tip.QueueFree(); return; }
            var tw = tip.CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(tip, "modulate", new Color(1, 1, 1, 0), dur);
            tw.TweenCallback(Callable.From(tip.QueueFree));
        }

        // Kill + drop the in-flight fade-in tween (never the fade-out, which owns the tip's QueueFree).
        private void KillShowTween()
        {
            if (_tween != null && _tween.IsValid()) _tween.Kill();
            _tween = null;
        }

        // The themed tooltip surface: surface_3 + cut 4 + shadow_pop + line_strong inset hairline, a bold
        // accent term over a plain body sentence. A plain Control (MouseFilter Ignore) — never a Popup.
        private PanelContainer BuildTooltip()
        {
            int width = _role == TooltipRole.Field ? ComponentMetrics.TooltipWidthField : ComponentMetrics.TooltipMaxWidthPop;
            int padH = _role == TooltipRole.Field ? ComponentMetrics.TooltipFieldPadH : ComponentMetrics.TooltipPopPadH;
            int padV = _role == TooltipRole.Field ? ComponentMetrics.TooltipFieldPadV : ComponentMetrics.TooltipPopPadV;

            var box = ChimeraStyleBox.Chamfer(ComponentMetrics.CutTooltip,
                ChimeraComponents.Col(ThemeTokens.Surface3), ChimeraComponents.Col(ThemeTokens.LineStrong), 1);
            box.WithContentMargins(padH, padV).WithShadow(ThemeTokens.GetShadow(ThemeTokens.ShadowPop));

            var pc = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            pc.AddThemeStyleboxOverride("panel", box);

            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", 3);
            v.MouseFilter = Control.MouseFilterEnum.Ignore;

            // Bold term: accent_bright + display font (bound so a live accent switch retints it — AC6).
            var term = new Label { Text = _term };
            term.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontDisplay));
            term.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tsm));
            ChimeraComponents.BindAccentColor(term, ThemeTokens.AccentBright);
            v.AddChild(term);

            // Plain body sentence: pop = t-xs / text-hi; field = 11px / text-mid.
            var body = new Label
            {
                Text = _body,
                AutowrapMode = TextServer.AutowrapMode.Word,
                CustomMinimumSize = new Vector2(width - 2 * padH, 0),
            };
            body.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontUi));
            body.AddThemeFontSizeOverride("font_size",
                ChimeraComponents.SizeOf(_role == TooltipRole.Field ? ThemeTokens.T2xs : ThemeTokens.Txs));
            body.AddThemeColorOverride("font_color",
                ChimeraComponents.Col(_role == TooltipRole.Field ? ThemeTokens.TextMid : ThemeTokens.TextHi));
            v.AddChild(body);

            pc.AddChild(v);
            return pc;
        }
    }
}
