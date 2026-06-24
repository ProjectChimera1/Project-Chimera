#nullable enable
using Godot;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Camera" phase (runtime position 7). Builds the RTS camera controller (seeded from persisted
    /// settings), the EntityPlacer, the SelectionSystem (wired to the deterministic flow-field bridge), and the
    /// CommandCardSystem. Publishes Cam / Placer / Selection / CommandCard on the context. Runs after Settings
    /// (reads camera prefs) and Navigation (selection needs the flow-field bridge). MainScene retains the
    /// MoveStartPosition and EnterBuildPlacementMode bridges (they touch the scenario / build-placement state it
    /// keeps). Behavior-identical to MainScene.SetupCamera.
    /// </summary>
    public sealed class CameraPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public CameraPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Camera";

        public void Run()
        {
            var cam = new RtsCameraController();
            cam.Position = Vector3.Zero;

            // Seed camera properties from persisted settings before first frame.
            var s = _ctx.SettingsMgr.Current;
            cam.PanSpeedMultiplier  = s.CameraSpeed;
            cam.ZoomSpeedMultiplier = s.CameraZoomSpeed;
            cam.EdgeScrollEnabled   = s.EdgeScrollEnabled;

            _ctx.Scene.AddChild(cam);
            _ctx.Cam = cam;

            var placer = new EntityPlacer();
            _ctx.Scene.AddChild(placer);
            placer.Initialize(cam, _ctx.World, _ctx.Nodes, _ctx.Resources, _ctx.Buildings, _ctx.FactionDef,
                              _ctx.Scene.MoveStartPosition, _ctx.FactionDef2);
            _ctx.Placer = placer;

            var selection = new SelectionSystem();
            _ctx.Scene.AddChild(selection);
            selection.Initialize(cam, _ctx.World, _ctx.FlowFieldBridge, _ctx.Buildings);
            _ctx.Selection = selection;

            var commandCard = new CommandCardSystem();
            _ctx.Scene.AddChild(commandCard);
            commandCard.Initialize(selection, _ctx.BuildSys, _ctx.Buildings, _ctx.Resources, _ctx.World);
            commandCard.OnWorkerBuildRequested += _ctx.Scene.EnterBuildPlacementMode;
            _ctx.CommandCard = commandCard;
        }
    }
}
