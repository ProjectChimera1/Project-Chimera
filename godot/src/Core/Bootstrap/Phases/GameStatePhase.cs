#nullable enable
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "GameState" phase (runtime position 3). Creates the <see cref="Core.GameState"/> node (the
    /// Edit/Play mode owner) and publishes it on the context — consumed by the input/process routing in
    /// MainScene and by Camera, TerrainBrush, WinConditionUi, MainMenu, TriggerEditor, MapGenerator, and the
    /// match lifecycle. Behavior-identical to the former MainScene.SetupGameState.
    /// </summary>
    public sealed class GameStatePhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public GameStatePhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "GameState";

        public void Run()
        {
            var gameState = new GameState();
            _ctx.Scene.AddChild(gameState);
            _ctx.GameState = gameState;
        }
    }
}
