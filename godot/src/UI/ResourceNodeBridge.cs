using Godot;
using ProjectChimera.Core;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Renders resource nodes as glowing ore deposits.
    /// One MeshInstance3D per node slot — hidden when depleted.
    /// Scale shrinks as supply is consumed (visual depletion feedback).
    /// </summary>
    public partial class ResourceNodeBridge : Node3D
    {
        private ResourceNodeStore _nodes = null!;
        private MeshInstance3D[]  _meshes = null!;

        // Ore nodes: warm yellow glow
        private static readonly Color ORE_COLOR      = new Color(1.0f, 0.82f, 0.15f);
        private static readonly Color ORE_EMIT_COLOR = new Color(1.0f, 0.75f, 0.0f);

        public void Initialize(ResourceNodeStore nodes)
        {
            _nodes  = nodes;
            _meshes = new MeshInstance3D[ResourceNodeStore.MAX_NODES];

            var sharedMesh = BuildOreMesh();

            for (int i = 0; i < ResourceNodeStore.MAX_NODES; i++)
            {
                var mi = new MeshInstance3D();
                mi.Mesh    = sharedMesh;
                mi.Visible = false;
                AddChild(mi);
                _meshes[i] = mi;
            }
        }

        public override void _Process(double delta)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                var mi = _meshes[i];

                if (!_nodes.Active[i])
                {
                    mi.Visible = false;
                    continue;
                }

                mi.Visible = true;

                // Position
                var pos = _nodes.Position[i];
                mi.GlobalPosition = new Vector3(pos.X.ToFloat(), 0.7f, pos.Z.ToFloat());

                // Scale down as ore is depleted
                float ratio = _nodes.SupplyTotal[i].Raw > 0
                    ? _nodes.SupplyRemaining[i].ToFloat() / _nodes.SupplyTotal[i].ToFloat()
                    : 0f;
                float s = 0.3f + 0.7f * Mathf.Sqrt(ratio); // shrinks toward 30% of original
                mi.Scale = new Vector3(s, s * 0.6f, s);     // slightly flattened "crystal" shape
            }
        }

        private static Mesh BuildOreMesh()
        {
            // Hexagonal prism-ish: use a sphere scaled to a crystal shape
            var sphere = new SphereMesh();
            sphere.Radius = 0.7f;
            sphere.Height = 1.1f;
            sphere.RadialSegments = 8;
            sphere.Rings = 4;

            var mat = new StandardMaterial3D();
            mat.AlbedoColor      = ORE_COLOR;
            mat.EmissionEnabled  = true;
            mat.Emission         = ORE_EMIT_COLOR * 1.5f;
            mat.ShadingMode      = BaseMaterial3D.ShadingModeEnum.Unshaded;
            sphere.Material      = mat;
            return sphere;
        }
    }
}
