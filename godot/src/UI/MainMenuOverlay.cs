#nullable enable
using Godot;
using System;
using ProjectChimera.UI.Components; // ChimeraComponents, ChimeraMark, ChimeraTooltip
using ProjectChimera.UI.Theme;       // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;       // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Full-screen main menu shown when the game first launches (Story 3.11 restyle — UX-DR67). Dismissed
    /// when the player chooses a game mode. Drawn entirely from the shared design system (main.tres Theme +
    /// the ChimeraComponents kit): the Chimera seal, a display wordmark, the tagline
    /// "Build the game. Then play it.", the primary nav (Play / Create / Browse / Settings / Quit) from the
    /// themed button component, and a mono version/build footer.
    ///
    /// Honesty invariant (amended UX-DR68): nothing here advertises an unbuilt system — no ranked/MMR, no
    /// live online count, no Multiplayer/Campaign destination. Skirmish is offline (vs AI, 1–4 players).
    /// Multiplayer is owned by Epic 9, Campaign/Tutorial by Story 13.1, the final honesty sweep by 11.12.
    ///
    /// Modes:
    ///   Play      — enter Play mode immediately with the current scenario (offline, vs AI).
    ///   Create    — enter Edit mode (map/scenario editor).
    ///   Browse    — open ContentBrowserPanel to load a community map.
    ///   Generate Map (AI) — auxiliary editor entry (kept reachable, off the primary five).
    ///   Settings  — toggle the SettingsPanel.
    ///   Quit      — exit the application.
    ///
    /// Usage (MainMenuPhase): new MainMenuOverlay(); AddChild(...); Initialize(version); wire the events.
    /// </summary>
    public partial class MainMenuOverlay : CanvasLayer
    {
        // ── Events (public contract — preserved verbatim from the pre-restyle overlay) ──────────

        public event Action? OnPlaySkirmish;
        public event Action? OnCreate;
        public event Action? OnBrowse;
        public event Action? OnGenerateMap;
        public event Action? OnSettings;
        public event Action? OnQuit;

        // ── Kit context (self-owned; _accent only created when this overlay is the first kit consumer) ──

        private GodotTheme        _theme  = null!;
        private AccentController?  _accent;

        // ── State ─────────────────────────────────────────────────────────────

        private Label _versionLabel = null!;

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>Build the menu UI from the shared Theme/kit.</summary>
        /// <param name="version">Version/build string shown in the footer, e.g. "0.1-alpha".</param>
        public void Initialize(string version = "0.1")
        {
            Layer   = 20; // topmost — above everything
            Visible = true;

            EnsureKitInitialized(); // MUST run before any ChimeraComponents.* call, or the factory throws

            // ── Anchor root (a CanvasLayer has no Theme — apply it on the root Control, which propagates) ──
            var root = new Control();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Stop; // eat clicks so nothing behind the title reacts
            root.Theme = _theme;
            AddChild(root);

            // ── Void backdrop (surface token, not a hardcoded color) ──────────
            var backdrop = new ColorRect { Color = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type) };
            backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            backdrop.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(backdrop);

            // ── Centered brand + nav column ───────────────────────────────────
            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(center);

            var col = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            col.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S4));
            center.AddChild(col);

            // Chimera seal.
            var mark = ChimeraMark.Create(96);
            mark.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            col.AddChild(mark);

            // Wordmark (display font) + tagline (body).
            var wordmark = Heading("PROJECT CHIMERA", ThemeTokens.T4xl);
            wordmark.HorizontalAlignment = HorizontalAlignment.Center;
            col.AddChild(wordmark);

            var tagline = Body("Build the game. Then play it.", ThemeTokens.TextMid, ThemeTokens.Tlg);
            tagline.HorizontalAlignment = HorizontalAlignment.Center;
            col.AddChild(tagline);

            // Spacer between brand and nav.
            col.AddChild(new Control { CustomMinimumSize = new Vector2(0, ChimeraComponents.Const(ThemeTokens.S4)) });

            // ── Primary nav — Play / Create / Browse / Settings / Quit (UX-DR67) ──
            var nav = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
            nav.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2) + 2);
            col.AddChild(nav);

            AddNavButton(nav, "Play", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Lg,
                "Play Skirmish",
                "Load the current map and start an offline match against the AI (1–4 players).",
                () => { Visible = false; OnPlaySkirmish?.Invoke(); });

            AddNavButton(nav, "Create", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Create",
                "Open the map editor to build and test your own scenarios.",
                () => { Visible = false; OnCreate?.Invoke(); });

            AddNavButton(nav, "Browse", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Browse",
                "Download and play maps shared by other creators via mod.io.",
                () => { Visible = false; OnBrowse?.Invoke(); });

            AddNavButton(nav, "Settings", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Settings",
                "Adjust gameplay, audio, and accessibility options.",
                () => OnSettings?.Invoke()); // does NOT close the menu

            // Auxiliary editor entry — kept reachable, off the primary five (ghost, smaller).
            var auxSep = new Control { CustomMinimumSize = new Vector2(0, ChimeraComponents.Const(ThemeTokens.S1)) };
            nav.AddChild(auxSep);

            AddNavButton(nav, "Generate Map (AI)", ChimeraComponents.ButtonVariant.Ghost, ChimeraComponents.ButtonSize.Default,
                "Generate Map (AI)",
                "Describe a map concept in plain English and let Claude build it.",
                () => { Visible = false; OnGenerateMap?.Invoke(); });

            var quitSep = new Control { CustomMinimumSize = new Vector2(0, ChimeraComponents.Const(ThemeTokens.S1)) };
            nav.AddChild(quitSep);

            AddNavButton(nav, "Quit", ChimeraComponents.ButtonVariant.Danger, ChimeraComponents.ButtonSize.Lg,
                "Quit",
                "Exit Project Chimera.",
                () => OnQuit?.Invoke());

            // ── Version/build footer (mono, text-lo, lower-right) ─────────────
            _versionLabel = new Label { Text = $"v{version}" };
            _versionLabel.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontMono, ThemeTokens.Type));
            _versionLabel.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txs, ThemeTokens.Type));
            _versionLabel.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextLo, ThemeTokens.Type));
            _versionLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
            _versionLabel.OffsetRight  = -16f;
            _versionLabel.OffsetBottom = -12f;
            _versionLabel.OffsetLeft   = -160f;
            _versionLabel.OffsetTop    = -32f;
            _versionLabel.HorizontalAlignment = HorizontalAlignment.Right;
            _versionLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(_versionLabel);
        }

        // ── Kit bootstrap (mirrors HeroPickerOverlay.EnsureKitInitialized) ─────────────────

        private void EnsureKitInitialized()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void AddNavButton(VBoxContainer parent, string label,
                                  ChimeraComponents.ButtonVariant variant, ChimeraComponents.ButtonSize size,
                                  string tipTerm, string tipBody, Action onPress)
        {
            var btn = ChimeraComponents.Button(label, variant, size);
            btn.CustomMinimumSize = new Vector2(340, 0);
            btn.Pressed += onPress;
            // Hover-AND-keyboard-focus tooltip (UX-DR53). A Button is already a focus + hover target.
            ChimeraTooltip.Attach(btn, tipTerm, tipBody, ChimeraTooltip.TooltipRole.Field);
            parent.AddChild(btn);
        }

        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            return l;
        }

        private Label Body(string text, StringName colorToken, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontUi, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(colorToken, ThemeTokens.Type));
            return l;
        }
    }
}
