#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 7.13 — a host-owned, per-tick TRANSIENT ring buffer of the four SIM-DRIVEN built-in DSL event
    /// occurrences (<c>unit_damaged</c>, <c>unit_trained</c>, <c>ability_cast</c>, <c>hero_level</c>). The producing
    /// systems (<c>DamageResolver</c>, <c>BuildingSystem</c>, <c>AbilityCastSystem</c>, <c>HeroXpSystem</c>) — all of
    /// which tick BEFORE <c>ScenarioDirector</c> (index 15, last) — push an occurrence at their deterministic
    /// tick-boundary site; the director drains it in <c>CollectEvents</c> into its base-event buffer and
    /// <see cref="Clear"/>s it, exactly like <c>DeathFeed</c>.
    ///
    /// <para>Drained + cleared every tick, so it is EMPTY at the checksum boundary → NOT folded into
    /// <see cref="SimChecksum"/> (the <c>DeathFeed</c>/<c>CombatEventQueue</c> posture). The EFFECTS a subscribed
    /// trigger produces (add_resources, set_variable, …) fold through their own stores — only those move a golden,
    /// never these transient raises. Pure C# (no Godot / fractional primitive / wall-clock); deterministic
    /// drop-newest at capacity (a dropped raise simply fires no trigger — non-critical, identical on every peer).</para>
    ///
    /// <para>Determinism: producers push in ascending SYSTEM order and, within a system, ascending entity/building id,
    /// so the drained order is a deterministic function of sim state — identical on every peer.</para>
    /// </summary>
    public sealed class DslSimEventFeed
    {
        /// <summary>Capacity — the max sim-event raises buffered per tick (sizes the director's base-event headroom).</summary>
        public const int Capacity = 512;

        // ── The closed kind codes (index into ScenarioDirector's interned name table — no per-tick string here). ──
        /// <summary>unit_damaged occurrence code.</summary>
        public const int KindUnitDamaged = 0;
        /// <summary>unit_trained occurrence code.</summary>
        public const int KindUnitTrained = 1;
        /// <summary>ability_cast occurrence code.</summary>
        public const int KindAbilityCast = 2;
        /// <summary>hero_level occurrence code.</summary>
        public const int KindHeroLevel   = 3;

        private readonly int[] _kind = new int[Capacity];
        private readonly int[] _slot = new int[Capacity]; // the event's faction slot (0-based; -1 = none) for matching
        private readonly int[] _p0   = new int[Capacity];
        private readonly int[] _p1   = new int[Capacity];
        private readonly int[] _p2   = new int[Capacity];
        private int _count;

        /// <summary>Number of occurrences recorded this tick.</summary>
        public int Count => _count;

        /// <summary>The kind code of occurrence <paramref name="i"/>.</summary>
        public int KindAt(int i) => _kind[i];
        /// <summary>The faction slot of occurrence <paramref name="i"/> (0-based; -1 = none).</summary>
        public int SlotAt(int i) => _slot[i];
        /// <summary>Param 0 of occurrence <paramref name="i"/>.</summary>
        public int P0At(int i) => _p0[i];
        /// <summary>Param 1 of occurrence <paramref name="i"/>.</summary>
        public int P1At(int i) => _p1[i];
        /// <summary>Param 2 of occurrence <paramref name="i"/>.</summary>
        public int P2At(int i) => _p2[i];

        /// <summary>Record one sim-driven DSL event occurrence. Silently drops when full (non-critical, deterministic).</summary>
        public void Push(int kind, int factionSlot, int p0, int p1, int p2)
        {
            if (_count >= Capacity) return; // deterministic drop-newest
            _kind[_count] = kind;
            _slot[_count] = factionSlot;
            _p0[_count]   = p0;
            _p1[_count]   = p1;
            _p2[_count]   = p2;
            _count++;
        }

        /// <summary>Reset the buffer so the next tick starts fresh (called by <c>ScenarioDirector</c> after draining,
        /// and by <c>SimulationHost.ClearForReset</c>).</summary>
        public void Clear() => _count = 0;
    }
}
