#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Renders coloured flag-pole markers in world space — one per player slot. Story 6.7 generalizes the marker
    /// set from a hardcoded 2 to 2–4 (the engine ceiling, <c>Faction.Player4</c>): P1 blue, P2 red, P3 green, P4
    /// yellow. Only the placed slots' markers are visible; adding a start position (Story 6.7 add-slot) reveals its
    /// marker via <see cref="EnsureVisible"/>. Markers are visible in both Edit and Play so map designers can see
    /// start positions.
    ///
    /// Call SetPosition() whenever the editor places or moves a start-position marker.
    /// The Y-component of each marker tracks the terrain surface (defaults to 0).
    /// </summary>
    public partial class StartPositionBridge : Node
    {
        /// <summary>Engine ceiling — the as-built <c>Faction</c> enum tops at Player4 (4 slots). 5–8 is Story 9.2.</summary>
        public const int MAX_SLOTS = 4;

        private readonly Node3D?[] _markers = new Node3D?[MAX_SLOTS];

        private static readonly Color[] SLOT_COLORS =
        {
            new(0.20f, 0.50f, 1.00f), // slot 0 — P1 blue
            new(1.00f, 0.30f, 0.20f), // slot 1 — P2 red
            new(0.30f, 0.85f, 0.35f), // slot 2 — P3 green
            new(0.95f, 0.85f, 0.20f), // slot 3 — P4 yellow
        };

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create up to <see cref="MAX_SLOTS"/> flag-pole meshes and add them to the scene. <paramref name="slotPositions"/>
        /// supplies the initial world XZ for each PLACED slot (Y=0); markers beyond that count are built but hidden
        /// until <see cref="EnsureVisible"/>/<see cref="SetPosition"/> reveals them (an added start position).
        /// </summary>
        public void Initialize((float x, float z)[] slotPositions)
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                bool placed = i < slotPositions.Length;
                float px = placed ? slotPositions[i].x : 0f;
                float pz = placed ? slotPositions[i].z : 0f;

                _markers[i] = BuildFlagPole(SLOT_COLORS[i]);
                _markers[i]!.Position = new Vector3(px, 0f, pz);
                _markers[i]!.Visible  = placed;
                GetParent()!.AddChild(_markers[i]);
            }
        }

        /// <summary>
        /// Move the flag pole for <paramref name="slot"/> (0..3) to <paramref name="worldPos"/> (Y from terrain, 0 for
        /// flat). Placing/moving a slot also reveals its marker (an added slot's marker starts hidden).
        /// </summary>
        public void SetPosition(int slot, Vector3 worldPos)
        {
            if (slot < 0 || slot >= _markers.Length || _markers[slot] == null) return;
            _markers[slot]!.Position = new Vector3(worldPos.X, 0f, worldPos.Z);
            _markers[slot]!.Visible  = true;
        }

        /// <summary>Show (add) or hide (remove) a slot's marker without moving it — Story 6.7 add/remove start slots.</summary>
        public void EnsureVisible(int slot, bool visible)
        {
            if (slot < 0 || slot >= _markers.Length || _markers[slot] == null) return;
            _markers[slot]!.Visible = visible;
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
