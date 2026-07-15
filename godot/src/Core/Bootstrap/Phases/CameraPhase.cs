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
                              _ctx.Scene.MoveStartPosition, _ctx.FactionDef2,
                              _ctx.Host.Items, _ctx.Host.ItemRegistry, // Story 3.15 — Item placement mode
                              _ctx.Scene.SyncBuilding, _ctx.Scene.SyncUnit, _ctx.Scene.SyncResourceNode, // Story 6.1 — ScenarioData sync
                              _ctx.Scene.SyncProp, () => _ctx.Scenario); // Story 6.6 — prop sync + late-bound scenario read seam
            _ctx.Placer = placer;

            var selection = new SelectionSystem();
            _ctx.Scene.AddChild(selection);
            // Story 2.12: pass BuildSys (offline SetRally apply site) + CombatEvents (OrderDenied feedback bus).
            selection.Initialize(cam, _ctx.World, _ctx.FlowFieldBridge, _ctx.Buildings, _ctx.BuildSys, _ctx.CombatEvents,
                                  _ctx.Host.Items, _ctx.Host.ItemSys); // Story 3.15 — pickup/use affordance
            selection.SetResearchStore(_ctx.Host.Research); // Story 4.11 — aggregate upgrade line on the focus unit's panel
            _ctx.Selection = selection;

            var commandCard = new CommandCardSystem();
            _ctx.Scene.AddChild(commandCard);
            commandCard.Initialize(selection, _ctx.BuildSys, _ctx.Buildings, _ctx.Resources, _ctx.World);
            commandCard.SetAbilityRegistry(_ctx.AbilityRegistry); // Story 2.4b: the card needs the registry for ability labels (set on ctx by MainScene._Ready, before this phase)
            commandCard.SetReviveDeps(_ctx.Host.Heroes, _ctx.Host.RevivalRuntime); // Story 3.14: the card enumerates awaiting heroes + prices a revive
            commandCard.SetShopDeps(_ctx.Host.ItemSys, _ctx.Host.Items, _ctx.Host.ItemRegistry); // Story 3.16: shop Buy + inventory grid
            commandCard.SetResearchDeps(_ctx.Host.ResearchSys, _ctx.Host.Research); // Story 4.11: research button grid
            commandCard.OnWorkerBuildRequested += _ctx.Scene.EnterBuildPlacementMode;
            _ctx.CommandCard = commandCard;
        }
    }
}
