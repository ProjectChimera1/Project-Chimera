#nullable enable
using Godot;
using ProjectChimera.Core;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Minimap overlay — bottom-right corner of the HUD.
    ///
    /// Render layers (bottom → top):
    ///   1. SubViewportContainer — orthographic 3D render from directly above (terrain + buildings).
    ///   2. FogRect (TextureRect) — RGBA8 ImageTexture streamed from FogOfWarSystem.Grid each frame.
    ///        UNEXPLORED → opaque black  (alpha 210)
    ///        EXPLORED   → dim overlay   (alpha 110)
    ///        VISIBLE    → transparent   (alpha 0)
    ///   3. DotOverlay (Control) — crisp faction-colored dots for live units and buildings.
    ///   4. Border drawn by MinimapBridge._Draw().
    ///
    /// Click-to-pan: LMB on the minimap instantly pans the RTS camera pivot to the
    /// corresponding world XZ position. Hold and drag also works.
    ///
    /// Coordinate mapping:
    ///   World [-HALF_MAP .. +HALF_MAP] ↔ minimap pixel [0 .. SIZE].
    ///   HALF_MAP = 128 world units (matches Terrain3D region).
    /// </summary>
    public partial class MinimapBridge : Control
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float HALF_MAP    = 128f; // world-space half-extent
        private const int   SIZE        = 200;  // minimap pixel size (square)
        private const int   MARGIN      = 8;    // px gap from screen corner

        private const float UNIT_RADIUS = 2.0f;
        private const float BLDG_RADIUS = 3.5f;

        private static readonly Color P1_COLOR   = new Color(0.20f, 0.55f, 1.00f);
        private static readonly Color P2_COLOR   = new Color(1.00f, 0.25f, 0.25f);
        private static readonly Color BORDER_COL = new Color(0.12f, 0.12f, 0.12f, 0.90f);
        private static readonly Color BG_COL     = new Color(0.05f, 0.08f, 0.05f, 0.80f);

        // Fog alpha values (R=G=B=0, only alpha varies)
        private const byte FOG_UNEXPLORED = 210;
        private const byte FOG_EXPLORED   = 110;
        private const byte FOG_VISIBLE    = 0;

        // ── Sim + presentation references ─────────────────────────────────────

        private EntityWorld         _world    = null!;
        private BuildingStore       _buildings = null!;
        private FogOfWarSystem?     _fog;          // optional; minimap still works without it
        private RtsCameraController? _camCtrl;      // optional; click-to-pan disabled when null

        // ── Godot nodes ───────────────────────────────────────────────────────

        private SubViewport  _subViewport = null!;
        private TextureRect  _fogRect     = null!;
        private DotOverlay   _dots        = null!;

        // ── Fog texture ───────────────────────────────────────────────────────

        private Image?        _fogImage;
        private ImageTexture? _fogTexture;
        private byte[]?       _fogData;   // reusable RGBA scratch buffer — gridSize*gridSize*4 bytes

        // ── Nested dot-overlay ────────────────────────────────────────────────

        private partial class DotOverlay : Control
        {
            public new MinimapBridge? Owner;
            public override void _Draw() => Owner?.DrawDots(this);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Call after AddChild(this). Wires dependencies and builds the node subtree.
        /// </summary>
        /// <param name="fog">Optional. When supplied, draws fog-of-war over the minimap.</param>
        /// <param name="camCtrl">Optional. When supplied, LMB click pans the RTS camera.</param>
        public void Initialize(
            EntityWorld          world,
            BuildingStore        buildings,
            FogOfWarSystem?      fog     = null,
            RtsCameraController? camCtrl = null)
        {
            _world    = world;
            _buildings = buildings;
            _fog      = fog;
            _camCtrl  = camCtrl;

            // ── Root sizing & anchoring ──────────────────────────────────────
            SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
            Size         = new Vector2(SIZE, SIZE);
            OffsetRight  = -MARGIN;
            OffsetBottom = -MARGIN;

            // Capture mouse so click-to-pan works and clicks don't fall through to 3D
            MouseFilter = MouseFilterEnum.Stop;

            // ── SubViewport (3D top-down render) ─────────────────────────────
            var svp = new SubViewport
            {
                Size                   = new Vector2I(SIZE, SIZE),
                RenderTargetClearMode  = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            };

            var cam = new Camera3D
            {
                Projection      = Camera3D.ProjectionType.Orthogonal,
                Size            = HALF_MAP * 2f,
                Near            = 0.1f,
                Far             = 400f,
                Position        = new Vector3(0f, 200f, 0f),
                RotationDegrees = new Vector3(-90f, 0f, 0f),
            };
            svp.AddChild(cam);
            _subViewport = svp;

            var container = new SubViewportContainer
            {
                StretchShrink = 1,
                MouseFilter   = MouseFilterEnum.Ignore,
            };
            container.Size = new Vector2(SIZE, SIZE);
            container.AddChild(svp);
            AddChild(container);

            // ── Fog texture layer ─────────────────────────────────────────────
            if (_fog != null)
            {
                int gridSize = FogOfWarSystem.GRID_SIZE;
                _fogData    = new byte[gridSize * gridSize * 4];
                _fogImage   = Image.CreateFromData(
                    gridSize, gridSize, false, Image.Format.Rgba8, _fogData);
                _fogTexture = ImageTexture.CreateFromImage(_fogImage);

                _fogRect = new TextureRect
                {
                    Texture             = _fogTexture,
                    ExpandMode          = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode         = TextureRect.StretchModeEnum.Scale,
                    MouseFilter         = MouseFilterEnum.Ignore,
                };
                _fogRect.Size = new Vector2(SIZE, SIZE);
                AddChild(_fogRect);
            }

            // ── Dot overlay ───────────────────────────────────────────────────
            _dots = new DotOverlay
            {
                Owner       = this,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _dots.Size = new Vector2(SIZE, SIZE);
            AddChild(_dots);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void _Ready()
        {
            if (_subViewport != null)
                _subViewport.World3D = GetViewport().World3D;
        }

        public override void _Process(double _delta)
        {
            if (_fog != null) UpdateFogTexture();
            _dots?.QueueRedraw();
        }

        // ── Click-to-pan ──────────────────────────────────────────────────────

        public override void _GuiInput(InputEvent @event)
        {
            if (_camCtrl == null) return;

            if (@event is InputEventMouseButton mb &&
                mb.ButtonIndex == MouseButton.Left)
            {
                _camCtrl.PanTo(MinimapToWorld(mb.Position));
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion motion &&
                     (motion.ButtonMask & MouseButtonMask.Left) != 0)
            {
                _camCtrl.PanTo(MinimapToWorld(motion.Position));
                AcceptEvent();
            }
        }

        // ── Fog texture update ────────────────────────────────────────────────

        private void UpdateFogTexture()
        {
            if (_fogImage == null || _fogTexture == null || _fog == null || _fogData == null) return;

            int    gridSize = FogOfWarSystem.GRID_SIZE;
            byte[] grid     = _fog.Grid;
            int    cells    = gridSize * gridSize;

            for (int i = 0; i < cells; i++)
            {
                byte alpha = grid[i] switch
                {
                    FogOfWarSystem.VISIBLE  => FOG_VISIBLE,
                    FogOfWarSystem.EXPLORED => FOG_EXPLORED,
                    _                       => FOG_UNEXPLORED, // UNEXPLORED or unknown
                };
                int px       = i * 4;
                _fogData[px]     = 0;     // R
                _fogData[px + 1] = 0;     // G
                _fogData[px + 2] = 0;     // B
                _fogData[px + 3] = alpha; // A
            }

            _fogImage.SetData(gridSize, gridSize, false, Image.Format.Rgba8, _fogData);
            _fogTexture.Update(_fogImage);
        }

        // ── Custom drawing ────────────────────────────────────────────────────

        public override void _Draw()
        {
            DrawRect(new Rect2(0, 0, SIZE, SIZE), BG_COL);
            DrawRect(new Rect2(0, 0, SIZE, SIZE), BORDER_COL, false, 1.5f);
        }

        internal void DrawDots(Control canvas)
        {
            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                Vector2 px = WorldToMinimap(
                    _world.Position[i].X.ToFloat(),
                    _world.Position[i].Z.ToFloat());
                Color col = _world.FactionOf[i] == Faction.Player1 ? P1_COLOR : P2_COLOR;
                canvas.DrawCircle(px, UNIT_RADIUS, col);
            }

            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                Vector2 px = WorldToMinimap(
                    _buildings.Position[i].X.ToFloat(),
                    _buildings.Position[i].Z.ToFloat());
                Color col = _buildings.FactionOf[i] == Faction.Player1 ? P1_COLOR : P2_COLOR;
                canvas.DrawRect(
                    new Rect2(px - Vector2.One * BLDG_RADIUS, Vector2.One * BLDG_RADIUS * 2f),
                    col);
            }
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        private static Vector2 WorldToMinimap(float worldX, float worldZ)
        {
            float u = Mathf.Clamp((worldX + HALF_MAP) / (HALF_MAP * 2f), 0f, 1f);
            float v = Mathf.Clamp((worldZ + HALF_MAP) / (HALF_MAP * 2f), 0f, 1f);
            return new Vector2(u * SIZE, v * SIZE);
        }

        /// <summary>Convert a pixel position on the minimap back to a world XZ Vector3 (Y=0).</summary>
        private static Vector3 MinimapToWorld(Vector2 px)
        {
            float worldX = (px.X / SIZE) * (HALF_MAP * 2f) - HALF_MAP;
            float worldZ = (px.Y / SIZE) * (HALF_MAP * 2f) - HALF_MAP;
            return new Vector3(worldX, 0f, worldZ);
        }
    }
}
