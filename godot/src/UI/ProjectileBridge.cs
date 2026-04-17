using Godot;
using ProjectChimera.Combat;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presentation-layer bridge that renders all in-flight projectiles as small
    /// fast-moving spheres using a single MultiMeshInstance3D.
    ///
    /// Projectile positions are snapped to simulation data each frame (no interpolation —
    /// projectiles move fast enough that per-frame snap is imperceptible).
    /// The sphere is lifted by Y_OFFSET so projectiles appear above ground level.
    /// </summary>
    public partial class ProjectileBridge : Node3D
    {
        private ProjectileStore      _store = null!;
        private MultiMeshInstance3D  _mmi   = null!;
        private MultiMesh            _mm    = null!;

        /// <summary>Y offset (world units) applied during rendering so projectiles fly above terrain.</summary>
        private const float Y_OFFSET = 0.8f;

        public void Initialize(ProjectileStore store)
        {
            _store = store;

            // Build the shared sphere mesh for all projectile instances
            var sphere = new SphereMesh();
            sphere.Radius = 0.15f;
            sphere.Height = 0.30f;

            var mat = new StandardMaterial3D();
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.AlbedoColor = new Color(1.0f, 0.85f, 0.1f); // bright yellow
            mat.EmissionEnabled = true;
            mat.Emission = new Color(1.0f, 0.6f, 0.0f) * 2.0f;
            sphere.Material = mat;

            _mm = new MultiMesh();
            _mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
            _mm.Mesh            = sphere;
            _mm.InstanceCount   = ProjectileStore.MAX_PROJECTILES;

            // Hide all slots initially by zeroing their scale
            for (int i = 0; i < ProjectileStore.MAX_PROJECTILES; i++)
                _mm.SetInstanceTransform(i, Transform3D.Identity.Scaled(Vector3.Zero));

            _mmi = new MultiMeshInstance3D();
            _mmi.Multimesh = _mm;
            AddChild(_mmi);
        }

        public override void _Process(double delta)
        {
            if (_store == null) return;

            int count = _store.HighWaterMark;
            for (int i = 0; i < count; i++)
            {
                if (_store.Alive[i])
                {
                    Vector3 pos = _store.Position[i].ToGodotVector3();
                    pos.Y += Y_OFFSET;
                    _mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, pos));
                }
                else
                {
                    _mm.SetInstanceTransform(i, Transform3D.Identity.Scaled(Vector3.Zero));
                }
            }
        }
    }
}
