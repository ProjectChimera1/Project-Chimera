#nullable enable

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// One ordered step of the MainScene composition root (Story 1.8c / AR-3). A phase closes over its
    /// constructor-injected dependencies and its products, so <see cref="Run"/> is parameterless and this
    /// contract carries no Godot type — that is what keeps the phase kernel Godot-free and Tier-1-testable
    /// (the <c>PhaseOrderTest</c> pins phases by <see cref="Name"/>, never by a concrete presentation type).
    /// </summary>
    public interface ISetupPhase
    {
        /// <summary>The Godot-free identity that <c>PhaseOrderTest</c> and <see cref="ScenePhaseRunner"/> pin.</summary>
        string Name { get; }

        /// <summary>
        /// Execute this setup step. The phase already holds everything it needs (injected at construction),
        /// so this takes no arguments. The runner asserts the canonical order before invoking any phase, so a
        /// phase never has to defend its own position.
        /// </summary>
        void Run();
    }
}
