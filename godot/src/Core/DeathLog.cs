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
    /// it in <c>UpdateSnapshots</c> (the flags-snapshot horizon), so it is EMPTY at the checksum boundary → NOT
    /// folded into <see cref="SimChecksum"/> (the <c>DeathFeed</c>/<c>CombatEventQueue</c> posture; in director-less
    /// sims nothing reads it and the cap makes it inert). Pure C#, integer-only SoA — no Godot, no float.</para>
    ///
    /// <para>Capacity overflow deterministically drops the record (identical on every peer); the director then falls
    /// back to the per-slot flags diff for that slot — i.e. exactly the pre-log behavior. A dropped record only ever
    /// costs the recycled-slot attribution refinement, never a death the old diff would have surfaced.</para>
    /// </summary>
    public sealed class DeathLog
    {
        /// <summary>Max deaths recorded per tick; also sizes the director's base-event headroom for multi-death slots.
        /// Historically chosen to match the <c>DeathFeed</c> ring, which DW-616 has since made lossless (its drops were
        /// checksum-visible via hero XP); this log keeps its cap because its own overflow is covered by the director's
        /// flags-diff fallback described above — it is NOT a mirror of the feed any more.</summary>
        public const int CAPACITY = 256;

        private readonly int[] _victim     = new int[CAPACITY];
        private readonly int[] _victimSlot = new int[CAPACITY];
        private readonly int[] _killer     = new int[CAPACITY];
        private readonly int[] _killerSlot = new int[CAPACITY];
        private int _count;

        /// <summary>Number of deaths recorded this tick.</summary>
        public int Count => _count;

        /// <summary>Victim entity id of record <paramref name="i"/>. No bounds checking (drain loops read 0..Count).</summary>
        public int VictimAt(int i) => _victim[i];
        /// <summary>Victim faction slot of record <paramref name="i"/> (Player1 → 0; Neutral → −1).</summary>
        public int VictimSlotAt(int i) => _victimSlot[i];
        /// <summary>Killer entity id of record <paramref name="i"/> (−1 = unknown attacker).</summary>
        public int KillerAt(int i) => _killer[i];
        /// <summary>Killer faction slot of record <paramref name="i"/> (−1 = none/Neutral; Player1 → 0).</summary>
        public int KillerSlotAt(int i) => _killerSlot[i];

        /// <summary>Record one death. Silently (and deterministically) drops when full — the director's flags-diff
        /// fallback then covers the slot exactly as before the log existed.</summary>
        public void Push(int victim, int victimSlot, int killer, int killerSlot)
        {
            if (_count >= CAPACITY) return; // deterministic drop-newest
            _victim[_count]     = victim;
            _victimSlot[_count] = victimSlot;
            _killer[_count]     = killer;
            _killerSlot[_count] = killerSlot;
            _count++;
        }

        /// <summary>Reset so the next tick starts fresh (called by <c>ScenarioDirector.UpdateSnapshots</c> — the
        /// flags-snapshot horizon — and by <see cref="EntityWorld.Clear"/>).</summary>
        public void Clear() => _count = 0;
    }
}
