#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// The canonical UX-DR33 demo gallery for Story 3.1c — the polished proof that the FULL kit (every 3.1b
    /// simple component AND all 7 of 3.1c's composite/feedback components) composes on-brand from one Theme,
    /// and that a single <see cref="AccentController.SwitchAccent"/> retints the whole kit with zero stale
    /// surfaces. It replaces the throwaway 3.1b <c>component_preview</c> as the canonical demo (that scene is
    /// left intact).
    ///
    /// It reuses the proven <see cref="ComponentPreview"/> scaffold verbatim: load the committed
    /// <c>main.tres</c> (<c>CacheMode.Ignore</c>, never regenerated), bind a fresh
    /// <see cref="AccentController"/> + the <see cref="ChimeraComponents"/> factory, then lay out sections in
    /// a scroll/VBox. Every interaction (open dialog / show toast / toggle switch / focus a tooltip'd field /
    /// open a menu / switch accent) is driven by a Button whose <c>pressed</c> signal <c>/godot-verify</c>
    /// can emit — godot-mcp has no absolute-mouse click.
    ///
    /// Presentation layer. <c>Godot.Theme</c> is fully qualified (the Theme namespace shadows the bare name).
    /// </summary>
    public partial class ComponentGallery : Control
    {
        private Godot.Theme _theme = null!;
        private AccentController _accent = null!;
        private ChimeraToastHost _toasts = null!;
        private Label _currentAccentLabel = null!;
        private Label _liveCounter = null!;

        public override void _Ready()
        {
            // Load the committed canonical theme (do NOT regenerate/overwrite it — ResourceSaver re-mints ids).
            _theme = ResourceLoader.Load<Godot.Theme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();
            Theme = _theme;

            _accent = new AccentController { Name = "AccentController" };
            AddChild(_accent);
            _accent.Initialize(_theme);
            ChimeraComponents.Initialize(_theme, _accent);

            // The toast host lives on its own high CanvasLayer for the session.
            _toasts = ChimeraToastHost.Create();
            AddChild(_toasts);

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

            col.AddChild(ChimeraComponents.Heading("Project Chimera — full component kit  ·  Story 3.1c gallery", ThemeTokens.T2xl));

            BuildAccentRow(col);

            // ── 3.1c composite / feedback components (the new work) ──
            col.AddChild(Divider("Story 3.1c — composite + feedback"));
            BuildMarks(col);
            BuildSpinners(col);
            BuildSwitch(col);
            BuildTooltips(col);
            BuildMenu(col);
            BuildDialog(col);
            BuildToasts(col);

            // ── 3.1b simple components (proving the whole kit composes + retints together) ──
            col.AddChild(Divider("Story 3.1b — simple kit"));
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
            long n = Engine.GetFramesDrawn() % 1000000;
            _liveCounter.Text = $"{n:D6}";
        }

        // ── Accent switch (AC6): one op retints BOTH 3.1b and 3.1c surfaces ──
        private void BuildAccentRow(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("Accent switch (UX-DR4) — one op retints the WHOLE kit (3.1b + 3.1c)", ThemeTokens.Tlg));
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

        // ── mark (DR30) — seal + triad, procedural, accent-following ──
        private void BuildMarks(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("mark (DR30) — the Chimera Seal (procedural, retints live) + .triad", ThemeTokens.Tlg));
            var row = Row(24);
            row.AddChild(WithCaption(ChimeraMark.Create(96), "seal 96"));
            row.AddChild(WithCaption(ChimeraMark.Create(48), "seal 48"));
            row.AddChild(WithCaption(ChimeraMark.Create(24, triad: true), "triad 24"));
            row.AddChild(WithCaption(ChimeraMark.Create(18, triad: true), "triad 18"));
            col.AddChild(row);
        }

        // ── spinner (DR29) — 3 sizes + reduced-motion note ──
        private void BuildSpinners(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("spinner (DR29) — transmute loader (ring CW / tri CCW / core pulse)", ThemeTokens.Tlg));
            var row = Row(24);
            row.AddChild(WithCaption(ChimeraSpinner.Create(ComponentMetrics.SpinnerSm), "sm 22"));
            row.AddChild(WithCaption(ChimeraSpinner.Create(ComponentMetrics.SpinnerDefault), "default 48"));
            row.AddChild(WithCaption(ChimeraSpinner.Create(ComponentMetrics.SpinnerLg), "lg 96"));
            var label = ChimeraComponents.Body("“Transmuting…”", ThemeTokens.TextMid);
            label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(label);
            col.AddChild(row);

            // Reduced-motion toggle (UX-DR44 seam) — a Button /godot-verify can emit.
            var rm = ChimeraComponents.Button("Toggle reduced-motion", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            var rmState = ChimeraComponents.Body($"ChimeraMotion.ReducedMotion = {ChimeraMotion.ReducedMotion}", ThemeTokens.TextMid);
            rmState.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            rm.Pressed += () =>
            {
                ChimeraMotion.ReducedMotion = !ChimeraMotion.ReducedMotion;
                rmState.Text = $"ChimeraMotion.ReducedMotion = {ChimeraMotion.ReducedMotion}";
            };
            var rmRow = Row();
            rmRow.AddChild(rm);
            rmRow.AddChild(rmState);
            col.AddChild(rmRow);
        }

        // ── switch (DR31/54) — faceted toggle + Promote-to-Hero inline reveal ──
        private void BuildSwitch(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("switch (DR31/54) — faceted square knob, slides over speed; inline reveal", ThemeTokens.Tlg));

            // The hero-toggle pill: label + switch on the right, revealing placeholder leveling fields below.
            var pill = new PanelContainer();
            var pillBox = ChimeraStyleBox.Chamfer(ChimeraComponents.Const(ThemeTokens.CutSm),
                _theme.GetColor(ThemeTokens.Surface2, ThemeTokens.Type), _theme.GetColor(ThemeTokens.Line, ThemeTokens.Type));
            pillBox.WithContentMargins(13, 11);
            pill.AddThemeStyleboxOverride("panel", pillBox);
            pill.CustomMinimumSize = new Vector2(360, 0);

            var pillRow = new HBoxContainer();
            var pl = new Label { Text = "Promote to Hero", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            pl.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            pl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            pillRow.AddChild(pl);
            var sw = ChimeraSwitch.Create();
            sw.Name = "HeroSwitch"; // stable path for /godot-verify to toggle via emitted signal
            sw.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            pillRow.AddChild(sw);
            pill.AddChild(pillRow);

            // Placeholder leveling fields (real hero/advanced fields are 3.4+); hidden until the switch is on.
            var heroFields = new VBoxContainer { Visible = false };
            heroFields.AddThemeConstantOverride("separation", 4);
            var heroFieldsBody = ChimeraComponents.Body("Reveals leveling, XP curve & ultimate", ThemeTokens.TextMid);
            heroFieldsBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            heroFields.AddChild(heroFieldsBody);
            var fr = Row(16);
            fr.AddChild(WithLabel("start level", ChimeraComponents.NumInput(1, 1, 10, 1)));
            fr.AddChild(WithLabel("xp curve ×", ChimeraComponents.NumInput(120, 0, 999, 5)));
            heroFields.AddChild(fr);
            sw.BindReveal(heroFields);

            // A trigger button /godot-verify can emit to toggle the switch (no absolute-mouse click).
            var toggle = ChimeraComponents.Button("Toggle switch", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            toggle.Pressed += () => sw.SetOn(!sw.On);

            var standalone = Row(12);
            var standaloneBody = ChimeraComponents.Body("standalone:", ThemeTokens.TextMid);
            standaloneBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            standalone.AddChild(standaloneBody);
            standalone.AddChild(ChimeraSwitch.Create(true));
            standalone.AddChild(ChimeraSwitch.Create(false));
            standalone.AddChild(toggle);

            col.AddChild(pill);
            col.AddChild(heroFields);
            col.AddChild(standalone);
        }

        // ── tooltip (DR26/53/45) — hover AND keyboard focus, on any control ──
        private void BuildTooltips(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("tooltip (DR26/53) — reveals on hover AND keyboard focus (UX-DR45)", ThemeTokens.Tlg));

            // Field role: a "?" glyph carrying an .f-tip.
            var fieldRow = Row(8);
            fieldRow.AddChild(ChimeraComponents.FieldLabel("attack range"));
            var qm = new Label { Text = "?" };
            qm.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Txs));
            qm.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextLo, ThemeTokens.Type));
            qm.MouseFilter = MouseFilterEnum.Stop;
            ChimeraTooltip.Attach(qm, "Attack Range", "0 = melee. Tiles the unit can strike from.", ChimeraTooltip.TooltipRole.Field);
            fieldRow.AddChild(qm);
            col.AddChild(fieldRow);

            // Pop role on a focusable input, + a Button /godot-verify can emit to grab focus (reveals on focus).
            var input = ChimeraComponents.Input("hover or focus me");
            input.Name = "TooltipInput";
            input.CustomMinimumSize = new Vector2(220, 0);
            ChimeraTooltip.Attach(input, "Unit Name", "The display name creators and players see.");
            var focusBtn = ChimeraComponents.Button("Focus the field", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Sm);
            focusBtn.Pressed += () => input.GrabFocus();
            var popRow = Row(12);
            popRow.AddChild(input);
            popRow.AddChild(focusBtn);
            col.AddChild(popRow);
        }

        // ── menu (DR23) — dropdown popover + the styled .select ──
        private void BuildMenu(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("menu (DR23) — dropdown popover (is-active accent); .select popup styled", ThemeTokens.Tlg));

            var menu = ChimeraMenu.Create();
            menu.AddItem("Duplicate", 0);
            menu.AddItem("Rename", 1);
            menu.AddItem("Export…", 2, active: true, check: "✓");
            menu.AddItem("Delete", 3);
            AddChild(menu);
            var openBtn = ChimeraComponents.Button("Open menu", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            openBtn.Pressed += () => menu.OpenBelow(openBtn);

            var chosen = ChimeraComponents.Body("chosen: —", ThemeTokens.TextMid);
            chosen.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            menu.IdPressed += id => chosen.Text = $"chosen: item {id}";

            var row = Row(12);
            row.AddChild(openBtn);
            var sel = ChimeraComponents.Select("Melee", "Ranged", "Siege", "Air");
            sel.CustomMinimumSize = new Vector2(160, 0);
            row.AddChild(WithLabel("select (styled popup)", sel));
            row.AddChild(chosen);
            col.AddChild(row);
        }

        // ── dialog (DR27) — modal over a scrim, focus-trapped ──
        private void BuildDialog(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("dialog (DR27) — modal over a scrim, focus-trap, Esc, explicit destructive", ThemeTokens.Tlg));
            var row = Row(12);

            var open = ChimeraComponents.Button("Open dialog", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            open.Pressed += () =>
            {
                var d = ChimeraDialog.Create("Confirm action", "Apply these changes to the unit definition?");
                d.AddCancel("Cancel");
                d.AddConfirm("Apply");
                d.Open(this);
            };

            var leave = ChimeraComponents.Button("Leave match? (danger)", ChimeraComponents.ButtonVariant.Danger, ChimeraComponents.ButtonSize.Sm);
            leave.Pressed += () =>
            {
                var d = ChimeraDialog.Create("Leave match?", "Leaving now forfeits the match. This cannot be undone.");
                d.AddCancel("Stay");
                d.AddConfirm("Forfeit & Leave", danger: true);
                d.Open(this);
            };

            row.AddChild(open);
            row.AddChild(leave);
            col.AddChild(row);
        }

        // ── toast (DR28/64) — slide-in variants + banner-stall ──
        private void BuildToasts(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("toast (DR28) — slide-in + auto-dismiss (semantic bars) + banner-stall", ThemeTokens.Tlg));
            var row = Row(10);
            var def = ChimeraComponents.Button("Toast (default)", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            def.Pressed += () => _toasts.Show("Upgrade complete", "Iron Plating researched.", ChimeraToastHost.ToastVariant.Default, 8f);
            var danger = ChimeraComponents.Button("Toast (danger)", ChimeraComponents.ButtonVariant.Danger, ChimeraComponents.ButtonSize.Sm);
            danger.Pressed += () => _toasts.Show("Under attack!", "Your base at grid C-4 is taking damage.", ChimeraToastHost.ToastVariant.Danger, 6f);
            var warn = ChimeraComponents.Button("Toast (warn)", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            warn.Pressed += () => _toasts.Show("Low supply", "Build more farms to raise the cap.", ChimeraToastHost.ToastVariant.Warn, 6f);
            var ok = ChimeraComponents.Button("Toast (ok)", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            ok.Pressed += () => _toasts.Show("Saved", "Unit definition written.", ChimeraToastHost.ToastVariant.Ok, 5f);
            row.AddChild(def);
            row.AddChild(danger);
            row.AddChild(warn);
            row.AddChild(ok);
            col.AddChild(row);

            var stallRow = Row();
            stallRow.AddChild(ChimeraToastHost.StallBanner());
            col.AddChild(stallRow);
        }

        // ═══════════════════════ 3.1b sections (reused verbatim from ComponentPreview) ═══════════════════════

        private void BuildPanels(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("panel (DR13) — faceted cut-8; variants + cut-lg", ThemeTokens.Tlg));
            var row = Row(16);
            row.AddChild(FilledPanel(ChimeraComponents.Panel(), "panel (surface-1) + shadow-1"));
            row.AddChild(FilledPanel(ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Surface2), "panel --2"));
            row.AddChild(FilledPanel(ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Flat), "panel --flat"));
            row.AddChild(FilledPanel(ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Accent), "panel --accent"));
            col.AddChild(row);
        }

        private void BuildButtons(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("btn (DR14) + icon-btn (DR15)", ThemeTokens.Tlg));
            var variants = Row(10);
            variants.AddChild(ChimeraComponents.Button("Primary", ChimeraComponents.ButtonVariant.Primary));
            variants.AddChild(ChimeraComponents.Button("Secondary", ChimeraComponents.ButtonVariant.Secondary));
            variants.AddChild(ChimeraComponents.Button("Ghost", ChimeraComponents.ButtonVariant.Ghost));
            variants.AddChild(ChimeraComponents.Button("Danger", ChimeraComponents.ButtonVariant.Danger));
            col.AddChild(variants);
            var icons = Row(10);
            icons.AddChild(ChimeraComponents.IconButton("⚙"));
            icons.AddChild(ChimeraComponents.IconButton("▶", isActive: true));
            icons.AddChild(ChimeraComponents.IconButton("✎"));
            col.AddChild(icons);
        }

        private void BuildReadouts(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("chip/readout/tag (DR17/18/19) + kbd (DR16, the sole ROUND element)", ThemeTokens.Tlg));
            var chips = Row(10);
            chips.AddChild(ChimeraComponents.Chip("128"));
            chips.AddChild(ChimeraComponents.Chip("64", "ore"));
            col.AddChild(chips);
            var readouts = Row(24);
            readouts.AddChild(ChimeraComponents.Readout(_theme.GetColor(ThemeTokens.Ok, ThemeTokens.Type), "1240", "supply"));
            readouts.AddChild(ChimeraComponents.Readout(_theme.GetColor(ThemeTokens.Warn, ThemeTokens.Type), "087", "upkeep"));
            col.AddChild(readouts);
            var tags = Row(8);
            tags.AddChild(ChimeraComponents.Tag("Neutral"));
            tags.AddChild(ChimeraComponents.Tag("Locked", ChimeraComponents.TagVariant.Lock));
            tags.AddChild(ChimeraComponents.Tag("Ready", ChimeraComponents.TagVariant.Ok));
            tags.AddChild(ChimeraComponents.Tag("Accent", ChimeraComponents.TagVariant.Accent));
            tags.AddChild(ChimeraComponents.Tag("Error", ChimeraComponents.TagVariant.Danger));
            var keys = Row(6);
            var shortcutBody = ChimeraComponents.Body("shortcut:", ThemeTokens.TextMid);
            shortcutBody.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            keys.AddChild(shortcutBody);
            keys.AddChild(ChimeraComponents.Kbd("Ctrl"));
            keys.AddChild(ChimeraComponents.Kbd("A"));
            tags.AddChild(keys);
            col.AddChild(tags);
        }

        private void BuildProgress(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("progress (DR20) — accent / --ok / --xp", ThemeTokens.Tlg));
            col.AddChild(ProgressRow("accent", ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Default, 62)));
            col.AddChild(ProgressRow("--ok", ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Ok, 80)));
            col.AddChild(ProgressRow("--xp", ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Xp, 40)));
        }

        private void BuildInputs(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("input (DR22) + num-input (DR32)", ThemeTokens.Tlg));
            var fields = new VBoxContainer();
            fields.AddThemeConstantOverride("separation", 4);
            fields.AddChild(ChimeraComponents.FieldLabel("Unit name"));
            var input = ChimeraComponents.Input("e.g. Rebel Alchemist");
            input.CustomMinimumSize = new Vector2(260, 0);
            fields.AddChild(input);
            col.AddChild(fields);
            var row = Row(16);
            row.AddChild(WithLabel("damage (num-input)", ChimeraComponents.NumInput(42, 0, 999, 1)));
            col.AddChild(row);
        }

        private void BuildSlider(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("slider (DR21) — track + accent thumb, paired num-input", ThemeTokens.Tlg));
            var slider = ChimeraSlider.Create(35, 0, 100, 1);
            slider.CustomMinimumSize = new Vector2(320, 0);
            var wrap = Row();
            wrap.AddChild(slider);
            col.AddChild(wrap);
        }

        private void BuildTabs(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("tabs (DR24) — underline / --boxed / segment", ThemeTokens.Tlg));
            var row = Row(24);
            row.AddChild(ChimeraTabs.Create(ChimeraComponents.TabsVariant.Underline, "Overview", "Stats", "Abilities"));
            row.AddChild(ChimeraTabs.Create(ChimeraComponents.TabsVariant.Boxed, "One", "Two", "Three"));
            row.AddChild(ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced"));
            col.AddChild(row);
        }

        private void BuildListRows(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("list-row (DR25) — hover / selected / locked, single-select", ThemeTokens.Tlg));
            var list = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
            list.AddThemeConstantOverride("separation", 4);
            var group = new ListRowGroup();
            var r1 = ChimeraListRow.Create("Rebel Alchemist", group);
            var r2 = ChimeraListRow.Create("Homunculus Brute", group);
            var r3 = ChimeraListRow.Create("Sealed Unit (locked)", group);
            group.Select(r2);
            r3.SetLocked(true);
            list.AddChild(r1);
            list.AddChild(r2);
            list.AddChild(r3);
            col.AddChild(list);
        }

        private void BuildLiveNumbers(VBoxContainer col)
        {
            col.AddChild(ChimeraComponents.Heading("live numbers (UX-DR34) — mono tabular, no column jitter", ThemeTokens.Tlg));
            var tnum = _theme.GetFont(ThemeTokens.MonoTnum, ThemeTokens.Type);
            _liveCounter = new Label { Text = "000000" };
            _liveCounter.AddThemeFontOverride("font", tnum);
            _liveCounter.AddThemeFontSizeOverride("font_size", 23);
            _liveCounter.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            col.AddChild(_liveCounter);
        }

        // ── Helpers (mirrors ComponentPreview) ──
        private HBoxContainer Row(int sep = 12)
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", sep);
            return h;
        }

        private Label Divider(string text)
        {
            var l = new Label { Text = $"— {text} —" };
            l.AddThemeFontOverride("font", ChimeraComponents.DisplayTracked(1));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txl, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.AccentBright, ThemeTokens.Type));
            ChimeraComponents.BindAccentColor(l, ThemeTokens.AccentBright);
            return l;
        }

        private Control FilledPanel(PanelContainer panel, string caption)
        {
            panel.CustomMinimumSize = new Vector2(170, 64);
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

        private Control WithCaption(Control art, string caption)
        {
            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", 4);
            art.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            v.AddChild(art);
            var c = ChimeraComponents.Body(caption, ThemeTokens.TextMid);
            c.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            c.HorizontalAlignment = HorizontalAlignment.Center;
            v.AddChild(c);
            return v;
        }
    }
}
