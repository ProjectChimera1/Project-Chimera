#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Runs the composition-root <see cref="ISetupPhase"/> sequence in order, after first asserting the live
    /// literal matches <see cref="ScenePhaseOrder.Canonical"/>. This is the AR-3 / C1 guarantee that <c>_Ready</c>
    /// can never silently reorder: any drift (reorder / add / remove) throws at startup with a precise diff rather
    /// than booting in the wrong order. Godot-free — it only ever calls through the <see cref="ISetupPhase"/>
    /// interface, mirroring how <c>SimulationHost</c> owns the asserted system order and never a Godot type.
    /// </summary>
    public sealed class ScenePhaseRunner
    {
        private readonly IReadOnlyList<ISetupPhase> _phases;

        /// <param name="phases">The live ordered phase literal from <c>MainScene._Ready</c>.</param>
        public ScenePhaseRunner(IReadOnlyList<ISetupPhase> phases)
            => _phases = phases ?? throw new ArgumentNullException(nameof(phases));

        /// <summary>
        /// Assert the live order matches the canonical order, then run each phase in sequence. Story 11.1: an optional
        /// <paramref name="onPhaseStarting"/> progress seam is invoked immediately BEFORE each <c>phase.Run()</c> (after
        /// <see cref="AssertOrder"/>) with <c>(1-based index, total phase count, phase name)</c> — the real per-phase
        /// signal the loading screen renders. When null the behavior is byte-identical to before (no callback, same
        /// synchronous foreach). The runner stays synchronous; smooth cross-frame animation is a non-goal.
        /// </summary>
        public void Run(System.Action<int, int, string>? onPhaseStarting = null)
        {
            AssertOrder();
            int total = _phases.Count;
            int index = 0;
            foreach (ISetupPhase phase in _phases)
            {
                index++;
                onPhaseStarting?.Invoke(index, total, phase.Name);
                phase.Run();
            }
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> with a precise expected-vs-actual diff if the live
        /// literal drifts from <see cref="ScenePhaseOrder.Canonical"/> in count or in any position. Never silently
        /// reorders. Runs BEFORE any phase body, so a drift halts startup before side effects occur.
        /// </summary>
        public void AssertOrder()
        {
            string[] expected = ScenePhaseOrder.Canonical;

            if (_phases.Count != expected.Length)
                throw new InvalidOperationException(
                    $"ScenePhaseRunner: live phase count {_phases.Count} != canonical {expected.Length}. " +
                    $"Canonical=[{string.Join(", ", expected)}]; " +
                    $"live=[{string.Join(", ", _phases.Select(p => p.Name))}]. " +
                    "Phase order is test-guarded (AR-3/C1) — reconcile ScenePhaseOrder.Canonical and the _Ready literal.");

            for (int i = 0; i < expected.Length; i++)
            {
                if (_phases[i].Name != expected[i])
                    throw new InvalidOperationException(
                        $"ScenePhaseRunner: phase[{i}] is '{_phases[i].Name}' but the canonical order requires " +
                        $"'{expected[i]}'. Phase order is test-guarded (AR-3/C1) — change it only by editing " +
                        "ScenePhaseOrder.Canonical (and PhaseOrderTest), never by reordering the _Ready literal alone.");
            }
        }
    }
}
