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

            // Story 9.7: the Multiplayer destination — un-defers the honesty-gated slot. Opens the rebuilt N-slot
            // lobby (Direct LAN/IP + Nakama matchmaking). Replaces the dev-only Edit-mode `N` keybind as the entry.
            _ctx.MainMenu.OnMultiplayer += () => _ctx.LobbyUi.Show();

            _ctx.MainMenu.OnCreate += () =>
            {
                // Ensure we're in Edit mode.
                if (_ctx.GameState.Mode != GameMode.Edit)
                    _ctx.GameState.Toggle();
            };

            _ctx.MainMenu.OnBrowse += () =>
            {
                // Ensure Edit mode so the browser opens correctly.
                if (_ctx.GameState.Mode != GameMode.Edit)
                    _ctx.GameState.Toggle();
                _ctx.ContentBrowser.ToggleVisible();
            };

            // Story 9.11: the Replays destination — opens the replay browser (also reachable via the Edit-mode N
            // hotkey). ReplayBrowserPhase runs before MainMenuPhase, so ctx.ReplayBrowser already exists here.
            _ctx.MainMenu.OnReplays += () =>
            {
                if (_ctx.GameState.Mode != GameMode.Edit)
                    _ctx.GameState.Toggle();
                _ctx.ReplayBrowser.ToggleVisible();
            };

            _ctx.MainMenu.OnGenerateMap += () =>
            {
                // Switch to Edit mode and open the map generator panel.
                if (_ctx.GameState.Mode != GameMode.Edit)
                    _ctx.GameState.Toggle();
                _ctx.MapGenPanel.Toggle();
            };

            _ctx.MainMenu.OnSettings += () => _ctx.SettingsPanel.ToggleVisible();

            _ctx.MainMenu.OnQuit += () => _ctx.Scene.GetTree().Quit();

            GD.Print("[MainMenu] Initialized — showing title screen.");
        }
    }
}
