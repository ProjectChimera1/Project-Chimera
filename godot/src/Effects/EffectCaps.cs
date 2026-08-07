#nullable enable
namespace ProjectChimera.Effects
{
    /// <summary>
    /// The structural caps of the closed Effect-Graph (AR-8 / Story 2.1). Every bound the graph and its
    /// executor enforce is a NAMED constant here — never a bare literal at a use site — so the determinism
    /// analyzer's CHM0004 (magic-cap) advisory stays clean and so these are the single set folded, in file order,
    /// into the ruleset hash by <c>RulesetHash</c> (Story 9.4).
    ///
    /// <para><b>These caps ARE the ruleset fingerprint (Story 9.4 — no longer "later").</b>
    /// <c>Core.Definitions.RulesetHash.Compute()</c> folds EVERY cap below, in FILE ORDER, into the FNV-64 that
    /// <c>MatchAgreementHash</c> carries through the handshake — so two clients that disagree on any cap are
    /// handshake-rejected instead of desyncing in-sim. Consequences for anyone editing this file:
    /// changing a cap's VALUE moves the agreement hash (a wire-visible change); ADDING a cap requires folding it
    /// into <c>RulesetHash</c> and bumping its <c>AlgoVersion</c>. <c>RulesetHashTests</c> pins both — it asserts
    /// the fold against an independently hand-rolled byte stream AND that the number of caps declared here equals
    /// the number folded, so a new-but-unfolded cap fails Tier-1 rather than shipping silently.</para>
    ///
    /// <para><b>Every cap below names its own enforcer.</b> Each member doc states where the bound is actually
    /// applied — load-time validator, runtime clamp, or buffer size — so a reader never has to guess whether a
    /// constant is live. Keep it that way when adding one: a cap whose doc describes no enforcer is either
    /// unenforced (a bug) or its enforcer is undocumented (doc debt). <c>EffectCapsDocHygieneTests</c> guards
    /// this file against the specific rot that produced DW-535.</para>
    /// </summary>
    public static class EffectCaps
    {
        /// <summary>
        /// Maximum composition-nesting depth (count of composition nodes on any root→leaf path). A graph whose
        /// nesting exceeds this is rejected at load by <c>EffectBounds.Validate</c>; the executor also carries a
        /// defensive runtime backstop. Pinned by test (depth 8 runs; depth 9 rejected) — do not infer the exact
        /// semantics from this number alone.
        /// </summary>
        public const int MaxEffectDepth = 8;

        /// <summary>Maximum children a <c>SequenceEffect</c> may hold (structural fan-out of a Sequence).</summary>
        public const int MaxSequenceChildren = 8;

        /// <summary>
        /// Maximum number of targets a single <c>SearchAreaEffect</c> fans its child out to (runtime fan-out cap).
        /// Enforced by <c>SearchAreaEffect.FindTargets</c>, which applies it by TRUNCATING the already-ascending,
        /// already-matching id run the spatial query returned — the target predicate runs inside the query, so
        /// every id this could truncate is a genuine match. The dominant contributor to the work-stack peak.
        /// </summary>
        public const int MaxSearchTargets = 64;

        /// <summary>
        /// Size of the executor's reusable hit buffer — and therefore the WIDTH OF THE SELECTION WINDOW, not a
        /// pre-filter scratch capacity. <c>SpatialHash.QueryRadiusLowestIds</c> admits an entity to the buffer only
        /// if it already passes the target predicate, and once the buffer is full it evicts the largest id it holds
        /// rather than dropping the new candidate — so the buffer ends up holding the globally lowest N MATCHING
        /// ids in the whole radius, where N is this cap.
        ///
        /// <para><b>Consequence for anyone editing this value.</b> A SearchArea's effective fan-out is
        /// <c>min(MaxHitsPerSearch, MaxSearchTargets)</c> lowest-id matches, so lowering this below
        /// <see cref="MaxSearchTargets"/> silently narrows WHICH entities an area effect hits — it does not merely
        /// shrink a scratch array. Equal to <see cref="MaxSearchTargets"/> today, and still named distinctly because
        /// the two bound different stages: how many matches the query may RETAIN, versus how many the executor may
        /// fan out to. The selection contract is executable in <c>SearchAreaSelectionTests</c>.</para>
        /// </summary>
        public const int MaxHitsPerSearch = 64;

