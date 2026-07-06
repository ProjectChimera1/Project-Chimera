#nullable enable
using System.Collections.Generic;
using Godot;

namespace ProjectChimera.UI.Theme
{
    /// <summary>
    /// Throwaway proof harness for Story 3.1a (NOT a shipped surface — the real gallery is 3.1c).
    ///
    /// On ready it loads the committed <c>main.tres</c> (the artifact 3.1b+ consume) and applies it —
    /// it does NOT regenerate/overwrite that file; regeneration is <see cref="ThemeBuilder"/>'s explicit
    /// job, never a side effect of previewing. It then renders proofs for every acceptance criterion:
    ///   • AC4 — a swatch of every color token (surfaces/lines/text/accent/semantic/team).
    ///   • AC5 — labels in all 3 fonts + a JetBrains-Mono tabular-figure readout that stays aligned.
    ///   • AC2 — a chamfered panel (faceted, not rounded) + a "corner_detail 1↔8" teeth toggle that
    ///           momentarily rounds the corner to prove the facet is real.
    ///   • AC3 — teal/amber/violet buttons that retint every accent surface in one switch, including an
    ///           accent-filled chamfered button (the StyleBox seam) registered with the controller.
    ///
    /// Presentation layer. <c>Godot.Theme</c> is fully qualified (namespace shadows the bare name).
    /// </summary>
    public partial class ThemePreview : Control
    {
        private Godot.Theme _theme = null!;
        private AccentController _accent = null!;

        // Live accent-bound visuals refreshed on switch (swatches + labels; styleboxes are handled by
        // the controller). ColorRects with a baked Color don't auto-follow theme Color tokens.
        private readonly Dictionary<StringName, ColorRect> _accentSwatches = new();
        private readonly Dictionary<StringName, Label> _accentSwatchCaptions = new();
        private Label _currentAccentLabel = null!;
        private Label _accentTextLabel = null!;
        private Button _accentFillButton = null!;

        // Chamfer teeth toggle state.
        private StyleBoxFlat _chamferBox = null!;
        private Label _cornerDetailLabel = null!;

        // Live mono-tabular counter.
        private Label _monoCounter = null!;

        public override void _Ready()
        {
            // ── Load the committed canonical theme (the artifact 3.1b+ consume). Do NOT regenerate/
            //    overwrite it here — running this throwaway proof must never churn the git-tracked
            //    main.tres (ResourceSaver re-mints resource IDs on every save); regeneration is the
            //    explicit job of ThemeBuilder.Build/Save. Fall back to an in-memory build (no disk
            //    write) only if the committed file is somehow missing. ──
            _theme = ResourceLoader.Load<Godot.Theme>(
                         ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();
            Theme = _theme; // apply to the whole subtree

            _accent = new AccentController { Name = "AccentController" };
            AddChild(_accent);
            _accent.Initialize(_theme);

            // ── Scaffold: bg + scroll + column ──
            SetAnchorsPreset(LayoutPreset.FullRect);
            var bg = new ColorRect { Color = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type) };
            bg.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(bg);

            var scroll = new ScrollContainer();
            scroll.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(scroll);

            var margin = new MarginContainer();
            margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            foreach (var s in new[] { "left", "right", "top", "bottom" })
                margin.AddThemeConstantOverride($"margin_{s}", 24);
            scroll.AddChild(margin);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 18);
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            margin.AddChild(col);

            // ── Sections ──
            col.AddChild(Heading("Project Chimera — main.tres  ·  Story 3.1a proof", ThemeTokens.T2xl));
            BuildAccentRow(col);
            BuildChamferRow(col);
            BuildFontSection(col);
            BuildSwatchSection(col);

            RefreshAccentVisuals(); // seed accent-bound visuals for the default (teal)
        }

        public override void _Process(double delta)
        {
            // AC5 proof: a rapidly-changing number in a tabular-figure font keeps its columns fixed
            // (no horizontal jitter as digits change).
            long n = Engine.GetFramesDrawn() % 1000000;
            _monoCounter.Text = $"live: {n:D6}";
        }

