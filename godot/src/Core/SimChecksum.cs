#nullable enable
using System;
using ProjectChimera.Dsl;     // DslVarTable (Story 7.3 typed/scoped variable + timer fold)
using ProjectChimera.Effects; // ModifierStore (Option B fold param, Story 2.2b) + StatusFlags — same sim layer

namespace ProjectChimera.Core
{
    /// <summary>
    /// Computes a deterministic FNV-1a checksum over the full simulation world state.
    ///
    /// Used for desync detection in deterministic lockstep multiplayer (P2.4).
    /// Both peers compute this every N ticks and compare; a mismatch indicates divergence.
    ///
    /// Hashed state (in order, ascending entity ID):
    ///   - EntityWorld: Position (X, Y, Z) and Health for every alive entity
    ///   - EntityWorld terrain elevation: Elevation (Raw) per alive entity — added v15 (Story 6.3), the spawn-sampled
    ///     terrain height (a dedicated SoA array, NOT Position.Y).
    ///   - BuildingStore: Alive flag, Health, ConstructionTimer for every building slot
    ///   - ResourceStore: Ore, Crystal, SupplyUsed, SupplyCap, FactionBase for each active
    ///     faction (via FactionRegistry, ascending)
    ///   - SimRng: the shared generator's 64-bit State (low 32 bits then high 32 bits) — added v3 (Story 1.5)
    ///   - EntityWorld command state: per alive entity, CommandTarget + the patrol-route ring (PatrolCount,
    ///     PatrolIndex, PatrolDir, then count-driven PatrolWaypoints X/Y/Z) — added v4 (Story 1.12)
    ///   - EntityWorld separation config: per alive entity, CollisionRadius (Raw) + SeparationPriorityOf (int) —
    ///     added v5 (Story 1.13). CategoryOf is deliberately NOT hashed (presentation-read, like MeshType).
    ///   - EntityWorld attack delivery: per alive entity, Delivery (int) + ProjectileSpeed (Raw) — added v10
    ///     (Story 3.12), the authorable Hitscan/Projectile axis + per-unit projectile speed that combat reads in-tick.
    ///   - EntityWorld XP bounty: per alive entity, XpBounty (Raw) — added v11 (Story 3.13), the def-derived XP a unit
    ///     awards on death (HeroXpSystem reads it via the DeathFeed).
    ///   - HeroStore mutable state: ascending HeroId (FoldOrder) — live count, then per slot HeroId.Value (low/high),
    ///     Level, Xp (Raw), GrowthStacksApplied, and the reserved Story 3.14 revival fields — added v11 (Story 3.13),
    ///     now that the XP runtime mutates Level/Xp mid-match (dormant/not-folded since 3.2). A null store folds Mix(0).
    ///   - EntityWorld effective stats + ability/status: per alive entity, EffectiveAttackDamage, EffectiveMaxHealth,
    ///     EffectiveMoveSpeed, Energy (all Raw), and StatusFlagsOf (int) — added v6 (Story 2.2b), now that the
    ///     ModifierStore MUTATES them mid-match. Base* stays UNFOLDED (authored, in-tick-immutable).
    ///   - EntityWorld effective armor: per alive entity, EffectiveArmor (Raw) — added v8 (Story 2.6), the buffable
    ///     armor stat. ModifierSystem recomputes it mid-match (an aura grants +armor; DamageResolver subtracts it),
    ///     so it is peer-divergent sim truth. BaseArmor stays UNFOLDED (authored, the BaseAttackDamage posture).
    ///   - ModifierStore: per alive entity (ascending owner-id then slot), the active-instance count then, per slot,
    ///     modifierId / remainingTicks / ticksUntilPeriod / periodsRemaining / stackCount — added v6 (Story 2.2b).
    ///     The descriptor refs + caster id/faction are NOT folded (authored / peer-identical). A null store folds an
    ///     identical Mix(0) count per entity (≡ an empty store), so legacy callers and an empty store agree.
    ///   - EntityWorld ability cooldowns: per alive entity, AbilityCount then count-driven AbilityCooldownTicks
    ///     (int ticks) — added v7 (Story 2.4a), the first ability array that mutates mid-match (a cast starts a
    ///     cooldown; it ticks down each frame). AbilityId / MaxEnergy / PendingCast* are NOT hashed (authored /
    ///     transient); AbilityCount is folded ONLY as the cross-platform-safe count-driven loop bound (like PatrolCount).
    ///   - EntityWorld shift-queue: per alive entity, OrderQueueCount then count-driven OrderQueueCmd/TargetX/TargetZ
    ///     — added v9 (Story 2.12), the per-entity pending-order ring (OrderApplier appends a Shift order; OrderQueueSystem
    ///     pops the head on completion) → runtime-mutable sim truth. Count-driven (only populated slots hashed).
    ///   - BuildingStore rally point: per building, HasRallyPoint then RallyPoint X/Z — added v9 (Story 2.12, D-1), now
    ///     that rally is wire-driven (UnitCommand.SetRally) and read in-tick by SpawnTrainedUnit (mutable sim truth).
    ///   - ResourceNodeStore mutable state: live count, then ascending node id — SupplyRemaining, Active,
    ///     AssignedGatherers, IncomeTicksElapsed — added v13 (Story 4.7), the FIRST-EVER fold of this store (a
    ///     pre-existing gap; see AlgoVersion's doc). A null store folds Mix(0).
    ///   - ResearchStore mutable state: per ACTIVE faction (ascending -- the outer loop mirrors ResourceStore's
    ///     ActiveFactions iteration, not a raw faction-count stride), InProgressIndex then RemainingTicks, then an
    ///     inner count-driven loop bound by that faction's OWN CompletedLevels[idx].Length (a per-faction research-
    ///     entry count, never a fixed constant) over CompletedLevels[idx][r] plus the four cumulative stat deltas
    ///     (.Raw) per research index -- added v14 (Story 4.10), the FIRST-EVER fold of this store (4.9 built it
    ///     mid-match-mutable but explicitly deferred the fold to this story). A null store folds a single Mix(0).
    ///
    /// Versioned by <see cref="AlgoVersion"/> — bump on any change to the hashed set/order
    /// (forces an intentional golden re-baseline). MatchStats is deliberately NOT hashed
    /// (private, write-only scoreboard derived from already-hashed deaths — observational only).
    /// The Story 7.8 <c>DslVarReadback</c> (the presentation read rail) is likewise NOT folded — it is a
    /// version-stamped COPY of the already-hashed <see cref="ProjectChimera.Dsl.DslVarTable"/>, published once per
    /// tick for presentation only, so folding it would be redundant and (crucially) leaving it unfolded means a UI
    /// mismatch cannot desync (AR-32 read rail). It is never passed to <see cref="Compute"/> and never bumps
    /// <see cref="AlgoVersion"/>.
    ///
    /// All values are Fixed (int Raw) — platform-independent, no float arithmetic.
    /// </summary>
    public static class SimChecksum
    {
        // FNV-1a 32-bit constants
        private const uint FNV_OFFSET = 2166136261u;
        private const uint FNV_PRIME  = 16777619u;

