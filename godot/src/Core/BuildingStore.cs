namespace ProjectChimera.Core
{
    /// <summary>
    /// Type of building. Determines stats, production, and supply contribution.
    /// </summary>
    public enum BuildingType : byte
    {
        CommandCenter = 0,  // +10 supply cap; no production
        Barracks      = 1,  // Produces infantry combat units
        ArcheryRange  = 2,  // Produces ranged combat units (future)
        SiegeWorkshop = 3,  // Produces siege units (future)
        Aviary        = 4,  // Produces Air combat units (Story 2.8). APPEND-ONLY: values 0-3 are byte-serialized into replays/scenarios — never renumber.
    }

    /// <summary>
    /// Struct-of-Arrays storage for placed buildings.
    ///
    /// Buildings are static (non-moving). They contribute supply cap and/or
    /// serve as production queues. Each faction can have up to MAX_BUILDINGS total.
    ///
    /// Indexed by building ID (0 … Count-1).
    /// </summary>
    public class BuildingStore
    {
        public const int MAX_BUILDINGS = 64;

        // ── Core data ──────────────────────────────────────────────────────────
        public readonly bool[]         Alive;
        public readonly FixedVec3[]    Position;
        public readonly Faction[]      FactionOf;
        public readonly BuildingType[] Type;
        public readonly Fixed[]        Health;
        public readonly Fixed[]        MaxHealth;

        // ── Supply ─────────────────────────────────────────────────────────────
        /// <summary>Amount this building adds to its faction's SupplyCap when alive.</summary>
        public readonly int[]          SupplyBonus;

        // ── Construction ───────────────────────────────────────────────────────
        /// <summary>Seconds remaining until construction finishes (0 = complete).</summary>
        public readonly Fixed[]        ConstructionTimer;
        /// <summary>Total construction duration for the building type (seconds).</summary>
        public readonly Fixed[]        ConstructionDuration;

        // ── Production ─────────────────────────────────────────────────────────
        /// <summary>Seconds remaining until the current training job finishes (0 = idle).</summary>
        public readonly Fixed[]        ProductionTimer;
        /// <summary>Entity archetype being trained (0 = nothing queued).</summary>
        public readonly byte[]         ProductionQueue;

        // ── Rally point ─────────────────────────────────────────────────────────
        /// <summary>World position where newly trained units walk to after spawning.</summary>
        public readonly FixedVec3[]    RallyPoint;
        /// <summary>True when the player (or AI) has explicitly set a rally point for this building.</summary>
        public readonly bool[]         HasRallyPoint;

        /// <summary>Lifetime count of units trained here — cycles the spawn offset so
        /// held (no-rally) units never stack on the exact same fixed-point position.</summary>
        public readonly int[]          TrainedCount;

        // ── Management ─────────────────────────────────────────────────────────
        /// <summary>One past the highest slot ever allocated — the iteration/fold upper bound (SimChecksum folds
        /// <c>0..Count</c>). A monotonic high-water mark: recycling reuses dead slots BELOW Count, so Count only grows.</summary>
        public int Count { get; private set; }

        // ── Free-list recycling (Story 2.13, AC3.1) ────────────────────────────
        // Deterministic LIFO stack of dead slots available for reuse, mirroring ProjectileStore._freeList exactly.
        // UNFOLDED internal bookkeeping — NEVER a SimChecksum input (the Count-driven fold already covers 0..Count and
        // recycled slots are < Count, so the fold sees no new state). Create() pops a free slot before appending a new
        // one; Destroy() pushes the freed slot. LIFO from the deterministic tick ⇒ every peer maps slot→building alike.
        private readonly int[] _freeList = new int[MAX_BUILDINGS];
        private int            _freeCount;

        // ── Generation counter (Story 2.13, AC3.4 / Decision D-3) ──────────────
        // Per-slot recycle generation, bumped each time Create() reuses a dead slot. UNFOLDED (never a SimChecksum
        // input) — deterministic (the recycle sequence is deterministic) and caught transitively via the folded
        // Alive/Count. Every cross-tick building reference is PACKED as (Generation[slot] << 8) | slot and validated on
        // deref by TryResolveRef, so a stale ref to a since-recycled slot reverts CLEANLY instead of ABA-retargeting a
        // NEW building (restoring Story 2.9a's "stale → clean revert"). Golden-neutral: generation starts at 0, so
        // PackRef(slot) == slot for every never-recycled building ⇒ existing folded CommandTarget/wire values unchanged.
        public readonly int[] Generation = new int[MAX_BUILDINGS];

        public BuildingStore()
        {
            Alive                = new bool[MAX_BUILDINGS];
            Position             = new FixedVec3[MAX_BUILDINGS];
            FactionOf            = new Faction[MAX_BUILDINGS];
            Type                 = new BuildingType[MAX_BUILDINGS];
            Health               = new Fixed[MAX_BUILDINGS];
            MaxHealth            = new Fixed[MAX_BUILDINGS];
            SupplyBonus          = new int[MAX_BUILDINGS];
            ConstructionTimer    = new Fixed[MAX_BUILDINGS];
            ConstructionDuration = new Fixed[MAX_BUILDINGS];
            ProductionTimer      = new Fixed[MAX_BUILDINGS];
            ProductionQueue      = new byte[MAX_BUILDINGS];
            RallyPoint           = new FixedVec3[MAX_BUILDINGS];
            HasRallyPoint        = new bool[MAX_BUILDINGS];
            TrainedCount         = new int[MAX_BUILDINGS];
        }

        /// <summary>Returns true while the building is still being constructed.</summary>
        public bool IsUnderConstruction(int id) => ConstructionTimer[id] > Fixed.Zero;

        /// <summary>
        /// Place a new building. Returns its ID, or -1 if the store is full.
        /// </summary>
        public int Create(FixedVec3 position, Faction faction, BuildingType type)
        {
            // Story 2.13 (AC3.1) — recycle a dead slot first, else append a fresh one. The store is "full" (returns
            // -1) only when all MAX_BUILDINGS slots are SIMULTANEOUSLY live (free-list empty AND high-water at the cap),
            // not cumulatively — so a long match of build/destroy no longer silently exhausts the store.
            int id;
            if (_freeCount > 0)
            {
                id = _freeList[--_freeCount];    // reuse a dead slot (LIFO, deterministic)
                Generation[id]++;                // bump generation so stale packed refs to the prior occupant fail TryResolveRef
            }
            else if (Count < MAX_BUILDINGS)
                id = Count++;                    // append a fresh slot (Generation stays 0 — never recycled)
            else
                return -1;                       // all MAX_BUILDINGS slots are simultaneously live

            Alive[id]           = true;
            Position[id]        = position;
            FactionOf[id]       = faction;
            Type[id]            = type;
            ProductionTimer[id] = Fixed.Zero;
            ProductionQueue[id] = 0;
            TrainedCount[id]    = 0;
            // Story 2.12: reset the rally point on (re)allocation. Rally is FOLDED into SimChecksum (v9, D-1) and
            // read unconditionally, so a recycled slot must never inherit the prior building's rally (the SoA-recycle
            // trap). Harmless today under the append-only BuildingStore (Count only grows), but forward-safe for the
            // Story 2.13 slot recycling — and it keeps the unconditional fold deterministic if slots ever reuse.
            RallyPoint[id]      = FixedVec3.Zero;
            HasRallyPoint[id]   = false;
            // Story 2.13 (AC3.2, Decision 6) — zero SupplyBonus on EVERY (re)allocation, BEFORE the switch. The
            // `default:` branch below does NOT set it, so a recycled slot that was a CommandCenter (SupplyBonus=10)
            // would otherwise leak +10 supply into a default-typed building — the SoA-recycle trap. The CommandCenter
            // branch re-sets 10 explicitly; zeroing here is the robust guard (a future building type can't leak either).
            SupplyBonus[id]     = 0;

            // Per-type defaults
            switch (type)
            {
                case BuildingType.CommandCenter:
                    Health[id]              = Fixed.FromFloat(500f);
                    MaxHealth[id]           = Fixed.FromFloat(500f);
                    SupplyBonus[id]         = 10;
                    ConstructionDuration[id] = Fixed.FromFloat(15f);
                    break;
                case BuildingType.Barracks:
                    Health[id]              = Fixed.FromFloat(300f);
                    MaxHealth[id]           = Fixed.FromFloat(300f);
                    SupplyBonus[id]         = 0;
                    ConstructionDuration[id] = Fixed.FromFloat(10f);
                    break;
                case BuildingType.ArcheryRange:
                    Health[id]              = Fixed.FromFloat(300f);
                    MaxHealth[id]           = Fixed.FromFloat(300f);
                    SupplyBonus[id]         = 0;
                    ConstructionDuration[id] = Fixed.FromFloat(10f);
                    break;
                case BuildingType.SiegeWorkshop:
                    Health[id]              = Fixed.FromFloat(400f);
                    MaxHealth[id]           = Fixed.FromFloat(400f);
                    SupplyBonus[id]         = 0;
                    ConstructionDuration[id] = Fixed.FromFloat(12f);
                    break;
                case BuildingType.Aviary:
                    // Story 2.8, D-2 (Alec): mirrors the Siege Workshop (top-tier gate). HP/supply/construction
                    // come from HERE, not the JSON (building `hp` is vestigial).
                    Health[id]              = Fixed.FromFloat(350f);
                    MaxHealth[id]           = Fixed.FromFloat(350f);
                    SupplyBonus[id]         = 0;
                    ConstructionDuration[id] = Fixed.FromFloat(12f);
                    break;
                default:
                    Health[id]              = Fixed.FromFloat(200f);
                    MaxHealth[id]           = Fixed.FromFloat(200f);
                    ConstructionDuration[id] = Fixed.FromFloat(10f);
                    break;
            }

            ConstructionTimer[id] = ConstructionDuration[id];

            return id;
        }

        /// <summary>
        /// Story 3.10 (UX-DR62): restore this store to its EXACT post-construction state for the Edit↔Play reset —
        /// zero every SoA array + the free-list + generation counters and reset <see cref="Count"/> to 0. A cleared
        /// store is byte-for-byte equal to <c>new BuildingStore()</c> (buildings ARE folded into SimChecksum, so this
        /// must be exact). The re-apply's <c>PlaceBuildingDirect</c> re-creates the authored buildings against it.
        /// </summary>
        public void Clear()
        {
            System.Array.Clear(Alive);                System.Array.Clear(Position);
            System.Array.Clear(FactionOf);            System.Array.Clear(Type);
            System.Array.Clear(Health);               System.Array.Clear(MaxHealth);
            System.Array.Clear(SupplyBonus);          System.Array.Clear(ConstructionTimer);
            System.Array.Clear(ConstructionDuration); System.Array.Clear(ProductionTimer);
            System.Array.Clear(ProductionQueue);      System.Array.Clear(RallyPoint);
            System.Array.Clear(HasRallyPoint);        System.Array.Clear(TrainedCount);
            System.Array.Clear(Generation);           System.Array.Clear(_freeList);
            _freeCount = 0;
            Count      = 0;
        }

        /// <summary>Destroy a building — marks the slot dead and returns it to the free-list for reuse (Story 2.13, AC3.1).</summary>
        public void Destroy(int id)
        {
            // Bounds + double-free guard (mirrors ProjectileStore.Destroy): never push a slot onto the free-list twice,
            // which would hand the same slot to two future Create() calls and corrupt the store.
            if (id < 0 || id >= Count || !Alive[id]) return;
            Alive[id] = false;
            _freeList[_freeCount++] = id;
        }

        /// <summary>
        /// Story 2.13 (AC3.4) — pack a live building id into a generation-stamped CROSS-TICK reference
        /// <c>(Generation[slot] &lt;&lt; 8) | slot</c> (slot 0–63 = low 8 bits; generation = upper 24). Every holder that
        /// carries a building id across ticks stores this instead of the bare slot and validates it via
        /// <see cref="TryResolveRef"/> on deref. GOLDEN-NEUTRAL: at generation 0, <c>PackRef(slot) == slot</c>.
        /// </summary>
        public int PackRef(int slot) => (Generation[slot] << 8) | slot;

        /// <summary>
        /// Story 2.13 (AC3.4) — resolve a packed building reference back to a live slot. Returns true and the slot iff
        /// it is in bounds, ALIVE, and the generation still matches (the SAME building occupies it). A stale ref — the
        /// slot was recycled (generation bumped) or freed — returns false, so the holder reverts cleanly and NEVER
        /// retargets the new occupant (Story 2.9a's "stale → clean revert"). The -1 sentinel resolves false (255 ≥ Count).
        /// </summary>
        public bool TryResolveRef(int packed, out int slot)
        {
            slot = packed & 0xFF;                    // low 8 bits — always 0–255, so slot ≥ 0
            return slot < Count && Alive[slot] && Generation[slot] == (packed >> 8);
        }
    }
}
