#nullable enable
using System;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.5 — the cross-tick custom-event queue: <c>raise_event</c> actions with <c>next_tick: true</c>
    /// enqueue here; <c>ScenarioDirector.Tick</c> dequeues ALL pending entries into the same-tick drain seed at
    /// tick start, then clears. Unlike <c>CombatEventQueue</c>/<c>DeathFeed</c> (provably drained within the tick)
    /// this store is LIVE cross-tick sim state — non-empty at the checksum boundary whenever feedback is pending —
    /// so it folds into <c>SimChecksum</c> (the story's ONE sanctioned re-baseline — landed as AlgoVersion 17→18
    /// via the 7-5 re-land merge, after 7.6's v17 fold shipped first on master).
    ///
    /// Dense preallocated ints only (Godot-free, float-free, zero per-tick heap allocation): per entry the
    /// registry event index, the raiser slot (−1 = system), and a fixed <see cref="EventBounds.MaxEventParams"/>
    /// stride of param raws (Int value / <c>Fixed.Raw</c> / Bool 0-1; unused slots 0). Capacity is
    /// <see cref="EventBounds.MaxNextTickEventQueue"/> with DETERMINISTIC drop-newest overflow (documented: every
    /// peer executes the same enqueue order, so every peer drops the same entries — the fold stays identical).
    /// </summary>
    public sealed class DslEventQueue
    {
        private readonly int[] _eventIndex = new int[EventBounds.MaxNextTickEventQueue];
        private readonly int[] _raiser     = new int[EventBounds.MaxNextTickEventQueue];
        private readonly int[] _params     = new int[EventBounds.MaxNextTickEventQueue * EventBounds.MaxEventParams];
        private int _count;

        /// <summary>Number of pending next-tick events (enqueue order preserved).</summary>
        public int Count => _count;

        /// <summary>
        /// Enqueue a next-tick event occurrence. <paramref name="paramRaws"/> supplies up to
        /// <see cref="EventBounds.MaxEventParams"/> raws (missing tail slots store 0). Returns false when the queue
        /// is full — the deterministic, documented drop-newest overflow (never a throw, never a resize).
        /// </summary>
        public bool Enqueue(int eventIndex, int raiser, int[] paramRaws, int paramCount)
        {
            if (_count >= EventBounds.MaxNextTickEventQueue) return false; // drop-newest (deterministic seatbelt)
            int slot = _count++;
            _eventIndex[slot] = eventIndex;
            _raiser[slot]     = raiser;
            int baseIdx = slot * EventBounds.MaxEventParams;
            // Clamp against BOTH paramCount and the caller's actual array length — a caller passing
            // paramCount > paramRaws.Length must not throw mid-tick (missing slots store 0, same as a short tail).
            for (int p = 0; p < EventBounds.MaxEventParams; p++)
                _params[baseIdx + p] = (paramRaws != null && p < paramCount && p < paramRaws.Length) ? paramRaws[p] : 0;
            return true;
        }

        /// <summary>The registry event index of the pending entry at <paramref name="i"/> (enqueue order).
        /// Throws on an out-of-range index — after <see cref="Clear"/> stale slots would otherwise read back
        /// as plausible garbage (cold guard; the drain loop iterates strictly below <see cref="Count"/>).</summary>
        public int EventIndexAt(int i)
        {
            if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
            return _eventIndex[i];
        }

        /// <summary>The raiser slot of the pending entry at <paramref name="i"/> (−1 = system). Range-checked
        /// against <see cref="Count"/> like <see cref="EventIndexAt"/>.</summary>
        public int RaiserAt(int i)
        {
            if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
            return _raiser[i];
        }

        /// <summary>The param raw <paramref name="p"/> (0..<see cref="EventBounds.MaxEventParams"/>−1) of entry
        /// <paramref name="i"/>. Range-checked on both indices (cold guards, never per-tick allocation).</summary>
        public int ParamAt(int i, int p)
        {
            if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
            if ((uint)p >= (uint)EventBounds.MaxEventParams) throw new ArgumentOutOfRangeException(nameof(p));
            return _params[i * EventBounds.MaxEventParams + p];
        }

        /// <summary>Empty the queue (tick-start dequeue, <c>LoadScenario</c> reset, and <c>ClearForReset</c>).
        /// Count-driven reads make a per-slot wipe unnecessary; slots past <see cref="Count"/> are never read or folded.</summary>
        public void Clear() => _count = 0;

        /// <summary>
        /// Fold the pending queue into the running FNV-1a <paramref name="hash"/> (SimChecksum v18): a leading
        /// count, then per entry IN ENQUEUE ORDER the event index, the raiser slot, and the full
        /// <see cref="EventBounds.MaxEventParams"/> param-raw stride (fixed stride — a payload change in any slot
        /// moves the hash). The <see cref="DslVarTable.FoldInto"/> pattern: caller-supplied <paramref name="mix"/>
        /// so the fold shares <c>SimChecksum.Mix</c>.
        /// </summary>
        public void FoldInto(ref uint hash, Func<uint, int, uint> mix)
        {
            hash = mix(hash, _count);
            for (int i = 0; i < _count; i++)
            {
                hash = mix(hash, _eventIndex[i]);
                hash = mix(hash, _raiser[i]);
                int baseIdx = i * EventBounds.MaxEventParams;
                for (int p = 0; p < EventBounds.MaxEventParams; p++)
                    hash = mix(hash, _params[baseIdx + p]);
            }
        }

        /// <summary>Fold BYTE-IDENTICALLY to an empty queue's <see cref="FoldInto"/> (a single 0-count mix) so a
        /// NULL queue and a non-null EMPTY queue are interchangeable in <c>SimChecksum</c> (the
        /// <see cref="DslVarTable.FoldEmpty"/> pattern; legacy/test callers only — production always passes a real queue).</summary>
        public static void FoldEmpty(ref uint hash, Func<uint, int, uint> mix)
        {
            hash = mix(hash, 0);
        }
    }
}