        /// <summary>
        /// Version of the checksum ALGORITHM (which sim state is hashed, and in what order) — distinct from
        /// the 32-bit hash width. Stamped into every golden header so a baseline self-identifies, and pinned
        /// by the known-state guard test. Bump this by exactly one whenever the hashed set/order changes, and
        /// re-baseline the goldens in the SAME commit.
        ///   v1 — implicit, pre-1.3b: Ore only, per active faction (Stories 1.1–1.3a).
        ///   v2 — Story 1.3b: full per-faction coverage (Ore, Crystal, SupplyUsed, SupplyCap, FactionBase).
        ///   v3 — Story 1.5: fold the shared SimRng.State (low then high 32 bits) so a divergent RNG stream desyncs.
        ///   v4 — Story 1.12: fold per-entity CommandTarget + the patrol-route ring (PatrolCount/Index/Dir +
        ///        count-driven PatrolWaypoints) so the full RTS command vocabulary is hashed sim truth.
        ///   v5 — Story 1.13: fold per-entity CollisionRadius + SeparationPriorityOf (separation config is sim
        ///        truth — a peer divergence in either changes movement and must desync detectably). CategoryOf is
        ///        NOT folded (presentation-read formation input; its effect reaches the hash via Position).
        ///   v6 — Story 2.2b: the ModifierStore now mutates Effective* / Energy / StatusFlagsOf mid-match, so fold
        ///        per-entity EffectiveAttackDamage/EffectiveMaxHealth/EffectiveMoveSpeed/Energy + StatusFlagsOf, AND
        ///        the ModifierStore per-instance state (count-driven, ascending owner-id then slot). Base* stays
        ///        UNFOLDED (authored, in-tick-immutable). The ONE scheduled re-baseline of all goldens.
        ///   v7 — Story 2.4a: fold per-entity AbilityCooldownTicks (count-driven by AbilityCount, ascending slot) —
        ///        the first ability array that mutates mid-match (a cast starts the cooldown; it ticks down each
        ///        frame). AbilityId / MaxEnergy / PendingCast* stay UNFOLDED (authored / transient). The fold is
        ///        count-driven, so raising EntityWorld.MAX_ABILITIES_PER_UNIT later moves no golden. One scheduled re-baseline.
        ///   v8 — Story 2.6: fold per-entity EffectiveArmor — the buffable armor stat (Decision #6). The
        ///        ModifierSystem recomputes it mid-match (an aura grants +armor) and DamageResolver subtracts it,
        ///        so it is peer-divergent sim truth. BaseArmor stays UNFOLDED (authored, the BaseAttackDamage
        ///        posture). The passive DRIVERS (aura / on-hit / self-passive) add NO new folded state — they reuse
        ///        ModifierStore v6 / Health / Effective* and register passives via authored, not-folded SoA. One
        ///        scheduled re-baseline of all goldens (the existing combat is unchanged since BaseArmor=0 → −0).
        ///   v9 — Story 2.12: fold (a) the per-entity shift-queued order ring (OrderQueueCount + count-driven
        ///        OrderQueueCmd/TargetX/TargetZ) — runtime-mutable pending orders (append/pop mid-match); AND
        ///        (b) the per-building rally point (HasRallyPoint + RallyPoint X/Z) — now wire-driven (UnitCommand.SetRally)
        ///        and read in-tick by SpawnTrainedUnit, so genuinely mutable sim truth (D-1). Both count/flag-driven,
        ///        all int/Fixed.Raw → cross-platform. One scheduled re-baseline of ALL goldens (the known-state world
        ///        has an empty queue + no rally, so the pin moves purely by the added Mix(0) per entity/building).
        ///   v10 — Story 3.12: fold per-entity Delivery ((int) — Hitscan/Projectile) + ProjectileSpeed (.Raw) — the
        ///        authorable attack-delivery axis. Like AttackRange/SplashRadius/CategoryOf these are authored spawn-
        ///        constants whose effect also reaches the hash transitively (via Position/Health), but the story MANDATES
        ///        a direct fold: CombatSystem branches instant-vs-projectile on Delivery, and ProjectileSystem advances at
        ///        ProjectileSpeed, so a peer divergence in either changes combat resolution and must desync detectably.
        ///        Both int/Fixed.Raw → cross-platform safe. One scheduled re-baseline of ALL goldens (existing units keep
        ///        their exact behaviour — Delivery infers the old MELEE_THRESHOLD partition, ProjectileSpeed defaults to 18).
        ///   v11 — Story 3.13: the XP runtime first MUTATES HeroStore.Level/Xp mid-match, so fold (a) the per-entity
        ///        def-derived XpBounty (.Raw) in the entity loop (the Story 3.12 spawn-constant convention), AND (b) the
        ///        mutable HeroStore state — ascending HeroId (FoldOrder): live count, then per slot HeroId.Value (low/high),
        ///        Level, Xp.Raw, GrowthStacksApplied, plus Story 3.14's RESERVED revival fields (Alive3_14/AwaitingRevival
        ///        as int, RevivalTimer.Raw, RevivalLink) declared + folded now at their defaults so 3.14 needs no second
        ///        bump. Curve constants (MaxLevelOf/BaseXpOf/…) are NOT folded (authored/def-derived, the Delivery posture).
        ///        A null HeroStore folds Mix(0) count (dormant/legacy callers agree). All int/Fixed.Raw → cross-platform.
        ///        One scheduled re-baseline of ALL goldens (existing goldens have no heroes + XpBounty defaults to 0, so
        ///        the pin moves purely by the added Mix(0) hero-count + Mix(0) XpBounty per entity).
        ///   v12 — Story 3.15: the item/inventory sim first MUTATES mid-match, so fold (a) the mutable ItemStore — live
        ///        count, then per live slot (ascending slot) DefId, Charges, PosX.Raw, PosZ.Raw, Held (int), CarrierHeroSlot;
        ///        AND (b) the per-hero inventory — the INVENTORY_SLOTS packed ItemStore refs, in the same HeroStore
        ///        FoldOrder loop after the reserved revival fields. A null ItemStore folds a single Mix(0) count (dormant/
        ///        legacy callers agree). All int/Fixed.Raw → cross-platform. One scheduled re-baseline of ALL goldens
        ///        (existing goldens have no items + empty inventories, so the pin moves purely by the added Mix(0)
        ///        item-count + the INVENTORY_SLOTS Mix(-1) empty-slot refs per hero — of which existing goldens have none).
        ///   v13 — Story 4.7: <see cref="ResourceNodeStore"/> is folded into the checksum for the FIRST TIME (a
        ///        pre-existing desync-detection gap, not caused by this story — see the story's Design Notes) in the
        ///        SAME bump that adds the new mutable <c>IncomeTicksElapsed</c> counter (Income's periodic-credit
        ///        tick countdown), so a second immediate re-baseline is avoided. Fold: live count, then ascending
        ///        node id: SupplyRemaining.Raw, Active (int), AssignedGatherers, IncomeTicksElapsed — all int/
        ///        Fixed.Raw → cross-platform. A null store folds a single Mix(0) count (dormant/legacy callers agree
        ///        with an empty store). One scheduled re-baseline of ALL goldens (every existing golden has at least
        ///        one node, so the pin moves by the newly-folded per-node state even though GATHER behavior itself
        ///        is unchanged).
        ///   v14 — Story 4.10: <see cref="ResearchStore"/> is folded into the checksum for the FIRST TIME (4.9 made
        ///        it mid-match-mutable via the order start/tick/complete/cancel path but explicitly deferred the
        ///        fold to this immediately-following story — see 4.9's Design Notes). Fold, per ACTIVE faction
        ///        (ascending — the OUTER loop mirrors ResourceStore's <c>factions.ActiveFactions</c> iteration, NOT
        ///        the raw 0-4 <c>FACTION_COUNT</c> stride): InProgressIndex, RemainingTicks, then an INNER
        ///        count-driven loop bound by that faction's OWN <c>CompletedLevels[idx].Length</c> (a per-faction
        ///        research-entry count, never a fixed constant) over CompletedLevels[idx][r] plus the four
        ///        cumulative stat deltas (.Raw:
        ///        CumulativeMaxHealthDelta/CumulativeAttackDamageDelta/CumulativeMoveSpeedDelta/CumulativeArmorDelta)
        ///        per research index. The four cumulative deltas are folded DIRECTLY (not left to transitive
        ///        Effective* coverage) because they are genuinely mid-match-mutated sim truth that future-spawn
        ///        catch-up reads — the same posture as ModifierStore/EffectiveArmor/HeroStore.Xp. StartedAtPosition
        ///        stays UNFOLDED (write-once, read only to position the presentation-only ResearchComplete event —
        ///        the same posture as other completion-event positions). All int/Fixed.Raw → cross-platform. A null
        ///        store folds a single Mix(0) (legacy/test callers only; SimulationHost always passes a real store).
        ///        One scheduled re-baseline of ALL goldens (every existing golden's factions have no research, so
        ///        the pin moves purely by the added per-active-faction Mix(InProgressIndex)/Mix(RemainingTicks)).
        ///   v15 — Story 6.3: fold per-entity <c>Elevation</c> (.Raw) in the entity loop (after Health) — the new
        ///        terrain-elevation SoA array, sampled once at spawn from the authored heightmap. A dedicated array
        ///        (NOT Position.Y, which would move goldens as position + risk MovementSystem integration leaking it).
        ///        A peer divergence in a unit's elevation must desync detectably; it also feeds EffectiveVisionRange
        ///        when the height-advantage vision toggle is on (the toggle/bonus themselves are NOT folded — the fog
        ///        Grid is not in this checksum and no sim system consumes it, so a toggle mismatch cannot desync;
        ///        CanonicalModelHash/StartStateHash are deliberately untouched). Fixed.Raw → cross-platform. Every
        ///        existing golden scenario is FLAT (Elevation == 0), so this adds one Mix(0) per alive entity — the
        ///        AC-authorized intentional expansion that re-baselines ALL 23 per-tick goldens in this one commit.
        ///   v16 — Story 7.3: fold the top-level <see cref="DslVarTable"/> — live typed/scoped DSL variables + timers —
        ///        for the FIRST TIME (variables/timers were improvised inside ScenarioDirector and NEVER folded, a
        ///        silent-desync gap this story closes). After the ResearchStore fold, before the RNG fold, the table
        ///        folds: a leading Global-count then every live Global value (Fixed.Raw/int) in declaration/creation
        ///        index; then, per Per-player DECLARATION (declaration index ascending), EVERY player slot 0..7
        ///        ascending — NOT only the active factions, because SetInt/ClampSlot can write any slot and a written
        ///        slot must never escape the fold (the review-hardened form; ResourceStore's ActiveFactions loop is
        ///        deliberately NOT mirrored here); then a leading timer-count and every timer's remaining ticks in
        ///        creation index. TriggerLocal scratch is NEVER folded (per-firing, freed at trigger end). A null
        ///        store folds via DslVarTable.FoldEmpty — byte-identical to a non-null EMPTY table (a 0 global-count
        ///        mix + a 0 timer-count mix; legacy/test callers only, SimulationHost always passes a real store).
        ///        Every existing golden carries an EMPTY table (no declared vars/timers, empty triggers), so the fold
        ///        adds Mix(0)-count steps that move the hash even with zero live state — the epic-mandated,
        ///        behavior-neutral golden re-baseline (parity is proven by the migration/execution unit tests). All
        ///        int/Fixed.Raw → cross-platform safe. One scheduled re-baseline of ALL per-tick goldens.
        ///   v17 — Story 7.6: the bounded-loop / array / fuel layer folds. (a) <see cref="DslVarTable"/>'s FoldInto
        ///        gains an ARRAYS section between the per-player and timer sections (a leading array-declaration
        ///        count; per array its live element count then every live element raw, ascending element index) —
        ///        arrays are variables, so they fold inside the table. (b) A new <see cref="Dsl.DslLoopState"/>
        ///        store folds AFTER the DslVarTable and BEFORE the SimRng fold (SimRng stays last, the v16
        ///        precedent): a leading for_each_batched continuation-row count; per row (ascending node id) its
        ///        active flag / cursor / snapshot length / live snapshot entity ids; then the per-tick DSL fuel
        ///        consumed (the MaxDslOpsPerTick seatbelt's counter — reset each director tick, so a divergent
        ///        trigger workload desyncs detectably). A null DslLoopState folds byte-identically to an empty one
        ///        (FoldEmpty null≡empty; legacy/test callers only — SimulationHost always passes the real store).
        ///        Loop/array-free scenarios add only leading count/fuel Mix(0) steps — behavior-neutral, covered by
        ///        this story's ONE scheduled re-baseline of ALL goldens. All int → cross-platform safe.
        ///   v18 — Story 7.5 (landed via merge): fold the new <see cref="DslEventQueue"/> — the PENDING next-tick
        ///        custom-event raises — for the FIRST TIME. Unlike CombatEventQueue/DeathFeed (provably drained
        ///        within the tick) the queue is live CROSS-TICK sim state: it is non-empty at the checksum boundary
        ///        whenever a raise_event with next_tick=true is awaiting its dequeue, so a peer whose pending
        ///        feedback diverges must desync detectably. Folds AFTER the DslLoopState fold and BEFORE the SimRng
        ///        fold (SimRng stays last, the standing precedent): a leading count, then per entry in enqueue order
        ///        the registry event index, the raiser slot, and the fixed EventBounds.MaxEventParams param-raw
        ///        stride. A null queue folds via DslEventQueue.FoldEmpty — byte-identical to a non-null EMPTY queue
        ///        (a single Mix(0) count; legacy/test callers only, SimulationHost always passes a real queue).
        ///        Every existing golden carries an EMPTY queue, so the fold adds one Mix(0) that moves the hash with
        ///        zero live state — the merge's ONE named, recorded, behavior-neutral re-baseline of ALL per-tick
        ///        goldens. All ints → cross-platform safe.
        ///   v19 — Story 7.11: fold the new <see cref="WinStateStore"/> — the sim-layer win-condition runtime state —
        ///        for the FIRST TIME (win evaluation moved out of presentation into the deterministic
        ///        <c>WinConditionSystem</c>, so its live state must be peer-checksummed). Folds AFTER the
        ///        DslEventQueue fold and BEFORE the SimRng fold (SimRng stays last, the standing precedent): the
        ///        scalar <c>MatchTicks</c> grace/elapsed counter first, then per ACTIVE faction (ascending — the
        ///        ResourceStore/ResearchStore <c>ActiveFactions</c> iteration, NOT a raw 0-4 stride) KothHoldTicks,
        ///        SurvivalRemaining, and Verdict. A divergent hold-counter / survival deadline / latched verdict
        ///        between peers changes which match outcome resolves, so it must desync detectably. A null store
        ///        folds byte-identically to an empty one (a single Mix(0) MatchTicks then the per-active-faction
        ///        Mix(0) triples; legacy/test callers only — SimulationHost always passes a real store). Every
        ///        existing golden carries a fresh store whose MatchTicks advances deterministically and whose
        ///        per-faction fields stay 0 for the replay window, so the fold moves the hash — the story's ONE
        ///        scheduled, behavior-neutral re-baseline of ALL per-tick goldens. All ints → cross-platform safe.
        ///   v20 — Story 7.12: fold the new <see cref="AllianceStore"/> — the sim-owned per-faction team-id mask that
        ///        generalizes win resolution to N-faction, team-aware last-team-standing — for the FIRST TIME. Folds
        ///        immediately AFTER the WinStateStore block and BEFORE the RNG fold (SimRng stays last, the standing
        ///        precedent): one team-id Mix per ACTIVE faction (ascending — the ResourceStore/WinStateStore
        ///        ActiveFactions iteration, NEVER a raw 0-4 stride). A peer with a different team assignment resolves
        ///        victory differently (last-team-standing depends on who is allied with whom), so the mask must desync
        ///        detectably even though it is static within a match. A null store folds byte-identically to a
        ///        default-FFA store (Mix((int)f) per active faction — FFA is team id == slot index). Every existing
        ///        golden is FFA (the ONLY populated state in 1.0 — lobby team wiring is Story 9.15), so the fold adds
        ///        one Mix((int)f) per active faction and moves the hash even with the default mask — the story's ONE
        ///        scheduled, behavior-neutral re-baseline of ALL per-tick goldens. All ints → cross-platform safe.
        ///   v21 — Story 7.13: fold the new <see cref="TriggerEnabledStore"/> — the per-exec trigger-enabled runtime
        ///        mask (enable_trigger/disable_trigger flip a target trigger's enabled flag mid-match) — for the FIRST
        ///        TIME. Folds immediately AFTER the AllianceStore block and BEFORE the RNG fold (SimRng stays last, the
        ///        standing precedent): one enabled bit Mix per exec entry, in EXEC ORDER, with NO count prefix (the exec
        ///        count is static model state already covered by CanonicalModelHash). A divergent enabled set between
        ///        peers evaluates different triggers and must desync detectably. UNLIKE every other store here, a NULL
        ///        store folds NOTHING (zero Mix calls — a true no-op absent path, NOT the null≡empty→Mix(0) convention),
        ///        and a Count==0 store (a trigger-less scenario, or a ClearForReset-emptied store) likewise folds
        ///        nothing — so a scenario carrying ZERO DSL triggers hashes BYTE-IDENTICALLY to its pre-story (v20)
        ///        sequence (the re-baseline differential guard's clean control). Only scenarios that DECLARE triggers
        ///        move by their enabled bits. All ints → cross-platform safe. One scheduled re-baseline of the
        ///        trigger-carrying world goldens.
        ///   v22 — Story 11.6: the depth-1 <see cref="BuildingStore.ProductionQueue"/> byte (left UNFOLDED-while-dormant
        ///        by Story 2.8) widens to a real depth-5 mutable per-building queue that ALSO feeds <see cref="ResourceStore"/>
        ///        via cancel/refund, so it crosses the "mutable sim truth" line (the checksum-fold-timing rule) and is
        ///        folded into the building loop for the FIRST TIME: all <see cref="BuildingStore.QUEUE_DEPTH"/> queue
        ///        slots (row-major, ascending slot) PLUS the head <see cref="BuildingStore.ProductionTimer"/> (.Raw) per
        ///        building — the timer was likewise never folded before (production divergence surfaced only transitively
        ///        via spawned-unit world state). All byte/Fixed.Raw → cross-platform safe. Every existing golden with a
        ///        producer moves by its real queue/timer state; a producer-less scenario moves purely by the added
        ///        QUEUE_DEPTH×Mix(0) + Mix(0)-timer per building — the story's ONE scheduled re-baseline of ALL per-tick
        ///        goldens.
        ///   v23 — DW-78 (Phase B re-baseline batch): fold the per-worker gather state machine —
        ///        <c>GatherState</c>, <c>GatherTarget</c>, <c>CarryAmount</c> (.Raw), <c>CarryResourceType</c>, and
        ///        DW-80's <c>GateClosedTicks</c> — for the FIRST TIME. GatheringSystem mutates all five mid-match,
        ///        so a divergent carried load or gather phase is peer-divergent sim truth; today it only surfaces
        ///        transitively via Ore/Crystal at deposit, many ticks after the divergence.
        ///        BOUNDED, unlike every other per-entity block here: an entity at the full gatherer-inactive
        ///        default (Inactive / -1 / 0 / 0) folds ZERO Mix calls — the v21 TriggerEnabledStore no-op-absent
        ///        posture, NOT the null≡empty→Mix(0) convention. An UNCONDITIONAL fold would move EVERY scenario
        ///        (GatherTarget defaults to -1; Mix(-1) is not a no-op) and would leave the re-baseline
        ///        differential guard with no unperturbed scenario to use as a control; bounded, only scenarios
        ///        that actually gather move. All byte/int/Fixed.Raw → cross-platform safe.
        /// </summary>
        public const int AlgoVersion = 23;

