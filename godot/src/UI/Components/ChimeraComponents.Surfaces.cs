#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// Story 3.1b display components (non-interactive surfaces): panel (DR13), kbd (DR16), chip (DR17),
    /// readout (DR18), tag (DR19), progress (DR20). Part of the <see cref="ChimeraComponents"/> factory.
    /// Every color/cut/font is read from the canonical theme; <c>kbd</c> is the SOLE rounded element and
    /// deliberately does NOT go through <see cref="ChimeraStyleBox.Chamfer"/>.
    /// </summary>
    public static partial class ChimeraComponents
    {
        // ── panel (UX-DR13) ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A chamfered content panel (cut 8). <c>--2</c> = surface-2 fill; <c>--flat</c> = no shadow;
        /// <c>--accent</c> = accent-bright border (registered, retints on accent switch). The two-layer
        /// cel-shade border is approximated with the lighter <c>edge_light</c> hairline (a single
        /// StyleBoxFlat carries one border color; a true 2-color backplate is a later refinement, not an AC).
        /// </summary>
        public static PanelContainer Panel(PanelVariant variant = PanelVariant.Default)
        {
            var pc = new PanelContainer();
            int cut = Const(ThemeTokens.Cut); // 8
            int pad = Const(ThemeTokens.S4);  // 16
            var bg = Col(variant == PanelVariant.Surface2 ? ThemeTokens.Surface2 : ThemeTokens.Surface1);

            StyleBoxFlat box;
            if (variant == PanelVariant.Accent)
            {
                // Shared, registered accent-border box: N accent panels track ONE box.
                box = SharedAccentBox("panel/accent", () =>
                {
                    var b = ChimeraStyleBox.Chamfer(cut, bg, Col(ThemeTokens.AccentBright));
                    b.WithContentMargins(pad, pad).WithShadow(ThemeTokens.GetShadow(ThemeTokens.Shadow1));
                    return b;
                }, Border(ThemeTokens.AccentBright));
            }
            else
            {
                box = ChimeraStyleBox.Chamfer(cut, bg, Col(ThemeTokens.EdgeLight));
                box.WithContentMargins(pad, pad);
                if (variant != PanelVariant.Flat)
                    box.WithShadow(ThemeTokens.GetShadow(ThemeTokens.Shadow1));
            }

            pc.AddThemeStyleboxOverride("panel", box);
            return pc;
        }

        // ── kbd (UX-DR16) — the ONE rounded element (hard exception to the chamfer language) ───────────

        /// <summary>
        /// A keycap chip: <b>rounded</b> 3px corners (corner_detail DEFAULT, NOT the faceted chamfer),
        /// surface-3 fill, line-strong border with a 2px bottom "lip", centered mono 11/700. This is the
        /// sole radiused surface in the whole kit (UX-DR35) — building it via <c>Chamfer</c> would be a bug.
        /// </summary>
        public static Control Kbd(string text)
        {
            var box = new StyleBoxFlat
            {
                BgColor = Col(ThemeTokens.Surface3),
                // ROUND — default corner_detail (a real radius), NOT ChimeraStyleBox.Chamfer's detail=1 facet.
                CornerRadiusTopLeft     = ComponentMetrics.KbdRadius,
                CornerRadiusTopRight    = ComponentMetrics.KbdRadius,
                CornerRadiusBottomLeft  = ComponentMetrics.KbdRadius,
                CornerRadiusBottomRight = ComponentMetrics.KbdRadius,
                BorderColor       = Col(ThemeTokens.LineStrong),
                BorderWidthTop    = 1,
                BorderWidthLeft   = 1,
                BorderWidthRight  = 1,
                BorderWidthBottom = ComponentMetrics.KbdBottomBorder, // 2px keycap lip
            };
            box.WithContentMargins(5, 1); // pad 1×5

            var label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
            label.AddThemeFontOverride("font", FontOf(ThemeTokens.FontMono));
            label.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.T2xs)); // 11
            label.AddThemeColorOverride("font_color", Col(ThemeTokens.TextMid));

            var pc = new PanelContainer { CustomMinimumSize = new Vector2(ComponentMetrics.KbdMinWidth, 0) };
            pc.AddThemeStyleboxOverride("panel", box);
            pc.AddChild(label);
            return pc;
        }

        // ── chip (UX-DR17) ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A small surface-2 pill (cut 5, inset line border) holding a mono <c>.num</c>, with an optional
        /// ui-font label before it. Static readout of a small count.
        /// </summary>
        public static PanelContainer Chip(string number, string? label = null)
        {
            var box = ChimeraStyleBox.Chamfer(
                Const(ThemeTokens.CutSm), Col(ThemeTokens.Surface2), Col(ThemeTokens.Line));
            box.WithContentMargins(Const(ThemeTokens.S3), Const(ThemeTokens.S1) + 1); // ~10 × ~5

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", Const(ThemeTokens.S2)); // gap s2

            if (!string.IsNullOrEmpty(label))
            {
                var lbl = new Label { Text = label };
                lbl.AddThemeFontOverride("font", FontOf(ThemeTokens.FontUi));
                lbl.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.Tsm));
                lbl.AddThemeColorOverride("font_color", Col(ThemeTokens.TextLo));
                row.AddChild(lbl);
            }

            var num = new Label { Text = number };
            num.AddThemeFontOverride("font", FontOf(ThemeTokens.MonoTnum)); // .num is mono tabular
            num.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.Tsm)); // 13
            num.AddThemeColorOverride("font_color", Col(ThemeTokens.TextHi));
            row.AddChild(num);

            var pc = new PanelContainer();
            pc.AddThemeStyleboxOverride("panel", box);
            pc.AddChild(row);
            return pc;
        }

        // ── readout (UX-DR18) ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A stat readout: a 22×22 faceted (cut 3) color-icon plate + a live mono-tnum value (text-hi,
        /// 18/700) + an uppercase display label (text-lo, 11, tracked). The value uses the tabular-figure
        /// role so a changing number's digit columns don't jitter (AC5).
        /// </summary>
        public static HBoxContainer Readout(Color iconColor, string value, string label)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", Const(ThemeTokens.S2));
            row.Alignment = BoxContainer.AlignmentMode.Begin;

            // 22×22 faceted icon plate (cut 3, filled with the caller's color).
            var plate = new Panel { CustomMinimumSize = new Vector2(ComponentMetrics.ReadoutIconSize, ComponentMetrics.ReadoutIconSize) };
            plate.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            plate.AddThemeStyleboxOverride("panel", ChimeraStyleBox.Chamfer(ComponentMetrics.CutMicro, iconColor, iconColor, 0));
            row.AddChild(plate);

            var val = new Label { Text = value };
            val.AddThemeFontOverride("font", MonoTnumBold());
            val.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.Tlg)); // 18
            val.AddThemeColorOverride("font_color", Col(ThemeTokens.TextHi));
            val.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(val);

            var lbl = new Label { Text = Up(label) };
            lbl.AddThemeFontOverride("font", DisplayTracked(1)); // ~0.12em tracking
            lbl.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.T2xs)); // 11
            lbl.AddThemeColorOverride("font_color", Col(ThemeTokens.TextLo));
            lbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(lbl);

            return row;
        }

        // ── tag (UX-DR19) ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// An uppercase display pill (cut 3) with a tinted-bg + colored-text pair. Neutral = surface-3 /
        /// text-mid; --lock = warn; --ok = ok; --danger = danger (each: <c>*_ink</c> dark tint bg + the
        /// bright semantic text); --accent = accent-bright text on accent-wash (both registered / subscribed
        /// so the whole tag retints on an accent switch).
        /// </summary>
        public static PanelContainer Tag(string text, TagVariant variant = TagVariant.Neutral)
        {
            StringName bgToken, textToken;
            switch (variant)
            {
                case TagVariant.Lock:   bgToken = ThemeTokens.WarnInk;   textToken = ThemeTokens.Warn;   break;
                case TagVariant.Ok:     bgToken = ThemeTokens.OkInk;     textToken = ThemeTokens.Ok;     break;
                case TagVariant.Danger: bgToken = ThemeTokens.DangerInk; textToken = ThemeTokens.Danger; break;
                case TagVariant.Accent: bgToken = ThemeTokens.AccentWash; textToken = ThemeTokens.AccentBright; break;
                default:                bgToken = ThemeTokens.Surface3;  textToken = ThemeTokens.TextMid; break;
            }

            StyleBoxFlat box;
            if (variant == TagVariant.Accent)
            {
                box = SharedAccentBox("tag/accent", () =>
                {
                    var b = ChimeraStyleBox.Chamfer(ComponentMetrics.CutMicro, Col(bgToken), Col(bgToken), 0);
                    b.WithContentMargins(Const(ThemeTokens.S2), Const(ThemeTokens.S1) - 1); // ~8 × ~3
                    return b;
                }, Fill(ThemeTokens.AccentWash));
            }
            else
            {
                box = ChimeraStyleBox.Chamfer(ComponentMetrics.CutMicro, Col(bgToken), Col(bgToken), 0);
                box.WithContentMargins(Const(ThemeTokens.S2), Const(ThemeTokens.S1) - 1);
            }

            var lbl = new Label { Text = Up(text), HorizontalAlignment = HorizontalAlignment.Center };
            lbl.AddThemeFontOverride("font", DisplayTracked(1)); // display, ~0.05em tracking
            lbl.AddThemeFontSizeOverride("font_size", SizeOf(ThemeTokens.T2xs)); // 11
            if (variant == TagVariant.Accent)
                BindAccentColor(lbl, ThemeTokens.AccentBright); // retints on accent switch
            else
                lbl.AddThemeColorOverride("font_color", Col(textToken));

            var pc = new PanelContainer();
            pc.AddThemeStyleboxOverride("panel", box);
            pc.AddChild(lbl);
            return pc;
        }

        // ── progress (UX-DR20) ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A themed progress bar (8px track). Default = accent fill (registered); <c>--ok</c> = green fill;
        /// <c>--xp</c> = accent fill (the 45° stripe is approximated as a solid accent fill — a StyleBoxFlat
        /// has no gradient/pattern; a real stripe needs a texture/shader, out of 3.1b scope). The accent
        /// glow is omitted on purpose: a stylebox shadow color is not a registered accent property, so a
        /// baked accent-glow would go stale on a switch (the fill/border retint correctly).
        /// </summary>
        public static ProgressBar Progress(ProgressVariant variant = ProgressVariant.Default, double value = 60)
        {
            var bar = new ProgressBar
            {
                MinValue = 0,
                MaxValue = 100,
                Value = value,
                ShowPercentage = false,
                CustomMinimumSize = new Vector2(0, ComponentMetrics.ProgressTrackHeight),
            };

            // Track (background): surface-3, cut 2, no content margin (the bar height is the track height).
            var track = ChimeraStyleBox.Chamfer(ComponentMetrics.CutProgress, Col(ThemeTokens.Surface3), Col(ThemeTokens.Line));
            bar.AddThemeStyleboxOverride("background", track);

            StyleBoxFlat fill;
            if (variant == ProgressVariant.Ok)
            {
                fill = ChimeraStyleBox.Chamfer(ComponentMetrics.CutProgress, Col(ThemeTokens.Ok), Col(ThemeTokens.Ok), 0);
            }
            else
            {
                // Default + --xp: accent fill, shared + registered so it retints on an accent switch.
                string key = variant == ProgressVariant.Xp ? "progress/xp/fill" : "progress/fill";
                fill = SharedAccentBox(key,
                    () => ChimeraStyleBox.Chamfer(ComponentMetrics.CutProgress, Col(ThemeTokens.Accent), Col(ThemeTokens.Accent), 0),
                    Fill(ThemeTokens.Accent), Border(ThemeTokens.Accent));
            }
            bar.AddThemeStyleboxOverride("fill", fill);
            return bar;
        }
    }
}
