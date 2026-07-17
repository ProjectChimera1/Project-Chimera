#nullable enable
namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.6 — the named caps for the bounded-loop / array / fuel layer, in ONE place (the
    /// <c>EffectCaps</c> / <c>ExprBounds</c> style: named constants enforced at a load-time gate, never inline
    /// literals). Every cap is enforced AT LOAD by <c>ScenarioValidator</c> AND the <c>ScenarioDirector.LoadScenario</c>
    /// backstop with a LOCATED error naming the constant — never a silent runtime truncation. The single runtime
    /// seatbelt (<see cref="MaxDslOpsPerTick"/>) halts deterministically at a whole-trigger boundary.
    ///
    /// These are corpus-validated dials, not free tuning knobs: raising one is a deliberate, recorded decision
    /// (it widens what a hostile/degenerate authored trigger set can make every peer execute per tick).
    /// </summary>
    public static class DslBounds
    {
        /// <summary>
        /// Maximum declared capacity of an <c>Array</c>-typed DSL variable (elements are preallocated at load).
        /// 64 matches <see cref="MaxForEachItems"/> so a full array always fits one un-batched <c>for_each</c>
        /// (arrays never need batching by construction).
        /// </summary>
        public const int MaxArrayCapacity = 64;

        /// <summary>
        /// Maximum iterations a single (non-batched) <c>for_each</c> may perform — the ceiling for the authored
        /// <c>up_to</c> cap and for <c>for_each_batched</c>'s per-tick <c>batch_size</c>. Matches
        /// <c>EffectCaps.MaxSearchTargets</c>-class fan-out bounds.
        /// </summary>
        public const int MaxForEachItems = 64;

        /// <summary>
        /// Maximum loop-container nesting depth (<c>for_each</c> / <c>for_each_batched</c> levels on any exec
        /// path; the outermost loop is depth 1). Deeper nesting is rejected at load — together with the
        /// cap-product cost check it bounds the static worst case, never a runtime truncation.
        /// </summary>
        public const int MaxLoopNesting = 4;

        /// <summary>
        /// Maximum <c>for_each_batched</c> nodes per scenario (each owns one preallocated continuation row in
        /// <see cref="DslLoopState"/>). Bounds the load-time row allocation and the per-tick drain fan-out.
        /// </summary>
        public const int MaxBatchedLoops = 8;

        /// <summary>
        /// Maximum entity ids one <c>for_each_batched</c> snapshot (continuation row) may hold. A larger live set
        /// is truncated at snapshot time to the LOWEST ids (deterministic ascending-id scan) — the row storage is
        /// preallocated at this size per batched node at load.
        /// </summary>
        public const int MaxBatchSnapshot = 2048;

        /// <summary>
        /// Maximum static worst-case op cost of a single trigger's action chain (action = 1, expression =
        /// compiled op count, run_effect = embedded effect-node count, for_each = 1 + iterationCap × body,
        /// branch = 1 + condition + max(then, else), for_each_batched = 1 + batch_size × body). Exceeding it is a
        /// LOAD reject naming this constant — the bounded-by-construction gate.
        /// </summary>
        public const int MaxDslOpsPerTrigger = 4096;

        /// <summary>
        /// Review P9 — the parse/compile-time RECURSION SEATBELT for the exec-chain walkers
        /// (<c>TriggerGraph.BuildExecutionOrder</c>, <c>DslLoopGate.CheckGraph</c>, and the
        /// <c>ScenarioDirector</c> item compile): the maximum container nesting depth (branch / for_each /
        /// for_each_batched levels) any walk will recurse into before rejecting LOCATED. This is a fail-closed
        /// guard so the load gates themselves cannot be stack-overflowed — hostile graph JSON with thousands of
        /// nested containers would otherwise drive unbounded recursion into an uncatchable
        /// <c>StackOverflowException</c> BEFORE the <see cref="MaxLoopNesting"/>/cost checks ever run. It is NOT
        /// an authoring dial: loops are already capped at <see cref="MaxLoopNesting"/> levels, and BRANCH
        /// nesting (which does not count toward <see cref="MaxLoopNesting"/>) is bounded by the cap-product cost
        /// check — every branch level costs at least 2 ops, so a chain deeper than
        /// <see cref="MaxDslOpsPerTrigger"/>/2 = 2048 was already cost-illegal, but 2048 recursion frames are
        /// not provably stack-safe on a default 1 MB thread stack. 256 sits far above any sane authored graph
        /// (any graph deeper is degenerate cost-burning nesting the gate would rather reject loudly than walk).
        /// </summary>
        public const int MaxExecWalkDepth = 256;

        /// <summary>
        /// The per-tick DSL fuel budget — the runtime SEATBELT for the dynamic aggregate of CHAIN-SIDE work
        /// (many individually-legal triggers firing the same tick) plus definitions that escaped the load gate.
        /// Fuel charges fired-chain execution only: actions, their value/index expressions, run_effect embeds,
        /// loop iterations, and batched drains. Trigger CONDITION evaluation and event collection are NOT
        /// charged — each condition expression is bounded per-expression by <c>ExprBounds</c>, but their sum
        /// scales with the trigger count outside this budget. Charging mirrors the static cost model;
        /// exhaustion halts the sweep deterministically at a whole-trigger boundary (the in-flight trigger
        /// completes; remaining triggers skip this tick and re-evaluate next tick). The consumed-this-tick
        /// value folds into <c>SimChecksum</c> via <see cref="DslLoopState"/>.
        /// </summary>
        public const int MaxDslOpsPerTick = 16384;
    }
}
