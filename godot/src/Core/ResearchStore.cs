#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Faction-scoped Struct-of-Arrays store for the Story 4.9 research order path — the mid-match-mutable
    /// substrate <see cref="ProjectChimera.Economy.ResearchSystem"/> reads/writes. Mirrors <see cref="ResourceStore"/>'s
    /// per-faction-array shape (indices 0-4, cast from <see cref="Faction"/>; index 0/Neutral unused) rather than
    /// <see cref="BuildingStore"/>'s per-building rows — research is faction-wide with exactly ONE order in progress
    /// at a time (no per-building queue; see the spec's Design Notes).
    ///
    /// <para>The outer per-faction arrays (<see cref="InProgressIndex"/>/<see cref="RemainingTicks"/>/
    /// <see cref="StartedAtPosition"/>) are fixed-size (5). The per-faction-per-research inner arrays
    /// (<see cref="CompletedLevels"/> and the four <c>Cumulative*Delta</c> arrays) are sized lazily via
    /// <see cref="EnsureCapacity"/> once a faction's <c>FactionDefinition.Research</c> count is known (the store
    /// itself carries no reference to <see cref="Definitions.FactionDefinition"/> — that stays
    /// <see cref="ProjectChimera.Economy.ResearchSystem"/>'s job, mirroring how <see cref="BuildingStore"/> never
    /// references a <c>FactionDefinition</c> either).</para>
    ///
    /// <para><c>int</c>/<see cref="Fixed"/>-only, ascending faction then ascending research index — shaped so
    /// Story 4.10 can fold it into <c>SimChecksum</c> without a redesign (this story does NOT wire that fold).</para>
    /// </summary>
    public class ResearchStore
    {
        private const int FACTION_COUNT = FactionRegistry.FACTION_ARRAY_SIZE; // 9: Neutral(0) + Player1..Player8

        /// <summary>Research index (into that faction's <c>FactionDefinition.Research</c>) currently in progress,
        /// or -1 when the faction is idle. Exactly one in-progress order per faction.</summary>
        public readonly int[] InProgressIndex;

        /// <summary>Ticks remaining on the in-progress order (decremented by 1 per <c>ResearchSystem.Tick</c> call —
        /// the authored unit IS the tick, no <see cref="Fixed"/>/dt conversion). Meaningless while idle.</summary>
        public readonly int[] RemainingTicks;

        /// <summary>World position the in-progress order was started at (the building's position) — read at
        /// completion to push <c>CombatEventType.ResearchComplete</c> there.</summary>
        public readonly FixedVec3[] StartedAtPosition;

        /// <summary>Per-faction-per-research completed-level COUNT (outer = faction, inner = research index, ascending
        /// <c>FactionDefinition.Research</c> list order). 0 = never completed a level of this research.</summary>
        public readonly int[][] CompletedLevels;

        /// <summary>
        /// Story 15-24a — the GENERALIZED cumulative store: one per-faction-per-research <c>Fixed[][]</c> lane
        /// PER REGISTRY STAT, indexed <c>[(int)StatId][faction][research]</c>. The sum of every completed
        /// level's contribution to that stat (legacy four keys + the <c>stat_deltas</c> lane), quantized once
        /// per completion. The four legacy fields below are ALIASES of this table's outer arrays — same
        /// objects, so every pre-15-24a reader (SimChecksum's hand-named folds, the save lanes, the UI
        /// upgrade summary) keeps reading exactly the values it always did with zero call-site churn.
        /// </summary>
        public readonly Fixed[][][] CumulativeByStat;

        /// <summary>Alias of <c>CumulativeByStat[(int)StatId.MaxHealth]</c> (see <see cref="CumulativeByStat"/>).</summary>
        public readonly Fixed[][] CumulativeMaxHealthDelta;

        /// <summary>Alias of <c>CumulativeByStat[(int)StatId.AttackDamage]</c>.</summary>
        public readonly Fixed[][] CumulativeAttackDamageDelta;

        /// <summary>Alias of <c>CumulativeByStat[(int)StatId.MoveSpeed]</c>.</summary>
        public readonly Fixed[][] CumulativeMoveSpeedDelta;

        /// <summary>Alias of <c>CumulativeByStat[(int)StatId.Armor]</c>.</summary>
        public readonly Fixed[][] CumulativeArmorDelta;

        public ResearchStore()
        {
            InProgressIndex   = new int[FACTION_COUNT];
            RemainingTicks    = new int[FACTION_COUNT];
            StartedAtPosition = new FixedVec3[FACTION_COUNT];
            CompletedLevels   = new int[FACTION_COUNT][];

            CumulativeByStat = new Fixed[Stats.StatVocabulary.Count][][];
            for (int s = 0; s < CumulativeByStat.Length; s++)
                CumulativeByStat[s] = new Fixed[FACTION_COUNT][];
            CumulativeMaxHealthDelta    = CumulativeByStat[(int)Stats.StatId.MaxHealth];
            CumulativeAttackDamageDelta = CumulativeByStat[(int)Stats.StatId.AttackDamage];
            CumulativeMoveSpeedDelta    = CumulativeByStat[(int)Stats.StatId.MoveSpeed];
            CumulativeArmorDelta        = CumulativeByStat[(int)Stats.StatId.Armor];

            for (int f = 0; f < FACTION_COUNT; f++)
            {
                InProgressIndex[f] = -1; // idle
                CompletedLevels[f] = System.Array.Empty<int>();
                for (int s = 0; s < CumulativeByStat.Length; s++)
                    CumulativeByStat[s][f] = System.Array.Empty<Fixed>();
            }
        }

        /// <summary>
        /// Grow faction <paramref name="faction"/>'s per-research inner arrays to at least
        /// <paramref name="researchCount"/> entries (never shrinks; a no-op if already large enough). Called by
        /// <see cref="ProjectChimera.Economy.ResearchSystem"/> once it knows a faction's authored
        /// <c>FactionDefinition.Research.Count</c> — the store itself never reads the definition. Existing values are
        /// preserved; newly grown slots default to 0 completed levels / <see cref="Fixed.Zero"/> cumulative deltas.
        /// Out-of-range <paramref name="faction"/> is a silent no-op (defensive; mirrors the rest of this store's
        /// bounds posture).
        /// </summary>
        public void EnsureCapacity(Faction faction, int researchCount)
        {
            int f = (int)faction;
            if (f < 0 || f >= FACTION_COUNT) return;
            if (researchCount <= CompletedLevels[f].Length) return;

            System.Array.Resize(ref CompletedLevels[f], researchCount);
            // 15-24a: every registry stat's lane grows together (the legacy fields alias four of these outers,
            // so the resize is visible through them — one growth path, no drift).
            for (int s = 0; s < CumulativeByStat.Length; s++)
                System.Array.Resize(ref CumulativeByStat[s][f], researchCount);
        }

        /// <summary>
        /// Story 3.10-style Edit↔Play reset support: restore every faction to idle and every per-research counter/
        /// cumulative delta to zero, WITHOUT shrinking the already-grown inner arrays (so a subsequent
        /// <see cref="EnsureCapacity"/> from the same construction-time faction defs stays a no-op). A cleared store
        /// is byte-for-byte equal to a freshly-constructed one for every already-sized faction.
        /// </summary>
        public void Clear()
        {
            for (int f = 0; f < FACTION_COUNT; f++)
            {
                InProgressIndex[f]   = -1;
                RemainingTicks[f]    = 0;
                StartedAtPosition[f] = default;
                System.Array.Clear(CompletedLevels[f]);
                for (int s = 0; s < CumulativeByStat.Length; s++) // 15-24a: every stat lane
                    System.Array.Clear(CumulativeByStat[s][f]);
            }
        }
    }
}
