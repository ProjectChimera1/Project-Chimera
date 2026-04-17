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
        /// <summary>Whether edge-of-screen panning is active. Toggled in-game with E.</summary>
        [Export] public bool EdgeScrollEnabled { get; set; } = true;
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
                switch (mb.ButtonIndex)
                {
                    case MouseButton.Middle:
                        _middleHeld = mb.Pressed;
                        break;
                    case MouseButton.WheelUp:
                        _zoomDist = Mathf.Clamp(_zoomDist - ZoomStep * ZoomSpeedMultiplier, ZoomMin, ZoomMax);
                        break;
                    case MouseButton.WheelDown:
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

            // WASD / arrows
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))    move += forward;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))  move -= forward;
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left))  move -= right;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) move += right;

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
    }
}
