using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Global game mode: Edit (place units, no simulation) vs Play (simulation runs).
    /// Add as a child node in the main scene; access via GameState.Instance.
    /// Toggle with F5.
    /// </summary>
    public enum GameMode { Edit, Play }

    public partial class GameState : Node
    {
        public static GameState Instance { get; private set; }

        public GameMode Mode { get; private set; } = GameMode.Edit;

        [Signal]
        public delegate void ModeChangedEventHandler(int newMode);

        public override void _Ready()
        {
            Instance = this;
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (@event is InputEventKey key && key.Pressed && !key.Echo
                && key.Keycode == Key.F5)
            {
                Toggle();
            }
        }

        /// <summary>Flip between Edit and Play modes.</summary>
        public void Toggle()
        {
            Mode = Mode == GameMode.Edit ? GameMode.Play : GameMode.Edit;
            EmitSignal(SignalName.ModeChanged, (int)Mode);
            GD.Print($"[GameState] Mode → {Mode}");
        }

        /// <summary>Switch to a specific mode (no-op if already in that mode).</summary>
        public void SetMode(GameMode mode)
        {
            if (Mode == mode) return;
            Mode = mode;
            EmitSignal(SignalName.ModeChanged, (int)Mode);
            GD.Print($"[GameState] Mode → {Mode}");
        }
    }
}
