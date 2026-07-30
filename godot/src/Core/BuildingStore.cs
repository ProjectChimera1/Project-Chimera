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
        /// <summary>Story 4.1: a data-defined building with no dedicated enum member (its id has no
        /// <see cref="TechTreeChecker"/> mapping). Sentinel — carries no baked stats of its own; must be placed via
        /// <see cref="BuildingStore.Create"/>'s resolved-stats params directly (the per-type switch has no case for
        /// it and would fall to its <c>default:</c> branch). Review-pass correction: <c>BuildingSystem.
        /// PlaceBuildingDirect</c>/<c>QueueWorkerBuild</c> resolve a definition via <c>TechTreeChecker.
        /// BuildingTypeId(type)</c>, which has no case for <see cref="Custom"/> and returns <c>""</c> — so a
        /// <see cref="Custom"/> building placed through <c>BuildingSystem</c> today resolves no definition and falls
        /// to the switch <c>default:</c> branch, NOT the resolved-stats path. Reachable with real stats only via a
        /// direct <see cref="BuildingStore.Create"/> call (e.g. future authoring tooling); wiring an end-to-end
        /// placement route through <c>BuildingSystem</c>/the editor is deferred to later stories (4.5/4.6).
        /// APPEND-ONLY: appended after Aviary, never renumbered.</summary>
        Custom        = 5,
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

        /// <summary>Story 11.6 (FR-74): depth of the per-building production queue (head = slot 0, in production; slots
        /// 1-4 waiting). <see cref="ProductionQueue"/> is a <c>MAX_BUILDINGS * QUEUE_DEPTH</c> row-major array: building
        /// <c>b</c>'s head is at <see cref="HeadIndex"/>(b), its waiting slots at +1..+(QUEUE_DEPTH-1). Per-slot
        /// encoding is unchanged from 2.8 (0 = empty, unitIndex+1 = concrete unit, 255 = empty-category fallback).</summary>
        public const int QUEUE_DEPTH = 5;

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

        // ── Hero revival (Story 3.14) ───────────────────────────────────────────
        /// <summary>True when this building can revive fallen heroes (from the authored <c>revives_heroes</c> flag,
        /// threaded at placement). A NON-FOLDED placement constant (like the Story 3.13 curve constants / MeshType):
        /// read only by the revive command-card affordance + <c>BuildingSystem.ReviveHeroCommand</c>'s guard, so a
        /// divergence surfaces transitively via the hero's revival state — it never itself enters the checksum.
        /// Default false; reset on (re)allocation and cleared on recycle (the SoA-recycle contract).</summary>
        public readonly bool[]         RevivesHeroes;

        // ── Item shops (Story 3.16) ─────────────────────────────────────────────
        /// <summary>True when this building is an item SHOP (from the authored <c>sells_items</c> flag, threaded at
        /// placement — mirrors <see cref="RevivesHeroes"/>). A NON-FOLDED placement constant: read only by the shop
        /// command-card affordance + <c>BuildingSystem.BuyItemCommand</c>'s guard; a divergence surfaces transitively via
        /// the minted item / spent resources. Default false; reset on every (re)allocation (the SoA-recycle contract).</summary>
        public readonly bool[]         SellsItems;
        /// <summary>The authored item-def ids this shop stocks (parallel to <see cref="SellsItems"/>), resolved at
        /// placement. A reference constant (never folded); an empty array for a non-shop / empty-stock building.</summary>
        public readonly string[][]     ShopStock;
        /// <summary>The <see cref="Fixed"/> buy radius (quantized from the authored float <c>shop_radius</c> at placement).
        /// A non-folded placement constant; <see cref="Fixed.Zero"/> ⇒ the buy command uses a fallback radius.</summary>
        public readonly Fixed[]        ShopRadius;

        // ── Data-driven identity (Story 4.1) ────────────────────────────────────
        /// <summary>The authored building-definition id for this slot (e.g. <c>"command_center"</c>, or
        /// <c>"watchtower"</c> for a <see cref="BuildingType.Custom"/> building with no enum member). A NON-FOLDED
        /// placement constant — set at <see cref="Create"/> to the passed <c>buildingId</c>, or (when omitted, the
        /// legacy no-def path) <see cref="TechTreeChecker.BuildingTypeId"/> of the enum <c>type</c>, so every existing
        /// enum-backed building still resolves a stable id. Reset on every (re)allocation (the SoA-recycle contract).</summary>
        public readonly string[]       DefinitionId;

        // ── Construction ───────────────────────────────────────────────────────
        /// <summary>Seconds remaining until construction finishes (0 = complete).</summary>
        public readonly Fixed[]        ConstructionTimer;
        /// <summary>Total construction duration for the building type (seconds).</summary>
        public readonly Fixed[]        ConstructionDuration;

        // ── Production ─────────────────────────────────────────────────────────
        /// <summary>Seconds remaining until the HEAD training job finishes (0 = idle). One per building — only the
        /// head (slot 0) has a running timer; waiting slots never accrue progress (Story 11.6, WC3 model).</summary>
        public readonly Fixed[]        ProductionTimer;
        /// <summary>Story 11.6: the depth-5 production queue, <c>MAX_BUILDINGS * QUEUE_DEPTH</c> row-major. Building
        /// <c>b</c>'s head is <see cref="HeadIndex"/>(b) (== <c>b*QUEUE_DEPTH</c>); waiting slots +1..+4. Per-slot byte:
        /// 0 = empty, (unitIndex+1) = concrete unit, 255 = empty-category fallback sentinel (Story 2.8 encoding, kept).</summary>
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
            ProductionQueue      = new byte[MAX_BUILDINGS * QUEUE_DEPTH];
            RallyPoint           = new FixedVec3[MAX_BUILDINGS];
            HasRallyPoint        = new bool[MAX_BUILDINGS];
            TrainedCount         = new int[MAX_BUILDINGS];
            RevivesHeroes        = new bool[MAX_BUILDINGS];
            SellsItems           = new bool[MAX_BUILDINGS];
            ShopStock            = new string[MAX_BUILDINGS][];
            ShopRadius           = new Fixed[MAX_BUILDINGS];
            DefinitionId         = new string[MAX_BUILDINGS];
        }

        /// <summary>Returns true while the building is still being constructed.</summary>
        public bool IsUnderConstruction(int id) => ConstructionTimer[id] > Fixed.Zero;

        // ── Production-queue helpers (Story 11.6) ──────────────────────────────
        /// <summary>Flat index of building <paramref name="b"/>'s HEAD production slot (== <c>b*QUEUE_DEPTH</c>). Its
        /// waiting slots are +1..+(QUEUE_DEPTH-1). Deterministic, pure — the single row-major coordinate every reader uses.</summary>
        public int HeadIndex(int b) => b * QUEUE_DEPTH;

        /// <summary>Index of building <paramref name="b"/>'s first EMPTY queue slot (0..QUEUE_DEPTH-1), or -1 when all
        /// slots are occupied (queue full). The queue is kept contiguous (append-at-first-empty + shift-on-remove), so
        /// the first zero terminates the occupied run.</summary>
        public int FirstEmptySlot(int b)
        {
            int head = b * QUEUE_DEPTH;
            for (int k = 0; k < QUEUE_DEPTH; k++)
                if (ProductionQueue[head + k] == 0) return k;
            return -1;
        }

        /// <summary>Number of occupied slots in building <paramref name="b"/>'s queue (0..QUEUE_DEPTH). Counts the
        /// contiguous run of non-empty slots from the head.</summary>
        public int QueueLength(int b)
        {
            int head = b * QUEUE_DEPTH, n = 0;
            for (int k = 0; k < QUEUE_DEPTH; k++)
                if (ProductionQueue[head + k] != 0) n++; else break;
            return n;
        }

        /// <summary>
        /// Place a new building. Returns its ID, or -1 if the store is full.
        /// </summary>
        public int Create(FixedVec3 position, Faction faction, BuildingType type, bool revivesHeroes = false,
                          bool sellsItems = false, string[] shopStock = null, Fixed shopRadius = default,
                          string buildingId = null, Fixed? health = null, int? supplyBonus = null,
                          Fixed? constructionDuration = null)
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
            // Story 11.6: reset ALL QUEUE_DEPTH slots on (re)allocation (the SoA-recycle contract — a recycled slot must
            // never inherit the prior building's queue). The queue is FOLDED into SimChecksum (v22) and read
            // unconditionally, so every widened slot must be zeroed, or a recycled slot diverges the byte-identical guard.
            { int q0 = id * QUEUE_DEPTH; for (int k = 0; k < QUEUE_DEPTH; k++) ProductionQueue[q0 + k] = 0; }
            TrainedCount[id]    = 0;
            // Story 2.12: reset the rally point on (re)allocation. Rally is FOLDED into SimChecksum (v9, D-1) and
            // read unconditionally, so a recycled slot must never inherit the prior building's rally (the SoA-recycle
            // trap). Harmless today under the append-only BuildingStore (Count only grows), but forward-safe for the
            // Story 2.13 slot recycling — and it keeps the unconditional fold deterministic if slots ever reuse.
            RallyPoint[id]      = FixedVec3.Zero;
            HasRallyPoint[id]   = false;
            // Story 3.14: reset the revive-capability flag on EVERY (re)allocation (the SoA-recycle contract — a recycled
            // slot must never inherit the prior building's capability), then set it from the authored/threaded flag.
            RevivesHeroes[id]   = revivesHeroes;
            // Story 3.16: reset the shop capability + stock + radius on EVERY (re)allocation (SoA-recycle contract — a
            // recycled slot must never inherit the prior building's shop), then set from the authored/threaded values.
            SellsItems[id]      = sellsItems;
            ShopStock[id]       = shopStock ?? System.Array.Empty<string>();
            ShopRadius[id]      = shopRadius;
            // Story 4.1: the authored definition id for this slot — the passed id, or (legacy no-def callers) the
            // enum's canonical id, so every existing enum-backed building still resolves a stable DefinitionId.
            // Reset on EVERY (re)allocation (the SoA-recycle contract — mirrors RevivesHeroes/SellsItems above).
            DefinitionId[id]    = buildingId ?? TechTreeChecker.BuildingTypeId(type);
            // Story 2.13 (AC3.2, Decision 6) — zero SupplyBonus on EVERY (re)allocation, BEFORE the switch. The
            // `default:` branch below does NOT set it, so a recycled slot that was a CommandCenter (SupplyBonus=10)
            // would otherwise leak +10 supply into a default-typed building — the SoA-recycle trap. The CommandCenter
            // branch re-sets 10 explicitly; zeroing here is the robust guard (a future building type can't leak either).
            SupplyBonus[id]     = 0;

            // Story 4.1 (AC2): a data-defined building resolved from a loaded BuildingDefinition — read stats from the
            // caller-supplied resolved values instead of the per-type switch below. All three must be supplied together
            // (the production callers in BuildingSystem either resolve all three from a def or none, preserving the
            // switch-fallback null-propagation contract); a Custom building has no switch case to fall back to, so this
            // is its ONLY path to non-default stats.
            if (health.HasValue && supplyBonus.HasValue && constructionDuration.HasValue)
            {
                Health[id]               = health.Value;
                MaxHealth[id]             = health.Value;
                SupplyBonus[id]           = supplyBonus.Value;
                ConstructionDuration[id]  = constructionDuration.Value;
                ConstructionTimer[id]     = ConstructionDuration[id];
                return id;
            }

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
                    // Story 2.8, D-2 (Alec): mirrors the Siege Workshop (top-tier gate). This branch is the
                    // switch-fallback default (used only when the caller has no resolved BuildingDefinition — see
                    // the resolved-stats short-circuit above, Story 4.1). It is NOT a source-of-truth override of
                    // JSON: since alpha/beta_faction.json now author matching hp/supply_bonus/construction_time for
                    // aviary, any Aviary placed through BuildingSystem's def-resolving call sites reads these exact
                    // values from data instead, and only a caller that never resolves a def hits this switch case.
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
            System.Array.Clear(RevivesHeroes);
            System.Array.Clear(SellsItems);           System.Array.Clear(ShopStock);
            System.Array.Clear(ShopRadius);           System.Array.Clear(DefinitionId);
            System.Array.Clear(Generation);           System.Array.Clear(_freeList);
            _freeCount = 0;
            Count      = 0;
        }

        /// <summary>
        /// Story 11.3 (SP save/load): restore the private high-water <see cref="Count"/> + free-list after the
        /// persistence layer has written the SoA arrays (incl. the public <see cref="Generation"/>) directly. Godot-free
        /// integer bookkeeping only; copies at most <see cref="MAX_BUILDINGS"/> free-list entries (defensive).
        /// </summary>
        /// <summary>Story 11.3 (SP save/load): the active portion of the recycle free-list (LIFO order preserved) for a
        /// save — the exact next slot <see cref="Create"/> will reuse. Returns a fresh copy.</summary>
        public int[] CaptureFreeList()
        {
            var copy = new int[_freeCount];
            System.Array.Copy(_freeList, copy, _freeCount);
            return copy;
        }

        public void RestoreManagement(int count, int[] freeList, int freeCount)
        {
            Count = count < 0 ? 0 : (count > MAX_BUILDINGS ? MAX_BUILDINGS : count);
            System.Array.Clear(_freeList);
            int n = freeCount < 0 ? 0 : (freeCount > MAX_BUILDINGS ? MAX_BUILDINGS : freeCount);
            if (freeList != null)
                for (int i = 0; i < n && i < freeList.Length; i++) _freeList[i] = freeList[i];
            _freeCount = n;
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