        /// <summary>
        /// Compute a full-state checksum for desync detection.
        /// Call after all systems have ticked for the current frame.
        /// </summary>
        public static uint Compute(EntityWorld world, BuildingStore buildings, ResourceStore resources,
                                   FactionRegistry factions, ModifierStore? modifiers = null, HeroStore? heroes = null,
                                   ItemStore? items = null, ResourceNodeStore? nodes = null, ResearchStore? research = null,
                                   DslVarTable? vars = null, DslLoopState? loopState = null,
                                   DslEventQueue? dslEvents = null, WinStateStore? winState = null,
                                   AllianceStore? alliances = null, TriggerEnabledStore? triggerEnabled = null)
        {
            // Contract guard for the registry param added in Story 1.3a: a future direct caller (e.g. the
            // 1.9a/9.1 server checksum collector) gets a clear error instead of an opaque NRE in the Ore loop.
            System.ArgumentNullException.ThrowIfNull(factions);

            uint hash = FNV_OFFSET;

            // ── Entity positions and health ───────────────────────────────────────
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) continue;

                hash = Mix(hash, world.Position[i].X.Raw);
                hash = Mix(hash, world.Position[i].Y.Raw);
                hash = Mix(hash, world.Position[i].Z.Raw);
                hash = Mix(hash, world.Health[i].Raw);

