#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;                 // Story 7.4 — canonical-order edge iteration in the compile backstop
using ProjectChimera.Combat;       // DamageTable, CombatEventQueue, DeathFeed
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;     // DslVarReadback (Story 7.8 presentation read rail)
using ProjectChimera.Dsl;
using ProjectChimera.Economy;
using ProjectChimera.Effects;      // EffectExecutor, EffectContext, EffectNode, ModifierStore
using ProjectChimera.Multiplayer;  // Story 7.13 — OrderApplier.ApplyActiveOrder (order_units sim leaf)
using ProjectChimera.Navigation;   // SpatialHash (run_effect SearchArea fan-out)

namespace ProjectChimera.Core
{
    /// <summary>
    /// Evaluates scenario triggers each simulation tick by walking the graph-canonical IR directly (Story 7.3,
    /// superseding 7.2's flat lowering). Pure C# — no Godot dependency. Runs last in the simulation loop so it
    /// sees fully-updated world state (post-combat, post-construction).
    ///
    /// Delegates fire for effects that require the presentation layer (spawn, message, sound, victory). Pure sim
    /// mutations — timers, variables, add_resources — happen directly inside Tick() against the top-level
    /// <see cref="DslVarTable"/> (folded into <see cref="SimChecksum"/>). An <see cref="EffectActionNode"/>
    /// (run_effect) executes its embedded D1 effect subgraph via the EXISTING <see cref="EffectExecutor"/> (no
    /// second executor).
    ///
    /// Story 7.5 — custom events: a trigger may SUBSCRIBE to a declared custom event (drain-only dispatch), RAISE
    /// one same-tick (appended to the FIFO work list, drained occurrence-major after the base sweep — handlers
    /// never nest) or next-tick (the checksummed <see cref="DslEventQueue"/>), and read the dispatch frame via
    /// <c>event.&lt;param&gt;</c> expressions. The load-time <see cref="EventDispatchPlan"/> (the SAME shared
    /// routine the ScenarioValidator gate runs) proves same-tick acyclicity and the <see cref="EventBounds"/>
    /// caps, so the tick-time drain is bounded by construction.
    /// </summary>
    public class ScenarioDirector : ISimSystem, IExprWorld
    {
        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly BuildingStore _buildings;
        private readonly ResourceStore _resources;

        // Story 9.2 — the match's active-faction registry, used to generalize the per-tick threshold poll from the
        // literal 2-player loop to iterate every active faction (slots 0..ActiveCount-1). Nullable by design: a
        // direct test construction that passes null falls back to the historical 2-slot poll (SimulationHost always
        // supplies its checksum registry). NOT folded — read-only iteration span, mirrors WinConditionSystem's use.
        private readonly FactionRegistry? _factionRegistry;

        // Story 7.3 — the top-level typed/scoped variable + timer store (owned by SimulationHost, folded into
        // SimChecksum). Replaces the former ad-hoc _variableNames/_variableValues/_timerNames/_timerRemaining lists.
        private readonly DslVarTable _vars;

        // ── Trigger runtime state ─────────────────────────────────────────────

        // Story 7.3 — the direct-execution view of the graph IR, in the TOTAL trigger order (Priority desc, then
        // ascending persistent node-id). Built ONCE per LoadScenario. Replaces the flat TriggerDefinition[] +
        // _triggerOrder: the tick now walks the graph, so run_effect (which has no flat form) executes too.
        private List<TriggerGraph.TriggerExec> _execs = new();
        private bool[] _triggerFired    = Array.Empty<bool>();  // run_once guard, indexed by _execs position
        private int[]  _triggerCooldown = Array.Empty<int>();   // remaining ticks, indexed by _execs position

        // Story 7.13 — the FOLDED per-exec trigger-enabled runtime mask (host-owned STABLE reference, shared with
        // SimChecksum). enable_trigger/disable_trigger flip an entry; both sweep gates consult it alongside
        // TriggerNode.Enabled. Unlike _triggerFired/_triggerCooldown it is NOT reallocated per LoadScenario (it is
        // reset in place so the checksum keeps folding the same object).
        private readonly TriggerEnabledStore _triggerEnabled;

        // Story 7.13 — persistent trigger-node id → _execs index (built once per LoadScenario). Resolves an
        // enable_trigger/disable_trigger/run_trigger target to the exec it controls/runs.
        private Dictionary<int, int> _triggerNodeIdToExec = new();

        // Story 7.14 — authored objective id → its reserved Global-Int DSL variable NAME (precomputed once per
        // LoadScenario so the show/complete/fail_objective leaves mutate via _vars.SetInt WITHOUT allocating a name
        // string in the tick). Only AUTHORED objectives get an entry (the synthesized default is presentation-only —
        // it declares no folded var, so an objective-less scenario adds NO folded state and its SimChecksum is
        // byte-identical). An objective action whose id is not in this map is a deterministic no-op.
        private Dictionary<string, string> _objectiveVarNameById =
            new(System.StringComparer.Ordinal);

        // Story 7.13 — the transient run_trigger nesting depth (reset at tick start; NOT folded — a per-tick scratch
        // counter, not cross-tick sim truth). Bounds synchronous run_trigger recursion by EventBounds.MaxRunTriggerDepth.
        private int _runDepth;

        // Story 7.13 — the host-owned transient sim-event feed (unit_damaged/unit_trained/ability_cast/hero_level),
        // drained in CollectEvents into the base-event buffer and cleared. NOT folded (empty at the checksum boundary).
        private readonly DslSimEventFeed _simEventFeed;

        // Story 7.15 — the host-owned trigger-debug OBSERVATION BUFFER (per-exec fire counts + tick-stamped ring).
        // Written UNCONDITIONALLY at the single FireTrigger choke point, AFTER the folded run-once/cooldown arming,
        // so the observation never perturbs the fold. NEVER folded into SimChecksum (the DslVarReadback posture); a
        // null (direct test constructor) self-owns one — the write then just lands nowhere observable.
        private readonly TriggerFireLog? _fireLog;

        // Story 7.13 — the interned kind name per DslSimEventFeed code (no per-tick string allocation).
        private static readonly string[] SimEventKindNames = { "unit_damaged", "unit_trained", "ability_cast", "hero_level" };

        // Story 7.13 (Arm D) — the transient player_chat pending buffer. A replicated player_chat occurrence arrives on
        // the folded _eventQueue (via TryEnqueueExternalDslEvent, marked with EventBounds.PlayerChatRailCode); the
        // tick-start dequeue SEPARATES it from the custom-event work-list (player_chat is a BUILT-IN dispatched by the
        // base sweep, not a _subscribedEvent) and stashes (sender, code) here. CollectEvents drains it into a base
        // "player_chat" FiredEvent and clears it — so it is EMPTY at the checksum boundary → NOT folded (the
        // DslSimEventFeed posture). Sized to MaxNextTickEventQueue (the _eventQueue capacity, so a single tick's
        // dequeue can never overflow it); deterministic drop-newest guard for defence-in-depth.
        private readonly int[] _pendingChatSender = new int[EventBounds.MaxNextTickEventQueue];
        private readonly int[] _pendingChatCode   = new int[EventBounds.MaxNextTickEventQueue];
        private int _pendingChatCount;

        // Story 7.4 — compiled expression programs, indexed by _execs position. Compiled ONCE per LoadScenario
        // (the two-phase contract: compile at load, zero-allocation eval in the tick). _condPrograms[i] are the
        // Bool programs ANDed with the trigger's legacy conditions. Empty for every expression-free (legacy)
        // scenario, so legacy tick behavior is byte-identical (Block-If parity).
        private ExprProgram[][]  _condPrograms  = Array.Empty<ExprProgram[]>();
        private static readonly ExprProgram[]  NoCondPrograms  = Array.Empty<ExprProgram>();

        // Story 7.6 — the compiled NESTED execution tree, indexed by _execs position (one CompiledItem per
        // TriggerGraph.ExecItem: leaf programs, preallocated loop snapshot buffers, run_effect fuel costs, and
        // the batched-row link). Built ONCE per LoadScenario; the tick walks it with zero heap allocation.
        private CompiledItem[][] _items = Array.Empty<CompiledItem[]>();
        private static readonly CompiledItem[] NoItems = Array.Empty<CompiledItem>();

        /// <summary>Story 7.6 — one compiled node of the nested execution tree (load-time allocated).</summary>
        private sealed class CompiledItem
        {
            public NodeBase Node = null!;
            public ExprProgram? Value;         // set_variable / array_push / array_set RHS
            public ExprProgram? Index;         // array_set index
            public ExprProgram? Cond;          // branch condition (compiled inCondition:false)
            public CompiledItem[] Body = Array.Empty<CompiledItem>();
            public CompiledItem[] Then = Array.Empty<CompiledItem>();
            public CompiledItem[] Else = Array.Empty<CompiledItem>();
            // Story 7.13 — random_choice: one compiled sub-chain per weighted branch + the parallel weights.
            public CompiledItem[][] Branches = Array.Empty<CompiledItem[]>();
            public int[] Weights = Array.Empty<int>();
            public int   WeightTotal;          // precomputed sum of Weights (the draw bound)
            public int[]? Snapshot;            // for_each loop-entry snapshot buffer (UpTo / array capacity)
            public int RunEffectCost;          // run_effect: embedded effect-node count (the fuel charge)
            public int BatchRow = -1;          // for_each_batched: its DslLoopState continuation row
            // Story 7.5 — the compiled raise plan for a TOP-LEVEL raise_event item (null on every other kind;
            // raise_event inside a body/then/else sub-chain is rejected at BOTH load gates, so a nested raise
            // item can only exist for a hostile direct caller and executes as a fail-safe no-op).
            public EventDispatchPlan.RaiseCompiled? Raise;
        }

        // Story 7.6 — the checksummed Layer-3 runtime state (per-tick fuel + batched continuation rows), shared
        // with SimChecksum via SimulationHost. Direct test constructors get a private instance.
        private readonly DslLoopState _loopState;

        // Story 7.6 — batched-row bookkeeping (parallel to _loopState rows, ascending node id): the owning
        // trigger's _execs index, and the batched item's position in that trigger's TOP-LEVEL compiled chain
        // (items after it are the continuation chain, run on the completion tick).
        private int[] _rowExecIdx = Array.Empty<int>();
        private int[] _rowItemPos = Array.Empty<int>();
        // Per _execs position: the trigger's batched continuation row (-1 = none). While its row is ACTIVE the
        // trigger is suppressed in the sweep.
        private int[] _batchRowOfTrigger = Array.Empty<int>();

        // Story 7.4 — the live world the IExprWorld seam scans (set at Tick entry; count() reads it).
        private EntityWorld? _exprWorld;

        // ── Story 7.5 — custom events: registry, per-exec dispatch info, buffers (ALL allocated at load) ─────

        /// <summary>The FiredEvent.Type marker for custom-event occurrences (a const — no per-tick string).</summary>
        private const string CustomEventType = "custom_event";

        // The cross-tick next-tick queue (host-owned + checksum-folded in production; self-owned for direct
        // test construction — determinism-identical either way, the tests fold their own instance).
        private readonly DslEventQueue _eventQueue;

        // The closed custom-event registry, resolved at LoadScenario (names are loaded references — the tick
        // never constructs a string).
        private string[] _eventNames       = Array.Empty<string>();
        private int[]    _eventParamCounts = Array.Empty<int>();
        // Story 7.9 — per-event allowed-raiser faction slots (registry order), for the RUNTIME sim-side raiser
        // authorization of a button-originated DslEvent (TryEnqueueExternalDslEvent). Reads-only; no new folded state.
        private int[][]  _eventAllowedRaisers = Array.Empty<int[]>();
        // Story 7.9 — the fixed-width scratch handed to DslEventQueue.Enqueue for an external (button) raise, so the
        // sim-side gate allocates nothing per raise. Slots past the wire's 2 args stay 0 (never written).
        private readonly int[] _externalArgScratch = new int[EventBounds.MaxEventParams];

        // Per-exec dispatch info (parallel to _execs): the subscribed custom-event index (-1 = built-in events
        // only) and the per-occurrence opt-in flag (any compiled program of the trigger reads event params). The
        // compiled raise plans live ON the trigger's top-level CompiledItems (CompiledItem.Raise).
        private int[]  _subscribedEvent = Array.Empty<int>();
        private bool[] _paramReading    = Array.Empty<bool>();

        // The preallocated BASE event buffer (replaces the per-tick `new List<FiredEvent>(16)`): sized at load to
        // the worst-case emission (deaths + building completions + timers + thresholds + match_start).
        private FiredEvent[] _baseEvents = Array.Empty<FiredEvent>();
        private int _baseEventCount;

        // The same-tick FIFO work list: seeded with the next-tick dequeue at tick start, appended by same-tick
        // raises in execution order, drained occurrence-major after the base sweep. Fixed capacity with
        // deterministic drop-newest overflow (EventBounds.MaxSameTickWorkList — a documented seatbelt for
        // world-driven volume, distinct from 7.6 fuel).
        private readonly FiredEvent[] _workList = new FiredEvent[EventBounds.MaxSameTickWorkList];
        private int _workHead, _workCount;

        // The current dispatch frame (event param raws) expressions read via PushEventParam, and the separate
        // raise-arg scratch (args evaluate against the CURRENT frame, so they must not clobber it).
        private readonly int[] _frameScratch = new int[EventBounds.MaxEventParams];
        private int _frameCount;
        private readonly int[] _raiseScratch = new int[EventBounds.MaxEventParams];

        // Zero-alloc building-type names for the building_completed payload (BuildingType.ToString() allocates;
        // the enum is byte-backed and append-only, so an index table is stable).
        private static readonly string[] BuildingTypeNames = Enum.GetNames(typeof(BuildingType));

        // ── Named regions (Story 6.4) ─────────────────────────────────────────
        private RegionStore _regions = RegionStore.Empty;

        // ── run_effect runtime (Story 7.3) ────────────────────────────────────
        // The director owns its OWN EffectExecutor + SpatialHash (the AbilityCastSystem pattern — "no second
        // executor" forbids a re-implementation of the effect runtime, not a second INSTANCE of the shared class).
        // The remaining sinks are injected from SimulationHost via SetEffectRuntime once every store exists.
        private readonly EffectExecutor _effectExecutor = new EffectExecutor();
        private readonly SpatialHash    _effectSpatial  = new SpatialHash();
        private DamageTable       _damageTable  = DamageTable.Default;
        private ModifierStore?    _modifiers;
        private CombatEventQueue? _combatEvents;
        private MatchStats?       _matchStats;
        private DeathFeed?        _deaths;

        // Story 7.8 — the presentation READ RAIL (owned by SimulationHost, wired via SetReadback). At the end of
        // every Tick the director publishes a version-stamped COPY of the FINAL post-tick _vars into it (once per
        // tick, at the tick boundary). Presentation-only — NEVER folded into SimChecksum, so a UI mismatch cannot
        // desync. Null for direct test constructors that don't wire a read rail.
        private DslVarReadback? _readback;
        private uint _publishTick;

        // ── Per-tick scratch ──────────────────────────────────────────────────
        private readonly List<string> _expiredTimers = new();

        // ── Change-detection snapshots ────────────────────────────────────────

        private readonly EntityFlags[] _prevFlags          = new EntityFlags[EntityWorld.MAX_ENTITIES];
        private readonly bool[]        _prevBuildingDone   = new bool[BuildingStore.MAX_BUILDINGS];

        private bool _firstTick = true;

        // ── Presentation-layer delegates ──────────────────────────────────────

        /// <summary>Requests the presentation layer to spawn units. (unitId, factionSlot, x, z, count).</summary>
        public Action<string, int, Fixed, Fixed, int>? OnSpawnUnit;