        /// <summary>
        /// Pre-allocated size of the executor's explicit work-stack (AC2 "never grows beyond pre-allocated size").
        ///
        /// DERIVATION (the static worst case the other caps imply): the deepest, widest valid graph is a chain of
        /// <see cref="MaxEffectDepth"/> <c>SearchAreaEffect</c> nodes, each fanning out to <see cref="MaxSearchTargets"/>.
        /// Descending one child per level (LIFO) leaves (MaxSearchTargets-1) un-popped siblings at each of the first
        /// (MaxEffectDepth-1) levels, plus a full MaxSearchTargets fan-out of leaves pushed at the deepest level:
        ///
        ///     peak = (MaxEffectDepth-1) * (MaxSearchTargets-1) + MaxSearchTargets
        ///          = 7 * 63 + 64 = 505 simultaneous frames.
        ///
        /// (Mixing in Sequence nodes — fan-out 8 — can only LOWER the peak, so the all-SearchArea chain is the
        /// worst case.) The executor additionally fail-closes if the stack pointer would ever exceed this, so an
        /// underestimate can never overflow — it would at worst silently drop effects (proven by test in 5.2).
        /// </summary>
        public const int MaxEffectFrames =
            (MaxEffectDepth - 1) * (MaxSearchTargets - 1) + MaxSearchTargets;

        /// <summary>
        /// Maximum units a single spawn action may create. Enforced at LOAD — <c>ScenarioValidator</c> and
        /// <c>DslLoopGate</c> both reject a <c>spawn_unit</c> whose <c>count</c> falls outside <c>1..</c>this — and
        /// again at RUNTIME as a seatbelt, where <c>ScenarioDirector.ExecuteLeaf</c> clamps the count it hands to
        /// <c>OnSpawnUnit</c> (Story 7.6 retired the bare literal 50 both places).
        /// </summary>
        public const int MaxSpawnCount = 64;

        /// <summary>
        /// Maximum periodic pulses a <c>PersistentEffect</c> may have SCHEDULED AT ONCE. <c>ModifierStore</c>
        /// enforces it: <c>InstallPersistent</c> clamps the authored <c>period_count</c> to this, and the per-slot
        /// period schedule is armed to it. This bounds the schedule WIDTH, not a modifier's lifetime — a modifier
        /// that outlives the window is re-armed in place by <c>Advance</c>, so a duration far past 256 keeps pulsing
        /// (see <c>ModifierPeriodSemanticsTests</c> / <c>LifelongHotTests</c>).
        /// </summary>
        public const int MaxPersistentPeriods = 256;

        /// <summary>
        /// Maximum simultaneous active <c>Modifier</c>/<c>PersistentEffect</c> instances per entity in the
        /// <c>ModifierStore</c> (Story 2.2b). The store's per-entity slot ring is sized to this; a same-tick apply
        /// that would exceed it is refused DETERMINISTICALLY (dropped, never overflowed). Named here (CHM0004-clean)
        /// so the store never carries a bare-literal cap.
        /// </summary>
        public const int MaxModifiersPerEntity = 8;

        /// <summary>
        /// Maximum number of <c>SearchAreaEffect</c> nodes on any single root→leaf path (Story 2.3, AC4). The
        /// depth (<see cref="MaxEffectDepth"/>) and per-Sequence (<see cref="MaxSequenceChildren"/>) caps bound a
        /// graph's SIZE but NOT its worst-case execution COUNT: a chain of nested SearchArea nodes multiplies
        /// fan-out per level (up to <see cref="MaxSearchTargets"/>^depth ≈ 64⁸ leaf executions — the hang the 2.1
        /// review surfaced). Bounding SearchArea nesting to 2 caps a single cast's area-fan-out at
        /// <see cref="MaxSearchTargets"/>² = 4096 executions (chain-lightning fits; 3-deep area cascades are
        /// rejected). Enforced by <c>AbilityValidator</c>; folded into <c>RulesetHash</c> (Story 9.4).
        /// </summary>
        public const int MaxSearchAreaDepth = 2;