                // ── Terrain elevation (v15, Story 6.3) ────────────────────────────
                // Per-entity terrain elevation, sampled once at spawn from the authored heightmap (a dedicated SoA
                // array, NOT Position.Y). A divergent elevation between peers must desync detectably (and it also feeds
                // EffectiveVisionRange when the height-advantage toggle is on). Fixed.Raw → cross-platform safe. Every
                // current golden scenario is flat (Elevation == 0), so this adds one Mix(0) per entity — the AC-authorized
                // intentional re-baseline of ALL per-tick goldens.
                hash = Mix(hash, world.Elevation[i].Raw);

                // ── Command state (v4, Story 1.12) ────────────────────────────────
                // The full RTS command vocabulary's persistent per-entity state IS sim truth: a peer divergence
                // in a forced/follow target or a patrol route must desync detectably. Count-driven + ascending,
                // all int / Fixed.Raw → cross-platform safe: the Story 1.12 golden IS compared on both CI legs
                // (NOT Windows-gated, unlike the float-scoring AI golden).
                hash = Mix(hash, world.CommandTarget[i]);
                hash = Mix(hash, world.PatrolCount[i]);
                hash = Mix(hash, world.PatrolIndex[i]);
                hash = Mix(hash, world.PatrolDir[i]);
                int wpBase  = i * EntityWorld.MAX_PATROL_WAYPOINTS;
                int wpCount = world.PatrolCount[i];
                // Defensive (Review, Story 1.12): never read past the per-entity ring. OrderApplier caps the
                // count at MAX_PATROL_WAYPOINTS today, so this can't fire — but a future writer that sets a
                // larger count must not turn a logic slip into an OOB read inside per-tick desync detection.
                if (wpCount > EntityWorld.MAX_PATROL_WAYPOINTS) wpCount = EntityWorld.MAX_PATROL_WAYPOINTS;
                for (int k = 0; k < wpCount; k++)
                {
                    hash = Mix(hash, world.PatrolWaypoints[wpBase + k].X.Raw);
                    hash = Mix(hash, world.PatrolWaypoints[wpBase + k].Y.Raw);
                    hash = Mix(hash, world.PatrolWaypoints[wpBase + k].Z.Raw);
                }

