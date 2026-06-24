#nullable enable
namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// No-op <see cref="ILogSink"/> for the Godot-free contexts: the Tier-1 test project and (Story 1.9a)
    /// the headless ServerBootstrap. Drops every message so a headless/test tick stays side-effect-free.
    /// </summary>
    public sealed class NullLogSink : ILogSink
    {
        /// <summary>Shared instance — the sink is stateless, so a singleton is safe and convenient.</summary>
        public static readonly NullLogSink Instance = new();

        public void Info(string message) { }
        public void Warn(string message) { }
    }
}