        /// <summary>
        /// Maximum total node count in one ability's effect graph (Story 2.3, AC4) — the absolute graph-size
        /// ceiling that, together with <see cref="MaxSearchAreaDepth"/>, bounds the worst-case work of a single
        /// cast. Enforced by <c>AbilityValidator</c>'s iterative node walk; folded into <c>RulesetHash</c>
        /// (Story 9.4).
        /// </summary>
        public const int MaxTotalEffectNodes = 64;

        /// <summary>
        /// Maximum authored <c>SearchAreaEffect.Radius</c>, in whole world units — the SPATIAL third of the
        /// worst-case-work bound, beside <see cref="MaxSearchAreaDepth"/> (how deeply searches nest) and
        /// <see cref="MaxTotalEffectNodes"/> (how large the graph is). Enforced at LOAD by
        /// <c>EffectBounds.Validate</c>, so every authoring surface already behind that gate inherits it —
        /// abilities (<c>AbilityValidator</c>), items (<c>ItemDefinitionValidator</c>), and a scenario's
        /// <c>run_effect</c> embeds (<c>ScenarioValidator</c>). Folded into <c>RulesetHash</c> (DW-534) because it
        /// is a structural bound two builds must agree on before they may share a match.
        ///
        /// <para><b>Why the ceiling has to live at authoring time (DW-534).</b>
        /// <c>SpatialHash.QueryRadiusLowestIds</c> visits every candidate in radius with no early exit: a full
        /// result buffer must NOT end its scan, because a later candidate may still carry a lower id, and that is
        /// exactly what makes its selection global rather than dependent on grid geometry. The scan therefore
        /// cannot bound its own cost without giving up that contract, so the only remaining ceiling is the authored
        /// radius. With no cap the walk was limited only by the grid's own bounds checks —
        /// <c>cellRadius = radius/CELL_SIZE + 1</c> against <c>GRID_DIM=32</c> cells of <c>CELL_SIZE=10</c> — so an
        /// authored radius of 320 visited ALL 1024 cells and ran the target predicate over every alive entity (up
        /// to <c>EntityWorld.MAX_ENTITIES</c>), once per outer target when nested, on the 30 Hz lockstep tick path
        /// where an overrun stalls every peer instead of degrading locally.</para>
        ///
        /// <para><b>The value.</b> 64 world units holds <c>cellRadius</c> at 7, i.e. at most 15×15 = 225 of the
        /// hash's 1024 cells however wide an author writes it, while leaving an order of magnitude of headroom over
        /// real content (the widest radius shipped today is 5) and still covering a quarter of the largest
        /// supported map's 256-unit width. <c>EffectRadiusCapTests</c> pins both halves: the cell-span arithmetic
        /// that makes this a cost ceiling, and the headroom over the shipped ability files.</para>
        /// </summary>
        public const int MaxSearchRadius = 64;

        /// <summary>
        /// DW-272 / Story 15.12 — the runtime ceiling on how far a STACKED periodic modifier's pulse may scale with its
        /// stack count. <c>ModifierStore.Advance</c> enforces it at the pulse boundary: the effective scale is
        /// <c>min(stackCount, MaxPeriodicStackScale)</c>, applied as the repeat COUNT for
        /// <see cref="ProjectChimera.Effects.PeriodicStackMode.Repeat"/> (that many graph runs) or as the magnitude
        /// MULTIPLIER for <see cref="ProjectChimera.Effects.PeriodicStackMode.Multiply"/> (one run at ×scale). Without
        /// this bound a modifier authored with a large <c>max_stacks</c> could multiply a per-tick pulse's work/damage
        /// without limit. 8 == the per-entity ring cap (<see cref="MaxModifiersPerEntity"/>), the most independent
        /// stacks that can coexist, so it never truncates a legitimately-reachable stack count while still capping a
        /// pathological authored one. A new cap here MOVES <c>RulesetHash</c> (it is folded in file order), so
        /// <c>RulesetHash.AlgoVersion</c> is bumped and its pins re-recorded in the same commit
        /// (<c>RulesetHashTests</c> enforces the count parity).
        /// </summary>
        public const int MaxPeriodicStackScale = 8;
    }
}
