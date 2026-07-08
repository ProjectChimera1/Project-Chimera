#nullable enable
using System;

namespace ProjectChimera.Core
{
    /// <summary>
    /// A stable, cross-match hero identity (Story 3.2, AR-12 / D-4). Deliberately a thin <c>readonly struct</c>
    /// wrapper over a <see cref="ulong"/> so it can NEVER be accidentally mixed with a recycled
    /// <see cref="EntityWorld"/> id or a raw <see cref="HeroStore"/> slot index (both are <c>int</c>) — the two
    /// things a hero identity must OUTLIVE. The value is profile-assigned (a monotonic id, or a deterministic hash
    /// of profile-id + hero-save-slot) so it survives across matches and across <see cref="EntityWorld"/> id
    /// recycling; the ASSIGNMENT mechanism itself is Story 3.9 (this story defines the type + the contract only).
    /// <c>ulong</c> gives ample space for a per-save-slot id (FR-7e: multiple saved heroes per player) and folds as
    /// two <c>Mix(int)</c> (low/high 32 bits), exactly like the <c>SimRng</c> state. Total-ordered by
    /// <see cref="Value"/> (unique per hero), which the ascending-identity checksum fold requires.
    /// </summary>
    public readonly struct HeroId : IEquatable<HeroId>
    {
        /// <summary>The stable identity value. Unique per persisted hero; a total order for the fold.</summary>
        public readonly ulong Value;

        public HeroId(ulong value) => Value = value;

        public bool Equals(HeroId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is HeroId o && o.Value == Value;
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(HeroId a, HeroId b) => a.Value == b.Value;
        public static bool operator !=(HeroId a, HeroId b) => a.Value != b.Value;
        public override string ToString() => $"HeroId({Value})";
    }

    /// <summary>
    /// Sparse Struct-of-Arrays storage for persistent heroes (Story 3.2, AR-12) — the pure-sim data substrate every
    /// later hero feature (3.9 load, 3.13 XP/leveling, 3.14 death/revival, 3.15 items) stands on.
    ///
    /// It is a STRUCTURAL CLONE of the Story-2.13 <see cref="BuildingStore"/> (D-5): a monotonic <see cref="Count"/>
    /// high-water fold bound, a LIFO free-list for slot recycling, a per-slot generation counter for ABA-armor, and
    /// a <see cref="PackRef"/>/<see cref="TryResolveRef"/> generation-stamped handle so a stale cross-tick reference
    /// resolves <c>false</c> rather than silently retargeting a NEW hero. The ONE thing HeroStore does differently:
    /// it is keyed by a stable <see cref="HeroId"/> (a persisted identity), NOT by its slot — and rows are folded /
    /// iterated ASCENDING BY <see cref="HeroId"/> (see <see cref="FoldOrder"/>), so the same set of heroes hashes
    /// identically regardless of which slots they happen to occupy or the order they were minted (producer-
    /// independence: the M2-local loader and the M5-server must agree byte-for-byte).
    ///
    /// Determinism posture (D-1 = DEFER): in Story 3.2 NO system mutates HeroStore mid-match (XP is 3.13, load is
    /// 3.9), so per the checksum-fold-timing rule the store is DORMANT and is NOT folded into the per-tick
    /// <see cref="SimChecksum"/> (exactly like 2.2a's dormant effective-stat arrays). 3.2 covers HeroStore via the
    /// init-time <see cref="Definitions.StartStateHash"/> (AC3) and establishes the <see cref="FoldOrder"/> ascending-
    /// identity contract so Story 3.13 flips on the per-tick fold with no re-derivation.
    ///
    /// Pure C#: no <c>using Godot</c>, <see cref="Fixed"/> (16.16) for gameplay numerics, <c>int</c>/<c>ulong</c>
    /// for identity/bookkeeping, ascending-order iteration only — it is sim code, in-scope for the release analyzer
    /// gate (no <c>float</c>, no <c>Dictionary</c> enumeration, no <c>System.Random</c>).
    /// </summary>
    public class HeroStore
    {
        /// <summary>Maximum simultaneous live heroes. Mirrors <see cref="BuildingStore.MAX_BUILDINGS"/> (a proven cap).
        /// The <see cref="PackRef"/> encoding reserves the low 8 bits for the slot, so this is bumpable up to 256
        /// (past that the pack width must widen). Ample for playable-faction × heroes-per-player today.</summary>
        public const int MAX_HEROES = 64;

        // ── Live per-hero data (D-6) — the fields that either mutate mid-match (folded from 3.13) or load as
        //    deterministic init state (covered by StartStateHash now). Kept deliberately LEAN (see the class doc):
        //    stat growth is a ModifierStore source, energy/mana is the existing entity SoA, and the leveling curve /
        //    xp bounty / signature+ultimate slots / revival curves are DEFINITION data on UnitDefinition/manifest —
        //    none of those are per-instance HeroStore accumulators. ─────────────────────────────────────────────
        /// <summary>Slot occupancy. A dead (recycled) slot is <c>false</c>; the fold/iteration skip it.</summary>
        public readonly bool[]   Alive    = new bool[MAX_HEROES];
        /// <summary>The stable cross-match identity for this row (D-4) — the fold's ascending sort key. NOT the slot,
        /// NOT the <see cref="EntityWorld"/> id.</summary>
        public readonly HeroId[] Id       = new HeroId[MAX_HEROES];
        /// <summary>Reverse link to the <see cref="EntityWorld"/> entity currently embodying this hero (D-8), for
        /// kill-attribution. RUNTIME state (which entity, this match) — NOT canonical persisted state, so it is NOT
        /// folded into <see cref="Definitions.StartStateHash"/> (it would differ between the M2/M5 producers).</summary>
        public readonly int[]    EntityId = new int[MAX_HEROES];
        /// <summary>Hero level. Loaded as deterministic init state by Story 3.9 (so <see cref="Definitions.StartStateHash"/>
        /// must cover it); mutated mid-match by the Story 3.13 XP runtime (which is where the per-tick fold lands).</summary>
        public readonly int[]    Level    = new int[MAX_HEROES];
        /// <summary>Accumulated experience (<see cref="Fixed"/> 16.16). Init state loaded by 3.9; mutated by 3.13.</summary>
        public readonly Fixed[]  Xp       = new Fixed[MAX_HEROES];

        // ── RESERVED for the Story 3.13 co-timed SimChecksum fold (D-6) — DO NOT declare these yet ────────────────
        //   Story 3.13 AC2 folds HeroStore Level/Xp when the XP runtime first mutates them mid-match, and RESERVES
        //   Story 3.14's revival fields in the SAME AlgoVersion bump (one bump, not two). When 3.13/3.14 land, add
        //   these columns here (and reset them in Mint, alongside Level/Xp) so they ride that single fold:
        //     • bool[]  Alive3_14      — hero-is-on-the-field vs awaiting revival (distinct from the SLOT Alive above)
        //     • bool[]  AwaitingRevival — dead-but-persisted, counting down to respawn
        //     • Fixed[] RevivalTimer    — seconds remaining until revival
        //     • int[]   RevivalLink     — revive-location / building link (e.g. an Altar building id / spawn slot)
        //   Inventory/items are NOT reserved here — Story 3.15 owns a separate free-list ItemStore (fold #2); pre-
        //   declaring an Inventory[] saves no fold (the cost is triggered by first MUTATION, not declaration) and
        //   3.15 has not designed the shape (default-6 slots, per-scenario-configurable, charges possibly on the item).

        // ── Management — the monotonic high-water fold/iteration bound (mirrors BuildingStore.Count). Recycling
        //    reuses dead slots BELOW Count, so Count only grows. ─────────────────────────────────────────────────
        /// <summary>One past the highest slot ever allocated — the iteration bound (<see cref="FoldOrder"/> and any
        /// checksum fold scan <c>0..Count</c> for live slots). A monotonic high-water mark.</summary>
        public int Count { get; private set; }

        // ── Free-list recycling (mirrors BuildingStore / ProjectileStore) ──────────────────────────────────────
        // Deterministic LIFO stack of dead slots available for reuse. UNFOLDED internal bookkeeping — never a
        // checksum input (the fold scans live slots < Count and a recycled slot is < Count, so the fold sees no new
        // state). Mint() pops a free slot before appending; Destroy() pushes the freed slot. LIFO off the
        // deterministic tick ⇒ every peer maps slot→hero identically.
        private readonly int[] _freeList = new int[MAX_HEROES];
        private int            _freeCount;

        // ── Generation counter (ABA-armor, mirrors BuildingStore.Generation) ───────────────────────────────────
        // Per-slot recycle generation, bumped each time Mint() reuses a dead slot. UNFOLDED — deterministic (the
        // recycle sequence is deterministic) and caught transitively via the folded Alive/Count. Every cross-tick
        // hero reference (notably EntityWorld.HeroIndex, D-8) is PACKED as (Generation[slot] << 8) | slot and
        // validated on deref by TryResolveRef, so a stale ref to a since-recycled slot reverts CLEANLY instead of
        // ABA-retargeting a DIFFERENT hero. Golden-neutral: generation starts at 0, so PackRef(slot) == slot for
        // every never-recycled hero.
        public readonly int[] Generation = new int[MAX_HEROES];

        /// <summary>
        /// Mint a new hero row for the stable <paramref name="id"/>, linked to entity <paramref name="entityId"/>,
        /// at the given <paramref name="level"/> / <paramref name="xp"/>. Returns the slot, or -1 if the store is full
        /// OR <paramref name="id"/> is already live (the ascending-identity fold requires UNIQUE live ids — see the guard).
        /// Recycles a dead slot first (bumping its generation so stale packed refs to the prior occupant fail
        /// <see cref="TryResolveRef"/>), else appends a fresh one. EVERY live field is written here (there is no
        /// separate reset step — a recycled slot therefore carries NONE of the prior hero's state, the SoA-recycle
        /// trap); the reserved 3.13/3.14 fields above must reset here too when they are declared.
        /// </summary>
        public int Mint(HeroId id, int entityId, int level, Fixed xp)
        {
            // Contract (AC2 / FoldOrder producer-independence): a HeroId is UNIQUE across LIVE rows. FoldOrder sorts by
            // HeroId with a strict-'>' (stable) insertion sort, so two live rows sharing an id would fold in mint-order-
            // dependent SLOT order → a cross-producer StartStateHash divergence now, and a silent SimChecksum desync once
            // Story 3.13 reuses FoldOrder for the per-tick fold. Hard-reject a duplicate live id (deterministic, all
            // builds; the same -1 "mint refused" signal as a full store). Uniqueness is over LIVE rows only — a destroyed
            // row's id is free to re-mint (the fold skips dead slots).
            for (int i = 0; i < Count; i++)
                if (Alive[i] && Id[i].Value == id.Value)
                    return -1;

            int slot;
            if (_freeCount > 0)
            {
                slot = _freeList[--_freeCount]; // reuse a dead slot (LIFO, deterministic)
                Generation[slot]++;             // bump generation so stale packed refs to the prior occupant fail TryResolveRef
            }
            else if (Count < MAX_HEROES)
                slot = Count++;                 // append a fresh slot (Generation stays 0 — never recycled)
            else
                return -1;                      // all MAX_HEROES slots are simultaneously live

            Alive[slot]    = true;
            Id[slot]       = id;
            EntityId[slot] = entityId;
            Level[slot]    = level;
            Xp[slot]       = xp;
            return slot;
        }

        /// <summary>
        /// Story 3.10 (UX-DR62 / 3.9 deferred gap): bulk-clear the store to its EXACT post-construction state so the
        /// Edit↔Play reset can re-mint the deployed profile NON-ADDITIVELY (the confirmed 3.9 defect: per-slot
        /// <see cref="Destroy"/> + a monotonic <see cref="Count"/> could never empty the store, so re-deploying
        /// accumulated stale live rows). Empties every row array + the free-list and zeroes <see cref="Count"/> /
        /// <see cref="Generation"/>, so a cleared store is byte-for-byte equal to <c>new HeroStore()</c> and a re-mint
        /// after clear places exactly one row per placed hero. Fires no hooks; pure integer bulk wipe.
        /// </summary>
        public void Clear()
        {
            System.Array.Clear(Alive);
            System.Array.Clear(Id);
            System.Array.Clear(EntityId);
            System.Array.Clear(Level);
            System.Array.Clear(Xp);
            System.Array.Clear(Generation);
            System.Array.Clear(_freeList);
            _freeCount = 0;
            Count      = 0;
        }

        /// <summary>Destroy a hero row — marks the slot dead and returns it to the free-list for reuse. Bounds +
        /// double-free guarded (mirrors BuildingStore.Destroy): never push a slot twice, which would hand the same
        /// slot to two future Mint() calls and corrupt the store. The stable identity itself PERSISTS in the profile
        /// (Story 3.9); Destroy only tears down the in-match row.</summary>
        public void Destroy(int slot)
        {
            if (slot < 0 || slot >= Count || !Alive[slot]) return;
            Alive[slot] = false;
            _freeList[_freeCount++] = slot;
        }

        /// <summary>
        /// Pack a live hero slot into a generation-stamped CROSS-TICK reference <c>(Generation[slot] &lt;&lt; 8) | slot</c>
        /// (slot 0–63 in the low 8 bits, generation in the upper 24). Every holder that carries a hero across ticks —
        /// notably <see cref="EntityWorld.HeroIndex"/> — stores this instead of the bare slot and validates it via
        /// <see cref="TryResolveRef"/> on deref. GOLDEN-NEUTRAL: at generation 0, <c>PackRef(slot) == slot</c>.
        /// </summary>
        public int PackRef(int slot) => (Generation[slot] << 8) | slot;

        /// <summary>
        /// Resolve a packed hero reference back to a live slot. Returns true and the slot iff it is in bounds, ALIVE,
        /// and the generation still matches (the SAME hero occupies it). A stale ref — the slot was recycled
        /// (generation bumped) or freed — returns false, so the holder reverts cleanly and NEVER retargets the new
        /// occupant. The -1 sentinel (EntityWorld's "no hero") resolves false: <c>-1 &amp; 0xFF == 255 ≥ Count</c>.
        /// </summary>
        public bool TryResolveRef(int packed, out int slot)
        {
            slot = packed & 0xFF;                    // low 8 bits — always 0–255, so slot ≥ 0
            return slot < Count && Alive[slot] && Generation[slot] == (packed >> 8);
        }

        /// <summary>
        /// The deterministic checksum-fold / iteration order: the LIVE slot indices sorted ASCENDING by
        /// <see cref="Id"/> (<see cref="HeroId.Value"/>). This is the ascending-IDENTITY contract (AC2) — it is
        /// PRODUCER-INDEPENDENT: invariant to mint/destroy order and slot layout, so two producers (the M2-local
        /// loader and the M5-server) that mint the SAME heroes in DIFFERENT orders fold to the SAME bytes. Story 3.13
        /// reuses this exact order when it flips on the per-tick <see cref="SimChecksum"/> fold (it may pass a reused
        /// scratch buffer then to avoid the per-tick allocation; 3.2's init-once callers allocate).
        ///
        /// <see cref="HeroId"/> values are UNIQUE, so the sort is a TOTAL order — deterministic regardless of sort
        /// stability — and the compare is pure <c>ulong</c> integer arithmetic → byte-identical across platforms.
        /// Insertion sort (not LINQ / Array.Sort-with-comparer) keeps it allocation-lean and trivially auditable for
        /// the riskiest-story review; N is small (≤ live hero count).
        /// </summary>
        public int[] FoldOrder()
        {
            int live = 0;
            for (int slot = 0; slot < Count; slot++)
                if (Alive[slot]) live++;

            int[] order = new int[live];
            int n = 0;
            for (int slot = 0; slot < Count; slot++)
                if (Alive[slot]) order[n++] = slot;

            // Insertion sort by Id.Value ascending. Unique keys ⇒ a deterministic total order.
            for (int i = 1; i < n; i++)
            {
                int s = order[i];
                ulong key = Id[s].Value;
                int j = i - 1;
                while (j >= 0 && Id[order[j]].Value > key)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = s;
            }
            return order;
        }
    }
}
