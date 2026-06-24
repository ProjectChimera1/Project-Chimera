#nullable enable
using Godot;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "FlowFieldInit" phase (runtime position 14 — after FactionVisuals, before WinConditionUi).
    /// Initializes the flow field now that all scenario buildings are placed: RebuildObstacles seeds the obstacle
    /// map and Initialize snapshots the initial alive state so FlowFieldBridge's per-frame diff starts clean.
    /// Behavior-identical to the former inline _Ready block (MainScene.InitFlowField).
    /// </summary>
    public sealed class FlowFieldInitPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public FlowFieldInitPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "FlowFieldInit";

        public void Run()
        {
            _ctx.FlowFieldSys.RebuildObstacles(_ctx.Buildings);
            _ctx.FlowFieldBridge.Initialize(_ctx.World, _ctx.FlowFieldSys, _ctx.Buildings);
            GD.Print("[Navigation] FlowFieldBridge initialized — NavServer3D replaced for deterministic pathfinding.");
        }
    }
}