        // ── Accent switch row (AC3) ──
        private void BuildAccentRow(VBoxContainer col)
        {
            col.AddChild(Heading("Accent switch (UX-DR4)  —  retints every accent surface in one op", ThemeTokens.Tlg));

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            foreach (var palette in ThemeTokens.AccentPalettes)
            {
                string name = palette.Name;
                var btn = new Button { Text = name };
                btn.Pressed += () =>
                {
                    _accent.SwitchAccent(name);
                    RefreshAccentVisuals();
                };
                row.AddChild(btn);
            }
            _currentAccentLabel = new Label();
            row.AddChild(_currentAccentLabel);
            col.AddChild(row);

            _accentTextLabel = new Label { Text = "◆ accent-colored text (reads the shared token, retints live)" };
            col.AddChild(_accentTextLabel);
        }

        // ── Chamfer proof + teeth toggle (AC2) + accent-filled seam (AC3) ──
        private void BuildChamferRow(VBoxContainer col)
        {
            col.AddChild(Heading("Chamfer (UX-DR9)  —  faceted 45° TL+BR cut, not rounded", ThemeTokens.Tlg));

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 16);

            // Faceted panel from surface tokens (proves AC2). Reads cut/surface/edge from the committed theme.
            int cut = _theme.GetConstant(ThemeTokens.Cut, ThemeTokens.Type);
            _chamferBox = ChimeraStyleBox.Chamfer(
                cut,
                _theme.GetColor(ThemeTokens.Surface2, ThemeTokens.Type),
                _theme.GetColor(ThemeTokens.EdgeLight, ThemeTokens.Type));
            _chamferBox.WithContentMargins(20, 16).WithShadow(ThemeTokens.GetShadow(ThemeTokens.Shadow1));

            var panel = new PanelContainer { CustomMinimumSize = new Vector2(260, 90) };
            panel.AddThemeStyleboxOverride("panel", _chamferBox);
            var panelLabel = new Label { Text = "surface-2 panel\nchamfered TL + BR\n+ shadow-1" };
            panel.AddChild(panelLabel);
            row.AddChild(panel);

            // Teeth toggle: flip corner_detail 1 <-> 8 to show the corner go rounded, then revert.
            var teeth = new VBoxContainer();
            _cornerDetailLabel = new Label { Text = "corner_detail = 1 (chamfer)" };
            var toggle = new Button { Text = "toggle corner_detail 1↔8" };
            toggle.Pressed += () =>
            {
                _chamferBox.CornerDetail = _chamferBox.CornerDetail == 1 ? 8 : 1;
                _cornerDetailLabel.Text = _chamferBox.CornerDetail == 1
                    ? "corner_detail = 1 (chamfer)" : "corner_detail = 8 (ROUNDED — wrong, for contrast)";
            };
            teeth.AddChild(_cornerDetailLabel);
            teeth.AddChild(toggle);
            row.AddChild(teeth);

            // Accent-filled chamfered button — the StyleBox seam. Registered so it retints on switch.
            int cutSm = _theme.GetConstant(ThemeTokens.CutSm, ThemeTokens.Type);
            var accentBox = ChimeraStyleBox.Chamfer(
                cutSm,
                _theme.GetColor(ThemeTokens.Accent, ThemeTokens.Type),
                _theme.GetColor(ThemeTokens.Accent, ThemeTokens.Type), 0);
            accentBox.WithContentMargins(16, 10);
            _accent.RegisterAccentFill(accentBox);
            _accent.RegisterAccentBorder(accentBox);

            _accentFillButton = new Button { Text = "accent-filled chamfer" };
            foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
                _accentFillButton.AddThemeStyleboxOverride(state, accentBox);
            row.AddChild(_accentFillButton);

