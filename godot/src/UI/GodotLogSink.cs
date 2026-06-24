#nullable enable
using Godot;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presentation <see cref="ILogSink"/> that routes sim-spine logging to the Godot console
    /// (<c>Info → GD.Print</c>, <c>Warn → GD.PrintErr</c>). It lives in <c>src/UI</c> — deliberately OUTSIDE
    /// the Tier-1 sim-folder globs — so its <c>using Godot;</c> never pulls GodotSharp into the Godot-free
    /// test assembly (which would fail <c>GodotFreeBoundaryTest</c>). MainScene injects it into
    /// <see cref="SimulationHost"/>.
    /// </summary>
    public sealed class GodotLogSink : ILogSink
    {
        public void Info(string message) => GD.Print(message);
        public void Warn(string message) => GD.PrintErr(message);
    }
}
