#nullable enable
namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Deterministic logging seam (Story 1.8a / AR-4). Sim and Godot-free code logs ONLY through this —
    /// never <c>GD.Print</c>/<c>Console</c> — so the headless server and the Godot-free Tier-1 test project
    /// run without perturbing the tick or dragging GodotSharp into the sim assembly.
    ///
    /// Always INJECT an implementation (never a static ambient sink): each host supplies its own
    /// (<see cref="NullLogSink"/> for tests + the future ServerBootstrap; a presentation GodotLogSink for
    /// MainScene). Keep the surface tiny and string-based — there is no per-tick / per-entity logging path
    /// (the structured Fixed.Raw-arg refinement is deliberately deferred).
    /// </summary>
    public interface ILogSink
    {
        /// <summary>Low-frequency informational / diagnostic message.</summary>
        void Info(string message);

        /// <summary>Warning or recoverable-error message.</summary>
        void Warn(string message);
    }
}
