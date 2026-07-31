#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// Throwaway proof harness for Story 3.1b (NOT a shipped surface — the polished demo gallery with the
    /// UX-DR33 compose-from-kit guarantee is Story 3.1c). Loads the committed <c>main.tres</c> (never
    /// regenerates it), binds the <see cref="ChimeraComponents"/> factory to it + a fresh
    /// <see cref="AccentController"/>, then instantiates EVERY simple component across its variants/states
    /// for <c>/godot-verify</c>:
    ///   • all 13 components render, styled only from the theme;
    ///   • chamfers are faceted (kbd is the sole rounded element, shown for contrast);
    ///   • a cut-lg=14 surface is exercised (closes 3.1a deferred #4);
    ///   • the three accent buttons retint the WHOLE kit in one <see cref="AccentController.SwitchAccent"/>
    ///     — no manual per-surface refresh (the components self-retint via registration + AccentChanged);
    ///   • live numbers use the mono tabular-figure role (digit columns don't jitter).
    ///
    /// Presentation layer. <c>Godot.Theme</c> is fully qualified (the Theme namespace shadows the bare name).
    /// </summary>
    public partial class ComponentPreview : Control
    {
        private Godot.Theme _theme = null!;
        private AccentController _accent = null!;
        private Label _currentAccentLabel = null!;
        private Label _liveCounter = null!;

        public override void _Ready()
        {
            // Load the committed canonical theme (do NOT regenerate/overwrite it — ResourceSaver re-mints
            // resource IDs on every save; regeneration is ThemeBuilder's explicit job). Fall back to an
            // in-memory build only if the committed file is missing.
            _theme = ResourceLoader.Load<Godot.Theme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();
            Theme = _theme;

            _accent = new AccentController { Name = "AccentController" };
            AddChild(_accent);
            _accent.Initialize(_theme);

            ChimeraComponents.Initialize(_theme, _accent);

            // ── Scaffold: bg + scroll + column ──
            SetAnchorsPreset(LayoutPreset.FullRect);
            var bg = new ColorRect { Color = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type) };
            bg.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(bg);

            var scroll = new ScrollContainer();
            scroll.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(scroll);

            var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (var s in new[] { "left", "right", "top", "bottom" })
                margin.AddThemeConstantOverride($"margin_{s}", 24);
            scroll.AddChild(margin);

            var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            col.AddThemeConstantOverride("separation", 18);
            margin.AddChild(col);

            col.AddChild(ChimeraComponents.Heading("Project Chimera — component kit  ·  Story 3.1b proof", ThemeTokens.T2xl));

            BuildAccentRow(col);
            BuildPanels(col);
            BuildButtons(col);
            BuildReadouts(col);
            BuildProgress(col);
            BuildInputs(col);
            BuildSlider(col);
            BuildTabs(col);
            BuildListRows(col);
            BuildLiveNumbers(col);
        }

        public override void _Process(double delta)
        {
            // AC5: a fast-changing number in the tabular-figure role keeps its columns fixed (no jitter).
            long n = Engine.GetFramesDrawn() % 1000000;
            _liveCounter.Text = $"{n:D6}";
        }

        // ── Accent switch (AC4): retints the WHOLE kit in one op ──
        private void BuildAccentRow(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("Accent switch (UX-DR4) — one op retints the entire kit", ThemeTokens.Tlg));
            var row = Row();
            foreach (var palette in ThemeTokens.AccentPalettes)
            {
                string name = palette.Name;
                var btn = ChimeraComponents.Button(name, ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
                btn.Pressed += () =>
                {
                    _accent.SwitchAccent(name);
                    _currentAccentLabel.Text = $"current: {_accent.CurrentAccent}";
                };
                row.AddChild(btn);
            }
            _currentAccentLabel = ChimeraComponents.Body($"current: {_accent.CurrentAccent}", ThemeTokens.TextMid);
            _currentAccentLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(_currentAccentLabel);
            col.AddChild(row);
        }

        // ── panel (DR13) + a cut-lg=14 surface (closes 3.1a deferred #4) ──
        private void BuildPanels(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("panel (DR13) — faceted cut-8; variants + cut-lg", ThemeTokens.Tlg));
            var row = Row(16);
            row.AddChild(FilledPanel(ChimeraComponents.Panel(), "panel (surface-1) + shadow-1"));
            row.AddChild(FilledPanel(ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Surface2), "panel --2 (surface-2)"));
            row.AddChild(FilledPanel(ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Flat), "panel --flat (no shadow)"));
            row.AddChild(FilledPanel(ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Accent), "panel --accent (accent border)"));

            // cut-lg = 14 surface, built directly to exercise the largest global cut.
            int cutLg = _theme.GetConstant(ThemeTokens.CutLg, ThemeTokens.Type);
            var big = new PanelContainer { CustomMinimumSize = new Vector2(180, 70) };
            var box = ChimeraStyleBox.Chamfer(cutLg, _theme.GetColor(ThemeTokens.Surface3, ThemeTokens.Type), _theme.GetColor(ThemeTokens.EdgeLight, ThemeTokens.Type));
            box.WithContentMargins(16, 12).WithShadow(ThemeTokens.GetShadow(ThemeTokens.Shadow2));
            big.AddThemeStyleboxOverride("panel", box);
            var cutLgBody = ChimeraComponents.Body($"cut-lg = {cutLg}\n(dialog-scale facet)", ThemeTokens.TextMid);
            cutLgBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            big.AddChild(cutLgBody);
            row.AddChild(big);
            col.AddChild(row);
        }

        // ── btn (DR14) + icon-btn (DR15) ──
        private void BuildButtons(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("btn (DR14) — variants × sizes, :active depress, disabled", ThemeTokens.Tlg));

            var variants = Row(10);
            variants.AddChild(ChimeraComponents.Button("Primary", ChimeraComponents.ButtonVariant.Primary));
            variants.AddChild(ChimeraComponents.Button("Secondary", ChimeraComponents.ButtonVariant.Secondary));
            variants.AddChild(ChimeraComponents.Button("Ghost", ChimeraComponents.ButtonVariant.Ghost));
            variants.AddChild(ChimeraComponents.Button("Danger", ChimeraComponents.ButtonVariant.Danger));
            var disabled = ChimeraComponents.Button("Disabled", ChimeraComponents.ButtonVariant.Primary);
            disabled.Disabled = true;
            variants.AddChild(disabled);
            col.AddChild(variants);

            var sizes = Row(10);
            sizes.AddChild(ChimeraComponents.Button("Sm", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Sm));
            sizes.AddChild(ChimeraComponents.Button("Default", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Default));
            sizes.AddChild(ChimeraComponents.Button("Large", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Lg));
            col.AddChild(sizes);

            var block = ChimeraComponents.Button("Block button (full width)", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Block);
            block.CustomMinimumSize = new Vector2(320, 0);
            var blockWrap = Row();
            blockWrap.AddChild(block);
            col.AddChild(blockWrap);

            col.AddChild(ChimeraComponents.Heading("icon-btn (DR15) — 36×36, is-active, disabled (D-8)", ThemeTokens.Tlg));
            var icons = Row(10);
            icons.AddChild(ChimeraComponents.IconButton("⚙"));            // gear
            icons.AddChild(ChimeraComponents.IconButton("▶", isActive: true)); // play (active)
            icons.AddChild(ChimeraComponents.IconButton("✎"));            // pencil
            icons.AddChild(ChimeraComponents.IconButton("✖", disabled: true)); // x (disabled)
            col.AddChild(icons);
        }

        // ── chip (DR17) + readout (DR18) + tag (DR19) + kbd (DR16) ──
        private void BuildReadouts(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("readout trio (DR17/18/19) + kbd (DR16, the sole ROUND element)", ThemeTokens.Tlg));

            var chips = Row(10);
            chips.AddChild(ChimeraComponents.Chip("128"));
            chips.AddChild(ChimeraComponents.Chip("64", "ore"));
            chips.AddChild(ChimeraComponents.Chip("2400", "gold"));
            col.AddChild(chips);

            var readouts = Row(24);
            readouts.AddChild(ChimeraComponents.Readout(_theme.GetColor(ThemeTokens.Ok, ThemeTokens.Type), "1240", "supply"));
            readouts.AddChild(ChimeraComponents.Readout(_theme.GetColor(ThemeTokens.Warn, ThemeTokens.Type), "087", "upkeep"));
            readouts.AddChild(ChimeraComponents.Readout(_theme.GetColor(ThemeTokens.Info, ThemeTokens.Type), "3.4k", "apm"));
            col.AddChild(readouts);

            var tags = Row(8);
            tags.AddChild(ChimeraComponents.Tag("Neutral"));
            tags.AddChild(ChimeraComponents.Tag("Locked", ChimeraComponents.TagVariant.Lock));
            tags.AddChild(ChimeraComponents.Tag("Ready", ChimeraComponents.TagVariant.Ok));
            tags.AddChild(ChimeraComponents.Tag("Accent", ChimeraComponents.TagVariant.Accent));
            tags.AddChild(ChimeraComponents.Tag("Error", ChimeraComponents.TagVariant.Danger));
            col.AddChild(tags);

            var keys = Row(6);
            var shortcutBody = ChimeraComponents.Body("shortcut:", ThemeTokens.TextMid);
            shortcutBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            keys.AddChild(shortcutBody);
            keys.AddChild(ChimeraComponents.Kbd("Ctrl"));
            keys.AddChild(ChimeraComponents.Kbd("Shift"));
            keys.AddChild(ChimeraComponents.Kbd("A"));
            var roundedBody = ChimeraComponents.Body("(rounded 3px — contrast the facets)", ThemeTokens.TextMid);
            roundedBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            keys.AddChild(roundedBody);
            col.AddChild(keys);
        }

        // ── progress (DR20) ──
        private void BuildProgress(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("progress (DR20) — accent / --ok / --xp", ThemeTokens.Tlg));
            col.AddChild(ProgressRow("accent", ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Default, 62)));
            col.AddChild(ProgressRow("--ok", ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Ok, 80)));
            col.AddChild(ProgressRow("--xp", ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Xp, 40)));
        }

        // ── input (DR22) + select + num-input (DR32) ──
        private void BuildInputs(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("input (DR22) + .select + num-input (DR32)", ThemeTokens.Tlg));

            var fields = new VBoxContainer();
            fields.AddThemeConstantOverride("separation", 4);
            fields.AddChild(ChimeraComponents.FieldLabel("Unit name"));
            var input = ChimeraComponents.Input("e.g. Rebel Alchemist");
            input.CustomMinimumSize = new Vector2(260, 0);
            fields.AddChild(input);
            col.AddChild(fields);

            var row = Row(16);
            var sel = ChimeraComponents.Select("Melee", "Ranged", "Siege", "Air");
            sel.CustomMinimumSize = new Vector2(160, 0);
            row.AddChild(WithLabel("category (.select)", sel));
            row.AddChild(WithLabel("damage (num-input)", ChimeraComponents.NumInput(42, 0, 999, 1)));
            col.AddChild(row);
        }

        // ── slider (DR21) ──
        private void BuildSlider(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("slider (DR21) — track + accent thumb, paired num-input", ThemeTokens.Tlg));
            var slider = ChimeraSlider.Create(35, 0, 100, 1);
            slider.CustomMinimumSize = new Vector2(320, 0);
            var wrap = Row();
            wrap.AddChild(slider);
            col.AddChild(wrap);
        }

        // ── tabs (DR24) ──
        private void BuildTabs(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("tabs (DR24) — underline / --boxed / segment", ThemeTokens.Tlg));
            var row = Row(24);
            row.AddChild(ChimeraTabs.Create(ChimeraComponents.TabsVariant.Underline, "Overview", "Stats", "Abilities"));
            row.AddChild(ChimeraTabs.Create(ChimeraComponents.TabsVariant.Boxed, "One", "Two", "Three"));
            row.AddChild(ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced"));
            col.AddChild(row);
        }

        // ── list-row (DR25) ──
        private void BuildListRows(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("list-row (DR25) — hover / selected / locked, single-select", ThemeTokens.Tlg));
            var list = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
            list.AddThemeConstantOverride("separation", 4);
            var group = new ListRowGroup();
            var r1 = ChimeraListRow.Create("Rebel Alchemist", group);
            var r2 = ChimeraListRow.Create("Homunculus Brute (selected)", group);
            var r3 = ChimeraListRow.Create("Transmuter", group);
            var r4 = ChimeraListRow.Create("Sealed Unit (locked)", group);
            group.Select(r2);
            r4.SetLocked(true);
            list.AddChild(r1);
            list.AddChild(r2);
            list.AddChild(r3);
            list.AddChild(r4);
            col.AddChild(list);
        }

        // ── live tabular numbers (AC5) ──
        private void BuildLiveNumbers(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("live numbers (UX-DR34) — mono tabular, no column jitter", ThemeTokens.Tlg));
            var tnum = _theme.GetFont(ThemeTokens.MonoTnum, ThemeTokens.Type);
            _liveCounter = new Label { Text = "000000" };
            _liveCounter.AddThemeFontOverride("font", tnum);
            _liveCounter.AddThemeFontSizeOverride("font_size", 23);
            _liveCounter.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            var fixedWidthProof = new Label { Text = "1111111111\n1234567890 (columns align)" };
            fixedWidthProof.AddThemeFontOverride("font", tnum);
            fixedWidthProof.AddThemeFontSizeOverride("font_size", 18);
            fixedWidthProof.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextMid, ThemeTokens.Type));
            col.AddChild(_liveCounter);
            col.AddChild(fixedWidthProof);
        }

        // ── Helpers ──
        private HBoxContainer Row(int sep = 12)
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", sep);
            return h;
        }

        private Control FilledPanel(PanelContainer panel, string caption)
        {
            panel.CustomMinimumSize = new Vector2(180, 70);
            var captionLbl = ChimeraComponents.Body(caption, ThemeTokens.TextMid);
            captionLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            panel.AddChild(captionLbl);
            return panel;
        }

        private Control ProgressRow(string label, ProgressBar bar)
        {
            bar.CustomMinimumSize = new Vector2(240, ComponentMetrics.ProgressTrackHeight);
            bar.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            var row = Row(12);
            var lbl = ChimeraComponents.Body(label, ThemeTokens.TextMid);
            lbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            lbl.CustomMinimumSize = new Vector2(60, 0);
            row.AddChild(lbl);
            row.AddChild(bar);
            return row;
        }

        private Control WithLabel(string label, Control control)
        {
            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", 4);
            v.AddChild(ChimeraComponents.FieldLabel(label));
            v.AddChild(control);
            return v;
        }
    }
}
