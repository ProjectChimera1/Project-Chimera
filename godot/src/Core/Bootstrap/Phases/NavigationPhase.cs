#nullable enable
using Godot;
using ProjectChimera.Navigation;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Navigation" phase (runtime position 6). Bakes the NavigationMesh (from Terrain3D faces when
    /// present, else a flat fallback ground), then builds the PathRequestSystem, the deterministic
    /// FlowFieldSystem / FlowFieldBridge, and the NavObstacleManager. Publishes NavRegion / PathSystem /
    /// FlowFieldSys / FlowFieldBridge / NavObstacles on the context. Runs after Terrain (reads ctx.Terrain) and
    /// before Camera (which wires Selection to the flow-field bridge). The flow field's own Initialize is the
    /// separate "FlowFieldInit" phase (after ScenarioLoad). Behavior-identical to MainScene.SetupNavigation
    /// (+ InitialBakeWithTerrain).
    /// </summary>
    public sealed class NavigationPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public NavigationPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Navigation";

        public void Run()
        {
            const float HALF = 120f;

            // ── NavMesh settings — shared by both code paths ───────────────────
            var navMesh = new NavigationMesh();
            navMesh.AgentRadius   = 0.4f;
            navMesh.AgentHeight   = 1.8f;
            navMesh.AgentMaxClimb = 0.25f; // prevents routing over building tops
            navMesh.CellSize      = 1.0f;  // 1 m/cell — fast bake, good resolution
            navMesh.GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders;
            navMesh.GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.RootNodeChildren;

            var navRegion = new NavigationRegion3D();
            navRegion.NavigationMesh = navMesh;
            _ctx.Scene.AddChild(navRegion);
            _ctx.NavRegion = navRegion;

            if (_ctx.Terrain != null)
            {
                // ── Terrain3D path: bake from heightmap faces ─────────────────
                // Building StaticBody3D are added as children of navRegion by NavObstacleManager —
                // ParseSourceGeometryData picks them up for carving on each rebake.
                InitialBakeWithTerrain(navMesh);
                GD.Print("[Navigation] NavMesh baked via Terrain3D.generate_nav_mesh_source_geometry.");
            }
            else
            {
                // ── Fallback path: flat StaticBody3D ground ───────────────────
                const float GROUND_THICK = 0.2f;
                var groundShape = new BoxShape3D
                {
                    Size = new Vector3(HALF * 2f, GROUND_THICK, HALF * 2f)
                };
                var groundCollision = new CollisionShape3D { Shape = groundShape };
                var groundBody = new StaticBody3D
                {
                    Position = new Vector3(0f, -GROUND_THICK * 0.5f, 0f)
                };
                groundBody.AddChild(groundCollision);
                navRegion.AddChild(groundBody);
                navRegion.BakeNavigationMesh(false);
                GD.Print("[Navigation] NavMesh baked (flat StaticBody3D ground fallback).");
            }

            Rid navMap = _ctx.Scene.GetWorld3D().NavigationMap;

            var pathSystem = new PathRequestSystem();
            _ctx.Scene.AddChild(pathSystem);
            pathSystem.Initialize(_ctx.World, navMap);
            _ctx.PathSystem = pathSystem;

            // Flow field pathfinding — deterministic replacement for NavServer3D. The bridge is added to the
            // tree here so _Process runs; its Initialize() is the FlowFieldInit phase (after ScenarioLoad, so
            // the obstacle map has all buildings).
            var flowFieldSys    = new FlowFieldSystem();
            var flowFieldBridge = new FlowFieldBridge();
            _ctx.Scene.AddChild(flowFieldBridge);
            _ctx.FlowFieldSys    = flowFieldSys;
            _ctx.FlowFieldBridge = flowFieldBridge;

            // NavObstacleManager watches BuildingStore and rebakes on any change. Kept on the context so
            // TerrainBrush can call MarkDirty() after sculpting.
            var navObstacles = new NavObstacleManager();
            _ctx.Scene.AddChild(navObstacles);
            navObstacles.Initialize(_ctx.Buildings, navRegion, _ctx.Terrain);
            _ctx.NavObstacles = navObstacles;

            GD.Print($"[Navigation] Map RID={navMap}, walkable ±{HALF} units. NavObstacleManager + FlowFieldBridge active.");
        }

        /// <summary>
        /// Initial NavMesh bake using Terrain3D geometry. Called once before any buildings are placed (buildings
        /// are carved on the first NavObstacleManager rebake).
        /// </summary>
        private void InitialBakeWithTerrain(NavigationMesh navMeshTemplate)
        {
            const float HALF = 120f;

            // Duplicate so assigning the result back to ctx.NavRegion forces re-registration
            var navMesh = (NavigationMesh)navMeshTemplate.Duplicate()!;

            var sourceGeo = new NavigationMeshSourceGeometryData3D();

            // Parse any existing StaticBody3D children of the nav region (none yet, but future-safe)
            NavigationServer3D.ParseSourceGeometryData(navMeshTemplate, sourceGeo, _ctx.NavRegion);

            // Add terrain walkable surface from the heightmap (flat at Y=0 initially)
            var aabb = new Aabb(
                new Vector3(-HALF, -5f, -HALF),
                new Vector3(HALF * 2f, 10f, HALF * 2f));
            var faces = _ctx.Terrain!.Call("generate_nav_mesh_source_geometry", aabb, false)
                                  .As<Vector3[]>();
            if (faces.Length > 0)
                sourceGeo.AddFaces(faces, Transform3D.Identity);

            NavigationServer3D.BakeFromSourceGeometryData(navMesh, sourceGeo);
            _ctx.NavRegion.NavigationMesh = navMesh;
        }
    }
}
