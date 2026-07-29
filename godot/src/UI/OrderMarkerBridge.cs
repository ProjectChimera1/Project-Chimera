#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.4 (FR-74) — the order-confirmed ground marker. When the player issues a move/attack/patrol/etc. order,
    /// SelectionSystem spawns a short-lived faction-tinted ring at the resolved world target AT ISSUE TIME (masking the
    /// lockstep input-delay: the visual confirmation is immediate, while the deterministic sim effect still lands at
    /// exec-tick). Modeled on <c>BuildingBridge</c>'s pooled rally-pole markers.
    ///
    /// Pure presentation — a pool of pre-allocated <see cref="MeshInstance3D"/> rings that expand + fade out over a
    /// short lifetime, then free their slot. Touches no sim state.
    /// </summary>
    public partial class OrderMarkerBridge : Node3D
    {
        private const int   POOL_SIZE   = 12;
        private const float LIFETIME    = 0.6f;   // seconds
        private const float BASE_RADIUS = 1.2f;   // world units at spawn

        private MeshInstance3D[] _markers = System.Array.Empty<MeshInstance3D>();
        private float[]          _timer   = System.Array.Empty<float>();
        private StandardMaterial3D _mat    = null!;
        private int _next;

        public override void _Ready()
        {
            _mat = new StandardMaterial3D
            {
                ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency    = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor     = new Color(1f, 1f, 1f, 0.9f),
                EmissionEnabled = true,
                Emission        = new Color(1f, 1f, 1f),
            };

            _markers = new MeshInstance3D[POOL_SIZE];
            _timer   = new float[POOL_SIZE];
            for (int i = 0; i < POOL_SIZE; i++)
            {
                var torus = new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1.0f, Rings = 16, RingSegments = 8 };
                var node  = new MeshInstance3D { Mesh = torus, Visible = false };
                node.SetSurfaceOverrideMaterial(0, (StandardMaterial3D)_mat.Duplicate());
                AddChild(node);
                _markers[i] = node;
            }
        }

        /// <summary>Spawn a faction-tinted order-confirmed ring at <paramref name="worldTarget"/> (Y lifted just above
        /// the ground). Claims the next pool slot round-robin (overwrites the oldest if the pool is saturated).</summary>
        public void Spawn(Vector3 worldTarget, Color tint)
        {
            int i = _next;
            _next = (_next + 1) % POOL_SIZE;

            MeshInstance3D node = _markers[i];
            node.Position = new Vector3(worldTarget.X, 0.15f, worldTarget.Z);
            node.Scale    = Vector3.One * BASE_RADIUS;
            node.Visible  = true;
            if (node.GetSurfaceOverrideMaterial(0) is StandardMaterial3D m)
            {
                m.AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.9f);
                m.Emission    = new Color(tint.R, tint.G, tint.B);
            }
            _timer[i] = LIFETIME;
        }

        public override void _Process(double delta)
        {
            float dt = (float)delta;
            for (int i = 0; i < POOL_SIZE; i++)
            {
                if (_timer[i] <= 0f) continue;
                _timer[i] -= dt;
                if (_timer[i] <= 0f) { _markers[i].Visible = false; continue; }

                float t = _timer[i] / LIFETIME;               // 1 → 0
                _markers[i].Scale = Vector3.One * (BASE_RADIUS * (1.4f - 0.4f * t)); // expand slightly outward
                if (_markers[i].GetSurfaceOverrideMaterial(0) is StandardMaterial3D m)
                {
                    Color c = m.AlbedoColor;
                    m.AlbedoColor = new Color(c.R, c.G, c.B, 0.9f * t); // fade out
                }
            }
        }
    }
}