        /// <summary>Requests a toast notification. (text, durationSeconds).</summary>
        public Action<string, Fixed>? OnDisplayMessage;

        /// <summary>Requests a sound effect. (soundId)</summary>
        public Action<string>? OnPlaySound;

        /// <summary>Signals a match outcome. (winnerFactionSlot: 0=P1, 1=P2)</summary>
        public Action<int>? OnVictory;

        // ── Story 7.13 — the PRESENTATION-ONLY action-leaf delegates (mirror the On* pattern; C3-clean: the body
        //    may fire but never read/write sim state). NEVER folded into SimChecksum, so the checksum is
        //    byte-identical whether these fire or not — and they do NOT charge DSL fuel (fuel IS folded). ──

        /// <summary>Requests a camera pan to a named ScenarioCamera. (cameraName)</summary>
        public Action<string>? OnMoveCamera;

        /// <summary>Requests the cinematic letterbox/UI toggle. (enabled)</summary>
        public Action<bool>? OnCinematicMode;

        /// <summary>Requests a one-shot VFX at a point. (vfxId, x, z)</summary>
        public Action<string, Fixed, Fixed>? OnPlayVfx;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="loopState">Story 7.6 — the checksummed loop/fuel state (SimulationHost passes its shared
        /// instance so SimChecksum folds the same object; a null — direct test constructors — gets a private one).</param>
        /// <param name="eventQueue">Story 7.5 — the cross-tick next-tick event queue. Production
        /// (<c>SimulationHost</c>) passes its owned, checksum-folded instance; a null (direct test construction)
        /// self-owns one — determinism-identical, the caller just cannot fold what it does not hold.</param>
        /// <param name="triggerEnabled">Story 7.13 — the FOLDED per-exec trigger-enabled mask. Production
        /// (<c>SimulationHost</c>) passes its owned, checksum-folded instance (STABLE reference); a null (direct test
        /// construction) self-owns one — determinism-identical, the caller just cannot fold what it does not hold.</param>
        /// <param name="simEventFeed">Story 7.13 — the transient sim-event feed the four producer systems push into
        /// and the director drains each tick. A null self-owns one (the raises then never reach it unless the test
        /// pushes to the director's own feed via <see cref="SimEventFeed"/>).</param>
        /// <param name="fireLog">Story 7.15 — the non-folded trigger-debug observation buffer. Production
        /// (<c>SimulationHost</c>) passes its owned STABLE reference (never folded); a null (direct test construction)
        /// self-owns one — determinism-identical, the caller just cannot observe what it does not hold.</param>
        /// <param name="factions">Story 9.2 — the match's active-faction registry. Production (<c>SimulationHost</c>)
        /// passes its checksum registry so the per-tick threshold poll spans slots 0..ActiveCount-1; a null (direct
        /// test construction) falls back to the historical 2-slot poll. Read-only iteration span — NOT folded.</param>
        public ScenarioDirector(BuildingStore buildings, ResourceStore resources, DslVarTable vars,
            DslLoopState? loopState = null, DslEventQueue? eventQueue = null,
            TriggerEnabledStore? triggerEnabled = null, DslSimEventFeed? simEventFeed = null,
            TriggerFireLog? fireLog = null, FactionRegistry? factions = null)
        {
            _buildings     = buildings;
            _resources     = resources;
            _vars          = vars;
            _factionRegistry = factions; // Story 9.2 — active-faction span for the threshold poll (null ⇒ 2-slot legacy)
            _loopState     = loopState ?? new DslLoopState();
            _eventQueue    = eventQueue ?? new DslEventQueue();
            _triggerEnabled = triggerEnabled ?? new TriggerEnabledStore();
            _simEventFeed   = simEventFeed ?? new DslSimEventFeed();
            // Nullable by design: a null fire log means "do not observe" (headless determinism runs, dedicated
            // server). The write at FireTrigger is `_fireLog?.Record(...)`, so a null buffer performs NO fire-log
            // work — which is exactly what makes the differential guard meaningful (a run with the buffer is proven
            // byte-identical to one with the write genuinely absent). Real games always pass a SimulationHost-owned
            // buffer, so recording is unconditional there (visibility-independent, never gated on overlay state).
            _fireLog        = fireLog;
        }

        /// <summary>Story 7.13 — the director's transient sim-event feed (production shares the host's; a direct test
        /// constructor that passed null can push occurrences here to exercise the drain).</summary>
        public DslSimEventFeed SimEventFeed => _simEventFeed;

        /// <summary>
        /// Story 7.3 — inject the run_effect runtime sinks (constructed after this director in <c>SimulationHost</c>).
        /// The embedded effect graph executes against these: <paramref name="damageTable"/> is required (a null falls
        /// back to <see cref="DamageTable.Default"/>); the rest are optional (a graph with no modifier/feedback leaf
        /// never reads them). Idempotent; call once after the stores are built.
        /// </summary>
        public void SetEffectRuntime(DamageTable? damageTable, ModifierStore? modifiers,
            CombatEventQueue? combatEvents, MatchStats? matchStats, DeathFeed? deaths)
        {
            _damageTable  = damageTable ?? DamageTable.Default;
            _modifiers    = modifiers;
            _combatEvents = combatEvents;
            _matchStats   = matchStats;
            _deaths       = deaths;
        }

        /// <summary>
        /// Story 6.4: supply the resolved <see cref="RegionStore"/> the <c>unit_in_region</c> condition scans.
        /// </summary>
        public void SetRegionStore(RegionStore? store) => _regions = store ?? RegionStore.Empty;

        /// <summary>
        /// Story 7.8 — wire the presentation read rail. <c>SimulationHost</c> passes its shared
        /// <see cref="DslVarReadback"/> so that at each tick boundary the director publishes a version-stamped COPY of
        /// the final post-tick variable state into it (presentation-only; NEVER folded into <c>SimChecksum</c>). Its
        /// declarations are (re)initialized from the same <c>ScenarioData</c> at <see cref="LoadScenario"/>.
        /// </summary>
        public void SetReadback(DslVarReadback? readback) => _readback = readback;

        /// <summary>
        /// Story 7.9 — the sim-side raiser-AUTHORIZATION gate for a custom-UI Button raise arriving on the lockstep
        /// bus. Invoked by the single <c>OrderApplier.Apply</c> at the deterministic command-application point (before
        /// <c>StepOnce</c>), identically on every peer and in replay, so its verdict is byte-identical everywhere.
        ///
        /// Bounds-checks <paramref name="eventIndex"/> against the loaded registry, then authorizes
        /// <paramref name="raiserFaction"/> (a 0-based faction SLOT, or −1 = system, always legal) against the
        /// event's declared <c>allowed_raisers</c> (the load-time mirror at the runtime seam 7.5 earmarked). An
        /// authorized raise enqueues into the EXISTING checksum-folded <see cref="DslEventQueue"/> (the button's args
        /// fill the event's declared param slots; the unused wire slot is dropped, never truncating a declared
        /// param). Returns false — a DETERMINISTIC no-op drop, never a throw, never a client-side button-disable — on
        /// an out-of-range index, an unauthorized raiser, or a full queue (drop-newest). Adds NO new folded state.
        /// </summary>
        public bool TryEnqueueExternalDslEvent(int eventIndex, int raiserFaction, int arg0, int arg1)
        {
            // Story 7.13 (Arm D) — the built-in player_chat rail. Recognised BEFORE the custom-registry range guard
            // (the sentinel PlayerChatRailCode is far above _eventNames.Length and would else be rejected). The sim
            // accepts ONLY a bounded integer chat code + a real player sender slot — never a string. arg0 is the chat
            // CODE; arg1 is unused on this rail. Params are stored P0=sender, P1=code to match LoadBuiltinFrame's
            // player_chat case. Any out-of-range sender or code is a DETERMINISTIC no-op drop (no mutation, no throw),
            // identical on every peer and in replay.
            if (eventIndex == EventBounds.PlayerChatRailCode)
            {
                if ((uint)raiserFaction >= (uint)FactionRegistry.PLAYER_COUNT) return false; // -1/system or out-of-range slot
                if ((uint)arg0 >= (uint)EventBounds.MaxChatCode)               return false; // code out of [0, MaxChatCode)
                _externalArgScratch[0] = raiserFaction; // P0 = sender
                _externalArgScratch[1] = arg0;          // P1 = code
                return _eventQueue.Enqueue(EventBounds.PlayerChatRailCode, raiserFaction, _externalArgScratch, 2);
            }

            if ((uint)eventIndex >= (uint)_eventNames.Length) return false; // out of range → deterministic no-op

            if (raiserFaction != -1)
            {
                int[] allowed = _eventAllowedRaisers.Length > eventIndex
                    ? _eventAllowedRaisers[eventIndex] : Array.Empty<int>();
                if (Array.IndexOf(allowed, raiserFaction) < 0) return false; // unauthorized → deterministic drop
            }

            // Fill only the declared param slots from the 2-arg wire (a ≤2-param event by the CustomUiGate budget):
            // arg0→slot0, arg1→slot1. Slots past 2 stay 0 (never written); Enqueue clamps to the declared count.
            _externalArgScratch[0] = arg0;
            _externalArgScratch[1] = arg1;
            int paramCount = _eventParamCounts.Length > eventIndex ? _eventParamCounts[eventIndex] : 0;
            // Story 7.9 (PATCH 6) — re-enforce the wire budget sim-side: the DslEvent order carries only 2 arg slots, so
            // a wider event must NOT fire through this external (button) seam with silently-zeroed params. A tampered
            // order naming an event declaring > MaxButtonEventParams params is a deterministic drop (no mutation, no
            // throw). Triggers may still raise wider events directly via RaiseEventNode/DslEventQueue.Enqueue.
            if (paramCount > EventBounds.MaxButtonEventParams) return false;
            return _eventQueue.Enqueue(eventIndex, raiserFaction, _externalArgScratch, paramCount);
        }

        /// <summary>
        /// Load triggers from a freshly-applied scenario. Resets all runtime state.
        /// Call after ApplyScenario() so the initial alive-state snapshots are clean.
        /// </summary>
        public void LoadScenario(ScenarioData scenario)
        {
            // Story 7.3: build the execution graph by MERGING both trigger channels so neither is silently dropped.
            // The flat TriggerDefinition[] lowers via FromFlat (lossless, so legacy flat scenarios execute
            // byte-identically); when a trigger_graph canonical IR is ALSO present (graph-only triggers — e.g.
            // run_effect — authored via the editor's raw-IR hatch), it is parsed via FromJson and merged in with its
            // node ids offset past the flat graph's max id (no collisions). BuildExecutionOrder then runs over the
            // UNION, so the global Priority-desc / node-id-asc total order holds across BOTH channels. A malformed
            // trigger_graph fails closed at the converter parse AND at the Story 7.7 GraphStructureGate below (the
            // authoritative structural rulebook). The tick WALKS this graph directly, superseding 7.2's ToFlat().
            //
            // Review (7.4 pass 2): FAILURE-ATOMIC — every throwing step (parse, cycle guard, expression compile)
            // runs against LOCALS before any field is touched, so a caller that catches a located load error keeps
            // the previous scenario's coherent runtime state (pre-7.4, a compile throw could strand half-replaced
            // trigger state whose null program rows then NRE'd on the next Tick).
            TriggerGraph graph = TriggerGraph.FromFlat(scenario.Triggers);
            if (!string.IsNullOrWhiteSpace(scenario.TriggerGraphJson))
                graph.Merge(TriggerGraph.FromJson(scenario.TriggerGraphJson!));

            // Story 7.7 — gate/backstop reconciliation: the SAME shared rulebooks the ScenarioValidator gate runs
            // (GraphStructureGate + DslLoopGate.CheckDeclarations + CheckGraph + CheckSpawnCounts), applied
            // UNCONDITIONALLY for direct LoadScenario callers (the 7.6 HasLoopConstructs legacy-parity guard is
            // removed — one invocation posture at both gates). Duplicate variable declarations now always reject
            // here too (the validator always rejected them), so loop_var/array/expression typing can never gate
            // against a different declaration than runtime Resolve binds. All checks run against LOCALS before
            // any field commit (failure atomicity preserved).
            Dictionary<string, (DslValueType Elem, int Capacity)> arrayDecls = DslLoopGate.BuildArrayDecls(scenario.Variables);
            var loopDeclMap = BuildDeclMap(scenario, requireUnique: true);

            string? declErr = DslLoopGate.CheckDeclarations(scenario.Variables);
            if (declErr != null) throw new System.Text.Json.JsonException(declErr);

            // Story 7.14 — the objective + reserved-namespace DECLARATION rulebook, the SAME shared
            // ObjectiveResolver.CheckDeclarations the ScenarioValidator gate runs, applied here as the fail-closed
            // backstop for direct LoadScenario callers (gate/backstop parity — a caller bypassing the validator fails
            // closed identically; without this a malformed objectives array reached Resolve below and NRE'd / declared
            // colliding reserved vars). Runs against LOCALS before any field commit.
            string? objectiveDeclErr = ObjectiveResolver.CheckDeclarations(scenario);
            if (objectiveDeclErr != null) throw new System.Text.Json.JsonException(objectiveDeclErr);

            // Whole-graph structural rulebook (dup ids, dangling endpoints, port legality, exec/data forks, stray
            // data edges, unconsumed-expression compiles) BEFORE the execution-order walk, so structurally
            // malformed IR rejects located instead of relying on the walker's tolerances.
            string? structErr = GraphStructureGate.Check(graph, loopDeclMap, arrayDecls);
            if (structErr != null) throw new System.Text.Json.JsonException(structErr);

            List<TriggerGraph.TriggerExec> execs = graph.BuildExecutionOrder();

            // Story 7.6 (review) — the spawn-count backstop: the "never a silent runtime truncation" posture makes
            // an out-of-range count a loud load reject at BOTH gates; the ExecuteLeaf Math.Min clamp stays a
            // defense-in-depth seatbelt only.
            string? spawnErr = DslLoopGate.CheckSpawnCounts(execs);
            if (spawnErr != null) throw new System.Text.Json.JsonException(spawnErr);

            {
                var declaredRegions = new HashSet<string>(StringComparer.Ordinal);
                if (scenario.Regions != null)
                    foreach (ScenarioRegion rg in scenario.Regions)
                        if (rg != null && !string.IsNullOrEmpty(rg.Id)) declaredRegions.Add(rg.Id);
                // Story 7.14 — the set of MUTABLE objective ids an objective action may target: the authored,
                // reserved-var-backed objectives ONLY (matches _objectiveVarNameById below). The presentation-only
                // synthesized default is excluded, so an action targeting it rejects located here rather than being a
                // silent runtime no-op (fail-closed backstop parity with the ScenarioValidator gate).
                var mutableObjectives = new HashSet<string>(StringComparer.Ordinal);
                foreach (ResolvedObjective ro in ObjectiveResolver.Resolve(scenario))
                    if (ro.HasReservedVar) mutableObjectives.Add(ro.Id);
                string? loopErr = DslLoopGate.CheckGraph(graph, execs, loopDeclMap, arrayDecls,
                    id => declaredRegions.Contains(id),
                    id => mutableObjectives.Contains(id));
                if (loopErr != null) throw new System.Text.Json.JsonException(loopErr);
            }

            // Story 7.8 — the custom-UI widget-tree gate (caps/dup-ids/anchor/depth/bind-resolve+type-match), the
            // SAME shared CustomUiGate the ScenarioValidator runs, applied UNCONDITIONALLY here as the fail-closed
            // backstop for direct LoadScenario callers (parity by construction — the GraphStructureGate posture).
            // A null tree returns null (no-op). Runs against LOCALS before any field commit (failure atomicity).
            // Story 7.9 — pass CustomEvents (parity with the validator) so the backstop resolves Button raise targets,
            // enforces the ≤ MaxButtonEventParams budget, and type-matches authored args.
            string? uiErr = CustomUiGate.Check(scenario.CustomUi, loopDeclMap, arrayDecls, scenario.CustomEvents);
            if (uiErr != null) throw new System.Text.Json.JsonException(uiErr);

            // Story 7.4 — compile every condition-expression and set_variable value-expression ONCE (two-phase
            // contract). A compile failure throws a located JsonException, consistent with the cycle-guard posture
            // above (the ScenarioValidator gate rejects the same errors located BEFORE any apply; this is the
            // fail-closed backstop for direct LoadScenario callers). Expression-free scenarios compile nothing.
            // Story 7.6 — the per-item compile now also builds the nested CompiledItem execution tree (loop
            // snapshot buffers, branch/array programs, run_effect fuel costs).
            // Story 7.5 — it ALSO runs the full custom-event backstop (registry rules, single-subscription,
            // raise-arg arity/type/wire, DAG proof + EventBounds caps) through the SHARED EventDispatchPlan
            // routine, and threads each trigger's event-parameter map into every program compile.
            CompiledPrograms compiled = CompileExpressionPrograms(scenario, graph, execs, arrayDecls);

            // Story 7.6 — collect the for_each_batched continuation rows from the TOP-LEVEL compiled chains, in
            // ascending node-id order (the drain phase's total order across rows). All locals — committed below.
            var batchedRows = new List<(int NodeId, int ExecIdx, int ItemPos)>();
            for (int i = 0; i < compiled.Items.Length; i++)
                for (int j = 0; j < compiled.Items[i].Length; j++)
                    if (compiled.Items[i][j].Node is ForEachBatchedNode fbNode)
                        batchedRows.Add((fbNode.Id, i, j));
            batchedRows.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));