            col.AddChild(row);
        }

        // ── Fonts + mono tabular (AC5) ──
        private void BuildFontSection(VBoxContainer col)
        {
            col.AddChild(Heading("Typography (UX-DR7/8/34)", ThemeTokens.Tlg));

            col.AddChild(FontLabel("Chakra Petch — display / headings  (font-display)", ThemeTokens.FontDisplay, 23));
            col.AddChild(FontLabel("Space Grotesk — UI body, the default font  (font-ui)", ThemeTokens.FontUi, 15));
            col.AddChild(FontLabel("JetBrains Mono — code & numbers  (font-mono)  0123456789", ThemeTokens.FontMono, 15));

            // Tabular-figure proof: two equal-length rows whose columns align because tnum fixes digit width.
            var tnumFont = _theme.GetFont(ThemeTokens.MonoTnum, ThemeTokens.Type);
            var rowA = new Label { Text = "tnum  1234567890" };
            var rowB = new Label { Text = "tnum  1111111111" };
            foreach (var l in new[] { rowA, rowB })
            {
                l.AddThemeFontOverride("font", tnumFont);
                l.AddThemeFontSizeOverride("font_size", 18);
            }
            col.AddChild(rowA);
            col.AddChild(rowB);

            _monoCounter = new Label { Text = "live: 000000" };
            _monoCounter.AddThemeFontOverride("font", tnumFont);
            _monoCounter.AddThemeFontSizeOverride("font_size", 18);
            col.AddChild(_monoCounter);
        }

        // ── Swatch grid of every color token (AC4) ──
        private void BuildSwatchSection(VBoxContainer col)
        {
            col.AddChild(Heading("Color tokens (UX-DR1/2/3/4/5/6)  —  team colors reserved (no chrome)", ThemeTokens.Tlg));

            var grid = new GridContainer { Columns = 8 };
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 8);

            foreach (var (token, _) in ThemeTokens.ColorTokens)
                grid.AddChild(Swatch(token, isAccent: false));
            foreach (var token in ThemeTokens.AccentTokens)
                grid.AddChild(Swatch(token, isAccent: true));

            col.AddChild(grid);
        }

        // ── Helpers ──
        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            return l;
        }

        private Label FontLabel(string text, StringName fontToken, int size)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(fontToken, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", size);
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextMid, ThemeTokens.Type));
            return l;
        }

        private Control Swatch(StringName token, bool isAccent)
        {
            var cell = new VBoxContainer { CustomMinimumSize = new Vector2(120, 0) };
            var color = _theme.GetColor(token, ThemeTokens.Type);
            var rect = new ColorRect { Color = color, CustomMinimumSize = new Vector2(112, 34) };
            var caption = new Label { Text = $"{token}\n#{color.ToHtml(true)}" };
            caption.AddThemeFontSizeOverride("font_size", 11);
            caption.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextLo, ThemeTokens.Type));
            cell.AddChild(rect);
            cell.AddChild(caption);
            if (isAccent)
            {
                _accentSwatches[token] = rect;
                _accentSwatchCaptions[token] = caption;
            }
            return cell;
        }

        /// <summary>Re-read accent-bound visuals after a switch (swatches, current label, accent text).</summary>
        private void RefreshAccentVisuals()
        {
            foreach (var (token, rect) in _accentSwatches)
            {
                var c = _theme.GetColor(token, ThemeTokens.Type);
                rect.Color = c;
                if (_accentSwatchCaptions.TryGetValue(token, out var cap))
                    cap.Text = $"{token}\n#{c.ToHtml(true)}";
            }

            var accentColor = _theme.GetColor(ThemeTokens.Accent, ThemeTokens.Type);
            _currentAccentLabel.Text = $"current: {_accent.CurrentAccent}";
            _currentAccentLabel.AddThemeColorOverride("font_color", accentColor);
            _accentTextLabel.AddThemeColorOverride("font_color", accentColor);
            _accentFillButton.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.AccentInk, ThemeTokens.Type));
        }
    }
}