                // ── Separation / formation config (v5, Story 1.13) ────────────────
                // CollisionRadius + SeparationPriorityOf are read in-sim by MovementSystem every tick on every
                // peer, so a content divergence in either must desync detectably. CategoryOf is NOT folded: it is
                // presentation-read (formation planning, like MeshType) and its effect reaches the hash only
                // transitively via Position, so a divergent local CategoryOf cannot desync. Both are int/Fixed.Raw
                // → cross-platform safe (the new formation-separation golden is compared on BOTH CI legs).
                hash = Mix(hash, world.CollisionRadius[i].Raw);
                hash = Mix(hash, (int)world.SeparationPriorityOf[i]);

                // ── Attack delivery (v10, Story 3.12) ─────────────────────────────
                // Delivery (Hitscan/Projectile) branches CombatSystem's instant-vs-projectile resolution, and
                // ProjectileSpeed drives the projectile advance step — both read in-sim, so a peer divergence in
                // either changes combat outcomes and must desync detectably. Authored spawn-constants folded by story
                // mandate (like AttackRange transitively via Position). int / Fixed.Raw → cross-platform safe.
                hash = Mix(hash, (int)world.Delivery[i]);
                hash = Mix(hash, world.ProjectileSpeed[i].Raw);

                // ── XP bounty (v11, Story 3.13) ───────────────────────────────────
                // The def-derived per-unit XP bounty this unit awards on death — read by HeroXpSystem when it drains the
                // DeathFeed, so a peer divergence changes hero XP/level outcomes. Folded directly (the Story 3.12
                // spawn-constant-folding convention: authored, but folded for a uniform re-baseline + coverage teeth).
                // Fixed.Raw → cross-platform safe.
                hash = Mix(hash, world.XpBounty[i].Raw);

                // ── Effective stats + ability resource + status (v6, Story 2.2b) ──
                // The ModifierStore now MUTATES these mid-match (a modifier changes Effective*; an ability debits
                // Energy; a status modifier sets StatusFlagsOf), so they are peer-divergent sim truth and must fold.
                // Base* is deliberately NOT folded (authored, in-tick-immutable — exactly as the pre-2.2a
                // AttackDamage/MaxHealth/Speed were never folded). All int / Fixed.Raw → cross-platform safe.
                hash = Mix(hash, world.EffectiveAttackDamage[i].Raw);
                hash = Mix(hash, world.EffectiveMaxHealth[i].Raw);
                hash = Mix(hash, world.EffectiveMoveSpeed[i].Raw);
                // EffectiveArmor (v8, Story 2.6): the buffable armor stat. ModifierSystem recomputes it mid-match
                // (an aura grants +armor via Modifier.ArmorDelta) and DamageResolver subtracts it, so it is
                // peer-divergent sim truth and must fold. BaseArmor stays UNFOLDED (authored, in-tick-immutable —
                // the BaseAttackDamage posture). Fixed.Raw → cross-platform safe (the passive goldens are compared
                // on both CI legs). Existing content has BaseArmor=0 → EffectiveArmor=0 → a single Mix(0) per
                // entity moves the hash from v7 even though no real armor exists yet (the scheduled re-baseline).
                hash = Mix(hash, world.EffectiveArmor[i].Raw);
                hash = Mix(hash, world.Energy[i].Raw);
                hash = Mix(hash, (int)world.StatusFlagsOf[i]);

                // ── Active modifier instances (v6, Story 2.2b) — count-driven, ascending slot ──
                // A null store folds Mix(0) count (≡ an empty store), so a legacy 4-arg caller and a real empty store
                // hash identically. The descriptor refs + caster id/faction are NOT folded (authored/peer-identical).
                int modCount = modifiers?.CountAt(i) ?? 0;
                hash = Mix(hash, modCount);
                for (int s = 0; s < modCount; s++)
                {
                    hash = Mix(hash, modifiers!.ModifierIdAt(i, s));
                    hash = Mix(hash, modifiers!.RemainingTicksAt(i, s));
                    hash = Mix(hash, modifiers!.TicksUntilPeriodAt(i, s));
                    hash = Mix(hash, modifiers!.PeriodsRemainingAt(i, s));
                    hash = Mix(hash, modifiers!.StackCountAt(i, s));
                }

                // ── Ability cooldowns (v7, Story 2.4a) — count-driven, ascending slot ──
                // AbilityCooldownTicks is the first per-entity ability array that MUTATES mid-match (a cast starts
                // it; AbilityCastSystem ticks it down each frame), so it is peer-divergent sim truth and must fold.
                // Count-driven by AbilityCount (the cross-platform-safe loop bound, like PatrolCount) — only the
                // populated slots are hashed, never the stride or empty slots, so raising MAX_ABILITIES_PER_UNIT
                // later moves no golden. AbilityId / MaxEnergy are authored/peer-identical (NOT folded, like MeshType);
                // PendingCast* are transient (cleared by AbilityCastSystem before this checksum). All int → x-platform.
                int abCount = world.AbilityCount[i];
                if (abCount > EntityWorld.MAX_ABILITIES_PER_UNIT) abCount = EntityWorld.MAX_ABILITIES_PER_UNIT; // defensive
                hash = Mix(hash, abCount);
                int abBase = i * EntityWorld.MAX_ABILITIES_PER_UNIT;
                for (int s = 0; s < abCount; s++)
                    hash = Mix(hash, world.AbilityCooldownTicks[abBase + s]);