            // Story 7.5 — size the preallocated base-event buffer to the worst-case per-tick emission: every
            // entity dying (MAX_ENTITIES) + every building completing (MAX_BUILDINGS) + every DISTINCT timer name
            // expiring (declared + create_timer action names — timer identity is static text, so this bound is
            // load-computable; the whole-graph node walk also covers create_timer actions nested inside 7.6
            // container bodies, which the flat top-level Actions projection omits) + the 4 polled threshold
            // events + match_start.
            var timerNames = new HashSet<string>(StringComparer.Ordinal);
            if (scenario.Timers != null)
                foreach (ScenarioTimer t in scenario.Timers)
                    if (!string.IsNullOrEmpty(t.Name)) timerNames.Add(t.Name);
            foreach (NodeBase n in graph.Nodes)
                if (n is ActionNode { Kind: "create_timer" } ct && !string.IsNullOrEmpty(ct.TimerName))
                    timerNames.Add(ct.TimerName!);
            // Story 7.13 — add headroom for the sim-event feed drain (unit_damaged/unit_trained/ability_cast/
            // hero_level occurrences collected into the base buffer alongside the polled events) AND for Arm D's
            // player_chat pending buffer (up to EventBounds.MaxNextTickEventQueue occurrences drain into this SAME
            // base buffer), so the sizing is correct-by-construction rather than relying on entity/building headroom.
            // Story 9.2 — the threshold poll now emits 2 events (resource + unit_count) per ACTIVE faction, up to
            // 2*PLAYER_COUNT at N=8; reserve that plus 1 (match_start) rather than the old compile-time `+ 5`.
            var baseEvents = new FiredEvent[EntityWorld.MAX_ENTITIES + BuildingStore.MAX_BUILDINGS + timerNames.Count + (2 * FactionRegistry.PLAYER_COUNT + 1) + DslSimEventFeed.Capacity + EventBounds.MaxNextTickEventQueue];

            // Story 7.3: the typed/scoped variable + timer store declarations. The seconds→ticks conversion happens
            // HERE at the Core boundary (SecondsToTicks owns TICKS_PER_SECOND) so the table receives integer ticks
            // only. Declared timers start active at their tick count (I/O matrix). Story 7.6: Array declarations
            // carry their element type + capacity into the table's preallocated array store.
            var varDecls = new List<DslVarDecl>();
            if (scenario.Variables != null)
                foreach (ScenarioVariable v in scenario.Variables)
                    varDecls.Add(new DslVarDecl(v.Name, v.Type, v.Scope, ScopeInitialRaw(v.Type, v.Initial),
                        raw1: 0,
                        elementType: v.ElementType ?? DslValueType.Int,
                        capacity: v.Capacity ?? 0));

            // Story 7.14 — append one reserved Global-Int DSL variable per AUTHORED objective (ascending authored
            // order, AFTER the authored decls), seeded from the objective's initial_state ordinal. This is the ONLY
            // new folded state and it rides the existing v16 DslVarTable fold — NO SimChecksum bump. The synthesized
            // DEFAULT objective (a scenario with no authored objectives) is presentation-only and declares NOTHING
            // here, so every pre-7.14 scenario (all with no authored objectives — every golden) adds no folded var and
            // its per-tick SimChecksum is byte-identical. The show/complete/fail_objective leaves mutate these vars via
            // _vars.SetInt (the reserved name resolved through the precomputed id→name map below).
            var objectiveVarNames = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (ResolvedObjective ro in ObjectiveResolver.Resolve(scenario))
            {
                if (!ro.HasReservedVar) continue; // the synthesized default is presentation-only (no folded state)
                string reserved = ro.ReservedVarName;
                varDecls.Add(new DslVarDecl(reserved, DslValueType.Int, VarScope.Global, (int)ro.InitialState));
                objectiveVarNames[ro.Id] = reserved; // last-wins on a duplicate id (validator rejects dups at load)
            }
            var timerDecls = new List<DslTimerDecl>();
            if (scenario.Timers != null)
                foreach (ScenarioTimer t in scenario.Timers)
                    timerDecls.Add(new DslTimerDecl(t.Name, Math.Max(1, SecondsToTicks(t.Seconds))));

            // ── COMMIT (nothing below throws) ──────────────────────────────────

            // Story 7.4 (review patch): drop the world pinned by the previous run's Tick — a stale EntityWorld must
            // not survive a LoadScenario / Edit→Play reset (count() would otherwise scan the old world if anything
            // evaluated an expression before the first tick re-captures it).
            _exprWorld = null;

            _execs = execs;
            _triggerFired    = new bool[execs.Count];
            _triggerCooldown = new int[execs.Count];
            // Story 7.15 — reset the non-folded observation buffer alongside the fire-guard reallocation: fresh
            // per-exec fire counters + an empty recent-fire ring, so an F5 Edit→Play re-apply starts with no stale
            // counts. Sized to the exec count (== trigger count), the same index space _triggerFired uses.
            _fireLog?.Reset(execs.Count);
            // Story 7.15 (review patch) — install the exec→authored-Triggers[] index map the debug overlay uses for
            // trigger names + click-to-navigate. Exec order is (Priority desc, node-id asc), so it diverges from
            // authored order once any trigger uses a non-default Priority; because FromFlat emits trigger nodes in
            // authored order with strictly increasing ids, the authored index is the RANK of each exec's Trigger.Id.
            if (_fireLog != null && execs.Count > 0)
            {
                int n = execs.Count;
                var execToAuthored = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int id = execs[i].Trigger.Id;
                    int rank = 0;
                    for (int j = 0; j < n; j++)
                        if (execs[j].Trigger.Id < id) rank++; // ids are unique — no ties
                    execToAuthored[i] = rank;
                }
                _fireLog.SetAuthoredMapping(execToAuthored);
            }
            _condPrograms    = compiled.CondPrograms;
            _items           = compiled.Items;

            // Story 7.13 — reset the FOLDED trigger-enabled mask IN PLACE (grow/reuse the host-owned buffer, all
            // enabled), then seed each exec from its authored TriggerNode.Enabled; and build the persistent
            // trigger-node-id → exec-index map the enable/disable/run_trigger leaves resolve against. The store
            // reference is stable (never reallocated), so SimChecksum keeps folding the same object.
            _triggerEnabled.Reset(execs.Count);
            _triggerNodeIdToExec = new Dictionary<int, int>(execs.Count);
            for (int i = 0; i < execs.Count; i++)
            {
                _triggerEnabled.SetInitial(i, execs[i].Trigger.Enabled);
                _triggerNodeIdToExec[execs[i].Trigger.Id] = i;
            }
            _runDepth = 0;

            // Story 7.14 — commit the authored objective id → reserved-var-name map (built pre-commit above).
            _objectiveVarNameById = objectiveVarNames;

            // Story 7.5 — commit the custom-event runtime: registry references, per-exec dispatch info (the
            // compiled raise plans ride the committed Items tree), the sized base buffer, and a clean
            // queue/work-list/frame (a re-load never inherits pending feedback from the previous scenario).
            _eventNames       = compiled.EventNames;
            _eventParamCounts = compiled.EventParamCounts;
            _eventAllowedRaisers = compiled.EventAllowedRaisers; // Story 7.9 — runtime raiser auth
            _subscribedEvent  = compiled.SubscribedEvent;
            _paramReading     = compiled.ParamReading;
            _baseEvents       = baseEvents;
            _baseEventCount   = 0;
            _workHead = _workCount = 0;
            _pendingChatCount = 0; // Story 7.13 (Arm D) — a re-load never inherits a pending player_chat occurrence
            _frameCount = 0;
            _eventQueue.Clear();

            // Story 7.6 — (re)allocate the continuation rows + row bookkeeping (load-time allocation only).
            var rowNodeIds = new int[batchedRows.Count];
            _rowExecIdx = new int[batchedRows.Count];
            _rowItemPos = new int[batchedRows.Count];
            _batchRowOfTrigger = new int[execs.Count];
            for (int i = 0; i < _batchRowOfTrigger.Length; i++) _batchRowOfTrigger[i] = -1;
            for (int r = 0; r < batchedRows.Count; r++)
            {
                rowNodeIds[r]  = batchedRows[r].NodeId;
                _rowExecIdx[r] = batchedRows[r].ExecIdx;
                _rowItemPos[r] = batchedRows[r].ItemPos;
                _batchRowOfTrigger[batchedRows[r].ExecIdx] = r;
                compiled.Items[batchedRows[r].ExecIdx][batchedRows[r].ItemPos].BatchRow = r;
            }
            _loopState.ConfigureRows(rowNodeIds);

            _vars.InitFromDeclarations(varDecls, timerDecls);

            // Story 7.8 — (re)initialize the presentation read rail from the SAME declarations (Global/Per-player
            // scalars + Global arrays; TriggerLocal scratch is never in the read rail) and reset its tick stamp.
            _readback?.InitFromDeclarations(varDecls);
            _publishTick = 0;

            _firstTick = true;

            // Snapshot initial state so the first diff doesn't generate spurious events.
            Array.Clear(_prevFlags, 0, _prevFlags.Length);
            Array.Clear(_prevBuildingDone, 0, _prevBuildingDone.Length);

