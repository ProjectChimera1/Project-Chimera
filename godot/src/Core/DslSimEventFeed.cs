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
    /// <para><b>DW-850 — that emptiness is ENFORCED, not trusted.</b> <c>SimulationLoop.EnableTickBoundaryInvariants</c>
    /// checks <see cref="Count"/> == 0 after every system has ticked and before the checksum is taken, and throws
    /// naming this feed if it is not (the DW-766 tripwire, one feed over). It matters because a drained occurrence
    /// FIRES TRIGGERS, and those triggers mutate folded state (DSL variables, <c>WinStateStore</c>) — so residue here
    /// is an unhashed input to hashed state, not merely a stale buffer. The drain sits at a FIXED system index
    /// (<c>ScenarioDirector</c>, the last producer), so any future system registered past it that pushes here would
    /// otherwise re-open the hole silently.</para>
    ///
    /// <para>Determinism: producers push in ascending SYSTEM order and, within a system, ascending entity/building id,
    /// so the drained order is a deterministic function of sim state — identical on every peer.</para>
    /// </summary>
    public sealed class DslSimEventFeed
    {
        /// <summary>
        /// Capacity — the max sim-event raises buffered per tick (sizes the director's base-event headroom).
        ///
        /// <para>DW-192 (decision 2026-07-25): raised from 512 to 2×<see cref="EntityWorld.MAX_ENTITIES"/> so a
        /// mass-combat AoE tick no longer saturates in the NORMAL case — <c>DamageResolver.Apply</c> pushes one
        /// <c>unit_damaged</c> per hit AND per splash victim, so the worst realistic tick (every entity on a
        /// maxed-out map taking a direct hit plus a splash hit in the same tick: mass melee landing under a
        /// full-map volley, or two overlapping AoEs) is 2×MAX_ENTITIES pushes; the 500–2000-entity design target
        /// sits 4–16× under this ceiling.</para>
        ///
        /// <para>Memory/cost note: five int lanes × 8192 × 4 B = 160 KB (was 10 KB at 512), allocated ONCE at
        /// host construction; the director's base-event drain buffer grows by the same term at LoadScenario
        /// (~0.4 MB of <c>FiredEvent</c> slots). Per-tick cost is UNCHANGED — the drain walks <see cref="Count"/>
        /// (actual pushes), never Capacity, and <see cref="Clear"/> is O(1). Saturation past the cap remains the
        /// documented deterministic drop-newest seatbelt (identical on every peer), now a pathological edge
        /// (&gt;2 hits per entity per tick at the entity hard cap) rather than normal mass-combat behavior; the
        /// mathematical worst case (every in-flight projectile splashing every entity ≈ 2M pushes/tick) is not
        /// preallocatable and stays behind that same seatbelt.</para>
        /// </summary>
        public const int Capacity = EntityWorld.MAX_ENTITIES * 2; // 8192 — the entity cap, doubled. A sized buffer, not a
                                                                  // player count. (The operand order was once forced: the
                                                                  // NoHardcodedPlayerCount scan false-positived on a
                                                                  // leading `2 *`. Fixed in DW-582 — either order is fine.)

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
