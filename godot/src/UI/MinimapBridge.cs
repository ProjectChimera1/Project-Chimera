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
    ///        Per-cell alpha decided by MinimapFogPolicy.FogAlpha — DW-406: honors the spectator RevealAll flag
    ///        (mirrored from FogOfWarBridge via SetRevealAll), so a fully-revealed 3D view never sits over a
    ///        fogged minimap.
    ///   3. DotOverlay (Control) — crisp faction-colored dots for live units and buildings.
    ///        Gated by MinimapFogPolicy.ShouldDrawDot — DW-408: enemy dots draw only where the local fog is
    ///        currently VISIBLE (own dots always), so the minimap leaks nothing the main view hides.
    ///   4. Border drawn by MinimapBridge._Draw().
    ///
    /// Click/drag-to-pan: RETIRED (DW-940, 2026-08-12 — camera moves on arrow keys only). LMB on the minimap is
    /// consumed (never falls through to the 3D world); Alt+LMB still pings.
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
        // Story 11.4 (FR-74): the camera-view box + the under-attack alert flash colour.
        private static readonly Color CAM_BOX_COL = new Color(1.00f, 1.00f, 1.00f, 0.85f);
        private static readonly Color ALERT_COL   = new Color(1.00f, 0.20f, 0.15f);

        // Story 11.4 (FR-74): decaying minimap markers — Alt-click pings + under-attack alert flashes. A pulsing ring
        // that expands + fades over its lifetime, drawn on the topmost dot overlay. Purely presentation.
        private const float PING_TTL_SEC  = 2.4f;
        private const float ALERT_TTL_SEC = 2.0f;
        private struct Marker { public float WorldX, WorldZ, Age, Ttl, MaxRadius; public Color Color; }
        private readonly System.Collections.Generic.List<Marker> _markers = new();

        /// <summary>Story 11.4: invoked when the LOCAL player Alt-clicks the minimap (world XZ of the ping). Wired by
        /// MatchAlertPhase to play the ping cue and replicate it to allies in MP. Null offline / until wired.</summary>
        public System.Action<Vector3>? OnLocalPing;

        /// <summary>True while an Alt+LMB ping gesture is in flight (press seen, release pending). Suppresses the
        /// click-to-pan branches for the rest of the gesture so a ping never doubles as a camera jump.</summary>
        private bool _pingGesture;

        // Fog alpha values live in MinimapFogPolicy (DW-406/DW-408) — the Godot-free policy core shared with Tier-1
        // tests, so the texture mapping, the dot gate, and their regression net cannot drift apart.

        // ── Sim + presentation references ─────────────────────────────────────

        private EntityWorld         _world    = null!;
        private BuildingStore       _buildings = null!;
        private FogOfWarSystem?     _fog;          // optional; minimap still works without it
        private RtsCameraController? _camCtrl;      // optional; click-to-pan disabled when null

        // Story 9.5: the local player's faction, late-bound. Defaults to Player1 so every offline/single-player path and
        // any un-wired instance stay byte-identical to today; MinimapPhase wires it to _ctx.Lockstep?.EffectiveLocalFaction
        // (offline/spectator clamps to Player1) so a client assigned Player2..Player8 paints its OWN units/buildings as
        // own (P1_COLOR) and everyone else as P2_COLOR.
        private System.Func<Faction> _localFaction = () => Faction.Player1;

        /// <summary>Story 9.5: inject the live local-faction getter (see <see cref="MinimapPhase"/>).</summary>
        public void SetLocalFaction(System.Func<Faction> getter) => _localFaction = getter;

        // DW-406: whether the local view is fully revealed (match-start spectator, or the local player eliminated and
        // spectating out the match). Late-bound to FogOfWarBridge.RevealAll by MinimapPhase so EVERY site that flips
        // the 3D overlay's reveal — spectator match start, local-elimination spectate, and the reset back to false —
        // automatically drives the minimap fog AND the dot gate too. Defaults to false (normal fogged play), keeping
        // any un-wired instance byte-identical to the pre-DW-406 fogged read.
        private System.Func<bool> _revealAll = () => false;

        /// <summary>DW-406: inject the reveal-all getter (mirrors <c>FogOfWarBridge.RevealAll</c>; wired by
        /// <see cref="MinimapPhase"/>).</summary>
        public void SetRevealAll(System.Func<bool> getter) => _revealAll = getter;

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
            // All four anchors sit at the bottom-right corner, so EVERY offset must be negative to walk the rect back
            // on-screen. The previous code set only Right/Bottom: Left/Top stayed at 0, pinning the rect's top-left to
            // the raw (viewport width, viewport height) corner and giving it a NEGATIVE size, so the whole 200x200
            // panel — dots, fog, and 11.4's pings / camera box / alert flash — rendered off-screen at 1920x1080.
            // (The `Size = ...` line it replaced could not help: with the anchors already at 1,1 the setter just moved
            // Right/Bottom, which the two lines below then overwrote.)
            SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
            OffsetLeft   = -(SIZE + MARGIN);
            OffsetTop    = -(SIZE + MARGIN);
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
            AgeMarkers((float)_delta);
            _dots?.QueueRedraw();
        }

        // ── Story 11.4 (FR-74): pings + alert flashes ──────────────────────────

        /// <summary>Add a decaying ping ring at world XZ in the given colour (Alt-click ping, or an MP-replicated ping
        /// from an ally). Shown immediately; expands + fades over <see cref="PING_TTL_SEC"/>.</summary>
        public void AddPing(float worldX, float worldZ, Color color)
            => _markers.Add(new Marker { WorldX = worldX, WorldZ = worldZ, Ttl = PING_TTL_SEC, MaxRadius = 10f, Color = color });

        /// <summary>Add an under-attack alert flash at world XZ (a local unit/building was hit off-screen).</summary>
        public void FlashAlert(float worldX, float worldZ)
            => _markers.Add(new Marker { WorldX = worldX, WorldZ = worldZ, Ttl = ALERT_TTL_SEC, MaxRadius = 8f, Color = ALERT_COL });

        /// <summary>Story 11.4 review (P4): drop all decaying ping/alert markers (on match reset), so a new match does
        /// not carry the prior match's stale rings.</summary>
        public void ClearMarkers() => _markers.Clear();

        private void AgeMarkers(float delta)
        {
            for (int i = _markers.Count - 1; i >= 0; i--)
            {
                Marker m = _markers[i];
                m.Age += delta;
                if (m.Age >= m.Ttl) { _markers.RemoveAt(i); continue; }
                _markers[i] = m;
            }
        }

        // ── Click-to-pan ──────────────────────────────────────────────────────

        public override void _GuiInput(InputEvent @event)
        {
            // Story 11.4 (FR-74): Alt+LMB drops a minimap PING (shown immediately for the initiator; replicated to
            // allies in MP via OnLocalPing) rather than panning.
            //
            // Review fix: this branch consumed only the PRESS. The matching RELEASE fell through to the pan branch
            // below (which never tested mb.Pressed), and any drag between the two hit the motion branch, so an
            // Alt-click both pinged AND yanked the camera to the pinged point — confirmed in-engine, the pivot landed
            // on the clicked world position. _pingGesture now swallows the rest of the gesture.
            if (@event is InputEventMouseButton alt && alt.Pressed && alt.ButtonIndex == MouseButton.Left && alt.AltPressed)
            {
                _pingGesture = true;
                Vector3 world = MinimapToWorld(alt.Position);
                AddPing(world.X, world.Z, P1_COLOR); // the initiator sees its own ping in the own-view colour
                OnLocalPing?.Invoke(world);
                AcceptEvent();
                return;
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (!mb.Pressed)
                {
                    // End of the gesture. Always clear the latch here (a stranded latch would swallow the next real
                    // click) and never pan on release — the press and drag branches already moved the camera.
                    bool wasPing = _pingGesture;
                    _pingGesture = false;
                    if (wasPing) { AcceptEvent(); return; }
                    AcceptEvent();
                    return;
                }

                // A plain (Alt-free) press self-heals a latch stranded by a release delivered outside this Control.
                // DW-940: click-to-pan is RETIRED (2026-08-12, Alec's control scheme: camera moves on ARROW KEYS
                // ONLY — no edge scroll, no minimap jump). The click is still consumed (never falls through to the
                // 3D world as a unit order); Alt+LMB pings above are untouched. _camCtrl stays wired for the
                // camera-view box drawn on the dot overlay.
                _pingGesture = false;
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion motion &&
                     (motion.ButtonMask & MouseButtonMask.Left) != 0)
            {
                if (_pingGesture) { AcceptEvent(); return; } // dragging after a ping must not pan
                AcceptEvent(); // DW-940: drag-to-pan retired with click-to-pan (see above)
            }
        }

        // ── Fog texture update ────────────────────────────────────────────────

        private void UpdateFogTexture()
        {
            if (_fogImage == null || _fogTexture == null || _fog == null || _fogData == null) return;

            int    gridSize = FogOfWarSystem.GRID_SIZE;
            byte[] grid     = _fog.Grid;
            int    cells    = gridSize * gridSize;
            bool   revealAll = _revealAll(); // DW-406: hoisted — invariant across this frame's texture fill

            for (int i = 0; i < cells; i++)
            {
                byte alpha   = MinimapFogPolicy.FogAlpha(grid[i], revealAll);
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
            Faction me       = _localFaction(); // hoist out of the per-entity loops (invariant across this draw)
            bool matchRevealed = _revealAll();  // DW-406/DW-408: ditto — one read per draw
            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;
                if ((_world.Flags[i] & EntityFlags.Phased) != 0) continue; // DW-938: inside a building — no dot
                float wx = _world.Position[i].X.ToFloat();
                float wz = _world.Position[i].Z.ToFloat();
                bool  own = _world.FactionOf[i] == me;
                // DW-408: enemy dots draw only where the local fog currently SEES (own dots always; spectator
                // reveal-all and a fog-free minimap draw everything) — the minimap respects the main view's vision.
                if (!MinimapFogPolicy.ShouldDrawDot(own, matchRevealed, _fog, wx, wz)) continue;
                Vector2 px = WorldToMinimap(wx, wz);
                Color col = own ? P1_COLOR : P2_COLOR;
                canvas.DrawCircle(px, UNIT_RADIUS, col);
            }

            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                float wx = _buildings.Position[i].X.ToFloat();
                float wz = _buildings.Position[i].Z.ToFloat();
                bool  own = _buildings.FactionOf[i] == me;
                // DW-408: same gate for buildings — an unscouted enemy base must not pre-print on the minimap.
                if (!MinimapFogPolicy.ShouldDrawDot(own, matchRevealed, _fog, wx, wz)) continue;
                Vector2 px = WorldToMinimap(wx, wz);
                Color col = own ? P1_COLOR : P2_COLOR;
                canvas.DrawRect(
                    new Rect2(px - Vector2.One * BLDG_RADIUS, Vector2.One * BLDG_RADIUS * 2f),
                    col);
            }

            // Story 11.4 (FR-74): decaying ping / alert-flash rings (drawn ABOVE the dots).
            foreach (Marker m in _markers)
            {
                float t   = m.Ttl > 0f ? Mathf.Clamp(m.Age / m.Ttl, 0f, 1f) : 1f;
                float r   = 2f + m.MaxRadius * t;            // expand outward
                var   col = new Color(m.Color.R, m.Color.G, m.Color.B, 1f - t); // fade out
                Vector2 c = WorldToMinimap(m.WorldX, m.WorldZ);
                canvas.DrawArc(c, r, 0f, Mathf.Tau, 20, col, 1.5f);
            }

            // Story 11.4 (FR-74): the camera-view box — always drawn so the player knows what the main view covers.
            // Review follow-up: draw the camera's TRUE ground footprint (a trapezoid — the far edge is wider than the
            // near one on a tilted camera) rather than its axis-aligned bounds, which overstated the view so badly at
            // default zoom that the "box" covered the entire minimap.
            if (_camCtrl != null)
            {
                if (_camCtrl.TryGetViewQuad(out Vector2 qtl, out Vector2 qtr, out Vector2 qbr, out Vector2 qbl))
                {
                    var ring = new Vector2[5];
                    ring[0] = WorldToMinimap(qtl.X, qtl.Y);
                    ring[1] = WorldToMinimap(qtr.X, qtr.Y);
                    ring[2] = WorldToMinimap(qbr.X, qbr.Y);
                    ring[3] = WorldToMinimap(qbl.X, qbl.Y);
                    ring[4] = ring[0]; // close the ring
                    canvas.DrawPolyline(ring, CAM_BOX_COL, 1.0f);
                }
                else
                {
                    Rect2 vb = _camCtrl.GetViewBounds(); // headless fallback — world XZ (position = min corner)
                    Vector2 a = WorldToMinimap(vb.Position.X, vb.Position.Y);
                    Vector2 b = WorldToMinimap(vb.Position.X + vb.Size.X, vb.Position.Y + vb.Size.Y);
                    canvas.DrawRect(new Rect2(a, b - a), CAM_BOX_COL, filled: false, width: 1.0f);
                }
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
