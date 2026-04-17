#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Renders two coloured flag-pole markers in world space — one per player slot.
    /// P1 = blue, P2 = red. Markers are always visible so map designers can see
    /// start positions in both Edit and Play modes.
    ///
    /// Call SetPosition() whenever the editor places or moves a start-position marker.
    /// The Y-component of each marker tracks the terrain surface (defaults to 0).
    /// </summary>
    public partial class StartPositionBridge : Node
    {
        private readonly Node3D?[] _markers = new Node3D?[2];

        private static readonly Color[] SLOT_COLORS =
        {
            new(0.20f, 0.50f, 1.00f), // slot 0 — P1 blue
            new(1.00f, 0.30f, 0.20f), // slot 1 — P2 red
        };

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create the two flag-pole meshes and add them to the scene.
        /// <paramref name="pos"/> supplies the initial world XZ per slot (Y=0).
        /// </summary>
        public void Initialize((float x, float z)[] slotPositions)
        {
            for (int i = 0; i < 2; i++)
            {
                float px = i < slotPositions.Length ? slotPositions[i].x : 0f;
                float pz = i < slotPositions.Length ? slotPositions[i].z : 0f;

                _markers[i] = BuildFlagPole(SLOT_COLORS[i]);
                _markers[i]!.Position = new Vector3(px, 0f, pz);
                GetParent()!.AddChild(_markers[i]);
            }
        }

        /// <summary>
        /// Move the flag pole for <paramref name="slot"/> (0=P1, 1=P2) to
        /// <paramref name="worldPos"/> (Y is taken from terrain, use 0 for flat maps).
        /// </summary>
        public void SetPosition(int slot, Vector3 worldPos)
        {
            if (slot < 0 || slot >= _markers.Length || _markers[slot] == null) return;
            _markers[slot]!.Position = new Vector3(worldPos.X, 0f, worldPos.Z);
        }

        // ─────────────────────────────────────────────────────────────────────

        private static Node3D BuildFlagPole(Color flagColor)
        {
            var root = new Node3D();

            // ── Vertical pole (thin white box, 3u tall) ───────────────────────
            var pole = new MeshInstance3D
            {
                Mesh     = new BoxMesh { Size = new Vector3(0.15f, 3.0f, 0.15f) },
                Position = new Vector3(0f, 1.5f, 0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            pole.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Colors.White,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            root.AddChild(pole);

            // ── Flag (coloured, offset to the right of the pole top) ─────────
            var flag = new MeshInstance3D
            {
                Mesh     = new BoxMesh { Size = new Vector3(1.0f, 0.55f, 0.06f) },
                Position = new Vector3(0.58f, 2.75f, 0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            flag.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor     = flagColor,
                ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
                EmissionEnabled = true,
                Emission        = flagColor * 0.5f,
            };
            root.AddChild(flag);

            return root;
        }
    }
}
