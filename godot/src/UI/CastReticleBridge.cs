#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 15.11 (DW-280) — the cursor-following GROUND-CAST reticle. While the player has armed a
    /// <see cref="ProjectChimera.Core.Definitions.AbilityTargeting.GroundPoint"/> ability (from the command card),
    /// <see cref="SelectionSystem"/> shows this ring and steers it to the world point under the cursor each frame, so
    /// the player sees exactly where the cast will land before the left-click commits it. The click issues the cast
    /// and hides the reticle.
    ///
    /// Pure presentation — a single <see cref="MeshInstance3D"/> ring (modeled on <see cref="OrderMarkerBridge"/>'s
    /// pooled markers, but persistent + steered rather than spawned-and-faded). Touches no sim state; the deterministic
    /// cast still resolves at exec-tick from the wire-shipped ground point.
    /// </summary>
    public partial class CastReticleBridge : Node3D
    {
        private const float RADIUS = 2.0f; // world units — a generous "impact area" hint

        private MeshInstance3D _ring = null!;

        public override void _Ready()
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency    = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor     = new Color(1f, 0.55f, 0.15f, 0.85f), // warm cast-target amber
                EmissionEnabled = true,
                Emission        = new Color(1f, 0.55f, 0.15f),
            };
            var torus = new TorusMesh { InnerRadius = 0.85f, OuterRadius = 1.0f, Rings = 24, RingSegments = 8 };
            _ring = new MeshInstance3D { Mesh = torus, Visible = false, Scale = Vector3.One * RADIUS };
            _ring.SetSurfaceOverrideMaterial(0, mat);
            AddChild(_ring);
        }

        /// <summary>Show or hide the reticle (called when a ground-cast arm goes live / is committed or cancelled).</summary>
        public void SetActive(bool active)
        {
            if (_ring != null) _ring.Visible = active;
        }

        /// <summary>Steer the reticle to <paramref name="worldPoint"/> (Y lifted just above the ground). No-op when hidden.</summary>
        public void MoveTo(Vector3 worldPoint)
        {
            if (_ring == null || !_ring.Visible) return;
            _ring.Position = new Vector3(worldPoint.X, 0.15f, worldPoint.Z);
        }
    }
}
