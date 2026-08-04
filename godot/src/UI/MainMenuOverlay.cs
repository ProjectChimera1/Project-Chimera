#nullable enable
using Godot;
using System;
using ProjectChimera.Core.Persistence; // ISaveStore, SaveGameHeader, LocalSaveStore (DW-465 load picker metadata)
using ProjectChimera.UI.Components; // ChimeraComponents, ChimeraMark, ChimeraTooltip, ChimeraDialog
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
    /// live online count. Story 9.7 UN-DEFERS the Multiplayer destination (the lobby is now real: N-slot
    /// LAN/matchmaking), so it is a legitimate entry; Campaign/Tutorial stay owned by Story 13.1, the final
    /// honesty sweep by 11.12. Skirmish is offline (vs AI, 1–4 players).
    ///
    /// Modes:
    ///   Play        — enter Play mode immediately with the current scenario (offline, vs AI).
    ///   Multiplayer — open the multiplayer lobby (Story 9.7: Direct LAN/IP + Nakama matchmaking, N-slot).
    ///   Create      — enter Edit mode (map/scenario editor).
    ///   Browse      — open ContentBrowserPanel to load a community map.
    ///   Generate Map (AI) — auxiliary editor entry (kept reachable, off the primary five).
    ///   Settings    — toggle the SettingsPanel.
    ///   Quit        — exit the application.
    ///
    /// Usage (MainMenuPhase): new MainMenuOverlay(); AddChild(...); Initialize(version); wire the events.
    /// </summary>
    public partial class MainMenuOverlay : CanvasLayer
    {
        // ── Events (public contract — preserved verbatim from the pre-restyle overlay) ──────────

        public event Action? OnPlaySkirmish;
        public event Action? OnLoadGame; // DW-465 — open the cold-boot load-save picker
        public event Action? OnMultiplayer; // Story 9.7 — open the multiplayer lobby
        public event Action? OnCreate;
        public event Action? OnBrowse;
        public event Action? OnReplays; // Story 9.11 — open the replay browser
        public event Action? OnGenerateMap;
        public event Action? OnSettings;
        public event Action? OnQuit;

        // ── Kit context ──

        private GodotTheme        _theme  = null!;

        // ── State ─────────────────────────────────────────────────────────────

        private Label _versionLabel = null!;

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>Build the menu UI from the shared Theme/kit.</summary>
        /// <param name="version">Version/build string shown in the footer, e.g. "0.1-alpha".</param>
        public void Initialize(string version = "0.1")
        {
            Layer   = 20; // topmost — above everything
            Visible = true;

            _theme = ChimeraComponents.EnsureInitialized(this); // MUST run before any ChimeraComponents.* call, or the factory throws

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
            var wordmark = ChimeraComponents.Heading("PROJECT CHIMERA", ThemeTokens.T4xl);
            wordmark.HorizontalAlignment = HorizontalAlignment.Center;
            col.AddChild(wordmark);

            var tagline = ChimeraComponents.Body("Build the game. Then play it.", ThemeTokens.TextMid, ThemeTokens.Tlg);
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

            AddNavButton(nav, "Load Game", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Load Game",
                "Resume a saved single-player match from any save slot.",
                () => OnLoadGame?.Invoke()); // does NOT close the menu — the picker overlays it; a reject stays here

            AddNavButton(nav, "Multiplayer", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Multiplayer",
                "Play against other people — Direct LAN/IP or online matchmaking (up to 4 players).",
                () => { Visible = false; OnMultiplayer?.Invoke(); });

            AddNavButton(nav, "Create", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Create",
                "Open the map editor to build and test your own scenarios.",
                () => { Visible = false; OnCreate?.Invoke(); });

            AddNavButton(nav, "Browse", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Browse",
                "Download and play maps shared by other creators via mod.io.",
                () => { Visible = false; OnBrowse?.Invoke(); });

            AddNavButton(nav, "Replays", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Lg,
                "Replays",
                "Browse, watch, rename, and delete your recorded matches.",
                () => { Visible = false; OnReplays?.Invoke(); });

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

        // ── DW-465: the cold-boot load-save picker ─────────────────────────────

        /// <summary>A live load picker dialog — one at a time (mirrors InMatchMenuOverlay's _activeDialog guard).</summary>
        private ChimeraDialog? _activeLoadDialog;

        /// <summary>
        /// DW-465 — the main-menu Load Game slot picker (mirrors <c>InMatchMenuOverlay.OpenSlotPicker</c>'s load
        /// mode): lists the manual slots + the autosave slot with lenient <see cref="SaveGameHeader"/>
        /// metadata rows; only readable slots are choosable. Choosing one fires <paramref name="onPick"/> with the
        /// slot name — the scene then runs the fail-closed cold-boot plan (a reject stays on this menu with a toast).
        /// </summary>
        public void OpenLoadPicker(ISaveStore? saveStore, Action<string> onPick)
        {
            if (_activeLoadDialog != null) return; // one dialog at a time

            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            string[] slots = { "0", "1", "2", LocalSaveStore.AutosaveSlot };

            ChimeraDialog? dlg = null;
            bool any = false;
            foreach (string slot in slots)
            {
                SaveGameHeader hdr = saveStore != null
                    ? SaveGameHeader.Read(saveStore.PathFor(slot))
                    : SaveGameHeader.Unreadable();
                string label = slot == LocalSaveStore.AutosaveSlot ? "Autosave" : $"Slot {slot}";
                string meta  = hdr.IsReadable ? $"{label}  —  {hdr.MapId}  ·  tick {hdr.Tick}" : $"{label}  —  no save";
                var b = ChimeraComponents.Button(meta, ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Block);
                b.Disabled = !hdr.IsReadable; // only choosable when a save exists
                any |= hdr.IsReadable;
                string captured = slot;
                b.Pressed += () =>
                {
                    _activeLoadDialog = null;
                    if (dlg != null && GodotObject.IsInstanceValid(dlg)) dlg.QueueFree();
                    onPick(captured);
                };
                body.AddChild(b);
            }

            if (!any)
            {
                var none = ChimeraComponents.Body("No saved games yet — save from the in-match menu during a skirmish.",
                                                  ThemeTokens.TextMid);
                body.AddChild(none);
            }

            dlg = ChimeraDialog.CreateCustom("Load Game", body);
            dlg.AddCancel("Cancel");
            dlg.Dismissed += () => { _activeLoadDialog = null; };
            _activeLoadDialog = dlg;
            dlg.Open(this);
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

    }
}
