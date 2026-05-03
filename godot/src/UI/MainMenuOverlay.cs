#nullable enable
using Godot;
using System;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Full-screen main menu shown when the game first launches.
    /// Dismissed when the player chooses a game mode.
    ///
    /// Modes:
    ///   Skirmish  — enter Play mode immediately with the current scenario.
    ///   Create    — enter Edit mode (map/scenario editor).
    ///   Browse    — open ContentBrowserPanel to load a community map.
    ///   Replay    — open system file picker to choose a .chmr replay file (stub).
    ///   Settings  — toggle the SettingsPanel.
    ///   Quit      — exit the application.
    ///
    /// Usage (MainScene._Ready, before other setup):
    ///   _mainMenu = new MainMenuOverlay();
    ///   AddChild(_mainMenu);
    ///   _mainMenu.Initialize(version: "0.1-alpha");
    ///   _mainMenu.OnPlaySkirmish  += () => { /* enter Play mode */ };
    ///   _mainMenu.OnCreate        += () => { /* enter Edit mode */ };
    ///   _mainMenu.OnBrowse        += () => { _contentBrowser.ToggleVisible(); };
    ///   _mainMenu.OnSettings      += () => { _settingsPanel.ToggleVisible(); };
    ///   _mainMenu.OnQuit          += () => GetTree().Quit();
    /// </summary>
    public partial class MainMenuOverlay : CanvasLayer
    {
        // ── Events ────────────────────────────────────────────────────────────

        public event Action? OnPlaySkirmish;
        public event Action? OnCreate;
        public event Action? OnBrowse;
        public event Action? OnGenerateMap;
        public event Action? OnSettings;
        public event Action? OnQuit;

        // ── State ─────────────────────────────────────────────────────────────

        private Label _versionLabel = null!;

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>Build the menu UI.</summary>
        /// <param name="version">Version string shown in the lower-right corner, e.g. "0.1-alpha".</param>
        public void Initialize(string version = "0.1")
        {
            Layer   = 20; // topmost — above everything
            Visible = true;

            // ── Dark full-screen backdrop ─────────────────────────────────────
            var root = new ColorRect
            {
                Color = new Color(0.04f, 0.04f, 0.07f, 1f),
            };
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(root);

            // ── Grid background pattern (subtle) ─────────────────────────────
            // A thin grid shader gives the feel of the game's terrain grid.
            var gridRect = new ColorRect
            {
                Color = new Color(0.12f, 0.16f, 0.24f, 0.08f),
            };
            gridRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(gridRect);

            // ── Centered layout container ─────────────────────────────────────
            var center = new VBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center,
            };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            center.AddThemeConstantOverride("separation", 18);
            root.AddChild(center);

            // Left-margin spacer for visual balance.
            var innerMargin = new MarginContainer();
            innerMargin.AddThemeConstantOverride("margin_left",  0);
            innerMargin.AddThemeConstantOverride("margin_right", 0);
            center.AddChild(innerMargin);

            var innerVbox = new VBoxContainer();
            innerVbox.Alignment = BoxContainer.AlignmentMode.Center;
            innerVbox.AddThemeConstantOverride("separation", 16);
            innerVbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            center.AddChild(innerVbox);

            // ── Title ─────────────────────────────────────────────────────────
            var title = new Label
            {
                Text                = "PROJECT CHIMERA",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            title.AddThemeFontSizeOverride("font_size", 52);
            title.AddThemeColorOverride("font_color", new Color(0.75f, 0.88f, 1.0f));
            innerVbox.AddChild(title);

            var subtitle = new Label
            {
                Text                = "RTS CREATION PLATFORM",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            subtitle.AddThemeFontSizeOverride("font_size", 16);
            subtitle.AddThemeColorOverride("font_color", new Color(0.45f, 0.58f, 0.75f));
            innerVbox.AddChild(subtitle);

            // Spacer.
            innerVbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 30) });

            // ── Menu buttons ──────────────────────────────────────────────────
            var btnVbox = new VBoxContainer();
            btnVbox.AddThemeConstantOverride("separation", 10);
            btnVbox.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            innerVbox.AddChild(btnVbox);

            AddMenuButton(btnVbox, "Play Skirmish",
                "Load the current map and start a game against the AI.",
                isPrimary: true,
                onPress: () =>
                {
                    Visible = false;
                    OnPlaySkirmish?.Invoke();
                });

            AddMenuButton(btnVbox, "Create / Editor",
                "Open the map editor to build and test your own scenarios.",
                isPrimary: false,
                onPress: () =>
                {
                    Visible = false;
                    OnCreate?.Invoke();
                });

            AddMenuButton(btnVbox, "Browse Community Maps",
                "Download and play maps shared by other creators via mod.io.",
                isPrimary: false,
                onPress: () =>
                {
                    Visible = false;
                    OnBrowse?.Invoke();
                });

            AddMenuButton(btnVbox, "Generate Map (AI)",
                "Describe a map concept in plain English and let Claude build it.",
                isPrimary: false,
                onPress: () =>
                {
                    Visible = false;
                    OnGenerateMap?.Invoke();
                });

            AddMenuButton(btnVbox, "Settings",
                "Adjust camera speed, audio volumes, accessibility options.",
                isPrimary: false,
                onPress: () => OnSettings?.Invoke()); // does NOT close menu

            // Separator.
            btnVbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

            AddMenuButton(btnVbox, "Quit",
                "",
                isPrimary: false,
                isDangerous: true,
                onPress: () => OnQuit?.Invoke());

            // ── Version label (bottom-right corner) ───────────────────────────
            _versionLabel = new Label { Text = $"v{version}" };
            _versionLabel.AddThemeFontSizeOverride("font_size", 12);
            _versionLabel.AddThemeColorOverride("font_color", new Color(0.35f, 0.35f, 0.4f));
            _versionLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
            _versionLabel.OffsetRight  = -16f;
            _versionLabel.OffsetBottom = -12f;
            _versionLabel.OffsetLeft   = -120f;
            _versionLabel.OffsetTop    = -32f;
            _versionLabel.HorizontalAlignment = HorizontalAlignment.Right;
            root.AddChild(_versionLabel);
        }

        // ── Helper ────────────────────────────────────────────────────────────

        private static void AddMenuButton(VBoxContainer parent, string label, string tooltip,
                                          bool isPrimary, bool isDangerous = false,
                                          Action? onPress = null)
        {
            var btn = new Button
            {
                Text              = label,
                TooltipText       = tooltip,
                CustomMinimumSize = new Vector2(320, isPrimary ? 54 : 46),
            };
            btn.AddThemeFontSizeOverride("font_size", isPrimary ? 18 : 15);

            if (isPrimary)
            {
                btn.AddThemeColorOverride("font_color", new Color(0.1f, 0.1f, 0.15f));
                btn.AddThemeStyleboxOverride("normal", new StyleBoxFlat
                {
                    BgColor                 = new Color(0.45f, 0.72f, 1.0f, 0.9f),
                    CornerRadiusTopLeft     = 8, CornerRadiusTopRight    = 8,
                    CornerRadiusBottomLeft  = 8, CornerRadiusBottomRight = 8,
                    ContentMarginLeft = 20, ContentMarginRight  = 20,
                    ContentMarginTop  = 10, ContentMarginBottom = 10,
                });
                btn.AddThemeStyleboxOverride("hover", new StyleBoxFlat
                {
                    BgColor                 = new Color(0.6f, 0.85f, 1.0f, 1.0f),
                    CornerRadiusTopLeft     = 8, CornerRadiusTopRight    = 8,
                    CornerRadiusBottomLeft  = 8, CornerRadiusBottomRight = 8,
                    ContentMarginLeft = 20, ContentMarginRight  = 20,
                    ContentMarginTop  = 10, ContentMarginBottom = 10,
                });
            }
            else if (isDangerous)
            {
                btn.AddThemeColorOverride("font_color", new Color(0.85f, 0.35f, 0.35f));
            }
            else
            {
                btn.AddThemeColorOverride("font_color", new Color(0.82f, 0.88f, 0.95f));
            }

            if (onPress != null) btn.Pressed += onPress;
            parent.AddChild(btn);
        }
    }
}