                // ── Shift-queued order ring (v9, Story 2.12) — count-driven, ascending slot ──
                // The per-entity order queue MUTATES mid-match (OrderApplier appends a Shift order; OrderQueueSystem
                // pops the head on completion), so it is peer-divergent sim truth and must fold. Count-driven by
                // OrderQueueCount (the cross-platform-safe loop bound, like PatrolCount) — only the populated slots are
                // hashed, never the stride or empty slots, so raising MAX_ORDER_QUEUE later moves no golden. The command
                // byte is the masked 0-13 value (the wire's 0x80 queued flag never reaches here); targets are Fixed.Raw /
                // packed ints. All int → cross-platform safe (the shift-queue golden is compared on both CI legs).
                int oqCount = world.OrderQueueCount[i];
                if (oqCount > EntityWorld.MAX_ORDER_QUEUE) oqCount = EntityWorld.MAX_ORDER_QUEUE; // defensive
                hash = Mix(hash, oqCount);
                int oqBase = i * EntityWorld.MAX_ORDER_QUEUE;
                for (int s = 0; s < oqCount; s++)
                {
                    hash = Mix(hash, world.OrderQueueCmd[oqBase + s]);
                    hash = Mix(hash, world.OrderQueueTargetX[oqBase + s]);
                    hash = Mix(hash, world.OrderQueueTargetZ[oqBase + s]);
                }

                // ── Worker gather state (v23, DW-78) — BOUNDED fold, the v21 TriggerEnabledStore posture ──
                // GatheringSystem's per-worker state machine mutates GatherState/GatherTarget/CarryAmount/
                // CarryResourceType mid-match (and GateClosedTicks drives the DW-80 re-idle at the grace
                // threshold), so a peer whose worker carries a different load or sits in a different gather
                // phase must desync detectably — today it can diverge silently until the divergence surfaces
                // transitively via Ore/Crystal on deposit, many ticks later.
                //
                // BOUNDED, deliberately: an entity at the full gatherer-inactive default folds ZERO Mix calls
                // (NOT the null≡empty→Mix(0) convention the stores use). Every non-worker unit — and every
                // worker between trips — is at that default, so a scenario with no active gather state hashes
                // BYTE-IDENTICALLY to its pre-fold (v22) sequence. This is the v21 TriggerEnabledStore
                // precedent, and it is what keeps a clean no-perturbation control arm CONSTRUCTIBLE for the
                // re-baseline differential guard: an unconditional fold would move every scenario (GatherTarget
                // defaults to -1 and Mix(-1) is not a no-op), leaving the guard no unperturbed scenario to point at.
                //
                // GateClosedTicks folds in the SAME bump (checksum-fold-timing rule): DW-80 made it genuinely
                // mutable mid-match sim truth that changes behaviour, and it is only ever non-zero while
                // Gathering, so it costs no additional blast radius and avoids a second re-baseline later.
                // All byte/int/Fixed.Raw → cross-platform safe.
                GatherState gState = world.GatherState[i];
                int gTarget = world.GatherTarget[i];
                if (gState != GatherState.Inactive || gTarget >= 0 || world.CarryAmount[i].Raw != 0
                    || world.GateClosedTicks[i] != 0)
                {
                    hash = Mix(hash, (int)gState);
                    hash = Mix(hash, gTarget);
                    hash = Mix(hash, world.CarryAmount[i].Raw);
                    hash = Mix(hash, (int)world.CarryResourceType[i]);
                    hash = Mix(hash, world.GateClosedTicks[i]);
                }
            }

            // ── Building state ────────────────────────────────────────────────────
            int bCount = buildings.Count;
            for (int i = 0; i < bCount; i++)
            {
                hash = Mix(hash, buildings.Alive[i] ? 1 : 0);
                hash = Mix(hash, buildings.Health[i].Raw);
                hash = Mix(hash, buildings.ConstructionTimer[i].Raw);
                // ── Rally point (v9, Story 2.12, D-1) — the one in-tick-read BuildingStore field that was NOT folded.
                // Once rally is wire-driven (UnitCommand.SetRally) it becomes genuinely mutable-mid-match sim truth:
                // SpawnTrainedUnit reads it to send a trained unit Move→rally, so a peer divergence must desync
                // detectably (and directly, not only once a unit spawns and its Position drifts). RallyPoint is reset
                // to Zero in BuildingStore.Create, so an unset rally folds a stable Zero. All Fixed.Raw/int → x-platform.
                hash = Mix(hash, buildings.HasRallyPoint[i] ? 1 : 0);
                hash = Mix(hash, buildings.RallyPoint[i].X.Raw);
                hash = Mix(hash, buildings.RallyPoint[i].Z.Raw);

                // ── Production queue (v22, Story 11.6) — the depth-5 queue + head timer, folded for the FIRST TIME.
                // Widening the 2.8 depth-1 byte into a real mutable-mid-match queue that ALSO feeds ResourceStore via
                // cancel/refund crosses the "mutable sim truth" line (the checksum-fold-timing rule). Fold all
                // QUEUE_DEPTH slots row-major (ascending slot) so a peer divergence in ANY slot — head or waiting —
                // desyncs detectably, PLUS the head ProductionTimer (.Raw), which drives when the head completes and
                // was itself never folded. All byte/Fixed.Raw → cross-platform safe.
                int qBase = i * BuildingStore.QUEUE_DEPTH;
                for (int k = 0; k < BuildingStore.QUEUE_DEPTH; k++)
                    hash = Mix(hash, buildings.ProductionQueue[qBase + k]);
                hash = Mix(hash, buildings.ProductionTimer[i].Raw);
            }

            // ── Faction resources (all per-faction stores, active factions, ascending slot order) ──
            // Story 1.3b widened this from Ore-only to full coverage; checksum_algo_version bumped to 2.
            // Every public per-faction ResourceStore array is folded in here (proven by
            // SimChecksumCoverageGuardTest). MatchStats stays OUT by design (private observational scoreboard).
            // FactionBase is read in-tick by GatheringSystem (workers path to it to deposit), so a peer
            // divergence there would desync — it belongs in the hash even though it is constant within a match.
            foreach (Faction f in factions.ActiveFactions)
            {
                int idx = (int)f;
                hash = Mix(hash, resources.Ore[idx].Raw);
                hash = Mix(hash, resources.Crystal[idx].Raw);
                hash = Mix(hash, resources.SupplyUsed[idx]);        // int[] — pass directly, no .Raw
                hash = Mix(hash, resources.SupplyCap[idx]);         // int[]
                hash = Mix(hash, resources.FactionBase[idx].X.Raw); // FixedVec3 → three Fixed.Raw mixes
                hash = Mix(hash, resources.FactionBase[idx].Y.Raw);
                hash = Mix(hash, resources.FactionBase[idx].Z.Raw);
            }