            for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
            {
                _prevBuildingDone[i]  = _buildings.Alive[i]
                    && _buildings.ConstructionTimer[i] <= Fixed.Zero;
            }
        }

        /// <summary>Build the declared name → (type, scope) map for the loop gate (TryAdd — first declaration
        /// wins, matching DslVarTable.Resolve). Duplicate declarations reject when <paramref name="requireUnique"/>:
        /// armed by the 7.4 anyExpr rule AND (review, 7.6) by the presence of loop constructs.</summary>
        private static Dictionary<string, (DslValueType Type, VarScope Scope)> BuildDeclMap(
            ScenarioData scenario, bool requireUnique)
        {
            var map = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
            if (scenario.Variables != null)
                foreach (ScenarioVariable v in scenario.Variables)
                    if (v != null && !string.IsNullOrWhiteSpace(v.Name))
                        if (!map.TryAdd(v.Name, (v.Type, v.Scope)) && requireUnique)
                            throw new System.Text.Json.JsonException(
                                $"scenario variable '{v.Name}' is declared more than once.");
            return map;
        }

        /// <summary>The load-compiled program set (Story 7.4 expressions + Story 7.6 item trees + Story 7.5
        /// custom-event dispatch info), returned as LOCALS so a mid-compile throw never strands half-replaced
        /// director state (failure-atomic).</summary>
        private sealed class CompiledPrograms
        {
            public ExprProgram[][]  CondPrograms  = Array.Empty<ExprProgram[]>();
            public CompiledItem[][] Items         = Array.Empty<CompiledItem[]>();
            public string[] EventNames            = Array.Empty<string>();
            public int[]    EventParamCounts      = Array.Empty<int>();
            public int[][]  EventAllowedRaisers   = Array.Empty<int[]>();
            public int[]    SubscribedEvent       = Array.Empty<int>();
            public bool[]   ParamReading          = Array.Empty<bool>();
        }

        /// <summary>
        /// Story 7.4/7.5/7.6 — compile every expression subgraph the execution view surfaced (condition-in roots,
        /// item value/index/branch-condition roots, and raise-arg roots) into <see cref="ExprProgram"/>s held per
        /// trigger, via <see cref="ExprCompiler"/> against the scenario's declared-variable map and (7.5) each
        /// trigger's event-parameter map, and run the full 7.5 load-time backstop through the SHARED
        /// <see cref="EventDispatchPlan"/> routine (registry rules, single-subscription, raise-arg
        /// arity/type/wire, DAG proof + EventBounds caps). Located <see cref="System.Text.Json.JsonException"/>
        /// on any reject. Pure function of its inputs (review, 7.4 pass 2): fills and returns LOCAL arrays so a
        /// mid-compile throw never strands half-replaced director state — the caller commits them only after
        /// everything compiled.
        /// </summary>
        private static CompiledPrograms CompileExpressionPrograms(
            ScenarioData scenario, TriggerGraph graph, List<TriggerGraph.TriggerExec> execs,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls)
        {
            // GATE-ONLY checks (deliberately NOT mirrored in this backstop): the engine-ceiling faction bound on
            // slotted expr_var reads (CheckFactionSlot, ceiling Faction.Player4) is an authoring-policy rule the
            // ScenarioValidator owns — the compiler's structural [0, DslVarTable.PlayerSlots) bound plus the
            // CountAlive slot guard keep the runtime safe without it. Every OTHER expression consumer-edge check
            // the gate applies (the compile rulebook, Bool condition roots, single value-in edge, wire = type,
            // the value-in edge-shape rejects, duplicate declarations) is re-run below, so a direct LoadScenario
            // caller fails closed identically. The 7.6 loop/array rulebook is NOT duplicated here — it runs via
            // the SHARED DslLoopGate in LoadScenario (parity by construction).
            var condPrograms  = new ExprProgram[execs.Count][];
            var compiledItems = new CompiledItem[execs.Count][];

            // Legacy parity guard: expression-free graphs skip every NEW check below (a duplicate-declaration
            // direct-load, however malformed, must keep its exact pre-7.4 load behavior — the Block-If).
            // Story 7.5: any custom-event machinery (declared events, raise/subscription nodes) also opts INTO
            // the stricter checks — new machinery, native strictness; legacy content unaffected.
            bool anyExpr = scenario.CustomEvents is { Length: > 0 };
            foreach (NodeBase n in graph.Nodes)
            {
                if (anyExpr) break;
                if (ExprCompiler.IsExprNode(n) || n is RaiseEventNode
                    || (n is EventNode c && c.Kind == NodeKinds.CustomEvent))
                    anyExpr = true;
            }

            // Declared name → (type, scope), the same map shape the validator gate builds. WITH expressions
            // present, duplicates reject like the gate (review, 7.4 pass 2): a last-declaration-wins map would
            // type expressions against one slot while DslVarTable.Resolve reads another (PerPlayer-first),
            // silently confusing typed raws.
            Dictionary<string, (DslValueType Type, VarScope Scope)> declMap = BuildDeclMap(scenario, requireUnique: anyExpr);

            // ── Story 7.5 — the SHARED load-time analysis (the exact routine the validator gate runs): registry
            //    validation, custom_event/raise_event usage rules, single-subscription, raise-arg edge shape +
            //    compile, and the same-tick DAG proof + EventBounds caps. Located throw = the fail-closed backstop.
            //    It also yields each trigger's event-parameter map, which the compile passes below need so a
            //    handler's condition/value expressions can read event.<param>. ──
            if (!EventDispatchPlan.TryBuild(scenario.CustomEvents, graph, execs, declMap, arrayDecls,
                    maxRaiserSlotExclusive: FactionRegistry.PLAYER_COUNT, out EventDispatchPlan? evPlan, out string? evErr))
                throw new System.Text.Json.JsonException(evErr);
            EventDispatchPlan plan = evPlan!;

            // ── Per-edge parity scan (review, 7.4 pass 2 — mirrors the gate's consumer-edge loop over ALL data
            //    edges, not just exec-surfaced actions): a value-in edge whose src is NOT an expression node maps
            //    to root -1 in BuildExecutionOrder and would otherwise be SILENTLY ignored (the literal Value wins
            //    against the authored wiring); a value-in edge onto a run_effect or an action outside every exec
            //    chain would escape the per-exec loop below entirely. Canonical tuple order → deterministic
            //    first-fail, matching the gate. Story 7.5: raise-arg edges (Dst = a RaiseEventNode) are fully
            //    checked by the plan above and fall through here untouched (a RaiseEventNode is neither a
            //    TriggerNode nor an ActionNode). ──
            var byId = new Dictionary<int, NodeBase>(graph.Nodes.Count);
            foreach (NodeBase n in graph.Nodes)
                byId[n.Id] = n;
            List<DataEdge> sortedData = graph.DataEdges.OrderBy(e => e).ToList();
            var seenValueInPorts = new HashSet<int>();
            foreach (DataEdge de in sortedData)
            {
                bool srcIsExpr = byId.TryGetValue(de.Src, out NodeBase? src) && ExprCompiler.IsExprNode(src);
                byId.TryGetValue(de.Dst, out NodeBase? dst);

                if (de.DstPort == TriggerGraph.ActionValueInPort && dst is EffectActionNode)
                    throw new System.Text.Json.JsonException(
                        $"run_effect node {dst.Id}: a value-in edge is not allowed on run_effect (only a set_variable action takes a value expression).");
                if (de.DstPort == TriggerGraph.ActionValueInPort && dst is ActionNode && !srcIsExpr)
                    throw new System.Text.Json.JsonException(
                        $"action node {dst.Id}: the value-in edge source (node {de.Src}) is not an expression node.");

                if (!srcIsExpr || dst is null) continue;

                bool consumedByCondition = dst is TriggerNode && de.DstPort == TriggerGraph.TriggerConditionInPort;
                bool consumedByValueIn   = dst is ActionNode && de.DstPort == TriggerGraph.ActionValueInPort;
                // Every expression node emits on ExprDataOutPort (= 0); a consumed edge leaving any other src port
                // is a non-canonical encoding — reject located (the compiler applies the same rule to operand edges).
                if ((consumedByCondition || consumedByValueIn) && de.SrcPort != TriggerGraph.ExprDataOutPort)
                    throw new System.Text.Json.JsonException(
                        $"expr node {de.Src}: the consumer edge into node {de.Dst} leaves src port {de.SrcPort}; expression nodes emit only on port {TriggerGraph.ExprDataOutPort}.");

                if (consumedByValueIn)
                {
                    var act = (ActionNode)dst;
                    // Story 7.6 widening: array_push/array_set also take a value-in expression edge (their
                    // element typing runs via the shared DslLoopGate in LoadScenario).
                    if (act.Kind != "set_variable" && !NodeKinds.IsArrayActionKind(act.Kind))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: a value-in expression edge is only allowed on a set_variable, array_push, or array_set action (kind '{act.Kind}').");
                    if (!seenValueInPorts.Add(act.Id))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: multiple value-in expression edges (forked; exactly one allowed).");
                    if (NodeKinds.IsArrayActionKind(act.Kind))
                        continue; // typed/required-edge rules run in DslLoopGate.CheckGraph (LoadScenario)
                    if (string.IsNullOrEmpty(act.Variable))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: a set_variable with a value expression needs a target variable.");
                    if (!ExprCompiler.TryCompile(graph, de.Src, declMap, inCondition: false, plan.ParamMapFor(act.Id),
                            out ExprProgram? vp, out string? vErr, arrayDecls))
                        throw new System.Text.Json.JsonException($"set_variable value expression: {vErr}");
                    DslValueType target = declMap.TryGetValue(act.Variable!, out var tDecl) ? tDecl.Type : DslValueType.Int;
                    if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: set_variable target '{act.Variable}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                    if (vp!.ResultType != target)
                        throw new System.Text.Json.JsonException(
                            $"expr node {de.Src}: value expression result type {vp.ResultType} does not match target variable '{act.Variable}' ({target}).");
                    if (de.Wire != ExprCompiler.WireOf(target))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: the value-in edge wire '{de.Wire}' does not match target variable '{act.Variable}' ({target}).");
                }
            }

            var paramReading = new bool[execs.Count];
            for (int i = 0; i < execs.Count; i++)
            {
                TriggerGraph.TriggerExec ex = execs[i];

                // Story 7.5 — this trigger's event-parameter map (null unless it is the single subscriber of a
                // custom event or a unit_dies trigger), and the per-occurrence opt-in seed: raise ARGS that read
                // event params (compiled inside the plan) already make the trigger frame-sensitive.
                IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? exMap = plan.ParamMapFor(ex.Trigger.Id);
                paramReading[i] = plan.RaiseArgsReadEventParams[i];

                if (ex.ConditionExprRoots.Length == 0)
                {
                    condPrograms[i] = NoCondPrograms;
                }
                else
                {
                    var programs = new ExprProgram[ex.ConditionExprRoots.Length];
                    for (int j = 0; j < ex.ConditionExprRoots.Length; j++)
                    {
                        int root = ex.ConditionExprRoots[j];
                        if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: true, exMap, out ExprProgram? p, out string? err, arrayDecls))
                            throw new System.Text.Json.JsonException($"trigger '{ex.Trigger.Name}' condition expression: {err}");
                        if (p!.ResultType != DslValueType.Bool)
                            throw new System.Text.Json.JsonException(
                                $"trigger '{ex.Trigger.Name}' condition expression (expr node {root}): must evaluate to Bool, got {p.ResultType}.");
                        // Backstop parity with the gate: the condition-in edge must carry the Boolean wire.
                        foreach (DataEdge e in graph.DataEdges)
                            if (e.Src == root && e.Dst == ex.Trigger.Id && e.DstPort == TriggerGraph.TriggerConditionInPort
                                && e.Wire != DataWireType.Boolean)
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{ex.Trigger.Name}' condition expression (expr node {root}): the condition-in edge must carry the Boolean wire, got '{e.Wire}'.");
                        programs[j] = p;
                        paramReading[i] |= p!.ReadsEventParams; // Story 7.5 — per-occurrence opt-in is statically visible
                    }
                    condPrograms[i] = programs;
                }

                // Story 7.6 — compile the trigger's NESTED execution tree (leaf value/index programs, branch
                // conditions, loop snapshot buffers, run_effect fuel costs). For a container-free legacy chain
                // this reduces to the flat 7.4 value-program compile item-for-item (same rejects, same messages).
                // Story 7.5 — item programs compile against the trigger's event-parameter map and OR their
                // ReadsEventParams into the per-occurrence flag.
                compiledItems[i] = CompileItems(graph, ex.Items, declMap, arrayDecls, exMap,
                    ex.Trigger.Name, depth: 1, ref paramReading[i]);

                // Story 7.5 — attach the compiled raise plans to their TOP-LEVEL items. raise_event is
                // top-level-only (both gates reject it inside body/then/else sub-chains), and the flat Actions
                // projection mirrors the top-level chain item-for-item, so plan.Raises[i] — parallel to
                // ex.Actions — is complete and index-aligned with the top-level compiled chain.
                EventDispatchPlan.RaiseCompiled?[] raiseRow = plan.Raises[i];
                for (int j = 0; j < compiledItems[i].Length; j++)
                    if (compiledItems[i][j].Node is RaiseEventNode)
                        compiledItems[i][j].Raise = j < raiseRow.Length ? raiseRow[j] : null;
            }

            // Story 7.5 — stamp the per-occurrence flag onto the exec view (spec surface) and hand everything back
            // as locals for the failure-atomic commit.
            for (int i = 0; i < execs.Count; i++)
                execs[i].ReadsEventParams = paramReading[i];

            return new CompiledPrograms
            {
                CondPrograms     = condPrograms,
                Items            = compiledItems,
                EventNames       = plan.EventNames,
                EventParamCounts = plan.EventParamCounts,
                EventAllowedRaisers = plan.EventAllowedRaisers, // Story 7.9
                SubscribedEvent  = plan.SubscribedEvent,
                ParamReading     = paramReading,
            };
        }

        /// <summary>
        /// Story 7.6 — recursively compile one exec chain's <see cref="TriggerGraph.ExecItem"/>s into
        /// <see cref="CompiledItem"/>s. All allocation happens HERE at load (snapshot buffers sized up_to /
        /// declared array capacity; per-item programs), so the tick executor allocates nothing. Located
        /// JsonException on any compile reject (backstop posture; the DslLoopGate rulebook has already gated
        /// loop/array shapes whenever any 7.6 construct is present).
        ///
        /// <para>Review P9 — <paramref name="depth"/> is the chain's container nesting level (top-level = 1),
        /// capped at <see cref="DslBounds.MaxExecWalkDepth"/> with a located reject: the recursion seatbelt
        /// mirrored across all three exec-chain walkers, so a hostile deeply-nested tree can never
        /// stack-overflow this compile (an uncatchable process kill) instead of failing closed.</para>
        ///
        /// <para>Story 7.5 — every item program compiles against the owning trigger's
        /// <paramref name="eventParams"/> map (null for non-subscribing triggers) and ORs its
        /// <c>ReadsEventParams</c> into <paramref name="readsEventParams"/> (the per-occurrence dispatch
        /// opt-in). A <see cref="RaiseEventNode"/> item compiles to a bare item here — its plan is attached by
        /// the caller from the shared <see cref="EventDispatchPlan"/> (top-level only).</para>
        /// </summary>
        private static CompiledItem[] CompileItems(TriggerGraph graph, TriggerGraph.ExecItem[] items,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls,
            IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? eventParams,
            string triggerName, int depth, ref bool readsEventParams)
        {
            if (items.Length == 0) return NoItems;
            var compiled = new CompiledItem[items.Length];
            for (int j = 0; j < items.Length; j++)
            {
                TriggerGraph.ExecItem it = items[j];
                var ci = new CompiledItem { Node = it.Node };

                // Review P9 — the recursion seatbelt: reject located BEFORE recursing into a container's sub-chain.
                if ((it.Node is ForEachNode || it.Node is ForEachBatchedNode || it.Node is BranchNode
                        || it.Node is RandomChoiceNode)
                    && depth + 1 > DslBounds.MaxExecWalkDepth)
                    throw new System.Text.Json.JsonException(
                        $"trigger '{triggerName}' node {it.Node.Id}: container nesting depth {depth + 1} exceeds DslBounds.MaxExecWalkDepth={DslBounds.MaxExecWalkDepth} (the exec-walk recursion seatbelt).");

                switch (it.Node)
                {
                    case ForEachNode fe:
                    {
                        int bufSize;
                        if (fe.Source == "array")
                            bufSize = arrayDecls.TryGetValue(fe.ArrayName ?? "", out (DslValueType Elem, int Capacity) ad) ? ad.Capacity : 0;
                        else
                            bufSize = fe.UpTo;
                        ci.Snapshot = new int[bufSize < 0 ? 0 : bufSize];
                        ci.Body = CompileItems(graph, it.Body, declMap, arrayDecls, eventParams, triggerName, depth + 1, ref readsEventParams);
                        break;
                    }

                    case ForEachBatchedNode:
                        ci.Body = CompileItems(graph, it.Body, declMap, arrayDecls, eventParams, triggerName, depth + 1, ref readsEventParams);
                        break;

                    case BranchNode br:
                    {
                        if (it.CondExprRoot < 0)
                            throw new System.Text.Json.JsonException(
                                $"trigger '{triggerName}' branch node {br.Id}: requires a Bool expression wired into its condition-in data port.");
                        // Branch conditions compile inCondition:false — they evaluate INSIDE the trigger-local
                        // scope, so TriggerLocal/loop-var reads are legal (unlike the trigger condition-in).
                        if (!ExprCompiler.TryCompile(graph, it.CondExprRoot, declMap, inCondition: false, eventParams,
                                out ExprProgram? cp, out string? cErr, arrayDecls))
                            throw new System.Text.Json.JsonException($"trigger '{triggerName}' branch condition: {cErr}");
                        if (cp!.ResultType != DslValueType.Bool)
                            throw new System.Text.Json.JsonException(
                                $"trigger '{triggerName}' branch node {br.Id}: the condition expression must evaluate to Bool, got {cp.ResultType}.");
                        ci.Cond = cp;
                        readsEventParams |= cp.ReadsEventParams;
                        ci.Then = CompileItems(graph, it.Then, declMap, arrayDecls, eventParams, triggerName, depth + 1, ref readsEventParams);
                        ci.Else = CompileItems(graph, it.Else, declMap, arrayDecls, eventParams, triggerName, depth + 1, ref readsEventParams);
                        break;
                    }

                    case RandomChoiceNode rc:
                    {
                        // Story 7.13 — weights + one compiled sub-chain per branch (the DslLoopGate has already
                        // rejected an empty/zero-total/negative/over-cap weight set at both gates; this just compiles
                        // the branches the executor draws among). Weights are copied so the compiled item is
                        // self-contained; WeightTotal is the NextInt draw bound.
                        ci.Weights = (int[])rc.Weights.Clone();
                        int total = 0;
                        for (int k = 0; k < ci.Weights.Length; k++) total += ci.Weights[k];
                        ci.WeightTotal = total;
                        var branches = it.Branches.Length == 0 ? Array.Empty<CompiledItem[]>() : new CompiledItem[it.Branches.Length][];
                        for (int k = 0; k < it.Branches.Length; k++)
                            branches[k] = CompileItems(graph, it.Branches[k], declMap, arrayDecls, eventParams, triggerName, depth + 1, ref readsEventParams);
                        ci.Branches = branches;
                        break;
                    }

                    case EffectActionNode eff:
                        ci.RunEffectCost = DslLoopGate.CountEffectNodes(eff.Effect);
                        break;

                    case ActionNode act when NodeKinds.IsArrayActionKind(act.Kind):
                    {
                        // Shapes/types are gated by DslLoopGate (always reachable here — an array action IS a
                        // 7.6 construct); compile the programs the executor evaluates.
                        if (it.ValueExprRoot >= 0)
                        {
                            if (!ExprCompiler.TryCompile(graph, it.ValueExprRoot, declMap, inCondition: false, eventParams,
                                    out ExprProgram? vp, out string? vErr, arrayDecls))
                                throw new System.Text.Json.JsonException($"trigger '{triggerName}' {act.Kind} value expression: {vErr}");
                            // Story 7.5 (merge review): re-assert the element-type rule here — DslLoopGate skips
                            // its static type check for event-param-reading subgraphs, so this map-aware compile
                            // is the enforcing one (parity with the validator's event-param pass).
                            DslValueType elem = arrayDecls.TryGetValue(act.Variable ?? "", out (DslValueType Elem, int Capacity) vad)
                                ? vad.Elem : DslValueType.Int;
                            if (vp!.ResultType != elem)
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{triggerName}' action node {act.Id} ({act.Kind}): value expression type {vp.ResultType} does not match array '{act.Variable}' element type {elem}.");
                            ci.Value = vp;
                            readsEventParams |= vp.ReadsEventParams;
                        }
                        if (it.IndexExprRoot >= 0)
                        {
                            if (!ExprCompiler.TryCompile(graph, it.IndexExprRoot, declMap, inCondition: false, eventParams,
                                    out ExprProgram? ip, out string? iErr, arrayDecls))
                                throw new System.Text.Json.JsonException($"trigger '{triggerName}' {act.Kind} index expression: {iErr}");
                            if (ip!.ResultType != DslValueType.Int)
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{triggerName}' action node {act.Id} ({act.Kind}): the index expression must be Int, got {ip.ResultType}.");
                            ci.Index = ip;
                            readsEventParams |= ip.ReadsEventParams;
                        }
                        break;
                    }

                    case ActionNode act:
                    {
                        int root = it.ValueExprRoot;
                        if (root >= 0)
                        {
                            // The unchanged 7.4 rulebook for set_variable value expressions (same rejects/messages).
                            if (act.Kind != "set_variable" || string.IsNullOrEmpty(act.Variable))
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{triggerName}' action node {act.Id}: a value-in expression edge is only allowed on a set_variable action with a target variable.");
                            if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: false, eventParams, out ExprProgram? p, out string? err, arrayDecls))
                                throw new System.Text.Json.JsonException($"trigger '{triggerName}' set_variable value expression: {err}");
                            DslValueType target = declMap.TryGetValue(act.Variable!, out var decl) ? decl.Type : DslValueType.Int;
                            if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{triggerName}' set_variable target '{act.Variable}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                            if (p!.ResultType != target)
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{triggerName}' set_variable value expression (expr node {root}): result type {p.ResultType} does not match target variable '{act.Variable}' ({target}).");
                            ci.Value = p;
                            readsEventParams |= p.ReadsEventParams;
                        }
                        break;
                    }

                    // A RaiseEventNode falls through with no case: it carries no value/index/cond roots (the
                    // exec-chain walk special-cases it — its data ports 0..3 are ARG ports, resolved by the
                    // shared EventDispatchPlan, never as value/index roots). The caller attaches its plan.
                }

                compiled[j] = ci;
            }
            return compiled;
        }

        /// <summary>Story 7.4 — the <c>count(faction)</c> built-in's world seam: alive entities of the given slot,
        /// via the existing deterministic ascending-id <see cref="CountAlive"/> scan. Reads the world captured at
        /// Tick entry; 0 before the first tick or for a slot with no live entities.</summary>
        int IExprWorld.CountAlive(int factionSlot)
        {
            EntityWorld? world = _exprWorld;
            if (world is null) return 0;
            // A COMPUTED slot can be any int (only LITERAL count() arguments are range-checked at compile) — an
            // out-of-range slot counts 0 instead of wrapping the (Faction)(slot + 1) cast onto Neutral/garbage.
            if (factionSlot < 0 || factionSlot >= DslVarTable.PlayerSlots) return 0;
            return CountAlive(world, (Faction)(factionSlot + 1));
        }

        // ── Story 7.13 — the state-read built-ins (the count() seam pattern). All PURE (mutate nothing) and TOTAL
        //    (never throw in-tick): a dead/out-of-range read returns the defined sentinel; a null world (before the
        //    first tick) folds the same sentinel. Entities iterate ascending id skipping !IsAlive. ──

        int IExprWorld.EntityHpRaw(int entityId)
        {
            EntityWorld? world = _exprWorld;
            if (world is null || entityId < 0 || entityId >= world.HighWaterMark || !world.IsAlive(entityId)) return 0;
            return world.Health[entityId].Raw;
        }

        int IExprWorld.EntityOwnerSlot(int entityId)
        {
            EntityWorld? world = _exprWorld;
            if (world is null || entityId < 0 || entityId >= world.HighWaterMark || !world.IsAlive(entityId)) return -1;
            return (int)world.FactionOf[entityId] - 1; // 0-based slot (Player1 → 0); Neutral → -1
        }

        void IExprWorld.EntityPosition(int entityId, out int rawX, out int rawZ)
        {
            EntityWorld? world = _exprWorld;
            if (world is null || entityId < 0 || entityId >= world.HighWaterMark || !world.IsAlive(entityId))
            {
                rawX = 0; rawZ = 0;
                return;
            }
            FixedVec3 p = world.Position[entityId];
            rawX = p.X.Raw;
            rawZ = p.Z.Raw;
        }

        int IExprWorld.UnitCountTag(int factionSlot, int tagBit)
        {
            EntityWorld? world = _exprWorld;
            if (world is null || factionSlot < 0 || factionSlot >= DslVarTable.PlayerSlots) return 0;
            var faction = (Faction)(factionSlot + 1);
            int n = 0, hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction && ((int)world.TagsOf[i] & tagBit) != 0) n++;
            return n;
        }

        int IExprWorld.UnitCountCategory(int factionSlot, int category)
        {
            EntityWorld? world = _exprWorld;
            if (world is null || factionSlot < 0 || factionSlot >= DslVarTable.PlayerSlots) return 0;
            var faction = (Faction)(factionSlot + 1);
            int n = 0, hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction && (int)world.CategoryOf[i] == category) n++;
            return n;
        }

        int IExprWorld.PlayerResourceRaw(int factionSlot, int resourceKind)
        {
            // ResourceStore is indexed by (int)Faction (0 = Neutral, 1 = Player1 …); a 0-based slot maps to slot+1.
            int idx = factionSlot + 1;
            if (factionSlot < 0 || idx >= _resources.Ore.Length) return 0;
            return resourceKind == 0 ? _resources.Ore[idx].Raw
                 : resourceKind == 1 ? _resources.Crystal[idx].Raw
                 : 0;
        }

        int IExprWorld.RegionUnitCount(string? regionName)
        {
            EntityWorld? world = _exprWorld;
            if (world is null || !_regions.TryGetIndex(regionName, out int rIdx)) return 0; // unknown region → 0 (total)
            int n = 0, hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && _regions.Contains(rIdx, world.Position[i])) n++;
            return n;
        }

        /// <summary>The stored initial value's raw int for a declared variable: Fixed/Point store the Fixed.Raw
        /// verbatim (preserved through the JSON boundary); the integer-valued types (Int/Bool/refs/timer) store the
        /// truncated integer, so an Int slot's GetInt returns a plain int (never a shifted Fixed.Raw).</summary>
        private static int ScopeInitialRaw(DslValueType type, Fixed initial) =>
            (type == DslValueType.Fixed || type == DslValueType.Point) ? initial.Raw : initial.ToInt();

        // ── ISimSystem ────────────────────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            _exprWorld = world; // Story 7.4 — expose the live world to the count() built-in for this tick
            try
            {
                // Story 7.6 — the per-tick fuel budget resets at the START of every director tick; everything the
                // director executes below (drains + the sweep + the custom-event dispatch drain) charges against
                // it. Legacy scenarios charge only their fired actions, and the consumed value folds into
                // SimChecksum either way (v17).
                _loopState.ResetFuel();

                // Story 7.13 — the transient run_trigger nesting counter resets at the START of every director tick
                // (per-tick scratch, NOT folded — the depth cap is a within-tick seatbelt).
                _runDepth = 0;

                if (_execs.Count == 0)
                {
                    // Story 7.13 — a trigger-less scenario still drains the sim-event feed producers pushed (no
                    // subscriber matches, but the feed must not accumulate across ticks).
                    _simEventFeed.Clear();
                    _pendingChatCount = 0; // Story 7.13 (Arm D) — parity: the player_chat rail must not accumulate either
                    // Review (7.3 follow-up): declared ScenarioData.Timers made trigger-less timers representable, and
                    // the folded remaining-ticks must still decrement per the "declared timers start active" contract —
                    // pre-7.3 the early-out was safe because timers could only exist via trigger actions. An empty table
                    // (every legacy scenario) makes this a no-op, so goldens/checksums are unmoved. Expiry events go
                    // nowhere without triggers (timer_expires is a trigger event), which is fine — the state is the point.
                    // Story 7.5: with no triggers there can be no raisers and no subscribers, so the queue is provably
                    // empty; the defensive clear keeps a (unreachable) stale entry from folding forever.
                    _expiredTimers.Clear();
                    _vars.TimerTickAndCollectExpired(_expiredTimers);
                    _eventQueue.Clear();
                    return;
                }

                // Story 7.5 — seed the same-tick work list with the next-tick DEQUEUE (dequeued events dispatch
                // before ALL of this tick's raises: they were enqueued first, and every raise APPENDS behind
                // them), then clear the queue — this tick's next_tick raises re-fill it for the next tick's seed.
                // MUST run BEFORE DrainBatchedRows: a raise fired by a batched continuation chain also appends
                // behind the dequeued events.
                _workHead = 0;
                _workCount = 0;
                _pendingChatCount = 0; // Story 7.13 (Arm D) — re-fill the transient player_chat rail from this dequeue
                int pending = _eventQueue.Count;
                for (int i = 0; i < pending; i++)
                {
                    // Story 7.13 (Arm D) — a player_chat-rail entry (marked with the reserved sentinel) is a BUILT-IN,
                    // dispatched by the base sweep (not a _subscribedEvent), so it must NOT seed the custom work-list.
                    // Stash (sender=P0, code=P1) on the transient rail; CollectEvents drains it into a base occurrence.
                    if (_eventQueue.EventIndexAt(i) == EventBounds.PlayerChatRailCode)
                    {
                        if (_pendingChatCount < _pendingChatSender.Length) // deterministic drop-newest (cannot overflow in practice)
                        {
                            _pendingChatSender[_pendingChatCount] = _eventQueue.ParamAt(i, 0);
                            _pendingChatCode[_pendingChatCount]   = _eventQueue.ParamAt(i, 1);
                            _pendingChatCount++;
                        }
                        continue;
                    }

                    if (_workCount >= _workList.Length) continue; // custom work-list full → drop-newest (existing seatbelt)
                    ref FiredEvent ev = ref _workList[_workCount++];
                    ev.Type        = CustomEventType;
                    ev.CustomIndex = _eventQueue.EventIndexAt(i);
                    ev.Slot        = _eventQueue.RaiserAt(i);
                    ev.Numeric     = 0;
                    ev.Data        = null;
                    ev.P0 = _eventQueue.ParamAt(i, 0);
                    ev.P1 = _eventQueue.ParamAt(i, 1);
                    ev.P2 = _eventQueue.ParamAt(i, 2);
                    ev.P3 = _eventQueue.ParamAt(i, 3);
                }
                _eventQueue.Clear();

                // Story 7.6 — the batched drain phase runs at the START of the director tick, BEFORE event
                // collection and the trigger sweep (ascending node-id across rows).
                DrainBatchedRows(world);

                CollectEvents(world);
                TickCooldowns();
                EvaluateTriggers(world);   // the base sweep (legacy semantics preserved); raises append to the work list
                DrainWorkList(world);      // per-occurrence custom dispatch, FIFO occurrence-major
                UpdateSnapshots(world);
            }
            finally
            {
                // Review (7.4 pass 2): the seam is scoped to THE TICK — don't retain the world reference between
                // ticks (any future between-tick evaluation entry point, e.g. an editor preview, would otherwise
                // scan a world the director no longer owns; LoadScenario's clear covers only the reset path).
                _exprWorld = null;

                // Story 7.8 — publish the presentation read rail EXACTLY ONCE per tick at the tick boundary, reading
                // the FINAL post-tick _vars (the director ticks LAST, and this runs after the sweep/drains/snapshots
                // and the early-out alike). Publishes a version-stamped COPY; writes only the readback, never sim
                // state; NEVER folded into SimChecksum (a UI mismatch cannot desync).
                _readback?.Publish(_vars, ++_publishTick);
            }
        }

        // ── Story 7.6 — batched drip (drain phase) ────────────────────────────

        /// <summary>
        /// Drain every ACTIVE continuation row: up to its <c>batch_size</c> snapshot entries this tick, in
        /// ascending node-id row order, each alive entity running the loop body anchored at itself (dead
        /// entities are SKIPPED at drain time but still consume their batch slot — the drip stays 10/10/5-shaped).
        /// Each row drains inside a fresh TriggerLocal scope. A row whose cursor reaches its snapshot length
        /// completes: the row deactivates and the trigger's CONTINUATION chain (the top-level items after the
        /// batched node) runs on this — the completion — tick. Fuel: a whole ROW is the drain-phase's
        /// "whole-trigger boundary": once exhausted, remaining rows skip this tick and resume next tick.
        /// </summary>
        private void DrainBatchedRows(EntityWorld world)
        {
            // Story 7.5 — batched bodies and continuation chains run OUTSIDE any dispatch frame (the row's
            // originating dispatch ended on its fire tick): an event.<param> read here deterministically
            // evaluates 0 (the documented total ExprProgram semantics), identically on every peer.
            _frameCount = 0;

            for (int row = 0; row < _loopState.RowCount; row++)
            {
                if (!_loopState.RowActive(row)) continue;
                if (_loopState.FuelExhausted) break; // whole-row boundary halt — rows resume next tick

                int execIdx = _rowExecIdx[row];
                CompiledItem batched = _items[execIdx][_rowItemPos[row]];
                var fb = (ForEachBatchedNode)batched.Node;

                _vars.Enter(); // entity drains re-enter a FRESH TriggerLocal scope per tick
                try
                {
                    _loopState.Charge(1); // the drain entry (mirrors the static model's per-loop op)
                    int cursor = _loopState.RowCursor(row);
                    int len    = _loopState.RowLength(row);
                    int end    = cursor + fb.BatchSize;
                    if (end > len) end = len;

                    for (int k = cursor; k < end; k++)
                    {
                        int ent = _loopState.RowId(row, k);
                        if (!world.IsAlive(ent)) continue; // killed since snapshot → skipped at drain time
                        ExecuteItems(batched.Body, world, ent);
                    }
                    _loopState.SetCursor(row, end);

                    if (end >= len)
                    {
                        _loopState.CompleteRow(row);
                        // The continuation chain (exec-out port 0 — the items AFTER the batched node) runs on
                        // the completion tick, inside the same fresh trigger-local scope.
                        CompiledItem[] chain = _items[execIdx];
                        for (int j = _rowItemPos[row] + 1; j < chain.Length; j++)
                            ExecuteItem(chain[j], world, -1);
                    }
                }
                finally { _vars.Exit(); }
            }
        }

        // ── Event collection ──────────────────────────────────────────────────

        /// <summary>Append one base event to the preallocated buffer (bounds-guarded drop-newest — unreachable
        /// under the load-time sizing, kept as the fail-closed backstop).</summary>
        private void AddBaseEvent(string type, int slot, int numeric, string? data,
                                  int p0 = 0, int p1 = 0, int p2 = 0, int p3 = 0)
        {
            if (_baseEventCount >= _baseEvents.Length) return; // defensive drop-newest (sizing covers worst case)
            ref FiredEvent ev = ref _baseEvents[_baseEventCount++];
            ev.Type = type; ev.Slot = slot; ev.Numeric = numeric; ev.Data = data;
            ev.CustomIndex = -1;
            ev.P0 = p0; ev.P1 = p1; ev.P2 = p2; ev.P3 = p3;
        }

        /// <summary>Fill the preallocated base-event buffer (Story 7.5: replaces the per-tick
        /// <c>new List&lt;FiredEvent&gt;(16)</c> — zero per-tick heap allocation on the event path). Emission
        /// order is byte-identical to the legacy list (match_start, deaths ascending entity id, building
        /// completions, timer expiries, thresholds); <c>unit_dies</c> now carries the killer-attribution payload.</summary>
        private void CollectEvents(EntityWorld world)
        {
            _baseEventCount = 0;

            // match_start fires on the very first tick after LoadScenario().
            if (_firstTick)
            {
                AddBaseEvent("match_start", -1, 0, null);
                _firstTick = false;
            }

            // Entity deaths — compare current Alive flag against previous snapshot (ascending entity id — the
            // per-occurrence emission order). Payload (Story 7.5): victim id, killer id, killer faction slot —
            // read from the attribution SoA DamageResolver.KillEntity wrote (both -1 for non-combat destroys).
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                bool wasAlive = (_prevFlags[i] & EntityFlags.Alive) != 0;
                bool isAlive  = world.IsAlive(i);
                if (wasAlive && !isAlive)
                {
                    int slot = (int)world.FactionOf[i] - 1; // Player1=1 → slot 0
                    AddBaseEvent("unit_dies", slot, 0, null,
                        p0: i, p1: world.KillerOf[i], p2: world.KillerFactionOf[i]);
                }
            }

            // Building completions (was under construction → now done).
            for (int i = 0; i < _buildings.Count; i++)
            {
                bool wasDone = _prevBuildingDone[i];
                bool isAlive = _buildings.Alive[i];
                bool isDone  = isAlive && _buildings.ConstructionTimer[i] <= Fixed.Zero;

                if (isAlive && !wasDone && isDone)
                    AddBaseEvent("building_completed", (int)_buildings.FactionOf[i] - 1, 0,
                        BuildingTypeNameOf(_buildings.Type[i]));
            }

            // Timers — decrement each ACTIVE timer and collect expiries in CREATION-INDEX (declaration) order via the
            // top-level store. Byte-identical to the legacy ScenarioDirector loop (same order, same "fires on the
            // tick it reaches 0"), now that timers live in the folded DslVarTable.
            _expiredTimers.Clear();
            _vars.TimerTickAndCollectExpired(_expiredTimers);
            for (int i = 0; i < _expiredTimers.Count; i++)
                AddBaseEvent("timer_expires", -1, 0, _expiredTimers[i]);

            // Threshold events — polled every tick so triggers can react to sustained states. Story 9.2: iterate
            // every ACTIVE faction (slots 0..ActiveCount-1) rather than the literal first two; a null registry
            // (direct test construction) preserves the historical 2-slot poll.
            for (int slot = 0; slot < (_factionRegistry?.ActiveCount ?? 2); slot++)
            {
                var faction = (Faction)(slot + 1);
                int oreRaw  = _resources.Ore[(int)faction].Raw;
                int units   = CountAlive(world, faction);
                AddBaseEvent("resource_threshold",   slot, oreRaw, null);
                AddBaseEvent("unit_count_threshold", slot, units,  null);
            }

            // ── Story 7.13 — drain the transient sim-event feed the four producer systems pushed THIS tick
            //    (unit_damaged/unit_trained/ability_cast/hero_level), in producer push order (deterministic: ascending
            //    system order, then ascending id within a system), into the base buffer with their typed payloads.
            //    Then clear the feed so the next tick starts fresh (the DeathFeed drain posture). Emitted AFTER the
            //    polled built-ins; a subscribed trigger fires per occurrence exactly like unit_dies. ──
            int simCount = _simEventFeed.Count;
            for (int i = 0; i < simCount; i++)
            {
                int code = _simEventFeed.KindAt(i);
                string kindName = (uint)code < (uint)SimEventKindNames.Length ? SimEventKindNames[code] : "";
                if (kindName.Length == 0) continue; // defensive (unknown code)
                AddBaseEvent(kindName, _simEventFeed.SlotAt(i), 0, null,
                    p0: _simEventFeed.P0At(i), p1: _simEventFeed.P1At(i), p2: _simEventFeed.P2At(i));
            }
            _simEventFeed.Clear();

            // ── Story 7.13 (Arm D) — drain the transient player_chat rail (dequeued THIS tick from the replicated,
            //    tick-stamped DslEvent order) into base "player_chat" occurrences. P0=sender, P1=code (matching
            //    LoadBuiltinFrame's player_chat case); the base occurrence's Slot is the sender faction so EventMatches
            //    gates on it exactly like unit_dies. Cleared same-tick → empty at the checksum boundary (unfolded). ──
            for (int i = 0; i < _pendingChatCount; i++)
                AddBaseEvent("player_chat", _pendingChatSender[i], 0, null,
                    p0: _pendingChatSender[i], p1: _pendingChatCode[i]);
            _pendingChatCount = 0;
        }

        /// <summary>Zero-alloc BuildingType→name (ToString() allocates per call; the enum is byte-backed,
        /// contiguous, and append-only, so Enum.GetNames order == value order).</summary>
        private static string? BuildingTypeNameOf(BuildingType t)
        {
            int i = (int)t;
            return i >= 0 && i < BuildingTypeNames.Length ? BuildingTypeNames[i] : null;
        }

        // ── Cooldown bookkeeping ──────────────────────────────────────────────

        private void TickCooldowns()
        {
            for (int i = 0; i < _triggerCooldown.Length; i++)
                if (_triggerCooldown[i] > 0) _triggerCooldown[i]--;
        }

        // ── Trigger evaluation ────────────────────────────────────────────────

        private void EvaluateTriggers(EntityWorld world)
        {
            // Walk the precomputed total order (Priority desc, then ascending node-id) built once in LoadScenario.
            // ExecuteTopLevel runs in this order, so equal-priority triggers writing shared state resolve last-writer
            // by ascending declaration/node-id, deterministically across peers (AR-16).
            //
            // Story 7.5: the BASE SWEEP keeps the legacy once-per-tick-per-trigger semantics (Block-If parity) —
            // EXCEPT a trigger whose compiled programs read event params, which dispatches once per matching base
            // occurrence in emission order (per-occurrence is opt-in by construction: statically visible at
            // compile, no schema flag, nothing existing changes). Custom-event subscribers never match here (no
            // base event carries a custom type) — they dispatch per-occurrence via the drain.
            for (int idx = 0; idx < _execs.Count; idx++)
            {
                // Story 7.6 — the fuel seatbelt halts the SWEEP at a whole-trigger boundary: the in-flight
                // trigger completed (it charged past the budget mid-run, untorn), and every remaining trigger
                // skips this tick and simply re-evaluates next tick — identically on every peer.
                if (_loopState.FuelExhausted) break;

                TriggerGraph.TriggerExec ex = _execs[idx];
                if (idx < _subscribedEvent.Length && _subscribedEvent[idx] >= 0)   continue; // Story 7.5 — drain-only
                if (!_triggerEnabled.IsEnabled(idx) || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue; // Story 7.13 — the runtime mask is SEEDED from TriggerNode.Enabled at load, so it SUBSUMES the authored flag; gate on the mask alone (dropping the redundant !t.Enabled) so enable_trigger can turn on an authored-disabled trigger
                // Story 7.6 — a trigger whose batched continuation row is still draining is SUPPRESSED in the
                // sweep (it cannot re-fire until the drip and its continuation chain complete). The RowCount
                // bound is the reset-window guard (review P8): SimulationHost.ClearForReset clears LoopState
                // rows while this director's bookkeeping survives until the re-apply's LoadScenario — a tick in
                // that window must treat the stale row index as "no active row", never index cleared storage.
                if (_batchRowOfTrigger[idx] >= 0 && _batchRowOfTrigger[idx] < _loopState.RowCount
                    && _loopState.RowActive(_batchRowOfTrigger[idx])) continue;

                if (idx < _paramReading.Length && _paramReading[idx])
                {
                    // Story 7.5 — per-occurrence base dispatch (param-reading triggers only): emission order —
                    // ascending entity id for deaths. Gates re-checked per dispatch (RunOnce fires at most once
                    // per match; a cooldown armed at fire suppresses the remaining same-tick occurrences).
                    for (int e = 0; e < _baseEventCount; e++)
                    {
                        if (_triggerFired[idx] || _triggerCooldown[idx] > 0) break;
                        // Story 7.6 parity (merge review): a batched row ACTIVATED by an earlier same-tick
                        // occurrence suppresses the remaining occurrences — the drain's per-dispatch re-check;
                        // without it a second occurrence re-fires the chain and re-activates the row, clobbering
                        // the in-flight drip snapshot.
                        if (_batchRowOfTrigger[idx] >= 0 && _batchRowOfTrigger[idx] < _loopState.RowCount
                            && _loopState.RowActive(_batchRowOfTrigger[idx])) break;
                        ref FiredEvent f = ref _baseEvents[e];
                        if (!MatchesAnyDef(ex.Events, in f))                 continue;
                        LoadBuiltinFrame(in f);
                        if (!AllConditionsMet(ex.Conditions, world))         continue;
                        if (!AllExprConditionsPass(idx))                     continue;
                        FireTrigger(idx, ex, world);
                    }
                    _frameCount = 0;
                    continue;
                }

                _frameCount = 0; // legacy path: no dispatch frame (its programs cannot read event params anyway)
                if (!AnyEventMatches(ex.Events))                                    continue;
                if (!AllConditionsMet(ex.Conditions, world))                        continue;
                // Story 7.4: compiled condition-expression programs AND with the legacy conditions above
                // (multi-condition semantics). Pre-checked Bool postfix programs; zero-allocation eval.
                if (!AllExprConditionsPass(idx))                                    continue;

                FireTrigger(idx, ex, world);
            }
        }

        /// <summary>Fire one trigger dispatch: trigger-local scope around the compiled top-level chain (Story 7.3
        /// — exactly as the legacy path), then RunOnce/cooldown arming. Shared by the base sweep AND the
        /// custom-event drain, so the gates behave identically per dispatch (a cooldown armed mid-tick suppresses
        /// same-tick re-dispatch; RunOnce fires at most once).</summary>
        private void FireTrigger(int idx, TriggerGraph.TriggerExec ex, EntityWorld world)
        {
            // Story 7.3: open a trigger-local scope for this firing (allocate/reset trigger-local scratch), run
            // the action chain, then free it — never engine-global, never folded.
            _vars.Enter();
            try { ExecuteTopLevel(idx, world); }
            finally { _vars.Exit(); }

            if (ex.Trigger.RunOnce) _triggerFired[idx] = true;

            int coolTicks = SecondsToTicks(ex.Trigger.CooldownSeconds);
            if (coolTicks > 0) _triggerCooldown[idx] = coolTicks;

            // Story 7.15 — record this fire UNCONDITIONALLY into the non-folded observation buffer, AFTER the folded
            // run-once/cooldown arming above. Pure int increment + ring append; NEVER folded into SimChecksum, so a
            // run with the buffer attached is byte-identical to one without. The tick stamp is the deterministic sim
            // tick this Tick() will publish (the _publishTick source: Publish uses ++_publishTick at the tick
            // boundary, so this in-progress tick is _publishTick + 1). No string/name in the tick — the exec idx is
            // resolved to a human-readable name PRESENTATION-side only.
            _fireLog?.Record(idx, (int)(_publishTick + 1));
        }

        /// <summary>Load the current dispatch frame from a BASE occurrence: only <c>unit_dies</c> carries a
        /// payload (victim / killer / killer_faction — 3 slots); every other built-in has none.</summary>
        private void LoadBuiltinFrame(in FiredEvent f)
        {
            // Story 7.13 — every built-in event carrying a readable payload fills the frame from its FiredEvent
            // P0..P2 slots; the width is the built-in's declared param count. Payload-less kinds (match_start,
            // thresholds, building/timer) leave an empty frame.
            switch (f.Type)
            {
                case "unit_dies":
                    _frameScratch[0] = f.P0; _frameScratch[1] = f.P1; _frameScratch[2] = f.P2; _frameScratch[3] = 0;
                    _frameCount = EventDispatchPlan.UnitDiesParamCount;
                    break;
                case "unit_damaged": // victim, attacker, amount
                    _frameScratch[0] = f.P0; _frameScratch[1] = f.P1; _frameScratch[2] = f.P2; _frameScratch[3] = 0;
                    _frameCount = 3;
                    break;
                case "unit_trained": // unit
                    _frameScratch[0] = f.P0; _frameScratch[1] = 0; _frameScratch[2] = 0; _frameScratch[3] = 0;
                    _frameCount = 1;
                    break;
                case "ability_cast": // caster, ability
                case "hero_level":   // hero, level
                case "player_chat":  // sender, code
                    _frameScratch[0] = f.P0; _frameScratch[1] = f.P1; _frameScratch[2] = 0; _frameScratch[3] = 0;
                    _frameCount = 2;
                    break;
                default:
                    _frameCount = 0;
                    break;
            }
        }

        // ── Story 7.5 — the same-tick FIFO work-list drain ─────────────────────

        /// <summary>Append a same-tick raise occurrence (deterministic drop-newest at
        /// <see cref="EventBounds.MaxSameTickWorkList"/> — the documented world-volume seatbelt).</summary>
        private void AppendWorkItem(int eventIndex, int raiser, int[] paramRaws, int paramCount)
        {
            if (_workCount >= _workList.Length) return; // drop-newest (identical on every peer — same execution order)
            ref FiredEvent ev = ref _workList[_workCount++];
            ev.Type        = CustomEventType;
            ev.CustomIndex = eventIndex;
            ev.Slot        = raiser;
            ev.Numeric     = 0;
            ev.Data        = null;
            ev.P0 = paramCount > 0 ? paramRaws[0] : 0;
            ev.P1 = paramCount > 1 ? paramRaws[1] : 0;
            ev.P2 = paramCount > 2 ? paramRaws[2] : 0;
            ev.P3 = paramCount > 3 ? paramRaws[3] : 0;
        }

        /// <summary>
        /// Drain the same-tick work list AFTER the base sweep: occurrence-major FIFO (seeded with the next-tick
        /// dequeue, appended by raises in execution order), each occurrence dispatched to its subscribed triggers
        /// in the precomputed total order, gates re-checked per dispatch. Handlers never nest — a raise executed
        /// during a dispatch APPENDS and defers to this loop (flat, deterministic, bounded by the load-proven DAG;
        /// <c>_vars.Enter/Exit</c> wraps each dispatch exactly as the base sweep does). Story 7.6 gates apply per
        /// dispatch too: a trigger whose batched row is draining is suppressed, and fuel exhaustion ABANDONS the
        /// entire remaining drain this tick (deterministic — the consumed fuel folds into SimChecksum; dropped
        /// same-tick work items are the same accepted-loss class as the fuel-skipped sweep; already-enqueued
        /// NEXT-tick events are unaffected and dispatch next tick after ResetFuel).
        /// </summary>
        private void DrainWorkList(EntityWorld world)
        {
            while (_workHead < _workCount)
            {
                int cur = _workHead++;
                int evIndex = _workList[cur].CustomIndex;
                if (evIndex < 0 || evIndex >= _eventParamCounts.Length) continue; // defensive (load gate makes this unreachable)
                int pc = _eventParamCounts[evIndex];

                for (int idx = 0; idx < _execs.Count; idx++)
                {
                    // Story 7.6 — the fuel seatbelt, checked per dispatch: exhaustion drops every remaining
                    // same-tick work item for this tick (whole-drain halt, identical on every peer).
                    if (_loopState.FuelExhausted) { _frameCount = 0; return; }

                    if (idx >= _subscribedEvent.Length || _subscribedEvent[idx] != evIndex) continue;
                    TriggerGraph.TriggerExec ex = _execs[idx];
                    if (!_triggerEnabled.IsEnabled(idx) || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue; // Story 7.13 — the runtime mask is SEEDED from TriggerNode.Enabled at load, so it SUBSUMES the authored flag; gate on the mask alone (dropping the redundant !t.Enabled) so enable_trigger can turn on an authored-disabled trigger
                    // Story 7.6 — batched-row suppression, exactly the sweep's check (incl. the reset-window
                    // RowCount bound): a subscriber whose drip is still draining cannot re-fire.
                    if (_batchRowOfTrigger[idx] >= 0 && _batchRowOfTrigger[idx] < _loopState.RowCount
                        && _loopState.RowActive(_batchRowOfTrigger[idx])) continue;

                    // Load the occurrence's payload as the dispatch frame (per trigger — an earlier handler's
                    // raises only wrote the separate raise scratch, but reloading keeps this trivially correct).
                    _frameScratch[0] = _workList[cur].P0;
                    _frameScratch[1] = _workList[cur].P1;
                    _frameScratch[2] = _workList[cur].P2;
                    _frameScratch[3] = _workList[cur].P3;
                    _frameCount = pc;

                    if (!AllConditionsMet(ex.Conditions, world)) continue;
                    if (!AllExprConditionsPass(idx))             continue;
                    FireTrigger(idx, ex, world);
                }
            }
            _frameCount = 0;
        }

        /// <summary>
        /// Convert a <see cref="Fixed"/> duration in seconds to whole sim ticks WITHOUT overflowing the Fixed
        /// multiply (64-bit intermediate). The single seconds→ticks boundary — it owns <c>TICKS_PER_SECOND</c>, so
        /// the Godot-free, Core-boundary-free <see cref="DslVarTable"/> receives integer ticks only (AC2/AR-14).
        /// </summary>
        private static int SecondsToTicks(Fixed seconds) =>
            (int)(((long)seconds.Raw * SimulationLoop.TICKS_PER_SECOND) >> Fixed.FRACTIONAL_BITS);

        // ── Snapshot update ───────────────────────────────────────────────────

        private void UpdateSnapshots(EntityWorld world)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                _prevFlags[i] = world.Flags[i];

            for (int i = 0; i < _buildings.Count; i++)
            {
                _prevBuildingDone[i]  = _buildings.Alive[i]
                    && _buildings.ConstructionTimer[i] <= Fixed.Zero;
            }
        }

        // ── Event matching ────────────────────────────────────────────────────

        private bool AnyEventMatches(EventNode[] evDefs)
        {
            foreach (var def in evDefs)
                for (int e = 0; e < _baseEventCount; e++)
                    if (EventMatches(def, in _baseEvents[e])) return true;
            return false;
        }

        /// <summary>One base occurrence against a trigger's event defs (the per-occurrence dispatch predicate).</summary>
        private static bool MatchesAnyDef(EventNode[] evDefs, in FiredEvent f)
        {
            foreach (var def in evDefs)
                if (EventMatches(def, in f)) return true;
            return false;
        }

        private static bool EventMatches(EventNode def, in FiredEvent f)
        {
            if (def.Kind != f.Type) return false;
            switch (def.Kind)
            {
                case "match_start":
                    return true;
                case "unit_dies":
                    return f.Slot == def.Faction;
                case "building_completed":
                    if (f.Slot != def.Faction) return false;
                    return string.IsNullOrEmpty(def.BuildingType) || f.Data == def.BuildingType;
                case "timer_expires":
                    return string.IsNullOrEmpty(def.TimerName) || f.Data == def.TimerName;
                case "resource_threshold":
                    if (f.Slot != def.Faction) return false;
                    return Compare(Fixed.FromRaw(f.Numeric), def.Amount, def.Operator);
                case "unit_count_threshold":
                    if (f.Slot != def.Faction) return false;
                    return Compare(f.Numeric, def.Count, def.Operator);
                // ── Story 7.13 — the five new built-in event sources match on the occurrence's faction slot (the
                //    victim / trained-unit / caster / hero / sender faction), exactly like unit_dies. ──
                case "unit_damaged":
                case "unit_trained":
                case "ability_cast":
                case "hero_level":
                case "player_chat":
                    return f.Slot == def.Faction;
                default:
                    return false;
            }
        }

        // ── Condition evaluation ──────────────────────────────────────────────

        private bool AllConditionsMet(ConditionNode[] conds, EntityWorld world)
        {
            foreach (var c in conds)
                if (!EvalCondition(c, world)) return false;
            return true;
        }

        /// <summary>Story 7.4 — every compiled condition-expression program of the trigger at <paramref name="idx"/>
        /// must evaluate non-zero (Bool true). ANDed with the legacy <see cref="AllConditionsMet"/> result, matching
        /// multi-condition semantics. No programs (every legacy scenario) ⇒ trivially true. Story 7.5 — programs
        /// evaluate against the CURRENT dispatch frame (event.&lt;param&gt; reads; empty frame for legacy dispatches).</summary>
        private bool AllExprConditionsPass(int idx)
        {
            ExprProgram[] programs = idx < _condPrograms.Length ? _condPrograms[idx] : NoCondPrograms;
            for (int i = 0; i < programs.Length; i++)
                if (programs[i].Eval(_vars, this, _frameScratch, _frameCount) == 0) return false;
            return true;
        }

        private bool EvalCondition(ConditionNode c, EntityWorld world)
        {
            var faction = (Faction)(c.Faction + 1);
            switch (c.Kind)
            {
                case "always":
                    return true;
                case "building_exists":
                {
                    if (string.IsNullOrEmpty(c.BuildingType)) return true;
                    if (!Enum.TryParse<BuildingType>(c.BuildingType, out var bt)) return false;
                    for (int i = 0; i < _buildings.Count; i++)
                        if (_buildings.Alive[i] && _buildings.FactionOf[i] == faction
                            && _buildings.Type[i] == bt
                            && _buildings.ConstructionTimer[i] <= Fixed.Zero)
                            return true;
                    return false;
                }
                case "resource_comparison":
                    return Compare(_resources.Ore[(int)faction], c.Amount, c.Operator);
                case "unit_count":
                    return Compare(CountAlive(world, faction), c.Count, c.Operator);
                case "variable_comparison":
                    if (string.IsNullOrEmpty(c.Variable)) return false;
                    // Story 7.3: read the Int-typed variable through the store. A declared PerPlayer var selects the
                    // player slot via the condition's Faction field; an undeclared name resolves to Global/Int/0
                    // (legacy GetVariable parity).
                    return Compare(_vars.GetInt(c.Variable, c.Faction), c.Value, c.Operator);
                case "unit_in_region":
                    if (!_regions.TryGetIndex(c.RegionId, out int rIdx)) return false;
                    int rhwm = world.HighWaterMark;
                    for (int i = 0; i < rhwm; i++)
                        if (world.IsAlive(i) && world.FactionOf[i] == faction
                            && _regions.Contains(rIdx, world.Position[i]))
                            return true;
                    return false;
                default:
                    return true;
            }
        }

        // ── Action execution ──────────────────────────────────────────────────

        /// <summary>
        /// Story 7.6 — run a fired trigger's TOP-LEVEL compiled chain. A <c>for_each_batched</c> item SNAPSHOTS
        /// (activating its continuation row) and STOPS the chain — the items after it are the continuation,
        /// executed by the drain phase on the completion tick. Everything else executes inline.
        /// </summary>
        private void ExecuteTopLevel(int execIdx, EntityWorld world)
        {
            CompiledItem[] items = execIdx < _items.Length ? _items[execIdx] : NoItems;
            for (int j = 0; j < items.Length; j++)
            {
                if (items[j].BatchRow >= 0)
                {
                    SnapshotBatched(items[j], world);
                    return; // the rest of the chain is the checksummed continuation (runs at drain completion)
                }
                ExecuteItem(items[j], world, anchor: -1);
            }
        }

        /// <summary>Execute a compiled sub-chain (loop body / branch arm) with the given run_effect anchor.</summary>
        private void ExecuteItems(CompiledItem[] items, EntityWorld world, int anchor)
        {
            for (int j = 0; j < items.Length; j++)
                ExecuteItem(items[j], world, anchor);
        }

        /// <summary>
        /// Story 7.6 — execute ONE compiled item. Zero heap allocation: loop snapshots fill the item's
        /// preallocated buffer; recursion depth is bounded by the load-gate nesting cap. Fuel is charged
        /// mirroring the static cost model (action = 1 + its expression op counts, run_effect = embedded node
        /// count, loop/branch entry = 1 + condition ops; Story 7.5: raise_event = 1, its arg-expression ops
        /// deliberately uncharged — the same accepted undercount class as condition expressions).
        /// <paramref name="anchor"/> is the current entity of the nearest enclosing ENTITY-source loop (-1 =
        /// none → run_effect keeps its legacy lowest-id-alive anchor).
        /// </summary>
        private void ExecuteItem(CompiledItem item, EntityWorld world, int anchor)
        {
            switch (item.Node)
            {
                case ForEachNode fe:
                {
                    _loopState.Charge(1);
                    int count = SnapshotForEach(item, fe, world);
                    int iter  = fe.UpTo > 0 && fe.UpTo < count ? fe.UpTo : count;
                    bool entitySource = fe.Source != "array";
                    for (int i = 0; i < iter; i++)
                    {
                        int v = item.Snapshot![i];
                        // The loop variable (a declared TriggerLocal) is written BEFORE each iteration: the
                        // element raw for arrays, the entity id (Int) for entity sources. SetRaw applies the
                        // central Bool 0/1 normalization for Bool-element arrays.
                        if (!string.IsNullOrEmpty(fe.LoopVar))
                            _vars.SetRaw(fe.LoopVar!, 0, v, 0);
                        // run_effect in the body anchors at the CURRENT entity for entity sources; an array
                        // loop leaves the inherited anchor untouched.
                        ExecuteItems(item.Body, world, entitySource ? v : anchor);
                    }
                    break;
                }

                case ForEachBatchedNode:
                    // Unreachable when the load gate ran (top-level only; intercepted by ExecuteTopLevel).
                    // Defensive no-op for a hostile direct caller — never a second drip path.
                    break;

                case BranchNode:
                {
                    _loopState.Charge(1 + (item.Cond?.OpCount ?? 0));
                    bool taken = item.Cond != null && item.Cond.Eval(_vars, this, _frameScratch, _frameCount) != 0;
                    ExecuteItems(taken ? item.Then : item.Else, world, anchor);
                    // Port-0 continuation items follow this one in the parent chain — they always run.
                    break;
                }

                case EffectActionNode effectNode:
                    _loopState.Charge(item.RunEffectCost);
                    RunEffect(effectNode, world, anchor);
                    break;

                case RaiseEventNode:
                {
                    // Story 7.5 — raise_event: evaluate the compiled arg programs against the CURRENT dispatch
                    // frame (a handler may forward event.<param> payloads) into the SEPARATE raise scratch (never
                    // clobbering the frame), then defer: same-tick raises APPEND to the FIFO work list (handlers
                    // never nest — the drain dispatches them flat), next-tick raises ride the checksummed queue
                    // (deterministic drop-newest at capacity). Zero heap allocation. Charges 1 op, matching the
                    // static DslLoopGate cost model.
                    _loopState.Charge(1);
                    EventDispatchPlan.RaiseCompiled? rp = item.Raise;
                    if (rp == null) break; // unreachable for gate/backstop-validated content (fail-safe no-op)
                    int n = rp.ArgPrograms.Length;
                    for (int p = 0; p < n; p++)
                        _raiseScratch[p] = rp.ArgPrograms[p].Eval(_vars, this, _frameScratch, _frameCount);
                    for (int p = n; p < EventBounds.MaxEventParams; p++)
                        _raiseScratch[p] = 0;
                    if (rp.NextTick)
                        _eventQueue.Enqueue(rp.EventIndex, rp.Raiser, _raiseScratch, n);
                    else
                        AppendWorkItem(rp.EventIndex, rp.Raiser, _raiseScratch, n);
                    break;
                }

                case ActionNode a:
                    _loopState.Charge(1 + (item.Value?.OpCount ?? 0) + (item.Index?.OpCount ?? 0));
                    ExecuteLeaf(a, item, world);
                    break;

                // ── Story 7.13 — the sim-side order_units leaf. Charges fuel (sim-affecting, exactly like an
                //    action) and issues the order to every matching alive unit ascending-id via OrderApplier. ──
                case OrderUnitsNode ou:
                    _loopState.Charge(1);
                    ExecuteOrderUnits(ou, world);
                    break;

                // ── Story 7.13 — the PRESENTATION-ONLY leaves. They fire a presentation delegate and NOTHING else:
                //    NO fuel charge (fuel is folded into SimChecksum), NO folded-store write — so the checksum is
                //    byte-identical whether they fire or not. Null delegates (headless) are a clean no-op. ──
                case MoveCameraNode mc:
                    OnMoveCamera?.Invoke(mc.CameraName);
                    break;

                case CinematicModeNode cm:
                    OnCinematicMode?.Invoke(cm.Enabled);
                    break;

                case PlayVfxNode pv:
                    OnPlayVfx?.Invoke(pv.VfxId, pv.X, pv.Z);
                    break;

                // ── Story 7.13 — the weighted exec container. Draws from the SINGLE shared SimRng stream (folded
                //    LAST), sums pre-computed weights, and selects the branch by subtracting down the array. Charges
                //    1 op (the container entry); the taken branch charges its own items. Port-0 continuation items
                //    follow this one in the parent chain (they always run, like a branch). ──
                case RandomChoiceNode:
                {
                    _loopState.Charge(1);
                    ExecuteRandomChoice(item, world, anchor);
                    break;
                }

                // ── Story 7.13 — enable_trigger/disable_trigger: flip the target's FOLDED runtime enabled flag. ──
                case EnableTriggerNode en:
                    _loopState.Charge(1);
                    if (_triggerNodeIdToExec.TryGetValue(en.TargetTriggerId, out int enIdx))
                        _triggerEnabled.Set(enIdx, true);
                    break;

                case DisableTriggerNode dis:
                    _loopState.Charge(1);
                    if (_triggerNodeIdToExec.TryGetValue(dis.TargetTriggerId, out int disIdx))
                        _triggerEnabled.Set(disIdx, false);
                    break;

                // ── Story 7.13 — run_trigger: synchronously run the target trigger's chain in place, depth-capped
                //    (a seatbelt halting at the WHOLE-TRIGGER boundary; self/mutual cycles were rejected at load). ──
                case RunTriggerNode rt:
                    _loopState.Charge(1);
                    ExecuteRunTrigger(rt, world);
                    break;

                // ── Story 7.14 — the three objective action leaves. Each charges 1 op (folded-state write, like an
                //    action) and flips the target objective's reserved Global-Int var via _vars.SetInt (the ordinal
                //    rides the existing v16 DslVarTable fold — no new store, no SimChecksum bump). No string enters
                //    the tick: the reserved var NAME was precomputed at load in _objectiveVarNameById. An id with no
                //    reserved var (the presentation-only default) is a deterministic no-op. ──
                case ShowObjectiveNode so:
                    _loopState.Charge(1);
                    if (_objectiveVarNameById.TryGetValue(so.ObjectiveId, out string? soVar))
                        _vars.SetInt(soVar, 0, (int)ObjectiveState.Active);
                    break;

                case CompleteObjectiveNode co:
                    _loopState.Charge(1);
                    if (_objectiveVarNameById.TryGetValue(co.ObjectiveId, out string? coVar))
                        _vars.SetInt(coVar, 0, (int)ObjectiveState.Complete);
                    break;

                case FailObjectiveNode fo:
                    _loopState.Charge(1);
                    if (_objectiveVarNameById.TryGetValue(fo.ObjectiveId, out string? foVar))
                        _vars.SetInt(foVar, 0, (int)ObjectiveState.Failed);
                    break;
            }
        }

        /// <summary>
        /// Story 7.13 — resolve a fired <c>random_choice</c>: sum weights, draw <c>world.Rng.NextInt(total)</c> (the
        /// SINGLE shared SimRng stream folded LAST in SimChecksum — no second stream, no reorder), and select the
        /// branch by subtracting down the weight array (branch k = the k-th weighted branch). A zero-weight branch is
        /// never selected; a selected empty branch is a deterministic no-op. Total &gt; 0 is guaranteed by the load
        /// gate; a defensive total ≤ 0 draws no branch.
        /// </summary>
        private void ExecuteRandomChoice(CompiledItem item, EntityWorld world, int anchor)
        {
            int total = item.WeightTotal;
            if (total <= 0 || item.Branches.Length == 0) return; // gate-rejected for authored content — defensive
            int draw = world.Rng.NextInt(total);
            for (int k = 0; k < item.Weights.Length; k++)
            {
                int w = item.Weights[k];
                if (draw < w)
                {
                    if (k < item.Branches.Length)
                        ExecuteItems(item.Branches[k], world, anchor);
                    return;
                }
                draw -= w;
            }
            // Unreachable when total > 0 (draw < total ⇒ some branch consumed it); defensive no-op otherwise.
        }

        /// <summary>
        /// Story 7.13 — synchronously execute a target trigger's action chain in place (the <c>run_trigger</c> leaf),
        /// bounded by <see cref="EventBounds.MaxRunTriggerDepth"/>. At the cap the run is a deterministic no-op (halts
        /// at the whole-trigger boundary — never mid-Sequence). Self/mutual run cycles were rejected at load, so the
        /// cap is a pure seatbelt. The target runs inside its OWN fresh TriggerLocal scope (like a normal fire); a
        /// target carrying a for_each_batched snapshots + activates its continuation row exactly as a direct fire.
        /// </summary>
        private void ExecuteRunTrigger(RunTriggerNode rt, EntityWorld world)
        {
            if (_runDepth >= EventBounds.MaxRunTriggerDepth) return; // seatbelt — whole-trigger boundary halt
            if (!_triggerNodeIdToExec.TryGetValue(rt.TargetTriggerId, out int target)) return; // gate-rejected — defensive
            // Story 7.13 (follow-up review, P2): honor the SAME batched-drip suppression every OTHER trigger-entry
            // path enforces (base sweep :1342, same-tick re-dispatch :1357, custom-event drain :1482). A target whose
            // for_each_batched continuation row is still ACTIVE must not be re-entered — a second SnapshotBatched
            // (ExecuteTopLevel :1651) resets the folded _loopState row cursor mid-drain (double-processing / lost
            // continuation). run_trigger was the one entry path that omitted this guard; skip deterministically at the
            // whole-trigger boundary so a still-draining batched target is left to complete its drip.
            if (_batchRowOfTrigger[target] >= 0 && _batchRowOfTrigger[target] < _loopState.RowCount
                && _loopState.RowActive(_batchRowOfTrigger[target])) return;
            // Story 7.13 (follow-up review, P1): a run target is a synchronous GOSUB, NOT an event fire — it has no
            // dispatch frame of its own. Run it at frame 0 (the batched-drain / legacy-fire convention) so its
            // programs read event.<param> as the defined sentinel 0 rather than BLEEDING the calling trigger's
            // dispatch frame (_frameCount/_frameScratch). Save/restore so the caller's remaining chain is unaffected.
            int savedFrame = _frameCount;
            _frameCount = 0;
            _runDepth++;
            _vars.Enter();
            try { ExecuteTopLevel(target, world); }
            finally { _vars.Exit(); _runDepth--; _frameCount = savedFrame; }
        }

        /// <summary>
        /// Story 7.13 — issue <c>order_units</c>: for every alive unit matching the ascending-id selection
        /// (faction filter −1 = any; optional region point-in-rect), apply the order via
        /// <see cref="OrderApplier.ApplyActiveOrder"/> — the SAME command→CommandState mapping a hand-issued order
        /// uses, so it folds through the existing entity/order state (no new checksum fold). An empty selection /
        /// unknown region is a deterministic no-op. Presentation path-request delegates stay null (sim-side issue).
        /// </summary>
        private void ExecuteOrderUnits(OrderUnitsNode ou, EntityWorld world)
        {
            UnitCommand cmd = ou.Command switch
            {
                "move"          => UnitCommand.Move,
                "attack_move"   => UnitCommand.AttackMove,
                "stop"          => UnitCommand.Stop,
                "hold_position" => UnitCommand.HoldPosition,
                _               => UnitCommand.Stop, // unreachable: command is parse-validated to the closed set
            };

            bool useRegion = !string.IsNullOrEmpty(ou.RegionId);
            int rIdx = -1;
            if (useRegion && !_regions.TryGetIndex(ou.RegionId, out rIdx))
                return; // unknown region (gate-rejected for authored content) → no-op

            int tx = ou.X.Raw, tz = ou.Z.Raw;
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (ou.Faction >= 0 && (int)world.FactionOf[i] != ou.Faction + 1) continue;
                if (useRegion && !_regions.Contains(rIdx, world.Position[i])) continue;
                OrderApplier.ApplyActiveOrder(world, i, cmd, tx, tz);
            }
        }

        /// <summary>
        /// Story 7.6 — snapshot the loop's collection AT ENTRY into the item's preallocated buffer, returning
        /// the element count. Arrays copy their live elements; entity sources scan alive units in ASCENDING id
        /// (the SearchAreaEffect sort-snapshot pattern; the scan itself is already ascending so no sort is
        /// needed), applying the faction filter (-1 = any) and, for region_units, the region rect. The scan
        /// stops once the buffer (sized up_to) is full — the lowest ids win, deterministically.
        /// </summary>
        private int SnapshotForEach(CompiledItem item, ForEachNode fe, EntityWorld world)
        {
            int[] buffer = item.Snapshot!;
            if (fe.Source == "array")
                return _vars.ArraySnapshot(fe.ArrayName ?? "", buffer);

            int n = 0;
            bool useRegion = fe.Source == "region_units";
            int rIdx = -1;
            if (useRegion && !_regions.TryGetIndex(fe.RegionId, out rIdx))
                return 0; // unknown region (gate-rejected for authored content) → empty snapshot
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm && n < buffer.Length; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (fe.Faction >= 0 && (int)world.FactionOf[i] != fe.Faction + 1) continue;
                if (useRegion && !_regions.Contains(rIdx, world.Position[i])) continue;
                buffer[n++] = i;
            }
            return n;
        }

        /// <summary>
        /// Story 7.6 — a fired <c>for_each_batched</c>: snapshot ascending alive-unit ids into its preallocated
        /// continuation row (cap <c>DslBounds.MaxBatchSnapshot</c> — lowest ids win) and activate the row. The
        /// body does NOT run on the fire tick; the drain phase drips it from the next tick on, and the trigger
        /// stays suppressed until the drip and its continuation complete.
        /// </summary>
        private void SnapshotBatched(CompiledItem item, EntityWorld world)
        {
            var fb = (ForEachBatchedNode)item.Node;
            // Reset-window guard (review P8): after SimulationHost.ClearForReset clears the LoopState rows, a
            // tick BEFORE the re-apply's LoadScenario could fire this trigger against cleared row storage —
            // skip the snapshot deterministically (no row exists to drain). Unreachable on the normal path
            // (LoadScenario always reconfigures the rows before any tick).
            if (item.BatchRow >= _loopState.RowCount) return;
            _loopState.Charge(1);
            _loopState.BeginSnapshot(item.BatchRow);

            bool useRegion = fb.Source == "region_units";
            int rIdx = -1;
            if (useRegion && !_regions.TryGetIndex(fb.RegionId, out rIdx))
                return; // unknown region → empty snapshot (completes on the next drain tick)
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (fb.Faction >= 0 && (int)world.FactionOf[i] != fb.Faction + 1) continue;
                if (useRegion && !_regions.Contains(rIdx, world.Position[i])) continue;
                if (!_loopState.SnapshotAppend(item.BatchRow, i)) break; // deterministic lowest-id truncation
            }
        }

        /// <summary>The leaf action switch (the 7.3/7.4 semantics unchanged; Story 7.6 adds the three array
        /// actions and reconciles the spawn clamp to the named <see cref="EffectCaps.MaxSpawnCount"/>; Story 7.5
        /// threads the current dispatch frame into every program eval so handler actions read event.&lt;param&gt;).</summary>
        private void ExecuteLeaf(ActionNode a, CompiledItem item, EntityWorld world)
        {
            switch (a.Kind)
            {
                case "spawn_unit":
                    if (!string.IsNullOrEmpty(a.UnitId))
                        // Story 7.6: the runtime SEATBELT is the named structural cap (the validator gate is the
                        // loud reject for authored counts beyond it — no literal 50 remains).
                        OnSpawnUnit?.Invoke(a.UnitId, a.Faction, a.X, a.Z, Math.Min(a.Count, EffectCaps.MaxSpawnCount));
                    break;
                case "display_message":
                    if (!string.IsNullOrEmpty(a.Text))
                        OnDisplayMessage?.Invoke(a.Text, a.Duration);
                    break;
                case "play_sound":
                    if (!string.IsNullOrEmpty(a.SoundId))
                        OnPlaySound?.Invoke(a.SoundId);
                    break;
                case "victory":
                    OnVictory?.Invoke(a.Faction);
                    break;
                case "defeat":
                    OnVictory?.Invoke(1 - a.Faction); // other faction wins
                    break;
                case "create_timer":
                    if (!string.IsNullOrEmpty(a.TimerName) && a.TimerSeconds > Fixed.Zero)
                        // Clamp to >= 1 tick so a sub-frame duration still fires (matches the legacy latency).
                        _vars.TimerSet(a.TimerName, Math.Max(1, SecondsToTicks(a.TimerSeconds)));
                    break;
                case "add_resources":
                {
                    var faction = (Faction)(a.Faction + 1);
                    _resources.AddOre(faction, a.Amount);
                    break;
                }
                case "set_variable":
                    if (!string.IsNullOrEmpty(a.Variable))
                    {
                        // Story 7.4: a compiled RHS program (value-in expression edge) evaluates the typed raw
                        // and writes through SetRaw (Bool targets normalize to 0/1; Fixed raw-exact). Otherwise
                        // the 7.3 literal path is unchanged: PerPlayer selects the player slot via the action's
                        // Faction field; undeclared → Global/Int (legacy SetVariable parity).
                        if (item.Value != null)
                            _vars.SetRaw(a.Variable, a.Faction, item.Value.Eval(_vars, this, _frameScratch, _frameCount), 0);
                        else
                            _vars.SetInt(a.Variable, a.Faction, a.Value);
                    }
                    break;
                // ── Story 7.6 — the array actions (total runtime semantics; the gate guarantees shapes) ──
                case "array_push":
                    if (!string.IsNullOrEmpty(a.Variable) && item.Value != null)
                        _vars.ArrayPush(a.Variable, item.Value.Eval(_vars, this, _frameScratch, _frameCount)); // at capacity → no-op
                    break;
                case "array_set":
                    if (!string.IsNullOrEmpty(a.Variable) && item.Value != null && item.Index != null)
                    {
                        int idx = item.Index.Eval(_vars, this, _frameScratch, _frameCount);
                        _vars.ArraySet(a.Variable, idx, item.Value.Eval(_vars, this, _frameScratch, _frameCount)); // OOB → no-op
                    }
                    break;
                case "array_clear":
                    if (!string.IsNullOrEmpty(a.Variable))
                        _vars.ArrayClear(a.Variable);
                    break;
            }
        }

        /// <summary>
        /// Story 7.3 — execute an embedded <see cref="EffectActionNode"/> (run_effect) via the EXISTING
        /// <see cref="EffectExecutor"/> (no second executor). 7.3 has no target-parameterization on the node (that
        /// is later scope — 7.13 action leaves), so the effect runs against a deterministic anchor: the lowest-id
        /// alive entity (its faction is the caster faction). A world with no alive entity anchors at -1, so the
        /// executor runs but every IsAlive-guarded leaf/SearchArea no-ops. Deterministic (ascending-id anchor,
        /// Fixed-only), so it never perturbs a legacy scenario's checksum (goldens embed no run_effect).
        ///
        /// <para>Story 7.6 — <paramref name="anchorOverride"/>: inside an ENTITY-source loop body the effect
        /// anchors at the CURRENT entity (caster = primary target = that unit); every non-loop run_effect passes
        /// -1 and keeps the legacy lowest-id-alive anchor.</para>
        /// </summary>
        private void RunEffect(EffectActionNode node, EntityWorld world, int anchorOverride = -1)
        {
            if (node.Effect is null) return;

            int anchor;
            if (anchorOverride >= 0)
            {
                // The override is used VERBATIM — a current-loop entity killed by an earlier iteration anchors
                // dead, so the effect's IsAlive-guarded leaves no-op (never silently re-anchored elsewhere).
                anchor = anchorOverride;
            }
            else
            {
                anchor = -1;
                int hwm = world.HighWaterMark;
                for (int i = 0; i < hwm; i++)
                    if (world.IsAlive(i)) { anchor = i; break; }
            }

            Faction anchorFaction = anchor >= 0 ? world.FactionOf[anchor] : Faction.Neutral;
            _effectSpatial.Rebuild(world); // rebuild for SearchArea fan-out (director runs last in the tick)
            var ctx = new EffectContext(world, casterId: anchor, primaryTargetId: anchor, casterFaction: anchorFaction,
                                        _damageTable, spatial: _effectSpatial, _combatEvents, _matchStats,
                                        modifierStore: _modifiers, deaths: _deaths);
            _effectExecutor.Run(node.Effect, in ctx);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int CountAlive(EntityWorld world, Faction faction)
        {
            int n = 0;
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction) n++;
            return n;
        }

        // ≈ the prior 0.01f float tolerance (0.01 × 65536 = 655.36 → 655 raw) so ==/!= behavior is closely preserved.
        private static readonly Fixed CompareEpsilon = Fixed.FromRaw(655);

        private static bool Compare(Fixed a, Fixed b, string op) => op switch
        {
            ">"  => a > b,
            "<"  => a < b,
            ">=" => a >= b,
            "<=" => a <= b,
            "==" => Fixed.Abs(a - b) <  CompareEpsilon,
            "!=" => Fixed.Abs(a - b) >= CompareEpsilon,
            _    => false
        };

        private static bool Compare(int a, int b, string op) => op switch
        {
            ">"  => a > b,
            "<"  => a < b,
            ">=" => a >= b,
            "<=" => a <= b,
            "==" => a == b,
            "!=" => a != b,
            _    => false
        };

        // ── Internal event record ─────────────────────────────────────────────

        /// <summary>
        /// One event occurrence. A MUTABLE struct written in place into the PREALLOCATED base/work-list buffers
        /// (Story 7.5 — the former readonly struct rode a per-tick <c>List&lt;FiredEvent&gt;</c> allocation).
        /// Story 7.5 widens it with the fixed <see cref="EventBounds.MaxEventParams"/> payload slots
        /// (<see cref="P0"/>..<see cref="P3"/>: unit_dies = victim/killer/killer_faction; custom events = the
        /// evaluated raise-arg raws) and the custom-event registry index (<see cref="CustomIndex"/>, -1 for
        /// built-ins; custom occurrences carry the raiser slot in <see cref="Slot"/>). Type/Data are loaded
        /// references or consts — no per-tick string construction.
        /// </summary>
        private struct FiredEvent
        {
            public string  Type;
            public int     Slot;        // -1 = no faction (built-ins); the raiser slot for custom occurrences
            public int     Numeric;     // typed numeric payload: ore raw-Fixed integer, or unit count
            public string? Data;        // string payload: building type, timer name (null when unused)
            public int     CustomIndex; // custom-event registry index (-1 = a built-in event)
            public int     P0, P1, P2, P3; // Story 7.5 payload raws (EventBounds.MaxEventParams slots)
        }
    }
}
