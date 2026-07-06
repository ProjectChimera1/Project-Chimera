#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// toast (UX-DR28/64) — transient notifications. A <see cref="ChimeraToastHost"/> (a high
    /// <see cref="CanvasLayer"/>, D-6) owns a top-left stack; <see cref="Show"/> builds a faceted toast
    /// (surface_2, cut_sm, shadow_2 + line_strong inset, a 3px full-height left accent bar recolored per
    /// variant), slides it in from the left (~250ms), then auto-dismisses after a delay.
    ///
    /// The stack is managed manually (not a VBoxContainer) precisely so each toast can be tweened on its own
    /// <c>position</c> for the slide-in — a container would own the layout and fight the tween. Variant bars
    /// use FIXED semantic tokens (danger/warn/ok); the DEFAULT bar is accent (bound so it retints on a
    /// switch). <see cref="StallBanner"/> is the sibling MP-stall visual (a warn pill + a warn spinner);
    /// wiring it to a real lagging peer is Epic 11 — this is the visual only.
    ///
    /// Presentation layer.
    /// </summary>
    public partial class ChimeraToastHost : CanvasLayer
    {
        /// <summary>Toast variants: default (accent bar) + the three semantic bars.</summary>
        public enum ToastVariant { Default, Danger, Warn, Ok }

        private const int LeftMargin = ComponentMetrics.ToastHostMargin;
        private const int TopMargin = ComponentMetrics.ToastHostMargin;

        private readonly List<Control> _toasts = new();
        // Active per-toast reflow (position:y) tweens, killed + rebuilt each Reflow so a rapid burst of
        // dismissals can't stack competing y-tweens on one toast (3.1c review). Slide-in/out use position:x.
        private readonly Dictionary<Control, Tween> _reflowTweens = new();

        /// <summary>Create a toast host. Add it to the tree once; it lives for the UI session.</summary>
        public static ChimeraToastHost Create()
        {
            return new ChimeraToastHost { Layer = ChimeraComponents.OverlayLayerToast };
        }

        /// <summary>
        /// Show a toast: a display-font title + a smaller message, a variant-colored left bar, sliding in from
        /// the left and auto-dismissing after <paramref name="seconds"/>.
        /// </summary>
        public void Show(string title, string msg, ToastVariant variant = ToastVariant.Default, float seconds = 4f)
        {
            var toast = BuildToast(title, msg, variant);
            AddChild(toast);
            toast.ResetSize();

            float restX = LeftMargin;
            float y = NextY();
            _toasts.Add(toast);

            double dur = ChimeraMotion.Seconds(ComponentMetrics.ToastSlideMs);
            if (dur <= 0.0)
            {
                toast.Position = new Vector2(restX, y);
                toast.Modulate = Colors.White;
            }
            else
            {
                // Start off-screen to the left, fully transparent, then slide + fade to rest.
                toast.Position = new Vector2(restX - toast.Size.X - 8f, y);
                toast.Modulate = new Color(1, 1, 1, 0);
                var tw = toast.CreateTween().SetParallel(true)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                tw.TweenProperty(toast, "position:x", restX, dur);
                tw.TweenProperty(toast, "modulate", Colors.White, dur);
            }

            // Auto-dismiss after the lifetime (the interval is a timeout, not motion — always applied).
            var life = CreateTween();
            life.TweenInterval(seconds);
            life.TweenCallback(Callable.From(() => Dismiss(toast)));
        }

        // Y for the next toast = below the current stack.
        private float NextY()
        {
            float y = TopMargin;
            foreach (var t in _toasts)
                if (GodotObject.IsInstanceValid(t)) y += t.Size.Y + ChimeraComponents.Const(ThemeTokens.S2);
            return y;
        }

        private void Dismiss(Control toast)
        {
            if (!GodotObject.IsInstanceValid(toast) || !_toasts.Contains(toast)) return;
            _toasts.Remove(toast);

            double dur = ChimeraMotion.Seconds(ComponentMetrics.ToastSlideMs);
            if (dur <= 0.0) { toast.QueueFree(); Reflow(); return; }
            var tw = toast.CreateTween().SetParallel(true)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(toast, "position:x", LeftMargin - toast.Size.X - 8f, dur);
            tw.TweenProperty(toast, "modulate", new Color(1, 1, 1, 0), dur);
            tw.Chain().TweenCallback(Callable.From(() => { toast.QueueFree(); Reflow(); }));
        }

        // Re-stack remaining toasts upward after one leaves (slide Y up, or snap under reduced-motion).
        private void Reflow()
        {
            // Kill any in-flight reflow tweens first so a rapid burst of dismissals can't stack competing
            // position:y tweens on the same toast (3.1c review). Slide-in/out animate position:x, so they
            // never conflict with these. The dict is rebuilt below from the current live stack.
            foreach (var kv in _reflowTweens)
                if (kv.Value != null && kv.Value.IsValid()) kv.Value.Kill();
            _reflowTweens.Clear();

            float y = TopMargin;
            int gap = ChimeraComponents.Const(ThemeTokens.S2);
            double dur = ChimeraMotion.Seconds(ComponentMetrics.ToastSlideMs);
            foreach (var t in _toasts)
            {
                if (!GodotObject.IsInstanceValid(t)) continue;
                if (dur <= 0.0)
                {
                    t.Position = new Vector2(t.Position.X, y);
                }
                else
                {
                    var tw = t.CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    tw.TweenProperty(t, "position:y", y, dur);
                    _reflowTweens[t] = tw;
                }
                y += t.Size.Y + gap;
            }
        }

        private PanelContainer BuildToast(string title, string msg, ToastVariant variant)
        {
            // Faceted surface with ZERO content margin so the accent bar is flush + full-height; the content
            // column carries its own padding.
            var box = ChimeraStyleBox.Chamfer(ChimeraComponents.Const(ThemeTokens.CutSm),
                ChimeraComponents.Col(ThemeTokens.Surface2), ChimeraComponents.Col(ThemeTokens.LineStrong), 1);
            box.WithShadow(ThemeTokens.GetShadow(ThemeTokens.Shadow2));
            var pc = new PanelContainer { CustomMinimumSize = new Vector2(ComponentMetrics.ToastWidth, 0) };
            pc.AddThemeStyleboxOverride("panel", box);

            var outer = new HBoxContainer();
            outer.AddThemeConstantOverride("separation", 0);
            pc.AddChild(outer);

            // 3px full-height left accent bar (::after). Default = accent (bound, retints); else semantic.
            var bar = new ColorRect
            {
                CustomMinimumSize = new Vector2(ComponentMetrics.ToastAccentBar, 0),
                SizeFlagsVertical = Control.SizeFlags.Fill,
            };
            if (variant == ToastVariant.Default)
            {
                bar.Color = Colors.White;
                ChimeraComponents.BindAccentModulate(bar, ThemeTokens.Accent);
            }
            else
            {
                bar.Color = ChimeraComponents.Col(BarToken(variant));
            }
            outer.AddChild(bar);

            // Padded content: icon glyph + (title / msg).
            var pad = new MarginContainer();
            pad.AddThemeConstantOverride("margin_left", ComponentMetrics.ToastPadH);
            pad.AddThemeConstantOverride("margin_right", ComponentMetrics.ToastPadH);
            pad.AddThemeConstantOverride("margin_top", ComponentMetrics.ToastPadV);
            pad.AddThemeConstantOverride("margin_bottom", ComponentMetrics.ToastPadV);
            pad.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            outer.AddChild(pad);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            pad.AddChild(row);

            var icon = new Label
            {
                Text = GlyphFor(variant),
                CustomMinimumSize = new Vector2(ComponentMetrics.ToastIcon, ComponentMetrics.ToastIcon),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            icon.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tlg));
            if (variant == ToastVariant.Default) ChimeraComponents.BindAccentColor(icon, ThemeTokens.Accent);
            else icon.AddThemeColorOverride("font_color", ChimeraComponents.Col(BarToken(variant)));
            row.AddChild(icon);

            var textCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            textCol.AddThemeConstantOverride("separation", 2);
            var titleLbl = new Label { Text = title };
            titleLbl.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontDisplay));
            titleLbl.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tsm));
            titleLbl.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextHi));
            textCol.AddChild(titleLbl);
            var msgLbl = new Label { Text = msg, AutowrapMode = TextServer.AutowrapMode.Word };
            msgLbl.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontUi));
            msgLbl.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Txs));
            msgLbl.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextLo));
            textCol.AddChild(msgLbl);
            row.AddChild(textCol);

            return pc;
        }

        private static StringName BarToken(ToastVariant v) => v switch
        {
            ToastVariant.Danger => ThemeTokens.Danger,
            ToastVariant.Warn => ThemeTokens.Warn,
            ToastVariant.Ok => ThemeTokens.Ok,
            _ => ThemeTokens.Accent,
        };

        // Asset-free glyphs (the emoji/unicode convention the existing panels use for icons).
        private static string GlyphFor(ToastVariant v) => v switch
        {
            ToastVariant.Danger => "⚠",
            ToastVariant.Warn => "⚠",
            ToastVariant.Ok => "✓",
            _ => "◆",
        };

        /// <summary>
        /// The <c>banner-stall</c> MP-stall visual (UX-DR28/64): a centered warn pill (warn-tinted bg + warn
        /// inset border + display font) holding a warn-tinted <c>sm</c> spinner and "Waiting for peer…".
        /// Visual only — wiring to a real lagging peer is Epic 11. Static factory (place it centered anywhere).
        /// </summary>
        public static PanelContainer StallBanner()
        {
            var warn = ChimeraComponents.Col(ThemeTokens.Warn);
            var box = ChimeraStyleBox.Chamfer(ComponentMetrics.CutBannerStall,
                new Color(warn.R, warn.G, warn.B, ComponentMetrics.BannerStallBgAlpha), warn, 1);
            box.WithContentMargins(ComponentMetrics.BannerStallPadH, ComponentMetrics.BannerStallPadV);
            var pc = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
            pc.AddThemeStyleboxOverride("panel", box);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            row.AddChild(ChimeraSpinner.Create(ComponentMetrics.SpinnerSm, overrideColor: warn));
            var lbl = new Label { Text = "Waiting for peer…" };
            lbl.AddThemeFontOverride("font", ChimeraComponents.DisplayTracked(1));
            lbl.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tsm));
            lbl.AddThemeColorOverride("font_color", warn);
            lbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(lbl);
            pc.AddChild(row);
            return pc;
        }
    }
}