            // ── HeroStore mutable state (v11, Story 3.13) — ascending HeroId (FoldOrder), count-driven ──
            // The Story 3.13 XP runtime mutates HeroStore.Level/Xp mid-match (the store was DORMANT since 3.2), so it is
            // now folded per-tick. Fold the live count, then per live slot IN FoldOrder ORDER (ascending HeroId — producer-
            // independent): HeroId.Value (low/high 32 bits, the SimRng pattern), Level, Xp.Raw, GrowthStacksApplied, and
            // the reserved Story 3.14 revival fields (declared + folded now at their defaults). Curve constants are NOT
            // folded (authored/def-derived spawn constants — the Delivery/AttackDamage posture). A null store folds a
            // single Mix(0) count (≡ an empty/dormant store), so a legacy 5-arg caller and an empty store agree.
            if (heroes != null)
            {
                int[] hOrder = heroes.FoldOrder();
                hash = Mix(hash, hOrder.Length);
                for (int k = 0; k < hOrder.Length; k++)
                {
                    int slot = hOrder[k];
                    ulong hid = heroes.Id[slot].Value;
                    hash = Mix(hash, (int)(hid & 0xFFFFFFFFUL)); // low 32 bits
                    hash = Mix(hash, (int)(hid >> 32));          // high 32 bits
                    hash = Mix(hash, heroes.Level[slot]);
                    hash = Mix(hash, heroes.Xp[slot].Raw);
                    hash = Mix(hash, heroes.GrowthStacksApplied[slot]);
                    // Story 3.14 reserved fields — folded at their Mint defaults (Alive3_14 == true) so 3.14 needs no bump.
                    hash = Mix(hash, heroes.Alive3_14[slot] ? 1 : 0);
                    hash = Mix(hash, heroes.AwaitingRevival[slot] ? 1 : 0);
                    hash = Mix(hash, heroes.RevivalTimer[slot].Raw);
                    hash = Mix(hash, heroes.RevivalLink[slot]);
                    // ── Per-hero inventory (v12, Story 3.15) — the INVENTORY_SLOTS packed ItemStore refs on this row.
                    //    Fixed-stride (not count-driven): empty slots fold their -1 sentinel, so a pickup/drop that changes
                    //    a ref moves the hash. Ascending slot within the (already ascending-HeroId) fold order. ──
                    int invBase = slot * HeroStore.INVENTORY_SLOTS;
                    for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                        hash = Mix(hash, heroes.Inventory[invBase + s]);
                }
            }
            else
            {
                hash = Mix(hash, 0); // null store ≡ empty (dormant): fold an identical count-0 mix
            }

            // ── ItemStore mutable state (v12, Story 3.15) — live count, then per live slot (ascending slot) ──
            // The item/inventory sim mutates the ItemStore mid-match (placement, pickup ground→held, use/charge, drop),
            // so it is peer-divergent sim truth and must fold. Count-driven over 0..Count (a recycled slot is < Count and
            // skipped by Alive), all int / Fixed.Raw → cross-platform. A null store folds a single Mix(0) count (dormant/
            // legacy callers agree with an empty store).
            if (items != null)
            {
                int liveItems = 0;
                for (int i = 0; i < items.Count; i++)
                    if (items.Alive[i]) liveItems++;
                hash = Mix(hash, liveItems);
                for (int i = 0; i < items.Count; i++)
                {
                    if (!items.Alive[i]) continue;
                    hash = Mix(hash, items.DefId[i]);
                    hash = Mix(hash, items.Charges[i]);
                    hash = Mix(hash, items.PosX[i].Raw);
                    hash = Mix(hash, items.PosZ[i].Raw);
                    hash = Mix(hash, items.Held[i] ? 1 : 0);
                    hash = Mix(hash, items.CarrierHeroSlot[i]);
                }
            }
            else
            {
                hash = Mix(hash, 0); // null store ≡ empty: fold an identical count-0 mix
            }

            // ── ResourceNodeStore mutable state (v13, Story 4.7) — live count, then ascending node id ──
            // The FIRST-EVER fold of this store (a pre-existing desync-detection gap, not caused by this story —
            // see the AlgoVersion doc). Nodes are append-only (no recycling), so 0..Count IS every node, in stable
            // ascending-id order. Folds the pre-existing static-ish fields (SupplyRemaining/Active/AssignedGatherers)
            // alongside the new mutable IncomeTicksElapsed (Income's periodic-credit countdown) in the SAME bump.
            // All int/Fixed.Raw → cross-platform. A null store folds a single Mix(0) count (dormant/legacy callers
            // agree with an empty store).
            if (nodes != null)
            {
                hash = Mix(hash, nodes.Count);
                for (int n = 0; n < nodes.Count; n++)
                {
                    hash = Mix(hash, nodes.SupplyRemaining[n].Raw);
                    hash = Mix(hash, nodes.Active[n] ? 1 : 0);
                    hash = Mix(hash, nodes.AssignedGatherers[n]);
                    hash = Mix(hash, nodes.IncomeTicksElapsed[n]);
                }
            }
            else
            {
                hash = Mix(hash, 0); // null store ≡ empty: fold an identical count-0 mix
            }

            // ── ResearchStore mutable state (v14, Story 4.10) — OUTER loop per ACTIVE faction (ascending, mirrors
            // the ResourceStore.ActiveFactions loop above — NOT a raw 0-4 FACTION_COUNT stride), then an INNER
            // count-driven loop over that faction's OWN per-research state — the FIRST-EVER fold of this store
            // (4.9 built it mid-match-mutable but explicitly deferred the fold to this story). InProgressIndex/
            // RemainingTicks are the in-progress order countdown; CompletedLevels + the four cumulative deltas are
            // folded per research index, bound by that faction's OWN CompletedLevels[idx].Length (a per-faction
            // research-entry count, never a fixed constant — mirrors the AbilityCount/ResourceNodeStore count-driven
            // convention). The four cumulative deltas are genuinely
            // mid-match-mutated sim truth read directly by future-spawn catch-up, so they fold alongside
            // CompletedLevels rather than relying on transitive Effective* coverage (the ModifierStore/
            // EffectiveArmor/HeroStore.Xp posture). All int/Fixed.Raw → cross-platform. A null store folds a
            // single Mix(0) (legacy/test callers only; SimulationHost always passes a real store in production).
            if (research != null)
            {
                foreach (Faction f in factions.ActiveFactions)
                {
                    int idx = (int)f;
                    hash = Mix(hash, research.InProgressIndex[idx]);
                    hash = Mix(hash, research.RemainingTicks[idx]);
                    int researchCount = research.CompletedLevels[idx].Length;
                    for (int r = 0; r < researchCount; r++)
                    {
                        hash = Mix(hash, research.CompletedLevels[idx][r]);
                        hash = Mix(hash, research.CumulativeMaxHealthDelta[idx][r].Raw);
                        hash = Mix(hash, research.CumulativeAttackDamageDelta[idx][r].Raw);
                        hash = Mix(hash, research.CumulativeMoveSpeedDelta[idx][r].Raw);
                        hash = Mix(hash, research.CumulativeArmorDelta[idx][r].Raw);
                    }
                }
            }
            else
            {
                hash = Mix(hash, 0); // null store ≡ single Mix(0) (legacy/test callers only)
            }

            // ── DslVarTable mutable state (v16, Story 7.3) — live typed/scoped variables + timers ──
            // The FIRST-EVER fold of the DSL variable/timer store (variables/timers were improvised inside
            // ScenarioDirector and never folded — a silent-desync gap this story closes). The table folds Global then
            // Per-player variable values (EVERY player slot 0..7, ascending — because a write can target any slot, not
            // only active factions, so no written slot may escape the checksum) then timer remaining-ticks, each in
            // ascending declaration/creation index; TriggerLocal scratch is never folded. A NULL store folds
            // BYTE-IDENTICALLY to an EMPTY table (DslVarTable.FoldEmpty — a 0-global-count then a 0-timer-count), so
            // null and empty are interchangeable (legacy/test callers only; SimulationHost always passes a real
            // store). All int/Fixed.Raw → cross-platform safe.
            if (vars != null)
                vars.FoldInto(ref hash, Mix);
            else
                DslVarTable.FoldEmpty(ref hash, Mix); // null store ≡ empty table (byte-identical fold)

