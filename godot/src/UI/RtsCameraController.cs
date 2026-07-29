using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// RTS camera rig. This Node3D acts as the ground-level pivot.
    /// A Camera3D child orbits it at a configurable pitch and distance.
    ///
    /// Controls:
    ///   Pan:    WASD or arrow keys, plus edge-scroll when mouse nears viewport edge
    ///   Zoom:   Scroll wheel
    ///   Rotate: Hold middle mouse + drag horizontally
    ///   Tilt:   Hold middle mouse + drag vertically
    ///   E:      Toggle edge-of-screen panning on/off
    /// </summary>
    public partial class RtsCameraController : Node3D
    {
        [Export] public float PanSpeed { get; set; } = 30.0f;
        [Export] public float EdgeScrollMargin { get; set; } = 20.0f; // px
        /// <summary>Whether edge-of-screen panning is active. Off by default (avoids the camera flinging
        /// to a corner on load); toggle it on in-game with E.</summary>
        [Export] public bool EdgeScrollEnabled { get; set; } = false;
        [Export] public float ZoomStep { get; set; } = 8.0f;

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
            if (@event is InputEventKey key && key.Pressed && !key.Echo
                && key.Keycode == Key.E)
            {
                EdgeScrollEnabled = !EdgeScrollEnabled;
                GD.Print($"[Camera] Edge scroll {(EdgeScrollEnabled ? "ON" : "OFF")}");
                GetViewport().SetInputAsHandled();
                return;
            }

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

            // WASD / arrows — but NOT while typing into a text field. HandlePan polls Input.IsKeyPressed directly,
            // which bypasses GUI focus, so without this guard every letter typed into an editor field (id, name, …)
            // would also drive the camera (a→left, d→right, arrows→cursor+pan). Suppress keyboard pan whenever a
            // LineEdit/TextEdit owns focus (this also covers a SpinBox's internal LineEdit).
            if (!IsTypingInTextField())
            {
                if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))    move += forward;
                if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))  move -= forward;
                if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left))  move -= right;
                if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) move += right;
            }

            // Edge scroll (only when enabled and no middle-mouse drag to avoid fighting)
            if (EdgeScrollEnabled && !_middleHeld)
            {
                var rect = GetViewport()?.GetVisibleRect() ?? new Rect2(Vector2.Zero, Vector2.Zero);
                if (_mousePos.X < EdgeScrollMargin)               move -= right;
                if (_mousePos.X > rect.Size.X - EdgeScrollMargin) move += right;
                if (_mousePos.Y < EdgeScrollMargin)               move += forward;
                if (_mousePos.Y > rect.Size.Y - EdgeScrollMargin) move -= forward;
            }

            if (move.LengthSquared() > 0.001f)
                Position += move.Normalized() * (PanSpeed * PanSpeedMultiplier) * (float)delta;
        }

        /// <summary>True when a text-editing control owns keyboard focus (a LineEdit or TextEdit, including a
        /// SpinBox's internal LineEdit), so <see cref="HandlePan"/> must not consume WASD/arrows as camera pan.</summary>
        private bool IsTypingInTextField()
        {
            Control focus = GetViewport()?.GuiGetFocusOwner();
            return focus is LineEdit || focus is TextEdit;
        }

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
            const float MAP_HALF = 128f;
            GlobalPosition = new Vector3(
                Mathf.Clamp(worldPos.X, -MAP_HALF, MAP_HALF),
                GlobalPosition.Y,
                Mathf.Clamp(worldPos.Z, -MAP_HALF, MAP_HALF));
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
            Vector3 c = GlobalPosition;

            // Headless / not-yet-in-tree fallback: no viewport to project through, so keep the old zoom-derived
            // square. Only reachable before _Ready or outside a running tree; never on the live gate path.
            Viewport vp = _camera?.GetViewport();
            if (vp == null)
            {
                float half = _zoomDist * 0.85f;
                return new Rect2(c.X - half, c.Z - half, half * 2f, half * 2f);
            }

            Vector2 size = vp.GetVisibleRect().Size;
            if (size.X <= 0f || size.Y <= 0f)
            {
                float half = _zoomDist * 0.85f;
                return new Rect2(c.X - half, c.Z - half, half * 2f, half * 2f);
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int corner = 0; corner < 4; corner++)
            {
                var screen = new Vector2((corner & 1) == 0 ? 0f : size.X,
                                         (corner & 2) == 0 ? 0f : size.Y);
                Vector3 origin = _camera.ProjectRayOrigin(screen);
                Vector3 dir    = _camera.ProjectRayNormal(screen);

                // Intersect with the pivot's ground plane (y = c.Y). dir.Y < 0 means the ray descends toward it;
                // anything else clears the horizon and is clamped to MAX_GROUND_REACH along the ray instead.
                float t = MAX_GROUND_REACH;
                if (dir.Y < -0.0001f)
                    t = Mathf.Min((origin.Y - c.Y) / -dir.Y, MAX_GROUND_REACH);

                Vector3 hit = origin + dir * t;
                minX = Mathf.Min(minX, hit.X); maxX = Mathf.Max(maxX, hit.X);
                minZ = Mathf.Min(minZ, hit.Z); maxZ = Mathf.Max(maxZ, hit.Z);
            }

            return new Rect2(minX, minZ, maxX - minX, maxZ - minZ);
        }

        /// <summary>Story 11.4 — is the given world position currently inside the camera view (XZ)? The under-attack
        /// alert fires only when this is FALSE (the player cannot already see the hit).</summary>
        public bool IsInView(Vector3 worldPos)
            => GetViewBounds().HasPoint(new Vector2(worldPos.X, worldPos.Z));
    }
}
