#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// slider (UX-DR21) as a thin stateful component (Story 3.1b D-2): a 6px chamfered track with a 14×18
    /// accent thumb (cut 4), paired with a <see cref="ChimeraComponents.NumInput"/> synced bidirectionally.
    ///
    /// Built as a custom composite rather than an <see cref="HSlider"/> because HSlider draws its grabber
    /// from a Texture2D icon — it can't carry a chamfered StyleBoxFlat thumb registered with the
    /// <see cref="AccentController"/>. Here the thumb is a real <see cref="Panel"/> whose accent stylebox
    /// retints on a switch like the rest of the kit (AC4). Carries plain <c>double</c> values (Fixed↔form
    /// binding is a 3.3+ editor concern, not the styled control's job).
    ///
    /// Presentation layer.
    /// </summary>
    public partial class ChimeraSlider : HBoxContainer
    {
        /// <summary>Emitted with the slider's new value on any change (drag or num-input).</summary>
        [Signal]
        public delegate void ValueChangedEventHandler(double value);

        private SliderTrack _track = null!;
        private SpinBox _num = null!;
        private bool _syncing;

        /// <summary>Current value.</summary>
        public double Value
        {
            get => _track.Value;
            set => _track.Value = value;
        }

        /// <summary>Build a slider + paired num-input over [min, max] with the given step.</summary>
        public static ChimeraSlider Create(double value = 0, double min = 0, double max = 100, double step = 1)
        {
            // Guard inverted bounds: System.Math.Clamp (SliderTrack.SetValue) throws when min > max, and it
            // fires synchronously during Create — unlike NumInput's Godot Range, which tolerates it.
            if (max < min) max = min;

            var s = new ChimeraSlider();
            s.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));

            s._track = new SliderTrack { MinValue = min, MaxValue = max, Step = step };
            s._track.Build();
            s._track.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            s._track.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            s.AddChild(s._track);

            s._num = ChimeraComponents.NumInput(value, min, max, step);
            s.AddChild(s._num);

            // Bidirectional sync (guarded against feedback loops).
            s._track.ValueChanged += v =>
            {
                if (!s._syncing) { s._syncing = true; s._num.Value = v; s._syncing = false; }
                s.EmitSignal(SignalName.ValueChanged, v);
            };
            s._num.ValueChanged += v =>
            {
                if (!s._syncing) { s._syncing = true; s._track.Value = v; s._syncing = false; }
            };

            s._track.Value = value;
            return s;
        }
    }

    /// <summary>
    /// The draggable track + accent thumb behind <see cref="ChimeraSlider"/>. A bare Control that draws a
    /// centered 6px track and a positioned 14×18 accent thumb, and maps mouse drags to a stepped value.
    /// Separate top-level class (not nested) so it can override <c>_GuiInput</c> as a Godot node.
    /// </summary>
    public partial class SliderTrack : Control
    {
        /// <summary>Emitted with the new value whenever it changes.</summary>
        [Signal]
        public delegate void ValueChangedEventHandler(double value);

        public double MinValue = 0;
        public double MaxValue = 100;
        public double Step = 1;

        private double _value;
        private Panel _thumb = null!;
        private bool _dragging;

        /// <summary>Current value; setting it clamps, snaps to step, and repositions the thumb.</summary>
        public double Value
        {
            get => _value;
            set => SetValue(value);
        }

        /// <summary>Construct the track + thumb subtree. Call once after newing.</summary>
        public void Build()
        {
            CustomMinimumSize = new Vector2(80, ComponentMetrics.SliderThumbHeight); // 18 tall (thumb height)
            MouseFilter = MouseFilterEnum.Stop;

            // Track: full width, 6px, vertically centered.
            var track = new Panel();
            track.AddThemeStyleboxOverride("panel",
                ChimeraStyleBox.Chamfer(0, ChimeraComponents.Col(ThemeTokens.Surface3), ChimeraComponents.Col(ThemeTokens.Line)));
            track.AnchorLeft = 0f;
            track.AnchorRight = 1f;
            track.AnchorTop = 0.5f;
            track.AnchorBottom = 0.5f;
            float halfTrack = ComponentMetrics.SliderTrackHeight / 2f;
            track.OffsetTop = -halfTrack;
            track.OffsetBottom = halfTrack;
            track.MouseFilter = MouseFilterEnum.Ignore; // clicks fall through to this control
            AddChild(track);

            // Thumb: 14×18 accent (cut 4), shared + registered so it retints on an accent switch.
            _thumb = new Panel();
            var thumbBox = ChimeraComponents.SharedAccentBox("slider/thumb",
                () => ChimeraStyleBox.Chamfer(ComponentMetrics.CutThumb, ChimeraComponents.Col(ThemeTokens.Accent), ChimeraComponents.Col(ThemeTokens.Accent), 0),
                ChimeraComponents.Fill(ThemeTokens.Accent), ChimeraComponents.Border(ThemeTokens.Accent));
            _thumb.AddThemeStyleboxOverride("panel", thumbBox);
            _thumb.AnchorTop = 0.5f;
            _thumb.AnchorBottom = 0.5f;
            _thumb.OffsetTop = -ComponentMetrics.SliderThumbHeight / 2f;
            _thumb.OffsetBottom = ComponentMetrics.SliderThumbHeight / 2f;
            _thumb.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(_thumb);

            Resized += UpdateThumb;
            UpdateThumb();
        }

        /// <inheritdoc/>
        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                _dragging = mb.Pressed;
                if (mb.Pressed) SetValueFromX(mb.Position.X);
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion mm && _dragging)
            {
                SetValueFromX(mm.Position.X);
                AcceptEvent();
            }
        }

        private void SetValueFromX(float x)
        {
            float w = Size.X;
            if (w <= 0f) return;
            double ratio = Mathf.Clamp(x / w, 0f, 1f);
            SetValue(MinValue + ratio * (MaxValue - MinValue));
        }

        private void SetValue(double value)
        {
            // double math — Godot's Mathf is float-only, so use System.Math for the value domain.
            double clamped = System.Math.Clamp(value, MinValue, MaxValue);
            // Snap MIN-relative (not zero-relative) to match Godot's Range/SpinBox grid, so the thumb and the
            // paired num-input agree even when MinValue is not an integer multiple of Step (3.1b review).
            if (Step > 0) clamped = MinValue + System.Math.Round((clamped - MinValue) / Step) * Step;
            clamped = System.Math.Clamp(clamped, MinValue, MaxValue);
            if (System.Math.Abs(clamped - _value) < 1e-9) { UpdateThumb(); return; }
            _value = clamped;
            UpdateThumb();
            EmitSignal(SignalName.ValueChanged, _value);
        }

        private void UpdateThumb()
        {
            if (_thumb == null) return;
            double span = MaxValue - MinValue;
            double ratio = span > 0 ? (_value - MinValue) / span : 0;
            float usable = Mathf.Max(0f, Size.X - ComponentMetrics.SliderThumbWidth);
            float x = (float)ratio * usable;
            _thumb.OffsetLeft = x;
            _thumb.OffsetRight = x + ComponentMetrics.SliderThumbWidth;
        }
    }
}
