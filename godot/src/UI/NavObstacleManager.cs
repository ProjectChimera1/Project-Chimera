#nullable enable
using Godot;
using ProjectChimera.Core;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Keeps NavigationRegion3D source geometry in sync with placed buildings so that
    /// path queries correctly route around building footprints.
    ///
    /// Each alive building gets a StaticBody3D child on the NavigationRegion3D node.
    /// When any building is added or destroyed the navmesh is re-baked synchronously.
    /// Baking is a rare event (player building placement) so a main-thread sync bake
    /// is acceptable.
    ///
    /// When a Terrain3D node is provided, baking uses Terrain3D.generate_nav_mesh_source_geometry
    /// so the walkable surface matches the heightmap. Without terrain, falls back to
    /// BakeNavigationMesh (StaticBody3D ground approach).
    ///
    /// Architecture: Presentation layer (uses Godot API). Pure read from BuildingStore.
    /// </summary>
    public partial class NavObstacleManager : Node
    {
        private BuildingStore      _buildings = null!;
        private NavigationRegion3D _region    = null!;

        /// <summary>Optional Terrain3D node. When non-null, rebakes use generate_nav_mesh_source_geometry.</summary>
        private Node3D? _terrain;

        // One StaticBody3D per building slot (null = slot unused or building dead)
        private readonly StaticBody3D?[] _bodies = new StaticBody3D?[BuildingStore.MAX_BUILDINGS];

        // Building footprint sizes — must match BuildingBridge.TYPE_SIZE exactly
        private static readonly Vector3[] TYPE_SIZE =
        {
            new(6f, 4f, 6f), // CommandCenter
            new(5f, 3f, 5f), // Barracks
            new(4f, 3f, 5f), // ArcheryRange
            new(5f, 3f, 7f), // SiegeWorkshop
        };

        // Half-extent of the nav bake AABB — must cover the full walkable map
        private const float BAKE_HALF = 120f;

        private bool _dirty = false;

        /// <summary>
        /// Schedule a NavMesh rebake on the next _Process frame.
        /// Called by TerrainBrush after a sculpt stroke ends (0.5 s debounce).
        /// </summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>
        /// Bind to the building store and the scene's NavigationRegion3D.
        /// Pass the Terrain3D node when using Terrain3D-based nav baking.
        /// Call once from MainScene._Ready() after both are initialised.
        /// </summary>
        public void Initialize(BuildingStore buildings, NavigationRegion3D region,
                               Node3D? terrain = null)
        {
            _buildings = buildings;
            _region    = region;
            _terrain   = terrain;
        }

        public override void _Process(double delta)
        {
            if (_buildings == null) return;

            for (int i = 0; i < _buildings.Count; i++)
            {
                bool alive   = _buildings.Alive[i];
                bool hasBody = _bodies[i] != null;

                if (alive && !hasBody)
                {
                    AddObstacle(i);
                    _dirty = true;
                }
                else if (!alive && hasBody)
                {
                    RemoveObstacle(i);
                    _dirty = true;
                }
            }

            if (_dirty)
            {
                _dirty = false;
                // Synchronous bake on the main thread — building placement is a rare event.
                if (_terrain != null)
                    RebakeWithTerrain();
                else
                    _region.BakeNavigationMesh(false); // legacy: flat StaticBody3D ground
            }
        }

        // ── Terrain-based rebake ──────────────────────────────────────────────

        /// <summary>
        /// Rebake the NavMesh using Terrain3D source geometry + building StaticBody3D
        /// footprints parsed from the NavigationRegion3D children.
        /// Creates a duplicate of the current navmesh so Godot detects a change and
        /// re-registers the region with NavigationServer3D.
        /// </summary>
        private void RebakeWithTerrain()
        {
            var navMeshTemplate = _region.NavigationMesh;
            // Duplicate so BakeFromSourceGeometryData produces a fresh mesh object;
            // assigning a new object to NavigationMesh forces Godot to re-register the region.
            var navMesh = (NavigationMesh)navMeshTemplate.Duplicate()!;

            var sourceGeo = new NavigationMeshSourceGeometryData3D();

            // Parse building StaticBody3D footprints from _region's children
            NavigationServer3D.ParseSourceGeometryData(navMeshTemplate, sourceGeo, _region);

            // Add terrain faces — covers ±BAKE_HALF in XZ, ±5 in Y
            var aabb = new Aabb(
                new Vector3(-BAKE_HALF, -5f, -BAKE_HALF),
                new Vector3(BAKE_HALF * 2f, 10f, BAKE_HALF * 2f));
            var facesVariant = _terrain!.Call("generate_nav_mesh_source_geometry", aabb, false);
            var faces = facesVariant.As<Vector3[]>();
            if (faces.Length > 0)
                sourceGeo.AddFaces(faces, Transform3D.Identity);

            NavigationServer3D.BakeFromSourceGeometryData(navMesh, sourceGeo);
            _region.NavigationMesh = navMesh;
        }

        // ── Obstacle management ───────────────────────────────────────────────

        /// <summary>Creates a StaticBody3D footprint for building <paramref name="id"/> and adds
        /// it as a child of the NavigationRegion3D so it is included in the next bake.</summary>
        private void AddObstacle(int id)
        {
            int t    = (int)_buildings.Type[id];
            var size = TYPE_SIZE[t];

            float wx = _buildings.Position[id].X.ToFloat();
            float wz = _buildings.Position[id].Z.ToFloat();

            var shape = new BoxShape3D { Size = size };

            var collision = new CollisionShape3D { Shape = shape };

            var body = new StaticBody3D();
            // Centre the box so its bottom is flush with Y=0 (ground level)
            body.Position = new Vector3(wx, size.Y * 0.5f, wz);
            body.AddChild(collision);
            _region.AddChild(body);

            _bodies[id] = body;
        }

        /// <summary>Removes and frees the StaticBody3D for building <paramref name="id"/>.</summary>
        private void RemoveObstacle(int id)
        {
            var body = _bodies[id]!;
            _region.RemoveChild(body); // remove from tree before bake
            body.Free();
            _bodies[id] = null;
        }
    }
}
