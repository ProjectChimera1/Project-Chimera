using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// RTS camera rig. This Node3D acts as the ground-level pivot.
    /// A Camera3D child orbits it at a configurable pitch and distance.
    ///
    /// Controls:
    ///   Pan:    ARROW KEYS ONLY (DW-940 — WASD, edge-scroll and minimap click-pan all retired 2026-08-12)
    ///   Zoom:   Scroll wheel
    ///   Rotate: Hold middle mouse + drag horizontally
    ///   Tilt:   Hold middle mouse + drag vertically
    ///   E:      (retired, DW-940 — was the edge-scroll toggle; the key now falls through unconsumed)
    /// </summary>
    public partial class RtsCameraController : Node3D
    {
        [Export] public float PanSpeed { get; set; } = 30.0f;
        [Export] public float EdgeScrollMargin { get; set; } = 20.0f; // px — RETIRED (DW-940), kept for scene compat
        /// <summary>RETIRED (DW-940): edge scrolling no longer exists — the property remains so scenes/settings that
        /// set it keep loading, but HandlePan never reads it. Whether edge-of-screen panning is active. Off by default (avoids the camera flinging
        /// to a corner on load); toggle it on in-game with E.</summary>
        [Export] public bool EdgeScrollEnabled { get; set; } = false;
        [Export] public float ZoomStep { get; set; } = 8.0f;

        /// <summary>Story 15.2 (Route C) — the PRESENTATION visual half-extent the pan clamp uses: playable
        /// <c>map_bounds</c> + non-playable <c>border_extent</c>, so the camera can travel across a bordered map's full
        /// visual width while the sim/placement stay pinned to ±<c>map_bounds</c>. Presentation-only: never folded into
        /// any hash, never read by the sim. Defaults to 128 (the fixed playable ceiling — today's behaviour) until
        /// <c>ScenarioLoadPhase</c> sets it from the loaded scenario.</summary>
        public float VisualHalfExtent { get; set; } = 128f;

        /// <summary>Multiplier applied on top of PanSpeed. Set from SettingsManager.</summary>
        public float PanSpeedMultiplier  { get; set; } = 1.0f;
        /// <summary>Multiplier applied on top of ZoomStep. Set from SettingsManager.</summary>
        public float ZoomSpeedMultiplier { get; set; } = 1.0f;
        [Export] public float ZoomMin { get; set; } = 8.0f;
        [Export] public float ZoomMax { get; set; } = 150.0f;
        [Export] public float RotateSensitivity { get; set; } = 0.4f; // deg/px
        [Export] public float TiltSensitivity { get; set; } = 0.25f;  // deg/px
        [Export] public float PitchMin { get; set; } = 15.0f;         // degrees above horizontal
        [Export] public float PitchMax { get; set; } = 80.0f;

        private Camera3D _camera;
        private float _pitchDeg = 50.0f;   // degrees above horizontal
        private float _zoomDist = 80.0f;
        private bool _middleHeld;
        private Vector2 _mousePos;

        // Screen shake state
        private float _shakeTime;
        private float _shakeDecay;   // original duration — used to compute decay factor
        private float _shakeStrength;

        public override void _Ready()
        {
            _camera = new Camera3D();
            AddChild(_camera);
            UpdateCameraTransform();
        }

        public override void _Process(double delta)
        {
            HandlePan((float)delta);
            UpdateCameraTransform();
            ApplyShake((float)delta);
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            // DW-940: the E edge-scroll toggle is RETIRED with edge scrolling itself (see HandlePan) — E no longer
            // does anything here, so the key falls through unconsumed for future bindings.

            if (@event is InputEventMouseButton mb)
            {
                // Zoom must not fire while the pointer is over UI (editor panels, the entity palette): the wheel
                // should scroll that panel, not zoom the world camera. GuiGetHoveredControl() is null over the bare
                // 3D viewport, so world zoom still works everywhere the cursor is not sitting on a Control.
                bool pointerOverUi = GetViewport().GuiGetHoveredControl() != null;
                switch (mb.ButtonIndex)
                {
                    case MouseButton.Middle:
                        _middleHeld = mb.Pressed;
                        break;
                    case MouseButton.WheelUp:
                        if (pointerOverUi) break;
                        _zoomDist = Mathf.Clamp(_zoomDist - ZoomStep * ZoomSpeedMultiplier, ZoomMin, ZoomMax);
                        break;
                    case MouseButton.WheelDown:
                        if (pointerOverUi) break;
                        _zoomDist = Mathf.Clamp(_zoomDist + ZoomStep * ZoomSpeedMultiplier, ZoomMin, ZoomMax);
                        break;
                }
            }
            else if (@event is InputEventMouseMotion motion)
            {
                _mousePos = motion.Position;

                if (_middleHeld)
                {
                    // Horizontal drag → yaw (rotate rig around Y)
                    RotateY(Mathf.DegToRad(-motion.Relative.X * RotateSensitivity));

                    // Vertical drag → pitch (tilt camera elevation)
                    _pitchDeg = Mathf.Clamp(
                        _pitchDeg + motion.Relative.Y * TiltSensitivity,
                        PitchMin, PitchMax);
                }
            }
        }

        private void HandlePan(float delta)
        {
            // World-space pan directions derived from rig's current yaw
            Vector3 forward = -GlobalTransform.Basis.Z;
            Vector3 right   =  GlobalTransform.Basis.X;
            // Flatten so pan never moves the pivot up/down
            forward.Y = 0; forward = forward.Normalized();
            right.Y   = 0; right   = right.Normalized();

            Vector3 move = Vector3.Zero;

            // ARROW KEYS ONLY (DW-940, 2026-08-12 — Alec's control scheme). WASD pan is retired: A is now the
            // attack-move chord (SelectionSystem), S was always double-booked with the Stop command, and W/D go
            // with them for a coherent scheme. Edge scroll and minimap click/drag-pan are retired in the same DW —
            // the camera moves on arrows (plus middle-mouse orbit + wheel zoom), full stop.
            // Still NOT while typing into a text field: HandlePan polls Input.IsKeyPressed directly, which
            // bypasses GUI focus, so arrows would otherwise cursor+pan inside editor fields.
            if (!IsTypingInTextField())
            {
                if (Input.IsKeyPressed(Key.Up))    move += forward;
                if (Input.IsKeyPressed(Key.Down))  move -= forward;
                if (Input.IsKeyPressed(Key.Left))  move -= right;
                if (Input.IsKeyPressed(Key.Right)) move += right;
            }

            if (move.LengthSquared() > 0.001f)
                Position += move.Normalized() * (PanSpeed * PanSpeedMultiplier) * (float)delta;
        }

        /// <summary>True when a text-editing control owns keyboard focus (a LineEdit or TextEdit, including a
        /// SpinBox's internal LineEdit), so <see cref="HandlePan"/> must not consume WASD/arrows as camera pan.</summary>
        private bool IsTypingInTextField() => TextFocusGuard.IsTyping(this);

        private void UpdateCameraTransform()
        {
            float pitchRad = Mathf.DegToRad(_pitchDeg);
            // Camera sits above (+Y) and behind (+Z in rig-local space) the pivot
            _camera.Position = new Vector3(
                0f,
                _zoomDist * Mathf.Sin(pitchRad),
                _zoomDist * Mathf.Cos(pitchRad)
            );
            // Always look at the pivot's world position
            _camera.LookAt(GlobalPosition, Vector3.Up);
        }

        /// <summary>
        /// Instantly move the camera pivot to <paramref name="worldPos"/> (XZ only).
        /// Used by the minimap click-to-pan feature.
        /// </summary>
        public void PanTo(Vector3 worldPos)
        {
            // Story 15.2 (Route C): the pan clamp now FOLLOWS the visual extent (map_bounds + border_extent), which
            // ScenarioLoad sets, instead of a fixed ±128. This is intentionally TIGHTER than the old fixed ±128 on a
            // sub-128 map (Small 80 / Medium 120 with no border) — the camera should not pan into off-playable void —
            // and equals ±128 only for a Large or bordered map (The Frontier: 128 + 32 = ±160). Defaults to 128 when no
            // scenario is loaded.
            float half = VisualHalfExtent;
            GlobalPosition = new Vector3(
                Mathf.Clamp(worldPos.X, -half, half),
                GlobalPosition.Y,
                Mathf.Clamp(worldPos.Z, -half, half));
        }

        /// <summary>
        /// Triggers a brief camera shake. A new call overrides a weaker or shorter active shake.
        /// </summary>
        /// <param name="duration">How long the shake lasts in seconds.</param>
        /// <param name="strength">Peak displacement in world units.</param>
        public void SetShake(float duration, float strength)
        {
            // Only override if the new shake is stronger or extends the current one
            if (duration > _shakeTime || strength > _shakeStrength)
            {
                _shakeTime     = duration;
                _shakeDecay    = duration;
                _shakeStrength = strength;
            }
        }

        /// <summary>Applies a decaying random offset to the camera position while shaking.</summary>
        private void ApplyShake(float delta)
        {
            if (_shakeTime <= 0f) return;

            _shakeTime -= delta;
            if (_shakeTime <= 0f)
            {
                _shakeTime = 0f;
                return;
            }

            float t   = _shakeDecay > 0f ? Mathf.Clamp(_shakeTime / _shakeDecay, 0f, 1f) : 0f;
            float str = _shakeStrength * t;

            // Offset the camera in its local XZ plane so the pivot target stays correct
            _camera.Position += new Vector3(
                (float)GD.RandRange(-str, str),
                0f,
                (float)GD.RandRange(-str, str));
        }

        /// <summary>Returns the internal Camera3D for raycasting.</summary>
        public Camera3D GetCamera() => _camera;

        // ── Story 11.4 (FR-74): viewport gate + minimap camera-box source ────────────────────────────────

        /// <summary>How far along a corner ray that never meets the ground plane (it points at or above the horizon —
        /// reachable at low pitch, where the top of the frustum clears horizontal) we still take a sample. Bounds the
        /// box instead of letting it run to infinity; comfortably past the 256-unit map so the effect is "treat the
        /// far distance as visible", which errs toward NOT raising an off-screen alert.</summary>
        private const float MAX_GROUND_REACH = 512f;

        /// <summary>
        /// Story 11.4 — the current camera view as an axis-aligned world-XZ rectangle. Used as the minimap
        /// camera-view box source and as the under-attack "outside viewport" gate.
        ///
        /// <para>Review fix: this was a symmetric <c>zoomDist * 0.85</c> square centered on the pivot, which the rig's
        /// geometry does not produce. At the default pitch the visible ground runs roughly 49 units BEHIND the pivot
        /// and 225 in FRONT, so the old box was wrong in both directions — it alerted for battles the player was
        /// watching, and at <see cref="ZoomMax"/> it spanned 255x255 over a 256x256 map, where the alert could never
        /// fire at all. Now the four viewport corners are projected onto the pivot's ground plane and the true
        /// footprint's AABB is returned, which tracks pitch, FOV, aspect and yaw for free.</para>
        /// </summary>
        public Rect2 GetViewBounds()
        {
            if (!TryGetViewQuad(out Vector2 tl, out Vector2 tr, out Vector2 br, out Vector2 bl))
                return FallbackBounds();

            float minX = Mathf.Min(Mathf.Min(tl.X, tr.X), Mathf.Min(br.X, bl.X));
            float maxX = Mathf.Max(Mathf.Max(tl.X, tr.X), Mathf.Max(br.X, bl.X));
            float minZ = Mathf.Min(Mathf.Min(tl.Y, tr.Y), Mathf.Min(br.Y, bl.Y));
            float maxZ = Mathf.Max(Mathf.Max(tl.Y, tr.Y), Mathf.Max(br.Y, bl.Y));
            return new Rect2(minX, minZ, maxX - minX, maxZ - minZ);
        }

        /// <summary>Headless / not-yet-in-tree fallback: no viewport to project through, so use the old zoom-derived
        /// square. Only reachable before _Ready or outside a running tree; never on the live gate path.</summary>
        private Rect2 FallbackBounds()
        {
            Vector3 c = GlobalPosition;
            float half = _zoomDist * 0.85f;
            return new Rect2(c.X - half, c.Z - half, half * 2f, half * 2f);
        }

        /// <summary>
        /// Story 11.4 — the TRUE ground footprint of the camera: the four viewport corners projected onto the pivot's
        /// ground plane, as world-XZ points in screen-corner order (top-left, top-right, bottom-right, bottom-left).
        ///
        /// <para>Review follow-up: a tilted perspective camera sees a TRAPEZOID, not a rectangle — the far edge is much
        /// wider than the near one. Reporting its axis-aligned bounding box overstated the view badly (613x274 world
        /// units at default zoom against a 256x256 map, i.e. "wider than the whole map"), which made the minimap
        /// camera-view box useless and the under-attack gate far more permissive than the player's actual screen. The
        /// quad is the honest shape; <see cref="GetViewBounds"/> keeps returning its AABB for callers that want a cheap
        /// bound.</para>
        ///
        /// <para>Returns false when there is no viewport to project through (headless / pre-_Ready).</para>
        /// </summary>
        public bool TryGetViewQuad(out Vector2 tl, out Vector2 tr, out Vector2 br, out Vector2 bl)
        {
            tl = tr = br = bl = Vector2.Zero;

            Viewport vp = _camera?.GetViewport();
            if (vp == null) return false;
            Vector2 size = vp.GetVisibleRect().Size;
            if (size.X <= 0f || size.Y <= 0f) return false;

            float planeY = GlobalPosition.Y;
            tl = GroundHit(new Vector2(0f,      0f),      planeY);
            tr = GroundHit(new Vector2(size.X,  0f),      planeY);
            br = GroundHit(new Vector2(size.X,  size.Y),  planeY);
            bl = GroundHit(new Vector2(0f,      size.Y),  planeY);
            return true;
        }

        /// <summary>Project one screen point onto the ground plane y=<paramref name="planeY"/>, as world XZ. A ray that
        /// clears the horizon (dir.Y >= 0, reachable at low pitch where the top of the frustum rises above horizontal)
        /// is clamped to <see cref="MAX_GROUND_REACH"/> along its own direction rather than escaping to infinity.</summary>
        private Vector2 GroundHit(Vector2 screen, float planeY)
        {
            Vector3 origin = _camera.ProjectRayOrigin(screen);
            Vector3 dir    = _camera.ProjectRayNormal(screen);

            float t = MAX_GROUND_REACH;
            if (dir.Y < -0.0001f)
                t = Mathf.Min((origin.Y - planeY) / -dir.Y, MAX_GROUND_REACH);

            Vector3 hit = origin + dir * t;
            return new Vector2(hit.X, hit.Z);
        }

        /// <summary>Story 11.4 — is the given world position currently inside the camera view (XZ)? The under-attack
        /// alert fires only when this is FALSE (the player cannot already see the hit).
        ///
        /// <para>Tests the real trapezoid, not its bounding box: the AABB's corners lie well outside the visible
        /// ground at any tilt, so a bounds test called hits "on screen" that the player could not see and silently
        /// swallowed their alert.</para></summary>
        public bool IsInView(Vector3 worldPos)
        {
            var p = new Vector2(worldPos.X, worldPos.Z);
            if (!TryGetViewQuad(out Vector2 tl, out Vector2 tr, out Vector2 br, out Vector2 bl))
                return FallbackBounds().HasPoint(p);
            return PointInQuad(p, tl, tr, br, bl);
        }

        /// <summary>Crossing-number point-in-polygon over a 4-point ring. Handles the non-convex ring that horizon
        /// clamping can produce, which a half-plane (all-same-side) test would get wrong.</summary>
        private static bool PointInQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            => RayCrosses(p, a, b) ^ RayCrosses(p, b, c) ^ RayCrosses(p, c, d) ^ RayCrosses(p, d, a);

        /// <summary>Does the +X ray from <paramref name="p"/> cross edge a→b? Half-open in Y so a vertex shared by two
        /// edges is counted exactly once.</summary>
        private static bool RayCrosses(Vector2 p, Vector2 a, Vector2 b)
        {
            if ((a.Y > p.Y) == (b.Y > p.Y)) return false;
            float t = (p.Y - a.Y) / (b.Y - a.Y);
            return p.X < a.X + t * (b.X - a.X);
        }
    }
}
