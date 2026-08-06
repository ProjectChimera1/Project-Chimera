#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// DW-367 — the per-tick TRANSIENT death log: one record per <c>DamageResolver.KillEntity</c> call (victim id,
    /// victim faction slot, killer entity id, killer faction slot — all snapshotted BEFORE
    /// <see cref="EntityWorld.Destroy"/> recycles the slot). Owned by <see cref="EntityWorld"/> (the same
    /// derived-attribution posture as <see cref="EntityWorld.KillerOf"/>) so the single death choke point can record
    /// unconditionally with no per-call-site plumbing.
    ///
    /// <para><c>ScenarioDirector.CollectEvents</c> drains it as the PRIMARY <c>unit_dies</c> source: unlike the
    /// per-slot <c>_prevFlags</c> Alive-diff (which merges a same-tick die→recycle→die on one slot into a single
    /// event carrying only the last killer's attribution — or loses the death entirely when the recycled occupant is
    /// still alive at collect time), the log surfaces EVERY combat death with its own attribution. The director wipes
    /// it in <c>UpdateSnapshots</c> (the flags-snapshot horizon) — and, since DW-551, on its trigger-less early-out
    /// too, which skips <c>UpdateSnapshots</c> and so used to let a trigger-less scenario ACCUMULATE records across
    /// ticks. The two wipe points together make "EMPTY at the tick boundary" a UNIVERSAL invariant for any
    /// director-driven sim — which is what lets the log stay OUT of <see cref="SimChecksum"/> (the
    /// <c>DeathFeed</c>/<c>CombatEventQueue</c> posture) and out of <c>SaveGameState</c>, whose <c>CaptureFrom</c> now
    /// ASSERTS the log is drained instead of trusting every caller to be between ticks. DW-548 keeps that invariant
    /// intact: the records logged AFTER the collect (the director's own trigger-phase kills) are handed to the
    /// director's own deferred rail before this log is wiped, so the log itself still ends every tick empty. In
    /// director-less sims nothing reads it and it simply grows to the tick's death count and is reset by
    /// <see cref="EntityWorld.Clear"/>. Pure C#, integer-only SoA — no Godot, no float.</para>
    ///
    /// <para><b>DW-674 — lossless, not capped.</b> This used to be a flat 256-record buffer that deterministically
    /// dropped every death past the cap, arguing that the director's per-slot flags-diff fallback still surfaced the
    /// dropped death so only the recycled-slot attribution refinement was lost. That argument was never proven by a
    /// test and is FALSE for exactly the case the log exists to cover: a same-tick die→recycle→die slot is
    /// alive-at-collect (or dead-with-one-event) under the diff, so a dropped record loses a whole <c>unit_dies</c>
    /// occurrence — and <c>unit_dies</c> triggers mutate FOLDED sim state (DSL vars, <c>run_effect</c> damage,
    /// spawns). That makes an overflow a determinism-visible loss, the same reasoning DW-616 applied to
    /// <see cref="ProjectChimera.Combat.DeathFeed"/>, and it gets the same remedy: the buffer is grown on demand
    /// (amortized doubling from <see cref="INITIAL_CAPACITY"/>) so a record is NEVER dropped. No priority lane is
    /// possible here either — every record carries the identical kind of folded consequence, so there is no low-value
    /// class to sacrifice.</para>
    ///
    /// <para><b>Determinism.</b> Growth is a pure function of the push sequence and touches nothing observable: only
    /// <see cref="Count"/> and the four <c>…At</c> readers over <c>[0, Count)</c> are ever read, records keep their
    /// exact push order across a copy, and the capacity itself is never folded, serialized, or replicated. Two peers
    /// that grew differently (say one restored a save mid-match) still drain an identical record sequence. Retained
    /// capacity is bounded by the peak deaths in one tick, itself bounded by the <c>KillEntity</c> calls a tick can
    /// make (&lt;= <see cref="EntityWorld.MAX_ENTITIES"/> plus in-tick respawn churn).</para>
    /// </summary>
    public sealed class DeathLog
    {
        /// <summary>DW-674 — the starting (and, on every tick a real match produces, final) buffer size. Deliberately
        /// the old flat cap: a tick at or below this appends exactly what it always did, at the same indices, so the
        /// common path carries no new cost and no behavioural change. Also sizes the director's base-event headroom
        /// for multi-death slots.</summary>
        public const int INITIAL_CAPACITY = 256;

        private int[] _victim     = new int[INITIAL_CAPACITY];
        private int[] _victimSlot = new int[INITIAL_CAPACITY];
        private int[] _killer     = new int[INITIAL_CAPACITY];
        private int[] _killerSlot = new int[INITIAL_CAPACITY];
        private int _count;

        /// <summary>Number of deaths recorded this tick.</summary>
        public int Count => _count;

        /// <summary>DW-674 — the current backing-array size. Diagnostic/test observability ONLY: no simulation system
        /// reads it, it is never folded into <see cref="SimChecksum"/>, and it is not part of any save or wire format,
        /// so two peers holding different capacities are not divergent.</summary>
        public int Capacity => _victim.Length;

        /// <summary>Victim entity id of record <paramref name="i"/>. No bounds checking (drain loops read 0..Count).</summary>
        public int VictimAt(int i) => _victim[i];
        /// <summary>Victim faction slot of record <paramref name="i"/> (Player1 → 0; Neutral → −1).</summary>
        public int VictimSlotAt(int i) => _victimSlot[i];
        /// <summary>Killer entity id of record <paramref name="i"/> (−1 = unknown attacker).</summary>
        public int KillerAt(int i) => _killer[i];
        /// <summary>Killer faction slot of record <paramref name="i"/> (−1 = none/Neutral; Player1 → 0).</summary>
        public int KillerSlotAt(int i) => _killerSlot[i];

        /// <summary>Record one death. DW-674: never drops — the buffer grows on demand (see the type remarks),
        /// because a dropped record silently loses a whole <c>unit_dies</c> occurrence on a recycled slot, and
        /// <c>unit_dies</c> triggers mutate folded sim state.</summary>
        public void Push(int victim, int victimSlot, int killer, int killerSlot)
        {
            if (_count == _victim.Length) Grow();
            _victim[_count]     = victim;
            _victimSlot[_count] = victimSlot;
            _killer[_count]     = killer;
            _killerSlot[_count] = killerSlot;
            _count++;
        }

        /// <summary>DW-674 — double the four parallel arrays, preserving push order exactly (ordered copies; the drain
        /// reads <c>[0, Count)</c>, so index-for-index preservation is the whole contract). Called only on the tick a
        /// death count first exceeds the current capacity; the grown buffers are then retained, so the growth is
        /// amortized and never recurs at that size.</summary>
        private void Grow()
        {
            int grown = _victim.Length == 0 ? INITIAL_CAPACITY : _victim.Length * 2;
            GrowOne(ref _victim,     grown, _count);
            GrowOne(ref _victimSlot, grown, _count);
            GrowOne(ref _killer,     grown, _count);
            GrowOne(ref _killerSlot, grown, _count);
        }

        /// <summary>Reallocate one lane to <paramref name="size"/>, copying the live prefix in order.</summary>
        private static void GrowOne(ref int[] lane, int size, int count)
        {
            var next = new int[size];
            System.Array.Copy(lane, next, count);
            lane = next;
        }

        /// <summary>Reset so the next tick starts fresh (called by <c>ScenarioDirector.UpdateSnapshots</c> — the
        /// flags-snapshot horizon, AFTER it has handed the post-collect records to its deferred rail (DW-548) — by
        /// the director's trigger-less early-out (DW-551), and by <see cref="EntityWorld.Clear"/>). Retains the grown
        /// capacity deliberately: re-shrinking would re-allocate on every busy tick, and the capacity is unobservable
        /// to the simulation.</summary>
        public void Clear() => _count = 0;
    }
}
