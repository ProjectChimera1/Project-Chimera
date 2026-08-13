#nullable enable
using System;
using ProjectChimera.Core.Definitions; // UnitDefinition (Story 3.14 SourceDef — the respawn def carried per hero)

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

        /// <summary>Story 3.14 (D-6): the <see cref="RevivalLink"/> "no link" sentinel. Default 0 is a valid building
        /// <c>PackRef</c>, so a hero that dies WITHOUT a revive order sets this instead; the countdown fires only while
        /// <c>RevivalLink != REVIVAL_NONE</c>. Mint leaves the field at its 0 default (an on-field hero ignores it).</summary>
        public const int REVIVAL_NONE = -1;

        /// <summary>Story 3.15: the fixed per-hero inventory STRIDE (WC3's 6 slots) — both the physical <see cref="Inventory"/>
        /// ring width AND the default usable count. The per-scenario <c>inventory_slot_count ∈ [1,6]</c> caps the USABLE
        /// slots below this ceiling; a ceiling above 6 is a future stride bump (out of scope).</summary>
        public const int INVENTORY_SLOTS = 6;

        /// <summary>Story 3.15: the <see cref="Inventory"/> "empty slot" sentinel (no held item ref).</summary>
        public const int INVENTORY_EMPTY = -1;

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

        // ── Story 3.13 mutable growth-tracking (folded into SimChecksum v11) ─────────────────────────────────────
        /// <summary>How many permanent per-level growth stacks have been applied to this hero's entity via
        /// <c>ModifierStore.Apply</c> (Story 3.13, D-3). Converges to <c>Level - 1</c> each tick — the
        /// <see cref="ProjectChimera.Combat.HeroXpSystem"/> applies <c>(Level-1) - GrowthStacksApplied</c> more stacks and
        /// sets this to <c>Level-1</c>. Genuine mutable sim state → FOLDED into <see cref="SimChecksum"/> (v11).</summary>
        public readonly int[]    GrowthStacksApplied = new int[MAX_HEROES];

        // ── Story 3.13 per-hero runtime curve/growth/share CONSTANTS (def-derived at mint; NOT folded — the
        //    AttackDamage/Delivery authored-constant posture; a divergence surfaces transitively via Level/Xp). ────
        /// <summary>Max level this hero can reach (from <c>HeroDefinition.MaxLevel</c>). Set in <see cref="Mint"/>.</summary>
        public readonly int[]    MaxLevelOf          = new int[MAX_HEROES];
        /// <summary>Base XP for the first level-up (the geometric-curve base, <see cref="Fixed"/>). Set in <see cref="Mint"/>.</summary>
        public readonly Fixed[]  BaseXpOf            = new Fixed[MAX_HEROES];
        /// <summary>Per-level geometric multiplier on the XP requirement (<see cref="Fixed"/>). Set in <see cref="Mint"/>.</summary>
        public readonly Fixed[]  XpGrowthOf          = new Fixed[MAX_HEROES];
        /// <summary>Radius (world units, <see cref="Fixed"/>) within which a hostile death credits this hero. Set in <see cref="Mint"/>.</summary>
        public readonly Fixed[]  XpShareRadiusOf     = new Fixed[MAX_HEROES];
        /// <summary>Flat max-health growth per level above 1 (<see cref="Fixed"/>). Set in <see cref="Mint"/>.</summary>
        public readonly Fixed[]  HealthPerLevelOf    = new Fixed[MAX_HEROES];
        /// <summary>Flat attack-damage growth per level above 1 (<see cref="Fixed"/>). Set in <see cref="Mint"/>.</summary>
        public readonly Fixed[]  DamagePerLevelOf    = new Fixed[MAX_HEROES];
        /// <summary>Flat armor growth per level above 1 (<see cref="Fixed"/>). Set in <see cref="Mint"/>.</summary>
        public readonly Fixed[]  ArmorPerLevelOf     = new Fixed[MAX_HEROES];
        /// <summary>DW-26: per-hero XP-gain MULTIPLIER (from <c>HeroDefinition.XpPerKill / 100</c>, <see cref="Fixed"/>) —
        /// each kill credit to this hero is <c>victim.XpBounty × this</c>. Neutral <see cref="Fixed.One"/> = full bounty
        /// (the pre-DW-26 behaviour). Set in <see cref="Mint"/> (null → <see cref="Fixed.One"/>); NON-folded (def-derived
        /// constant, the <see cref="BaseXpOf"/> posture) — a divergence surfaces transitively via the folded <see cref="Xp"/>.</summary>
        public readonly Fixed[]  XpGainFactorOf      = new Fixed[MAX_HEROES];

        // ── Story 15-21 per-hero ATTRIBUTE contributions (flattened by HeroAttributeResolver at the scenario-apply
        //    boundary; stride-AttributeStats.Count flat rings indexed slot * AttributeStats.Count + stat). Authored
        //    constants, the BaseXpOf posture — NOT folded: a hero's live attribute value is base + perLevel×(Level−1),
        //    a pure function of the FOLDED Level, so divergence surfaces transitively via ModifierStore/Energy. ──────
        /// <summary>Story 15-21: per-hero per-stat BASE attribute contribution (level 1), in
        /// <see cref="Definitions.AttributeStats"/> index order. Set in <see cref="Mint"/> (null → all-zero).</summary>
        public readonly Fixed[]  AttrStatBase        = new Fixed[MAX_HEROES * Definitions.AttributeStats.Count];
        /// <summary>Story 15-21: per-hero per-stat PER-LEVEL attribute contribution (auto-growth, D-2), in
        /// <see cref="Definitions.AttributeStats"/> index order. Set in <see cref="Mint"/> (null → all-zero).</summary>
        public readonly Fixed[]  AttrStatPerLevel    = new Fixed[MAX_HEROES * Definitions.AttributeStats.Count];

        /// <summary>Story 15-21: the hero's LIVE attribute-derived contribution for one closed-vocabulary stat —
        /// <c>base + perLevel × (Level − 1)</c>, a pure function of the FOLDED <see cref="Level"/> and the two
        /// authored-constant lanes (which is why the attribute table needs no folded state of its own). Used by the
        /// energy pair (<c>EnergyRegenSystem</c>) and presentation readouts; the four modifier-channel stats flow
        /// through the HeroXpSystem growth/base modifiers instead and must NOT be double-read from here.</summary>
        public Fixed AttributeStatAt(int slot, int stat)
        {
            int levelsAbove1 = Level[slot] - 1;
            if (levelsAbove1 < 0) levelsAbove1 = 0;
            int i = slot * Definitions.AttributeStats.Count + stat;
            return AttrStatBase[i] + AttrStatPerLevel[i] * Fixed.FromInt(levelsAbove1);
        }

        // ── RESERVED for Story 3.14 (death & revival), declared + folded NOW so 3.14 needs no second AlgoVersion bump
        //    (Story 3.13 D-2). Written to their zero/false defaults in Mint; folded at defaults into SimChecksum v11. ─
        /// <summary>Story 3.14 (reserved): hero is on the field (true) vs awaiting revival (false). Distinct from the
        /// SLOT <see cref="Alive"/>. Declared + folded now at its default (true set in <see cref="Mint"/>); no 3.13 system reads it.</summary>
        public readonly bool[]   Alive3_14           = new bool[MAX_HEROES];
        /// <summary>Story 3.14 (reserved): dead-but-persisted, counting down to respawn. Default false. Folded v11.</summary>
        public readonly bool[]   AwaitingRevival     = new bool[MAX_HEROES];
        /// <summary>Story 3.14 (reserved): seconds remaining until revival (<see cref="Fixed"/>). Default Zero. Folded v11.</summary>
        public readonly Fixed[]  RevivalTimer        = new Fixed[MAX_HEROES];
        /// <summary>Story 3.14 (reserved): revive-location / building link (e.g. an Altar building id, PACKED). Default 0.
        /// Folded v11. A hero not counting down carries <see cref="REVIVAL_NONE"/> (-1) here (set at the death transition).</summary>
        public readonly int[]    RevivalLink         = new int[MAX_HEROES];

        // ── Story 3.14 per-hero NON-FOLDED constants (respawn def + owner faction; the AttackDamage/curve-constant
        //    posture — authored/def-derived, so a divergence surfaces transitively via the folded revival state). Written
        //    in Mint (the SoA-recycle contract) — a recycled slot carries none of the prior hero's def/owner. ───────────
        /// <summary>Story 3.14: the <see cref="UnitDefinition"/> this hero spawned from, kept so a revival can re-spawn a
        /// fresh entity through the shared unit-spawn path (never duplicating <c>ApplyUnitDefinition</c>). Null when the
        /// hero was minted without a def (Tier-1 persistence tests) → those heroes cannot respawn. NOT folded (a class ref).</summary>
        public readonly UnitDefinition?[] SourceDef  = new UnitDefinition?[MAX_HEROES];
        /// <summary>Story 3.14: the faction that owns this hero — the anti-cheat check for a revive order (the order's
        /// <c>expectedFaction</c> must equal this) and the faction the respawn is created under. NOT folded (authored).</summary>
        public readonly Faction[] OwnerFaction       = new Faction[MAX_HEROES];
        /// <summary>
        /// Story 15-24c: the OWNING FACTION's attribute model, kept per hero so <c>HeroXpSystem</c> can evaluate
        /// THRESHOLD rows against the hero's live attribute totals at reconcile time. Linear rows do NOT need it
        /// (they are pre-flattened into <see cref="AttrStatBase"/>/<see cref="AttrStatPerLevel"/> at apply), but a
        /// step row is not expressible in that pair — see <c>HeroAttributeResolver.EvaluateAt</c>. Exactly the
        /// <see cref="SourceDef"/> posture: a NON-FOLDED authored class ref, written in <see cref="Mint"/> (the
        /// SoA-recycle contract) and RE-RESOLVED from the slot faction defs on save-load rather than persisted by
        /// value. Null ⇒ no model ⇒ no threshold contributions (the pre-15-24c behaviour, byte-for-byte).
        /// </summary>
        public readonly Definitions.AttributeModelDefinition?[] AttrModelOf = new Definitions.AttributeModelDefinition?[MAX_HEROES];

        // ── Story 3.15 per-hero INVENTORY (lives on the PERSISTED row so it survives death→revival by construction —
        //    the D-1 obligation carried forward from Story 3.14). Fixed-stride flat ring indexed
        //    slot * INVENTORY_SLOTS + s, holding PACKED ItemStore refs (INVENTORY_EMPTY = -1). FOLDED into SimChecksum
        //    (v12) in the per-hero FoldOrder loop; reset to all-empty on (re)Mint + Clear (the SoA-recycle contract —
        //    a re-minted row must never carry a prior hero's inventory). Hero inventory starts EMPTY each match (3.15;
        //    cross-match item persistence is Story 3.16). ─────────────────────────────────────────────────────────────
        /// <summary>Story 3.15: the per-hero inventory ring of PACKED <c>ItemStore</c> refs (<see cref="INVENTORY_EMPTY"/>
        /// = -1), indexed <c>slot * INVENTORY_SLOTS + s</c>. Folded into <see cref="SimChecksum"/> (v12) + init-time
        /// <see cref="Definitions.StartStateHash"/> (v2). Reset to all-empty on (re)Mint (the SoA-recycle contract).</summary>
        public readonly int[]     Inventory          = new int[MAX_HEROES * INVENTORY_SLOTS];

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
        public int Mint(HeroId id, int entityId, int level, Fixed xp,
                        int maxLevel = 0, Fixed baseXp = default, Fixed xpGrowth = default, Fixed xpShareRadius = default,
                        Fixed healthPerLevel = default, Fixed damagePerLevel = default, Fixed armorPerLevel = default,
                        UnitDefinition? sourceDef = null, Faction ownerFaction = default,
                        Fixed? xpGainFactor = null,
                        Fixed[]? attrStatBase = null, Fixed[]? attrStatPerLevel = null,
                        Definitions.AttributeModelDefinition? attrModel = null)
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
            // Story 3.13: per-hero runtime curve/growth/share constants (def-derived, passed at mint). Every live field
            // is written here (the SoA-recycle contract) — a recycled slot carries NONE of the prior hero's curve.
            MaxLevelOf[slot]       = maxLevel;
            BaseXpOf[slot]         = baseXp;
            XpGrowthOf[slot]       = xpGrowth;
            XpShareRadiusOf[slot]  = xpShareRadius;
            HealthPerLevelOf[slot] = healthPerLevel;
            DamagePerLevelOf[slot] = damagePerLevel;
            ArmorPerLevelOf[slot]  = armorPerLevel;
            // DW-26: per-hero XP-gain multiplier. A caller that passes none (tests, persistence restore) gets the neutral
            // Fixed.One (full bounty) — a Fixed default param cannot BE Fixed.One and default(Fixed) is Zero (which would
            // silently zero every non-passing caller's kill XP), so the nullable-null → One mapping is deliberate.
            XpGainFactorOf[slot]   = xpGainFactor ?? Fixed.One;
            // Story 15-21: flattened attribute contributions (HeroAttributeResolver output; null = no attributes →
            // all-zero, byte-identical to a pre-15-21 hero). Written unconditionally — the SoA-recycle contract: a
            // recycled slot must never carry the prior hero's contributions.
            int attrBase = slot * Definitions.AttributeStats.Count;
            for (int s = 0; s < Definitions.AttributeStats.Count; s++)
            {
                AttrStatBase[attrBase + s]     = attrStatBase     != null && s < attrStatBase.Length     ? attrStatBase[s]     : Fixed.Zero;
                AttrStatPerLevel[attrBase + s] = attrStatPerLevel != null && s < attrStatPerLevel.Length ? attrStatPerLevel[s] : Fixed.Zero;
            }
            // Story 3.13 mutable growth-tracking + Story 3.14 reserved revival state — zeroed on (re)mint so a recycled
            // slot never inherits prior growth/revival state (folded into SimChecksum v11).
            GrowthStacksApplied[slot] = 0;
            Alive3_14[slot]        = true;   // a freshly-minted hero is on the field
            AwaitingRevival[slot]  = false;
            RevivalTimer[slot]     = Fixed.Zero;
            RevivalLink[slot]      = 0;      // Mint default stays 0 (golden-neutral); the death transition sets REVIVAL_NONE
            // Story 3.14 non-folded constants (respawn def + owner faction) — written here per the SoA-recycle contract.
            SourceDef[slot]        = sourceDef;
            OwnerFaction[slot]     = ownerFaction;
            AttrModelOf[slot]      = attrModel; // Story 15-24c (same non-folded ref posture; recycle-reset by writing it here)
            // Story 3.15: a freshly-minted hero carries an EMPTY inventory (3.15 has no cross-match item persistence).
            // Reset every stride slot so a recycled row never inherits a prior hero's held-item refs (SoA-recycle contract).
            int invBase = slot * INVENTORY_SLOTS;
            for (int s = 0; s < INVENTORY_SLOTS; s++)
                Inventory[invBase + s] = INVENTORY_EMPTY;
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
            // Story 3.13 growth-tracking + curve constants.
            System.Array.Clear(GrowthStacksApplied);
            System.Array.Clear(MaxLevelOf);       System.Array.Clear(BaseXpOf);         System.Array.Clear(XpGrowthOf);
            System.Array.Clear(XpShareRadiusOf);  System.Array.Clear(HealthPerLevelOf); System.Array.Clear(DamagePerLevelOf);
            System.Array.Clear(ArmorPerLevelOf);   System.Array.Clear(XpGainFactorOf); // DW-26 (a re-Mint re-seeds it to One)
            System.Array.Clear(AttrStatBase);      System.Array.Clear(AttrStatPerLevel); // Story 15-21
            // Story 3.14 reserved revival state + non-folded constants.
            System.Array.Clear(Alive3_14);        System.Array.Clear(AwaitingRevival);  System.Array.Clear(RevivalTimer);
            System.Array.Clear(RevivalLink);      System.Array.Clear(SourceDef);        System.Array.Clear(OwnerFaction);
            System.Array.Clear(AttrModelOf); // Story 15-24c (non-folded ref lane — cleared with its SourceDef sibling)
            // Story 3.15: reset inventory. Array.Clear (→0) matches the ctor's zero-init, so a cleared store equals
            // `new HeroStore()`. The fold is count-driven over LIVE slots only (all reset to INVENTORY_EMPTY in Mint),
            // so the 0-vs-(-1) choice of the (never-folded) dead region is invisible; Clear keeps new-equality exact.
            System.Array.Clear(Inventory);
            System.Array.Clear(Generation);
            System.Array.Clear(_freeList);
            _freeCount = 0;
            Count      = 0;
        }

        /// <summary>
        /// Story 11.3 (SP save/load): restore the private high-water <see cref="Count"/> + free-list after the
        /// persistence layer has written the SoA row arrays (incl. the public <see cref="Generation"/> and
        /// <see cref="Inventory"/>) directly. Godot-free integer bookkeeping only; copies at most
        /// <see cref="MAX_HEROES"/> free-list entries (defensive).
        /// </summary>
        /// <summary>Story 11.3 (SP save/load): the active portion of the recycle free-list (LIFO order preserved) for a
        /// save — the exact next slot <see cref="Mint"/> will reuse. Returns a fresh copy.</summary>
        public int[] CaptureFreeList()
        {
            var copy = new int[_freeCount];
            System.Array.Copy(_freeList, copy, _freeCount);
            return copy;
        }

        public void RestoreManagement(int count, int[] freeList, int freeCount)
        {
            Count = count < 0 ? 0 : (count > MAX_HEROES ? MAX_HEROES : count);
            System.Array.Clear(_freeList);
            int n = freeCount < 0 ? 0 : (freeCount > MAX_HEROES ? MAX_HEROES : freeCount);
            if (freeList != null)
                for (int i = 0; i < n && i < freeList.Length; i++) _freeList[i] = freeList[i];
            _freeCount = n;
        }

        /// <summary>Destroy a hero row — marks the slot dead and returns it to the free-list for reuse. Bounds +
        /// double-free guarded (mirrors BuildingStore.Destroy): never push a slot twice, which would hand the same
        /// slot to two future Mint() calls and corrupt the store. The stable identity itself PERSISTS in the profile
        /// (Story 3.9); Destroy only tears down the in-match row.
        ///
        /// DW-52: also clears the freed slot's <see cref="EntityId"/> back-reference to -1 ("no linked entity",
        /// mirroring <see cref="EntityWorld.HERO_NONE"/>). Without this the dead row's <see cref="EntityId"/> dangles at
        /// a since-dead/recycled entity id. It is non-folded RUNTIME state (see the field doc), so clearing it is
        /// invisible to <see cref="SimChecksum"/> / <see cref="Definitions.StartStateHash"/> / every golden.</summary>
        public void Destroy(int slot)
        {
            if (slot < 0 || slot >= Count || !Alive[slot]) return;
            Alive[slot] = false;
            EntityId[slot] = -1;            // DW-52: clear the dangling back-reference (mirrors EntityWorld.HERO_NONE)
            _freeList[_freeCount++] = slot;
        }

        /// <summary>
        /// DW-52: free the hero row addressed by a generation-stamped packed handle (<see cref="PackRef"/>) — the exact
        /// value <see cref="EntityWorld.HeroIndex"/> stores per entity. Resolves the handle via <see cref="TryResolveRef"/>
        /// (ABA-safe) and, iff it names a LIVE current occupant, frees that slot via <see cref="Destroy"/> and returns
        /// <c>true</c>. A <see cref="EntityWorld.HERO_NONE"/> (-1) sentinel or any STALE handle (the slot was freed or
        /// recycled, generation bumped) resolves <c>false</c> → clean no-op returning <c>false</c>, never freeing the
        /// new occupant. This is the editor delete path's one-line hook (a non-hero unit's -1 handle no-ops for free).
        /// </summary>
        public bool DestroyByRef(int packedHeroRef)
        {
            if (!TryResolveRef(packedHeroRef, out int slot)) return false;
            Destroy(slot);
            return true;
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
