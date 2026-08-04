#nullable enable
using Godot;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "MainMenu" phase (runtime position 20). Creates the title-screen overlay (shown on first launch)
    /// and wires its buttons — Play Skirmish / Create / Browse / Generate Map / Settings / Quit — to mode toggles
    /// and the other UI panels. Publishes ctx.MainMenu. Behavior-identical to MainScene.SetupMainMenu.
    /// </summary>
    public sealed class MainMenuPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public MainMenuPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "MainMenu";

        public void Run()
        {
            _ctx.MainMenu = new MainMenuOverlay();
            _ctx.Scene.AddChild(_ctx.MainMenu);
            _ctx.MainMenu.Initialize(version: "0.1-alpha");

            // Story 11.1: the real skirmish setup screen, constructed here (hidden) so both the "Play" button and the
            // boot-failure fail-safe re-open can drive the same overlay. Back re-shows the title screen.
            _ctx.SkirmishSetup = new SkirmishSetupOverlay();
            _ctx.Scene.AddChild(_ctx.SkirmishSetup);
            _ctx.SkirmishSetup.Initialize(_ctx.Scene, onBack: () => { if (_ctx.MainMenu != null) _ctx.MainMenu.Visible = true; });

            _ctx.MainMenu.OnPlaySkirmish += () =>
            {
                // Story 11.1: "Play" now opens the skirmish setup screen (map/faction/team/AI selection) instead of
                // launching straight into the hardcoded scenario. The menu is already hidden by the overlay's own
                // Visible=false; the setup screen's Launch builds an in-memory ScenarioData and hands it to the
                // existing PendingGeneratedScenario + ReloadCurrentScene boot path.
                _ctx.SkirmishSetup.Open();
            };

            // DW-465: cold-boot Load Game — pick a save slot (lenient header rows), then run the fail-closed
            // cold-boot plan (parse → rebuild scenario from the header's launch record → hash gates → relaunch).
            // A reject stays on this menu with a located toast. GameOverOverlayPhase built ctx.SaveStore earlier.
            _ctx.MainMenu.OnLoadGame += () =>
                _ctx.MainMenu!.OpenLoadPicker(_ctx.SaveStore, slot => _ctx.Scene.LoadSaveFromMenu(slot));

            // Story 9.7: the Multiplayer destination — un-defers the honesty-gated slot. Opens the rebuilt N-slot
            // lobby (Direct LAN/IP + Nakama matchmaking). Replaces the dev-only Edit-mode `N` keybind as the entry.
            _ctx.MainMenu.OnMultiplayer += () => OpenFromMenu(_ctx.LobbyUi, () => _ctx.LobbyUi.Visible, needsEditMode: false, () => _ctx.LobbyUi.Show());

            _ctx.MainMenu.OnCreate += () =>
            {
                // Ensure we're in Edit mode.
                if (_ctx.GameState.Mode != GameMode.Edit)
                    _ctx.GameState.Toggle();
            };

            _ctx.MainMenu.OnBrowse += () =>
                // Edit mode so the browser opens correctly — restored on close by OpenFromMenu.
                OpenFromMenu(_ctx.ContentBrowser, () => _ctx.ContentBrowser.Visible, needsEditMode: true, () => _ctx.ContentBrowser.ToggleVisible());

            // Story 9.11: the Replays destination — opens the replay browser (also reachable via the Edit-mode N
            // hotkey). ReplayBrowserPhase runs before MainMenuPhase, so ctx.ReplayBrowser already exists here.
            _ctx.MainMenu.OnReplays += () =>
                OpenFromMenu(_ctx.ReplayBrowser, () => _ctx.ReplayBrowser.Visible, needsEditMode: true, () => _ctx.ReplayBrowser.ToggleVisible());

            _ctx.MainMenu.OnGenerateMap += () =>
                // Rooted on a Control, not a CanvasLayer — OpenFromMenu connects visibility_changed by name.
                OpenFromMenu(_ctx.MapGenPanel?.PanelRoot, () => _ctx.MapGenPanel!.PanelRoot.Visible,
                             needsEditMode: true, () => _ctx.MapGenPanel.Toggle());

            _ctx.MainMenu.OnSettings += () =>
                OpenFromMenu(_ctx.SettingsPanel, () => _ctx.SettingsPanel.Visible, needsEditMode: false, () => _ctx.SettingsPanel.ToggleVisible());

            _ctx.MainMenu.OnQuit += () => _ctx.Scene.GetTree().Quit();

            GD.Print("[MainMenu] Initialized — showing title screen.");
        }

        // ── Return-to-menu contract (reported by Alec, 2026-08-04) ───────────────────────────────────────────
        //
        // The overlay hides itself the moment a destination button is clicked, and every destination panel's
        // close was a bare `Visible = false`. Nothing re-showed the menu, so exiting Browse / Multiplayer /
        // Replays dropped the player into the live scene with no way back — "if I select Browse, then exit, it
        // plays the game". Browse/Replays additionally toggle into Edit mode to open, which is why the map
        // editor was what showed through.
        //
        // Two deliberate choices. (1) Hook `visibility_changed` instead of each panel's close BUTTON: these
        // panels hide from several paths (close button, Escape, load-and-close at ContentBrowserPanel.cs:397)
        // and a path added later is covered for free — instrumenting buttons would silently miss them, which is
        // the same "correct for what it knew about" failure that produced DW-609. (2) Scope it with a flag so
        // only a MENU-opened visit returns: the Edit-mode hotkey entries into the replay browser and lobby must
        // still close silently rather than summoning a title screen mid-session.
        //
        // Mirrors the SkirmishSetupOverlay `onBack` convention already used above.

        private readonly System.Collections.Generic.HashSet<Node> _returnArmed = new();
        private readonly System.Collections.Generic.HashSet<Node> _awaitingReturn = new();
        private GameMode? _modeBeforeDestination;

        /// <summary>
        /// Open a title-screen destination so that closing it comes BACK to the title screen, restoring the game
        /// mode this opened. <paramref name="panel"/> may be null (a phase that did not publish it yet) — the
        /// destination still opens, it just does not return. Arming is lazy so ordering between phases cannot
        /// leave a panel unwired.
        /// </summary>
        /// <param name="panel">A <see cref="CanvasLayer"/> or a <see cref="Control"/> root — both emit
        /// <c>visibility_changed</c>, so it is connected by NAME rather than through a shared base type (they
        /// have none that declares it).</param>
        /// <param name="isVisible">Reads that panel's current visibility.</param>
        private void OpenFromMenu(Node? panel, System.Func<bool>? isVisible, bool needsEditMode, System.Action open)
        {
            if (panel != null && isVisible != null)
            {
                if (_returnArmed.Add(panel))
                    panel.Connect("visibility_changed",
                        Callable.From(() => OnDestinationVisibilityChanged(panel, isVisible)));
                _awaitingReturn.Add(panel);
            }

            _modeBeforeDestination = _ctx.GameState?.Mode;
            if (needsEditMode && _ctx.GameState != null && _ctx.GameState.Mode != GameMode.Edit)
                _ctx.GameState.Toggle();

            open();
        }

        /// <summary>Re-show the title screen when a menu-opened destination hides. Ignores the show half of the
        /// signal, and any hide the menu did not open.</summary>
        private void OnDestinationVisibilityChanged(Node panel, System.Func<bool> isVisible)
        {
            if (isVisible()) return;
            if (!_awaitingReturn.Remove(panel)) return;

            // GameMode is binary (Edit/Play), so Toggle() is the restore.
            if (_modeBeforeDestination is GameMode prior && _ctx.GameState != null && _ctx.GameState.Mode != prior)
                _ctx.GameState.Toggle();
            _modeBeforeDestination = null;

            if (_ctx.MainMenu != null) _ctx.MainMenu.Visible = true;
        }
    }
}
