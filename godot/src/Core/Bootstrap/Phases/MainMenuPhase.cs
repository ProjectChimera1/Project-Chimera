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

            _ctx.MainMenu.OnPlaySkirmish += () =>
            {
                // Enter Play mode immediately with whatever scenario is loaded.
                if (_ctx.GameState.Mode != GameMode.Play)
                    _ctx.GameState.Toggle();
            };

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
