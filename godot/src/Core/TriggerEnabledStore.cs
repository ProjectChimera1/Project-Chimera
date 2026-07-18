#nullable enable
using System;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 7.13 — the sim-owned <b>trigger-enabled runtime mask</b>: a per-<c>_execs</c>-index boolean flipped by
    /// the <c>enable_trigger</c>/<c>disable_trigger</c> action leaves and consulted alongside the authored
    /// <c>TriggerNode.Enabled</c> at BOTH of <c>ScenarioDirector</c>'s sweep gates. Because a trigger can enable or
    /// disable ANOTHER trigger mid-match, this flag is genuinely mutable cross-tick sim truth and folds into
    /// <see cref="SimChecksum"/> (v21) — a peer whose enabled set diverges evaluates different triggers and must
    /// desync detectably. Idiomatically parallel to <see cref="WinStateStore"/>/<see cref="AllianceStore"/>:
    /// integer/bool-only, pure sim (no Godot / fractional primitive / wall-clock).
    ///
    /// <para><b>Stable reference, growable buffer.</b> <see cref="ProjectChimera.Core.Sim.SimulationHost"/> constructs
    /// this ONCE and shares it BY REFERENCE with both <c>ScenarioDirector</c> (the writer) and the checksum wiring
    /// (the folder) — it is NEVER reallocated per <c>LoadScenario</c> (unlike <c>_triggerFired</c>/<c>_triggerCooldown</c>,
    /// which are NOT folded). <see cref="Reset"/> grows/reuses the buffer in place and seeds every entry enabled; the
    /// director then overwrites per-exec from the authored <c>TriggerNode.Enabled</c> via <see cref="SetInitial"/>.</para>
    ///
    /// <para><b>Fold (v21):</b> in exec order with NO count prefix (the exec count is static model state already
    /// covered by <c>CanonicalModelHash</c>): <c>for i in 0..Count: Mix(IsEnabled(i) ? 1 : 0)</c>. A <b>null</b> store
    /// folds NOTHING (zero Mix calls — the true no-op absent path the re-baseline differential guard relies on), and a
    /// store whose <see cref="Count"/> is 0 (a trigger-less scenario, or a store <see cref="Clear"/>ed for reset)
    /// likewise folds nothing — so a scenario carrying zero DSL triggers hashes byte-identically to its pre-story
    /// (v20) sequence apart from the leading AlgoVersion bump semantics.</para>
    /// </summary>
    public sealed class TriggerEnabledStore
    {
        private bool[] _enabled = Array.Empty<bool>();

        /// <summary>The number of live exec entries folded (== the loaded scenario's trigger count). 0 for a
        /// trigger-less scenario or a <see cref="Clear"/>ed store — either folds nothing.</summary>
        public int Count { get; private set; }

        /// <summary>Resize (grow/reuse) the buffer to <paramref name="count"/> entries and seed EVERY entry enabled.
        /// Called once per <c>LoadScenario</c> after the exec list is built; the director then seeds the authored
        /// initial state per exec via <see cref="SetInitial"/>.</summary>
        public void Reset(int count)
        {
            if (count < 0) count = 0;
            if (_enabled.Length < count) _enabled = new bool[count];
            for (int i = 0; i < count; i++) _enabled[i] = true;
            Count = count;
        }

        /// <summary>Seed the authored initial enabled state for exec <paramref name="i"/> (from
        /// <c>TriggerNode.Enabled</c>) immediately after <see cref="Reset"/>. Out-of-range is a silent no-op.</summary>
        public void SetInitial(int i, bool enabled)
        {
            if ((uint)i < (uint)Count) _enabled[i] = enabled;
        }

        /// <summary>Flip the runtime enabled flag for exec <paramref name="execIdx"/> (the
        /// <c>enable_trigger</c>/<c>disable_trigger</c> runtime write). Out-of-range is a silent no-op.</summary>
        public void Set(int execIdx, bool enabled)
        {
            if ((uint)execIdx < (uint)Count) _enabled[execIdx] = enabled;
        }

        /// <summary>True when exec <paramref name="execIdx"/> is runtime-enabled. An out-of-range index returns true
        /// (defensive: the reset-window between <see cref="Clear"/> and the next <see cref="Reset"/> must never
        /// wrongly SUPPRESS a trigger — the sweep already checks <c>TriggerNode.Enabled</c> too).</summary>
        public bool IsEnabled(int execIdx) => (uint)execIdx < (uint)Count ? _enabled[execIdx] : true;

        /// <summary>Story 3.10-style Edit↔Play reset: empty the store (<see cref="Count"/> → 0) so a re-apply's
        /// <see cref="Reset"/> re-seeds it non-additively. A cleared store folds NOTHING (byte-identical to a null
        /// store) until the next <c>LoadScenario</c>.</summary>
        public void Clear() => Count = 0;
    }
}
