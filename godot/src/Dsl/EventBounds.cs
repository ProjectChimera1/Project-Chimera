#nullable enable
namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.5 — the named cost caps for the custom-event layer, in ONE place (the <see cref="ExprBounds"/> /
    /// <c>EffectBounds</c> style: named constants at a load-time gate, never inline literals). Every structural cap
    /// is enforced AT LOAD by <see cref="EventDispatchPlan"/> (at BOTH the <c>ScenarioValidator</c> gate and the
    /// <c>ScenarioDirector.LoadScenario</c> backstop) with a LOCATED error naming the constant — never silently
    /// truncated at runtime. The two runtime capacities (<see cref="MaxNextTickEventQueue"/> /
    /// <see cref="MaxSameTickWorkList"/>) are deterministic drop-newest seatbelts for WORLD-DRIVEN occurrence
    /// volume (mass deaths), NOT authored-structure gates — the AT-LOAD proof remains the authority over authored
    /// structure (and per-tick fuel is explicitly Story 7.6, not this).
    ///
    /// These are corpus-validated dials, not free tuning knobs (the WC3-class fixture in
    /// <c>EventDispatchPlanTests</c> proves a Glut-shaped on-death cascade + a deep-but-legal module chain load
    /// under the shipped values): raising one is a deliberate, recorded decision — it widens what a hostile or
    /// degenerate authored event web can make every peer dispatch per tick.
    /// </summary>
    public static class EventBounds
    {
        /// <summary>
        /// Maximum declared custom events per scenario. 64 comfortably covers a WC3-class module library (a few
        /// events per gameplay module × a dozen modules) while keeping the registry, the dispatch-plan analysis,
        /// and the per-event subscriber scan trivially bounded.
        /// </summary>
        public const int MaxCustomEvents = 64;

        /// <summary>
        /// Maximum typed parameters per declared custom event. 4 matches the built-in <c>unit_dies</c> payload
        /// (victim / killer / killer_faction) with one slot of headroom, and pins the fixed per-occurrence payload
        /// stride (<c>FiredEvent</c> widens by exactly this many int raws — never a per-event allocation).
        /// </summary>
        public const int MaxEventParams = 4;

        /// <summary>
        /// Story 7.9 — the maximum number of typed params a custom-UI <c>Button</c> may target on the custom event
        /// it raises through the lockstep bus. 2 is the fixed wire budget: a <c>DslEvent</c> order reuses the 11-byte
        /// <c>UnitOrder</c> (eventIndex→UnitId, arg0→TargetX.Raw, arg1→TargetZ.Raw) with NO wire widening, so a button
        /// may only raise an event declaring ≤ this many params. <c>CustomUiGate</c> rejects a button targeting a
        /// wider event with a located error naming this constant (wider-arg buttons are deferred, not truncated;
        /// triggers can still raise wider events via <c>RaiseEventNode</c>).
        /// </summary>
        public const int MaxButtonEventParams = 2;

        /// <summary>
        /// Story 7.9 — the per-player, per-tick cap on button-originated <c>DslEvent</c> commands buffered on the
        /// lockstep bus. 8 comfortably covers rapid legitimate interaction (a vote/buy flurry) while bounding what a
        /// spammed button can inject per tick. <c>LockstepManager.EnqueueDslEvent</c> enforces it DETERMINISTICALLY
        /// drop-newest (never a throw), mirroring the <c>_pendingCount &lt; MAX_ORDERS</c> idiom; DslEvents also share
        /// the existing 32-order packet budget.
        /// </summary>
        public const int MaxDslEventsPerTick = 8;

        /// <summary>
        /// Maximum triggers subscribed to any single event. 16 covers a heavily-observed hub event (every module
        /// watching one "wave_start") while bounding the per-occurrence dispatch scan.
        /// </summary>
        public const int MaxEventFanOut = 16;

        /// <summary>
        /// Maximum same-tick cascade depth: the longest same-tick raise path through the (load-proven acyclic)
        /// event-dispatch graph, counted in events. 8 allows a full module pipeline (A→B→…→H) in one tick;
        /// anything deeper is authored as <c>next_tick</c> feedback through the bounded queue.
        /// </summary>
        public const int MaxEventCascadeDepth = 8;

        /// <summary>
        /// Maximum memoized worst-case transitive dispatch ops from ANY single root occurrence (one dispatch = one
        /// subscribed-trigger evaluation; each same-tick raise recursively adds its own event's cost). 256 admits a
        /// wide diamond or a deep chain but rejects exponential same-tick amplification webs at load. The estimator
        /// bounds ops per root OCCURRENCE — occurrence COUNT is world-driven and seatbelted by
        /// <see cref="MaxSameTickWorkList"/>.
        /// </summary>
        public const int MaxCascadeOps = 256;

        /// <summary>
        /// Capacity of the cross-tick <see cref="DslEventQueue"/> (next-tick raises pending at the checksum
        /// boundary). Overflow is DETERMINISTIC drop-newest (documented, identical on every peer — the queue folds
        /// into <c>SimChecksum</c> v18, so a divergent drop would desync detectably; an identical one cannot).
        /// </summary>
        public const int MaxNextTickEventQueue = 64;

        /// <summary>
        /// Capacity of the same-tick dispatch work list (dequeued next-tick events + same-tick raises awaiting
        /// their FIFO drain). Sized for world-driven volume (mass deaths each raising into handlers): 1024 ≫
        /// <see cref="MaxCascadeOps"/> because occurrence count scales with the WORLD, not the authored structure.
        /// Overflow is deterministic drop-newest — a documented seatbelt, distinct from Story 7.6's runtime fuel.
        /// </summary>
        public const int MaxSameTickWorkList = 1024;

        /// <summary>
        /// Story 7.13 — the named runtime depth cap for synchronous <c>run_trigger</c> nesting: part of the ruleset
        /// IDENTITY (a constant, NOT folded into any checksum — like <see cref="MaxCascadeOps"/>). Self/mutual run
        /// cycles are rejected AT LOAD (tri-color DFS over the run-target graph), so an acyclic authored web can never
        /// exceed this; the cap is a pure seatbelt against a pathologically deep legal chain, halting DETERMINISTICALLY
        /// at the WHOLE-TRIGGER boundary (never mid-Sequence). 16 admits a deep module dispatch chain while bounding
        /// the synchronous per-tick run stack.
        /// </summary>
        public const int MaxRunTriggerDepth = 16;

        /// <summary>
        /// Story 7.13 — the maximum number of weighted branches a single <c>random_choice</c> may declare. Bounds the
        /// per-node branch-port fan-out (and the weight array) at load; a wider node is a located reject. 16 comfortably
        /// covers a weighted loot/spawn table while keeping the exec walk trivially bounded.
        /// </summary>
        public const int MaxRandomChoiceBranches = 16;

        /// <summary>
        /// Story 7.13 (Arm D) — the RESERVED wire/queue sentinel for the built-in <c>player_chat</c> rail, distinct
        /// from every custom-event registry index (customs are <c>0..MaxCustomEvents-1</c>). A player_chat occurrence
        /// rides the EXISTING 11-byte <c>UnitCommand.DslEvent</c> order (the Story 7.9 button wire) with this value in
        /// the order's <c>UnitId</c> (a <c>ushort</c> — so it MUST fit <c>0..65535</c> and stay above
        /// <see cref="MaxCustomEvents"/>). <c>TryEnqueueExternalDslEvent</c> recognises it BEFORE the registry-range
        /// guard and routes it to the transient player_chat rail rather than the custom-event registry; no replay
        /// VERSION change (0xFF00 = 65280).
        /// </summary>
        public const int PlayerChatRailCode = 0xFF00;

        /// <summary>
        /// Story 7.13 (Arm D) — the bounded ceiling (exclusive) on a <c>player_chat</c> chat CODE. The sim tick only
        /// ever sees a bounded integer code + sender slot — NEVER a string (the chat-string↔code map is presentation-
        /// side). A code outside <c>[0, MaxChatCode)</c> is a DETERMINISTIC no-op drop at
        /// <c>TryEnqueueExternalDslEvent</c> (no mutation, no throw), identical on every peer and in replay.
        /// </summary>
        public const int MaxChatCode = 1024;

        /// <summary>
        /// DW-349 — the RESERVED queue-code BASE of the built-in EDGE-EVENT RE-QUEUE rail. A one-shot base
        /// occurrence (match_start, unit_dies, building_completed, timer_expires, unit_damaged, unit_trained,
        /// ability_cast, hero_level, player_chat — the <c>ScenarioDirector.RequeueKindNames</c> ordinal order)
        /// that a fuel-exhausted sweep break or a batched-drain suppression prevented a SPECIFIC trigger from
        /// evaluating is PERSISTED into the checksummed <c>DslEventQueue</c> as <c>RequeueRailBase + kind</c>,
        /// raiser = the occurrence's faction slot, params P0..P2 = the payload raws and P3 = the TARGET exec
        /// index — redelivered next tick to THAT trigger alone, so an already-served trigger can never
        /// double-fire on the redelivery. Polled kinds (the thresholds) re-emit every tick and never ride this
        /// rail. Distinct from every custom-event registry index (<c>0..MaxCustomEvents-1</c>) and from
        /// <see cref="PlayerChatRailCode"/> (0xFF00); never accepted from the external wire
        /// (<c>TryEnqueueExternalDslEvent</c> range-rejects it). 0xFE00 = 65024.
        /// </summary>
        public const int RequeueRailBase = 0xFE00;
    }
}
