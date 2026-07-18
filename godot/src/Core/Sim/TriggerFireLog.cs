#nullable enable
using System;

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Story 7.15 — the trigger-debugging OBSERVATION BUFFER: per-exec fire counters + a fixed-capacity,
    /// tick-stamped ring of recent fires. Godot-free and pure (int-only: no <c>using Godot</c>, no
    /// <c>float</c>/<c>double</c>/<c>Mathf</c>, no wall-clock, no string formatting), so it compiles into the
    /// Godot-free Tier-1 test project and is exercised by headless determinism tests.
    ///
    /// <para><b>NEVER folded into <c>SimChecksum</c>.</b> This buffer is NEVER passed to
    /// <c>SimChecksum.Compute</c> and is NEVER registered with any <c>FoldInto</c> — the exact non-folded posture
    /// documented for <see cref="DslVarReadback"/>/<c>MatchStats</c> (<c>SimChecksum.cs</c>). It is written
    /// UNCONDITIONALLY at the single <c>ScenarioDirector.FireTrigger</c> choke point on every fire, regardless of
    /// whether any overlay exists or is visible, so two runs (overlay open vs closed, buffer attached vs not)
    /// perform byte-identical folded work and produce byte-identical <c>SimChecksum</c> streams. The overlay's
    /// visibility gates only the presentation-side PULL, never the sim-side write.</para>
    ///
    /// <para><b>Stable reference.</b> <see cref="ProjectChimera.Core.Sim.SimulationHost"/> constructs this ONCE and
    /// shares it BY REFERENCE with <c>ScenarioDirector</c> (the only writer). <see cref="Reset"/> grows/reuses the
    /// counts buffer in place and clears the ring alongside the director's <c>_triggerFired</c>/<c>_triggerCooldown</c>
    /// reallocation at <c>LoadScenario</c>, so an F5 Edit→Play re-apply starts with fresh counters and an empty log.</para>
    /// </summary>
    public sealed class TriggerFireLog
    {
        /// <summary>One recorded fire: the exec index that fired and the deterministic sim tick it fired on. Carries
        /// only ints — the human-readable trigger name is resolved PRESENTATION-side, never in the tick.</summary>
        public readonly struct FireEntry
        {
            public readonly int ExecIdx;
            public readonly int Tick;
            public FireEntry(int execIdx, int tick) { ExecIdx = execIdx; Tick = tick; }
        }

        /// <summary>Fixed capacity of the recent-fire ring (the "last N" the overlay renders newest-first).</summary>
        public const int RingCapacity = 256;

        // ── Per-exec fire counters (indexed by _execs position) ──
        private int[] _counts = Array.Empty<int>();
        private int   _execCount;

        // ── exec idx → authored ScenarioData.Triggers[] index ──
        // Exec order is (Priority desc, node-id asc), which diverges from authored order under non-default priority;
        // the debug overlay's names + click-to-navigate need the AUTHORED index, so the director supplies this map at
        // LoadScenario via SetAuthoredMapping. Defaults to identity (correct for the all-default-priority common case
        // and a safe fallback until the map is set). Presentation-only — never folded.
        private int[] _execToAuthored = Array.Empty<int>();

        // ── Fixed-capacity ring of recent fires ──
        private readonly FireEntry[] _ring = new FireEntry[RingCapacity];
        private int _ringHead; // next write position
        private int _ringLen;  // valid entries (<= RingCapacity)

        // Monotonic total number of Record() calls since the last Reset/Clear. The overlay watches this to append
        // only NEW fires (and to detect a reset — it going backwards means the log cleared). NOT folded.
        private long _totalRecorded;

        // Bumped on every Reset/Clear. Presentation reads it to detect a sim reset UNAMBIGUOUSLY: after an F5
        // re-apply of a match_start-heavy scenario the post-reset total can climb straight back to the pre-reset
        // high-water within a single frame, so a reader gating solely on TotalRecorded equality would misread it as
        // "nothing new" and leave stale pre-reset rows on screen. NOT folded.
        private int _generation;

        /// <summary>The number of live exec entries whose fire counts are tracked (== the loaded scenario's trigger
        /// count). 0 for a trigger-less scenario or a <see cref="Clear"/>ed store.</summary>
        public int ExecCount => _execCount;

        /// <summary>Number of entries currently held in the recent-fire ring (0..<see cref="RingCapacity"/>).</summary>
        public int RecentCount => _ringLen;

        /// <summary>Monotonic count of every fire recorded since the last <see cref="Reset"/>/<see cref="Clear"/>.
        /// Presentation watches this to append only newly-observed fires and to detect a reset (it drops to 0).</summary>
        public long TotalRecorded => _totalRecorded;

        /// <summary>Bumped on every <see cref="Reset"/>/<see cref="Clear"/>. Presentation watches this to detect a sim
        /// reset unambiguously — <see cref="TotalRecorded"/> alone can return to the same value after an F5 re-apply,
        /// which would otherwise leave a stale fired-log on screen. Presentation-only — never folded.</summary>
        public int Generation => _generation;

        /// <summary>Resize (grow/reuse) the counts buffer to <paramref name="execCount"/> entries, zero them, and
        /// empty the ring. Called once per <c>LoadScenario</c> alongside the director's fire-guard reallocation.</summary>
        public void Reset(int execCount)
        {
            if (execCount < 0) execCount = 0;
            if (_counts.Length < execCount) _counts = new int[execCount];
            for (int i = 0; i < execCount; i++) _counts[i] = 0;
            if (_execToAuthored.Length < execCount) _execToAuthored = new int[execCount];
            for (int i = 0; i < execCount; i++) _execToAuthored[i] = i; // identity until the director supplies the map
            _execCount     = execCount;
            _ringHead      = 0;
            _ringLen       = 0;
            _totalRecorded = 0;
            _generation++;
        }

        /// <summary>Install the exec→authored-<c>Triggers[]</c> index map (length up to <see cref="ExecCount"/>).
        /// Called by <c>ScenarioDirector.LoadScenario</c> after <see cref="Reset"/>. A pure read-side aid for the
        /// overlay's names/navigation — no folded state, no effect on <c>SimChecksum</c>.</summary>
        public void SetAuthoredMapping(ReadOnlySpan<int> execToAuthored)
        {
            int n = Math.Min(execToAuthored.Length, _execCount);
            for (int i = 0; i < n; i++) _execToAuthored[i] = execToAuthored[i];
        }

        /// <summary>Map an exec index to its authored <c>ScenarioData.Triggers[]</c> index (identity fallback for an
        /// out-of-range index or before <see cref="SetAuthoredMapping"/> runs).</summary>
        public int AuthoredIndex(int execIdx) =>
            (uint)execIdx < (uint)_execCount ? _execToAuthored[execIdx] : execIdx;

        /// <summary>Record one trigger fire. Called UNCONDITIONALLY from <c>ScenarioDirector.FireTrigger</c> AFTER
        /// the folded run-once/cooldown arming — a pure integer increment + ring append, no allocation. An
        /// out-of-range <paramref name="execIdx"/> still rings (defensive) but does not touch the counts buffer.</summary>
        public void Record(int execIdx, int tick)
        {
            if ((uint)execIdx < (uint)_execCount) _counts[execIdx]++;
            _ring[_ringHead] = new FireEntry(execIdx, tick);
            _ringHead = (_ringHead + 1) % RingCapacity;
            if (_ringLen < RingCapacity) _ringLen++;
            _totalRecorded++;
        }

        /// <summary>The fire count for exec <paramref name="execIdx"/> (0 for an out-of-range index).</summary>
        public int Count(int execIdx) => (uint)execIdx < (uint)_execCount ? _counts[execIdx] : 0;

        /// <summary>Read a recent fire, newest-first: <paramref name="i"/>=0 is the most recent, up to
        /// <see cref="RecentCount"/>-1. An out-of-range index returns a default (0,0) entry.</summary>
        public FireEntry Recent(int i)
        {
            if ((uint)i >= (uint)_ringLen) return default;
            int idx = _ringHead - 1 - i;
            idx %= RingCapacity;
            if (idx < 0) idx += RingCapacity;
            return _ring[idx];
        }

        /// <summary>Edit↔Play reset: empty the store (counts + ring) so a re-apply's <see cref="Reset"/> re-seeds it
        /// non-additively. Mirrors <c>TriggerEnabledStore.Clear</c>.</summary>
        public void Clear()
        {
            _execCount     = 0;
            _ringHead      = 0;
            _ringLen       = 0;
            _totalRecorded = 0;
            _generation++;
        }
    }
}
