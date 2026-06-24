#nullable enable
using System;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// An <see cref="ISetupPhase"/> whose body is an injected <see cref="System.Action"/>. This is the Step-7
    /// "no bodies move" vehicle: MainScene wraps each existing <c>SetupX()</c> method in one of these so the
    /// procedural <c>_Ready</c> sequence becomes an asserted <see cref="ISetupPhase"/>[] literal without
    /// relocating any setup code. Step 8 then replaces each wrapper with a concrete <c>*Phase</c> class one at
    /// a time. Godot-free — it holds only a name and an <see cref="Action"/>, never a Godot type.
    /// </summary>
    public sealed class DelegateSetupPhase : ISetupPhase
    {
        private readonly Action _run;

        /// <param name="name">The canonical phase identity (must match <see cref="ScenePhaseOrder.Canonical"/>).</param>
        /// <param name="run">The setup body to invoke — typically an existing MainScene <c>SetupX</c> method group.</param>
        public DelegateSetupPhase(string name, Action run)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _run = run ?? throw new ArgumentNullException(nameof(run));
        }

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public void Run() => _run();
    }
}