            // ── DslLoopState (v17, Story 7.6) — the loop layer's runtime state, AFTER the variable table and
            // BEFORE the RNG fold (SimRng stays last): the for_each_batched continuation rows (active/cursor/
            // length + live snapshot ids, ascending node-id row order) and the per-tick DSL fuel consumed. Both
            // are peer-divergent sim truth: a divergent drip cursor or fuel exhaustion point changes which
            // triggers execute. A NULL store folds byte-identically to an EMPTY one (FoldEmpty null≡empty;
            // legacy/test callers only — SimulationHost always passes the real store). All int → cross-platform.
            if (loopState != null)
                loopState.FoldInto(ref hash, Mix);
            else
                DslLoopState.FoldEmpty(ref hash, Mix); // null store ≡ empty state (byte-identical fold)

            // ── DslEventQueue pending next-tick events (v18, Story 7.5) — after the loop-state fold, before SimRng ──
            // The FIRST-EVER fold of the cross-tick custom-event queue: a raise_event with next_tick=true is LIVE
            // sim state at the checksum boundary (unlike CombatEventQueue/DeathFeed, which are provably drained
            // within the tick), so a divergent pending set between peers must desync detectably. Count-prefixed in
            // enqueue order: per entry the registry event index, raiser slot, and the fixed MaxEventParams param-raw
            // stride. A null queue folds BYTE-IDENTICALLY to an empty one (a single Mix(0) count — legacy/test
            // callers only; SimulationHost always passes a real queue). All ints → cross-platform safe.
            if (dslEvents != null)
                dslEvents.FoldInto(ref hash, Mix);
            else
                DslEventQueue.FoldEmpty(ref hash, Mix); // null queue ≡ empty queue (byte-identical fold)

            // ── WinStateStore (v19, Story 7.11) — the sim-layer win-condition runtime state, AFTER the DslEventQueue
            // fold and BEFORE the RNG fold (SimRng stays last, the standing precedent). The scalar MatchTicks
            // grace/elapsed counter folds first (once), then per ACTIVE faction (ascending — mirroring the
            // ResourceStore/ResearchStore ActiveFactions loop, NOT a raw 0-4 stride) KothHoldTicks / SurvivalRemaining
            // / Verdict. Each is peer-divergent live win-state: a divergent hold-counter, survival countdown, or
            // latched verdict changes which outcome resolves and must desync detectably. A null store folds
            // byte-identically to an empty one (Mix(0) MatchTicks then the per-active-faction Mix(0) triples). All int
            // → cross-platform safe.
            if (winState != null)
            {
                hash = Mix(hash, winState.MatchTicks);
                foreach (Faction f in factions.ActiveFactions)
                {
                    int idx = (int)f;
                    hash = Mix(hash, winState.KothHoldTicks[idx]);
                    hash = Mix(hash, winState.SurvivalRemaining[idx]);
                    hash = Mix(hash, winState.Verdict[idx]);
                }
            }
            else
            {
                hash = Mix(hash, 0); // null store ≡ empty: MatchTicks == 0
                foreach (Faction f in factions.ActiveFactions)
                {
                    hash = Mix(hash, 0);
                    hash = Mix(hash, 0);
                    hash = Mix(hash, 0);
                }
            }

            // ── AllianceStore team-id mask (v20, Story 7.12) — the sim-owned per-faction team assignment, folded
            // immediately AFTER the WinStateStore block and BEFORE the RNG fold (SimRng stays last, the standing
            // precedent). One team-id Mix per ACTIVE faction (ascending — the ResourceStore/WinStateStore
            // ActiveFactions iteration, NOT a raw 0-4 stride). The mask is static within a match but a peer with a
            // DIFFERENT team assignment resolves last-team-standing victory differently, so a divergent mask must
            // desync detectably. A null store folds byte-identically to a default-FFA store: FFA is team id == slot
            // index, so a null store folds Mix((int)f) per active faction — exactly what a default AllianceStore
            // yields. All int → cross-platform safe.
            if (alliances != null)
            {
                foreach (Faction f in factions.ActiveFactions)
                    hash = Mix(hash, alliances.TeamOf(f));
            }
            else
            {
                foreach (Faction f in factions.ActiveFactions)
                    hash = Mix(hash, (int)f); // null store ≡ default FFA (team id == slot index)
            }

            // ── TriggerEnabledStore per-exec enabled mask (v21, Story 7.13) — the trigger-enabled runtime state,
            // folded immediately AFTER the AllianceStore block and BEFORE the RNG fold (SimRng stays last, the
            // standing precedent). One enabled bit Mix per exec entry, in EXEC ORDER, with NO count prefix (the exec
            // count is static model state already covered by CanonicalModelHash). enable_trigger/disable_trigger flip
            // a target trigger's runtime enabled flag mid-match, so a peer whose enabled set diverges evaluates
            // different triggers and must desync detectably. NULL = SKIP ENTIRELY (zero Mix calls — NOT the
            // null≡empty→Mix(0) convention the other stores use): this store needs a true no-op absent path so the
            // re-baseline differential guard has a clean control, and a Count==0 store (a trigger-less scenario, or a
            // ClearForReset-emptied store) likewise folds nothing — so a scenario carrying zero DSL triggers hashes
            // byte-identically to its pre-story (v20) sequence. All ints → cross-platform safe.
            if (triggerEnabled != null)
            {
                for (int i = 0; i < triggerEnabled.Count; i++)
                    hash = Mix(hash, triggerEnabled.IsEnabled(i) ? 1 : 0);
            }

            // ── RNG state (v3, Story 1.5) ─────────────────────────────────────────
            // The single shared SimRng's state IS sim truth: once Epic 2 effects draw from it, a divergent
            // draw stream between peers must desync detectably. Folded as two int mixes (low/high 32 bits)
            // via the existing Mix primitive. State is constant (== seed) until something draws.
            ulong rng = world.Rng.State;
            hash = Mix(hash, (int)(rng & 0xFFFFFFFFUL)); // low 32 bits
            hash = Mix(hash, (int)(rng >> 32));          // high 32 bits

            return hash;
        }

        /// <summary>
        /// FNV-1a mix: feed a single int (4 bytes, little-endian) into the hash. Internal (not private) so the
        /// Story 7.13 re-baseline differential-guard's fold-position test can HAND-REPLICATE the fold tail
        /// (alliance → enabled → rng) to pin the enabled fold's exact position and count.
        /// </summary>
        internal static uint Mix(uint hash, int value)
        {
            uint v = (uint)value;
            hash ^= v & 0xFF;         hash *= FNV_PRIME;
            hash ^= (v >> 8) & 0xFF;  hash *= FNV_PRIME;
            hash ^= (v >> 16) & 0xFF; hash *= FNV_PRIME;
            hash ^= (v >> 24) & 0xFF; hash *= FNV_PRIME;
            return hash;
        }
    }
}
