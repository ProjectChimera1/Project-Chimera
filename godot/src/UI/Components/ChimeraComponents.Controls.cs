#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// Story 3.1b interactive controls: btn (DR14), icon-btn (DR15), input (DR22) + its <c>.select</c>
    /// chevron variant, num-input (DR32), and the uppercase field-label helper. Part of the
    /// <see cref="ChimeraComponents"/> factory. Accent-filled states are shared + registered so a switch
    /// retints them; accent text (btn-primary ink) subscribes to <c>AccentChanged</c>.
    /// </summary>
    public static partial class ChimeraComponents
    {
        // Fully-transparent fill for "no background" states (a structural value, not a design color).
        private static readonly Color Clear = new(0, 0, 0, 0);

        // ── btn (UX-DR14) ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A themed button. Variants primary/secondary/ghost/danger × sizes sm/default/lg/block. Display
        /// font, uppercased + tracked; cut-sm (5) facet; states normal/hover/pressed/focus/disabled. The
        /// CSS <c>:active</c> 1px depress is a +1/−1 content-margin shift on the pressed box. Filled accent
        /// variants (primary) share + register their state boxes; primary ink text subscribes to the switch.
        /// </summary>
        public static Button Button(string text, ButtonVariant variant = ButtonVariant.Primary,
                                    ButtonSize size = ButtonSize.Default)
        {
            // Size → (font-size token, horizontal pad, vertical pad). Block reuses Default's metrics.
            // AC2: token-valued paddings are read from the theme (24=s5, 16=s4). The sub-grid values
            // (11/6/13/9) are CSS-exact per-component dims with no matching spacing token, kept literal.
            (StringName fontSize, int padH, int padV) = size switch
            {
                ButtonSize.Sm => (ThemeTokens.T2xs, 11, 6),
                ButtonSize.Lg => (ThemeTokens.Tmd, Const(ThemeTokens.S5), 13),
                _             => (ThemeTokens.Tsm, Const(ThemeTokens.S4), 9),
            };

            var btn = new Godot.Button { Text = Up(text) };
            btn.AddThemeFontOverride("font", DisplayTracked(1)); // display + ~0.04em tracking
            btn.AddThemeFontSizeOverride("font_size", SizeOf(fontSize));
            if (size == ButtonSize.Block)
                btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

            int cut = Const(ThemeTokens.CutSm);
            string sizeKey = size.ToString();

            // Local builders. Non-accent states build fresh; accent states share + register (D-4).
            StyleBoxFlat Fresh(Color fill, Color border, int bw, bool depress)
            {
                var b = ChimeraStyleBox.Chamfer(cut, fill, border, bw);
                b.ContentMarginLeft = padH;
                b.ContentMarginRight = padH;
                b.ContentMarginTop = depress ? padV + 1 : padV;
                b.ContentMarginBottom = depress ? padV - 1 : padV;
                return b;
            }
            StyleBoxFlat AccentState(string state, StringName token, bool depress) => SharedAccentBox(
                $"btn/primary/{state}/{sizeKey}",
                () => Fresh(Col(token), Col(token), 0, depress),
                Fill(token), Border(token));

            StyleBoxFlat normal, hover, pressed;
            switch (variant)
            {
                case ButtonVariant.Primary:
                    normal  = AccentState("normal",  ThemeTokens.Accent,       false);
                    hover   = AccentState("hover",   ThemeTokens.AccentBright, false);
                    pressed = AccentState("pressed", ThemeTokens.AccentDim,    true);
                    // Ink text on all interactive states retints with the accent (near-black but correct).
                    BindAccentColorMulti(btn, ThemeTokens.AccentInk,
                        new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" });
                    break;

                case ButtonVariant.Danger:
                    normal  = Fresh(Col(ThemeTokens.Danger), Col(ThemeTokens.Danger), 0, false);
                    hover   = Fresh(Col(ThemeTokens.Danger), Col(ThemeTokens.Danger), 0, false);
                    pressed = Fresh(Col(ThemeTokens.Danger), Col(ThemeTokens.Danger), 0, true);
                    foreach (var it in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                        btn.AddThemeColorOverride(it, Col(ThemeTokens.DangerInk));
                    break;

                case ButtonVariant.Secondary:
                    normal  = Fresh(Clear, Col(ThemeTokens.LineStrong), 1, false);
                    hover   = Fresh(Col(ThemeTokens.Surface4), Col(ThemeTokens.LineStrong), 1, false);
                    pressed = Fresh(Col(ThemeTokens.Surface4), Col(ThemeTokens.LineStrong), 1, true);
                    btn.AddThemeColorOverride("font_color", Col(ThemeTokens.TextMid));
                    btn.AddThemeColorOverride("font_hover_color", Col(ThemeTokens.TextHi));
                    btn.AddThemeColorOverride("font_pressed_color", Col(ThemeTokens.TextHi));
                    btn.AddThemeColorOverride("font_focus_color", Col(ThemeTokens.TextHi));
                    break;

                default: // Ghost
                    normal  = Fresh(Clear, Clear, 0, false);
                    hover   = Fresh(Col(ThemeTokens.Surface2), Clear, 0, false);
                    pressed = Fresh(Col(ThemeTokens.Surface2), Clear, 0, true);
                    btn.AddThemeColorOverride("font_color", Col(ThemeTokens.TextMid));
                    btn.AddThemeColorOverride("font_hover_color", Col(ThemeTokens.TextHi));
                    btn.AddThemeColorOverride("font_pressed_color", Col(ThemeTokens.TextHi));
                    btn.AddThemeColorOverride("font_focus_color", Col(ThemeTokens.TextHi));
                    break;
            }

            // Disabled look is shared across variants: surface-1 fill, text-disabled glyph.
            var disabled = Fresh(Col(ThemeTokens.Surface1), Col(ThemeTokens.Line), 1, false);
            btn.AddThemeColorOverride("font_disabled_color", Col(ThemeTokens.TextDisabled));

            // Focus = an accent ring drawn OVER the current state (one shared, registered ring for all btns).
            var focusRing = SharedAccentBox("btn/focus",
                () => ChimeraStyleBox.Chamfer(cut, Clear, Col(ThemeTokens.Accent), 2),
                Border(ThemeTokens.Accent));

            btn.AddThemeStyleboxOverride("normal", normal);
            btn.AddThemeStyleboxOverride("hover", hover);
            btn.AddThemeStyleboxOverride("pressed", pressed);
            btn.AddThemeStyleboxOverride("disabled", disabled);
            btn.AddThemeStyleboxOverride("focus", focusRing);
            return btn;
        }

        // ── icon-btn (UX-DR15) ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A 36×36 square icon button carrying a centered glyph (emoji/unicode text, 18px — the asset-free
        /// convention the existing panels use; a Texture2D overload is trivial once icon art lands).
        /// <paramref name="isActive"/> = accent fill (registered) + accent-ink glyph (subscribed).
        /// <paramref name="disabled"/> (D-8, though the CSS omits it) = surface-1 + text-disabled, inert.
        /// </summary>
        public static Godot.Button IconButton(string glyph, bool isActive = false, bool disabled = false)
        {
            var btn = new Godot.Button
            {
                Text = glyph,
                Disabled = disabled,
                CustomMinimumSize = new Vector2(ComponentMetrics.IconBtnSize, ComponentMetrics.IconBtnSize),
            };
            btn.AddThemeFontSizeOverride("font_size", ComponentMetrics.IconBtnGlyph); // 18
            int cut = Const(ThemeTokens.CutSm);

            StyleBoxFlat Square(Color fill, Color border, int bw) => ChimeraStyleBox.Chamfer(cut, fill, border, bw);

            StyleBoxFlat normal, hover;
            if (isActive)
            {
                normal = SharedAccentBox("iconbtn/active",
                    () => Square(Col(ThemeTokens.Accent), Col(ThemeTokens.Accent), 0),
                    Fill(ThemeTokens.Accent), Border(ThemeTokens.Accent));
                hover = normal;
                BindAccentColor(btn, ThemeTokens.AccentInk); // accent-ink glyph, retints on switch
            }
            else
            {
                normal = Square(Col(ThemeTokens.Surface2), Col(ThemeTokens.Line), 1);
                hover  = Square(Col(ThemeTokens.Surface4), Col(ThemeTokens.Line), 1);
                btn.AddThemeColorOverride("font_color", Col(ThemeTokens.TextMid));
                btn.AddThemeColorOverride("font_hover_color", Col(ThemeTokens.TextHi));
            }

            var disabledBox = Square(Col(ThemeTokens.Surface1), Col(ThemeTokens.Line), 1);
            btn.AddThemeColorOverride("font_disabled_color", Col(ThemeTokens.TextDisabled));

            var focusRing = SharedAccentBox("btn/focus",
                () => ChimeraStyleBox.Chamfer(cut, Clear, Col(ThemeTokens.Accent), 2),
                Border(ThemeTokens.Accent));

            btn.AddThemeStyleboxOverride("normal", normal);
            btn.AddThemeStyleboxOverride("hover", hover);
            btn.AddThemeStyleboxOverride("pressed", isActive ? normal : hover);
            btn.AddThemeStyleboxOverride("disabled", disabledBox);
            btn.AddThemeStyleboxOverride("focus", focusRing);
            return btn;
        }

        // ── input (UX-DR22) + .select chevron variant ────────────────────────────────────────────────────

        /// <summary>
        /// A text field: surface-3, cut-sm, inset line border. Focus = accent ring + accent-wash tint
        /// (both registered so they retint). Placeholder = text-lo; caret = accent.
        /// </summary>
        public static LineEdit Input(string placeholder = "", string text = "")
        {
            var le = new LineEdit { PlaceholderText = placeholder, Text = text };
            le.AddThemeFontOverride("font", FontOf(ThemeTokens.FontUi));
            le.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.Tsm)); // 13
            le.AddThemeColorOverride("font_color", Col(ThemeTokens.TextHi));
            le.AddThemeColorOverride("font_placeholder_color", Col(ThemeTokens.TextLo));
            BindAccentColor(le, ThemeTokens.Accent, "caret_color");

            int cut = Const(ThemeTokens.CutSm);
            var normal = ChimeraStyleBox.Chamfer(cut, Col(ThemeTokens.Surface3), Col(ThemeTokens.Line));
            normal.WithContentMargins(Const(ThemeTokens.S3), 9);
            le.AddThemeStyleboxOverride("normal", normal);
            le.AddThemeStyleboxOverride("read_only", normal);

            // Focus overlay: accent-wash fill + accent ring (both track the accent).
            var focus = SharedAccentBox("input/focus", () =>
            {
                var b = ChimeraStyleBox.Chamfer(cut, Col(ThemeTokens.AccentWash), Col(ThemeTokens.Accent), 2);
                b.WithContentMargins(Const(ThemeTokens.S3), 9);
                return b;
            }, Fill(ThemeTokens.AccentWash), Border(ThemeTokens.Accent));
            le.AddThemeStyleboxOverride("focus", focus);
            return le;
        }

        /// <summary>
        /// The input's <c>.select</c> chevron variant: a themed <see cref="OptionButton"/> (surface-3,
        /// cut-sm, extra right padding for its built-in chevron). Its dropdown popup styling is Story 3.1c.
        /// </summary>
        public static OptionButton Select(params string[] items)
        {
            var ob = new OptionButton();
            foreach (var it in items) ob.AddItem(it);
            ob.AddThemeFontOverride("font", FontOf(ThemeTokens.FontUi));
            ob.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.Tsm));
            ob.AddThemeColorOverride("font_color", Col(ThemeTokens.TextHi));

            int cut = Const(ThemeTokens.CutSm);
            var box = ChimeraStyleBox.Chamfer(cut, Col(ThemeTokens.Surface3), Col(ThemeTokens.Line));
            box.ContentMarginLeft = Const(ThemeTokens.S3);
            box.ContentMarginTop = 9;
            box.ContentMarginBottom = 9;
            box.ContentMarginRight = 30; // room for the chevron (CSS padding-right 30)
            foreach (var st in new[] { "normal", "hover", "pressed", "focus", "disabled" })
                ob.AddThemeStyleboxOverride(st, box);
            return ob;
        }

        /// <summary>An uppercase display field-label (11px, tracked, text-lo) to sit above an input.</summary>
        public static Label FieldLabel(string text)
        {
            var l = new Label { Text = Up(text) };
            l.AddThemeFontOverride("font", DisplayTracked(1)); // display, ~0.1em
            l.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.T2xs)); // 11
            l.AddThemeColorOverride("font_color", Col(ThemeTokens.TextLo));
            return l;
        }

        // ── num-input (UX-DR32) ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A right-aligned mono-tnum number field, fixed 64px. Cut 3, surface-3. Focus = accent ring ONLY
        /// (NO wash) — the deliberate distinction from <see cref="Input"/>. Styles the SpinBox's internal
        /// LineEdit. Carries a plain <c>double</c> value (Fixed↔form binding is a 3.3+ editor concern).
        /// </summary>
        public static SpinBox NumInput(double value = 0, double min = 0, double max = 100, double step = 1)
        {
            var sb = new SpinBox
            {
                MinValue = min,
                MaxValue = max,
                Step = step,
                Value = value,
                CustomMinimumSize = new Vector2(ComponentMetrics.NumInputWidth, 0),
            };

            var le = sb.GetLineEdit();
            le.AddThemeFontOverride("font", MonoTnumBold()); // mono tabular 700
            le.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.Tsm)); // 13
            le.AddThemeColorOverride("font_color", Col(ThemeTokens.TextHi));
            le.Alignment = HorizontalAlignment.Right;

            int cut = ComponentMetrics.CutMicro; // 3
            var normal = ChimeraStyleBox.Chamfer(cut, Col(ThemeTokens.Surface3), Col(ThemeTokens.Line));
            normal.WithContentMargins(Const(ThemeTokens.S2), 6);
            le.AddThemeStyleboxOverride("normal", normal);
            le.AddThemeStyleboxOverride("read_only", normal);

            // Focus = ring only (no wash fill) — transparent bg, accent border.
            var focus = SharedAccentBox("numinput/focus", () =>
            {
                var b = ChimeraStyleBox.Chamfer(cut, Clear, Col(ThemeTokens.Accent), 2);
                b.WithContentMargins(Const(ThemeTokens.S2), 6);
                return b;
            }, Border(ThemeTokens.Accent));
            le.AddThemeStyleboxOverride("focus", focus);
            return sb;
        }

        // ── Shared helper: bind several theme color items to one accent token (btn multi-state ink) ──

        /// <summary>Bind multiple theme Color overrides on one control to a single accent token, updated in
        /// one handler on every switch (D-3). Use-after-free guarded; unsubscribed by <see cref="Reset"/>.</summary>
        internal static void BindAccentColorMulti(Control ctrl, StringName accentToken, string[] colorItems)
        {
            void Apply()
            {
                if (!GodotObject.IsInstanceValid(ctrl)) return;
                var c = Col(accentToken);
                foreach (var item in colorItems)
                    ctrl.AddThemeColorOverride(item, c);
            }
            AccentController.AccentChangedEventHandler handler = _ => Apply();
            Apply();
            TrackHandler(ctrl, handler);
        }

        /// <summary>
        /// Subscribe a raw handler to the accent switch, tracked against <paramref name="owner"/> so it is
        /// unsubscribed by <see cref="Reset"/> AND pruned once <paramref name="owner"/> is freed. For
        /// stateful components (tabs, slider) that must re-style their OWN active/registered surfaces on a
        /// switch beyond what a single color override covers. The handler should still guard the node with
        /// <c>IsInstanceValid</c>.
        /// </summary>
        internal static void SubscribeAccentChanged(GodotObject owner, AccentController.AccentChangedEventHandler handler)
        {
            TrackHandler(owner, handler);
        }
    }
}
