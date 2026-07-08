#nullable enable
using Godot;
using ProjectChimera.CreationSuite; // ItemCardPanel

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 3.16 "ItemCard" phase — mirrors <see cref="UnitCardPhase"/>. Constructs the code-built
    /// <see cref="ItemCardPanel"/>, adds it to the scene, initializes it against the item directory
    /// (<see cref="MainScene.ITEMS_DIR"/>), and publishes it on <see cref="SceneContext"/>. The Edit-mode open hotkey
    /// (G — I is reserved for in-match Inventory, K for the Ability editor) is bound in <c>MainScene._UnhandledInput</c>.
    /// </summary>
    public sealed class ItemCardPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public ItemCardPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "ItemCard";

        public void Run()
        {
            _ctx.ItemCardPanel = new ItemCardPanel();
            _ctx.Scene.AddChild(_ctx.ItemCardPanel);
            _ctx.ItemCardPanel.Initialize(MainScene.ITEMS_DIR, _ctx.GameState);
            GD.Print("[ItemCard] Initialized — press G in Edit mode to open the item editor.");
        }
    }
}
