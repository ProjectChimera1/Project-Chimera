#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;                 // Story 7.4 — canonical-order edge iteration in the compile backstop
using ProjectChimera.Combat;       // DamageTable, CombatEventQueue, DeathFeed
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Economy;
using ProjectChimera.Effects;      // EffectExecutor, EffectContext, EffectNode, ModifierStore
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
    /// </summary>
    public class ScenarioDirector : ISimSystem, IExprWorld
    {
        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly BuildingStore _buildings;
        private readonly ResourceStore _resources;

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

        // Story 7.4 — compiled expression programs, indexed by _execs position. Compiled ONCE per LoadScenario
        // (the two-phase contract: compile at load, zero-allocation eval in the tick). _condPrograms[i] are the
        // Bool programs ANDed with the trigger's legacy conditions; _valuePrograms[i][j] is action j's compiled
        // set_variable RHS (null = the legacy literal path). Empty for every expression-free (legacy) scenario,
        // so legacy tick behavior is byte-identical (Block-If parity).
        private ExprProgram[][]  _condPrograms  = Array.Empty<ExprProgram[]>();
        private ExprProgram?[][] _valuePrograms = Array.Empty<ExprProgram?[]>();
        private static readonly ExprProgram[]  NoCondPrograms  = Array.Empty<ExprProgram>();
        private static readonly ExprProgram?[] NoValuePrograms = Array.Empty<ExprProgram?>();

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

        // Per-exec dispatch info (parallel to _execs): the subscribed custom-event index (-1 = built-in events
        // only), the per-occurrence opt-in flag (any compiled program reads event params), and the compiled
        // raise plans (parallel to each exec's Actions; null rows = not a raise action).
        private int[]  _subscribedEvent = Array.Empty<int>();
        private bool[] _paramReading    = Array.Empty<bool>();
        private EventDispatchPlan.RaiseCompiled?[][] _raisePlans = Array.Empty<EventDispatchPlan.RaiseCompiled?[]>();

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

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="eventQueue">Story 7.5 — the cross-tick next-tick event queue. Production
        /// (<c>SimulationHost</c>) passes its owned, checksum-folded instance; a null (direct test construction)
        /// self-owns one — determinism-identical, the caller just cannot fold what it does not hold.</param>
        public ScenarioDirector(BuildingStore buildings, ResourceStore resources, DslVarTable vars,
                                DslEventQueue? eventQueue = null)
        {
            _buildings  = buildings;
            _resources  = resources;
            _vars       = vars;
            _eventQueue = eventQueue ?? new DslEventQueue();
        }

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
            // trigger_graph fails closed at the converter parse (7.3's only graph gate; the authoritative load-time
            // validator is 7.7). The tick WALKS this graph directly, superseding 7.2's ToFlat() lowering.
            //
            // Review (7.4 pass 2): FAILURE-ATOMIC — every throwing step (parse, cycle guard, expression compile)
            // runs against LOCALS before any field is touched, so a caller that catches a located load error keeps
            // the previous scenario's coherent runtime state (pre-7.4, a compile throw could strand half-replaced
            // trigger state whose null program rows then NRE'd on the next Tick).
            TriggerGraph graph = TriggerGraph.FromFlat(scenario.Triggers);
            if (!string.IsNullOrWhiteSpace(scenario.TriggerGraphJson))
                graph.Merge(TriggerGraph.FromJson(scenario.TriggerGraphJson!));
            List<TriggerGraph.TriggerExec> execs = graph.BuildExecutionOrder();

            // Story 7.4/7.5 — compile every condition-expression, set_variable value-expression, and raise-arg
            // program ONCE, and run the FULL 7.5 load-time backstop (registry validation, DAG proof + EventBounds
            // caps via the shared EventDispatchPlan routine, single-subscription rules). A failure throws a located
            // JsonException, consistent with the cycle-guard posture above (the ScenarioValidator gate rejects the
            // same errors located BEFORE any apply; this is the fail-closed backstop for direct LoadScenario
            // callers). Expression/event-free scenarios compile nothing (legacy parity).
            CompiledPrograms compiled = CompileExpressionPrograms(scenario, graph, execs);

            // Story 7.5 — size the preallocated base-event buffer to the worst-case per-tick emission: every
            // entity dying (MAX_ENTITIES) + every building completing (MAX_BUILDINGS) + every DISTINCT timer name
            // expiring (declared + create_timer action names — timer identity is static text, so this bound is
            // load-computable) + the 4 polled threshold events + match_start.
            var timerNames = new HashSet<string>(StringComparer.Ordinal);
            if (scenario.Timers != null)
                foreach (ScenarioTimer t in scenario.Timers)
                    if (!string.IsNullOrEmpty(t.Name)) timerNames.Add(t.Name);
            foreach (TriggerGraph.TriggerExec ex in execs)
                foreach (NodeBase act in ex.Actions)
                    if (act is ActionNode { Kind: "create_timer" } ct && !string.IsNullOrEmpty(ct.TimerName))
                        timerNames.Add(ct.TimerName!);
            var baseEvents = new FiredEvent[EntityWorld.MAX_ENTITIES + BuildingStore.MAX_BUILDINGS + timerNames.Count + 5];

            // Story 7.3: the typed/scoped variable + timer store declarations. The seconds→ticks conversion happens
            // HERE at the Core boundary (SecondsToTicks owns TICKS_PER_SECOND) so the table receives integer ticks
            // only. Declared timers start active at their tick count (I/O matrix).
            var varDecls = new List<DslVarDecl>();
            if (scenario.Variables != null)
                foreach (ScenarioVariable v in scenario.Variables)
                    varDecls.Add(new DslVarDecl(v.Name, v.Type, v.Scope, ScopeInitialRaw(v.Type, v.Initial)));
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
            _condPrograms    = compiled.CondPrograms;
            _valuePrograms   = compiled.ValuePrograms;

            // Story 7.5 — commit the custom-event runtime: registry references, per-exec dispatch info, compiled
            // raise plans, the sized base buffer, and a clean queue/work-list (a re-load never inherits pending
            // feedback from the previous scenario).
            _eventNames       = compiled.EventNames;
            _eventParamCounts = compiled.EventParamCounts;
            _subscribedEvent  = compiled.SubscribedEvent;
            _paramReading     = compiled.ParamReading;
            _raisePlans       = compiled.RaisePlans;
            _baseEvents       = baseEvents;
            _baseEventCount   = 0;
            _workHead = _workCount = 0;
            _frameCount = 0;
            _eventQueue.Clear();

            _vars.InitFromDeclarations(varDecls, timerDecls);

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

        /// <summary>The load-compiled program set (Story 7.4 expressions + Story 7.5 custom-event dispatch info),
        /// returned as LOCALS so a mid-compile throw never strands half-replaced director state (failure-atomic).</summary>
        private sealed class CompiledPrograms
        {
            public ExprProgram[][]  CondPrograms  = Array.Empty<ExprProgram[]>();
            public ExprProgram?[][] ValuePrograms = Array.Empty<ExprProgram?[]>();
            public string[] EventNames            = Array.Empty<string>();
            public int[]    EventParamCounts      = Array.Empty<int>();
            public int[]    SubscribedEvent       = Array.Empty<int>();
            public bool[]   ParamReading          = Array.Empty<bool>();
            public EventDispatchPlan.RaiseCompiled?[][] RaisePlans = Array.Empty<EventDispatchPlan.RaiseCompiled?[]>();
        }

        /// <summary>
        /// Story 7.4/7.5 — compile every expression subgraph the execution view surfaced (condition-in roots,
        /// set_variable value-in roots, and raise-arg roots) into <see cref="ExprProgram"/>s held per trigger, via
        /// <see cref="ExprCompiler"/> against the scenario's declared-variable map and (7.5) each trigger's
        /// event-parameter map, and run the full 7.5 load-time backstop through the SHARED
        /// <see cref="EventDispatchPlan"/> routine (registry rules, single-subscription, raise-arg arity/type/wire,
        /// DAG proof + EventBounds caps). Located <see cref="System.Text.Json.JsonException"/> on any reject.
        /// Pure function of its inputs (review, 7.4 pass 2): fills and returns LOCAL arrays so a mid-compile throw
        /// never strands half-replaced director state — the caller commits them only after everything compiled.
        /// </summary>
        private static CompiledPrograms CompileExpressionPrograms(
            ScenarioData scenario, TriggerGraph graph, List<TriggerGraph.TriggerExec> execs)
        {
            // GATE-ONLY checks (deliberately NOT mirrored in this backstop): the engine-ceiling faction bound on
            // slotted expr_var reads (CheckFactionSlot, ceiling Faction.Player4) is an authoring-policy rule the
            // ScenarioValidator owns — the compiler's structural [0, DslVarTable.PlayerSlots) bound plus the
            // CountAlive slot guard keep the runtime safe without it. Every OTHER expression consumer-edge check
            // the gate applies (the compile rulebook, Bool condition roots, single value-in edge, wire = type,
            // the value-in edge-shape rejects, duplicate declarations) is re-run below, so a direct LoadScenario
            // caller fails closed identically.
            var condPrograms  = new ExprProgram[execs.Count][];
            var valuePrograms = new ExprProgram?[execs.Count][];

            // Legacy parity guard: expression-free graphs skip every NEW check below (a duplicate-declaration
            // direct-load, however malformed, must keep its exact pre-7.4 load behavior — the Block-If).
            // Story 7.5: any custom-event machinery (declared events, raise/subscription/param-read nodes) also
            // opts INTO the stricter checks — new machinery, native strictness; legacy content unaffected.
            bool anyExpr = scenario.CustomEvents is { Length: > 0 };
            foreach (NodeBase n in graph.Nodes)
                if (ExprCompiler.IsExprNode(n) || n is RaiseEventNode
                    || (n is EventNode c && c.Kind == "custom_event"))
                { anyExpr = true; break; }

            // Declared name → (type, scope), the same map shape the validator gate builds. WITH expressions
            // present, duplicates reject like the gate (review, 7.4 pass 2): a last-declaration-wins map would
            // type expressions against one slot while DslVarTable.Resolve reads another (PerPlayer-first),
            // silently confusing typed raws.
            var declMap = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
            if (scenario.Variables != null)
                foreach (ScenarioVariable v in scenario.Variables)
                    if (!string.IsNullOrWhiteSpace(v.Name))
                    {
                        if (!declMap.TryAdd(v.Name, (v.Type, v.Scope)) && anyExpr)
                            throw new System.Text.Json.JsonException(
                                $"scenario variable '{v.Name}' is declared more than once.");
                    }

            // ── Story 7.5 — the SHARED load-time analysis (the exact routine the validator gate runs): registry
            //    validation, custom_event/raise_event usage rules, single-subscription, raise-arg edge shape +
            //    compile, and the same-tick DAG proof + EventBounds caps. Located throw = the fail-closed backstop.
            //    It also yields each trigger's event-parameter map, which the 7.4 compile passes below need so a
            //    handler's condition/value expressions can read event.<param>. ──
            if (!EventDispatchPlan.TryBuild(scenario.CustomEvents, graph, execs, declMap,
                    maxRaiserSlotExclusive: (int)Faction.Player4, out EventDispatchPlan? evPlan, out string? evErr))
                throw new System.Text.Json.JsonException(evErr);
            EventDispatchPlan plan = evPlan!;

            // ── Per-edge parity scan (review, 7.4 pass 2 — mirrors the gate's consumer-edge loop over ALL data
            //    edges, not just exec-surfaced actions): a value-in edge whose src is NOT an expression node maps
            //    to root -1 in BuildExecutionOrder and would otherwise be SILENTLY ignored (the literal Value wins
            //    against the authored wiring); a value-in edge onto a run_effect or an action outside every exec
            //    chain would escape the per-exec loop below entirely. Canonical tuple order → deterministic
            //    first-fail, matching the gate. Story 7.5: raise-arg edges (Dst = a RaiseEventNode) are fully
            //    checked by the plan above and skipped here. ──
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
                    if (act.Kind != "set_variable")
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: a value-in expression edge is only allowed on a set_variable action (kind '{act.Kind}').");
                    if (string.IsNullOrEmpty(act.Variable))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: a set_variable with a value expression needs a target variable.");
                    if (!seenValueInPorts.Add(act.Id))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: multiple value-in expression edges (forked; exactly one allowed).");
                    if (!ExprCompiler.TryCompile(graph, de.Src, declMap, inCondition: false, plan.ParamMapFor(act.Id),
                            out ExprProgram? vp, out string? vErr))
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
                        if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: true, exMap, out ExprProgram? p, out string? err))
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

                bool anyValue = false;
                for (int j = 0; j < ex.ActionValueExprRoots.Length; j++)
                    if (ex.ActionValueExprRoots[j] >= 0) { anyValue = true; break; }
                if (!anyValue)
                {
                    valuePrograms[i] = NoValuePrograms;
                    continue;
                }

                var values = new ExprProgram?[ex.Actions.Length];
                for (int j = 0; j < ex.Actions.Length; j++)
                {
                    int root = ex.ActionValueExprRoots[j];
                    if (root < 0) continue;
                    if (ex.Actions[j] is not ActionNode act || act.Kind != "set_variable" || string.IsNullOrEmpty(act.Variable))
                        throw new System.Text.Json.JsonException(
                            $"trigger '{ex.Trigger.Name}' action node {ex.Actions[j].Id}: a value-in expression edge is only allowed on a set_variable action with a target variable.");
                    if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: false, exMap, out ExprProgram? p, out string? err))
                        throw new System.Text.Json.JsonException($"trigger '{ex.Trigger.Name}' set_variable value expression: {err}");
                    DslValueType target = declMap.TryGetValue(act.Variable!, out var decl) ? decl.Type : DslValueType.Int;
                    if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                        throw new System.Text.Json.JsonException(
                            $"trigger '{ex.Trigger.Name}' set_variable target '{act.Variable}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                    if (p!.ResultType != target)
                        throw new System.Text.Json.JsonException(
                            $"trigger '{ex.Trigger.Name}' set_variable value expression (expr node {root}): result type {p.ResultType} does not match target variable '{act.Variable}' ({target}).");
                    values[j] = p;
                    paramReading[i] |= p!.ReadsEventParams; // Story 7.5 — per-occurrence opt-in is statically visible
                }
                valuePrograms[i] = values;
            }

            // Story 7.5 — stamp the per-occurrence flag onto the exec view (spec surface) and hand everything back
            // as locals for the failure-atomic commit.
            for (int i = 0; i < execs.Count; i++)
                execs[i].ReadsEventParams = paramReading[i];

            return new CompiledPrograms
            {
                CondPrograms     = condPrograms,
                ValuePrograms    = valuePrograms,
                EventNames       = plan.EventNames,
                EventParamCounts = plan.EventParamCounts,
                SubscribedEvent  = plan.SubscribedEvent,
                ParamReading     = paramReading,
                RaisePlans       = plan.Raises,
            };
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
                if (_execs.Count == 0)
                {
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
                // before base-sweep raises: they were enqueued first, and the base sweep APPENDS behind them), then
                // clear the queue — this tick's next_tick raises re-fill it for the next tick's seed.
                _workHead = 0;
                _workCount = 0;
                int pending = _eventQueue.Count;
                for (int i = 0; i < pending && _workCount < _workList.Length; i++)
                {
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

                CollectEvents(world);
                TickCooldowns();
                EvaluateTriggers(world);   // the legacy base sweep (semantics preserved); raises append to the work list
                DrainWorkList(world);      // per-occurrence custom dispatch, FIFO occurrence-major
                UpdateSnapshots(world);
            }
            finally
            {
                // Review (7.4 pass 2): the seam is scoped to THE TICK — don't retain the world reference between
                // ticks (any future between-tick evaluation entry point, e.g. an editor preview, would otherwise
                // scan a world the director no longer owns; LoadScenario's clear covers only the reset path).
                _exprWorld = null;
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

            // Threshold events — polled every tick so triggers can react to sustained states.
            for (int slot = 0; slot < 2; slot++)
            {
                var faction = (Faction)(slot + 1);
                int oreRaw  = _resources.Ore[(int)faction].Raw;
                int units   = CountAlive(world, faction);
                AddBaseEvent("resource_threshold",   slot, oreRaw, null);
                AddBaseEvent("unit_count_threshold", slot, units,  null);
            }
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
            // ExecuteActions runs in this order, so equal-priority triggers writing shared state resolve last-writer
            // by ascending declaration/node-id, deterministically across peers (AR-16).
            //
            // Story 7.5: the BASE SWEEP keeps the legacy once-per-tick-per-trigger semantics (Block-If parity) —
            // EXCEPT a trigger whose compiled programs read event params, which dispatches once per matching base
            // occurrence in emission order (per-occurrence is opt-in by construction: statically visible at
            // compile, no schema flag, nothing existing changes). Custom-event subscribers never match here (no
            // base event carries a custom type) — they dispatch per-occurrence via the drain.
            for (int idx = 0; idx < _execs.Count; idx++)
            {
                TriggerGraph.TriggerExec ex = _execs[idx];
                TriggerNode t = ex.Trigger;
                if (_subscribedEvent.Length > idx && _subscribedEvent[idx] >= 0)    continue; // drain-only
                if (!t.Enabled || _triggerFired[idx] || _triggerCooldown[idx] > 0)  continue;

                if (idx < _paramReading.Length && _paramReading[idx])
                {
                    // Per-occurrence base dispatch (param-reading triggers only): emission order — ascending
                    // entity id for deaths. Gates re-checked per dispatch (RunOnce fires at most once per match;
                    // a cooldown armed at fire suppresses the remaining same-tick occurrences).
                    for (int e = 0; e < _baseEventCount; e++)
                    {
                        if (_triggerFired[idx] || _triggerCooldown[idx] > 0) break;
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

        /// <summary>Fire one trigger dispatch: trigger-local scope around the action chain (Story 7.3 — exactly as
        /// the legacy path), then RunOnce/cooldown arming. Shared by the base sweep AND the custom-event drain, so
        /// the gates behave identically per dispatch.</summary>
        private void FireTrigger(int idx, TriggerGraph.TriggerExec ex, EntityWorld world)
        {
            _vars.Enter();
            try { ExecuteActions(idx, ex, world); }
            finally { _vars.Exit(); }

            if (ex.Trigger.RunOnce) _triggerFired[idx] = true;

            int coolTicks = SecondsToTicks(ex.Trigger.CooldownSeconds);
            if (coolTicks > 0) _triggerCooldown[idx] = coolTicks;
        }

        /// <summary>Load the current dispatch frame from a BASE occurrence: only <c>unit_dies</c> carries a
        /// payload (victim / killer / killer_faction — 3 slots); every other built-in has none.</summary>
        private void LoadBuiltinFrame(in FiredEvent f)
        {
            if (f.Type == "unit_dies")
            {
                _frameScratch[0] = f.P0;
                _frameScratch[1] = f.P1;
                _frameScratch[2] = f.P2;
                _frameScratch[3] = 0;
                _frameCount = EventDispatchPlan.UnitDiesParamCount;
            }
            else
            {
                _frameCount = 0;
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
        /// <c>_vars.Enter/Exit</c> wraps each dispatch exactly as the base sweep does).
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
                    if (idx >= _subscribedEvent.Length || _subscribedEvent[idx] != evIndex) continue;
                    TriggerGraph.TriggerExec ex = _execs[idx];
                    TriggerNode t = ex.Trigger;
                    if (!t.Enabled || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue;

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

        private void ExecuteActions(int idx, TriggerGraph.TriggerExec ex, EntityWorld world)
        {
            NodeBase[] actions = ex.Actions;
            ExprProgram?[] valuePrograms = idx < _valuePrograms.Length ? _valuePrograms[idx] : NoValuePrograms;
            EventDispatchPlan.RaiseCompiled?[]? raisePlans = idx < _raisePlans.Length ? _raisePlans[idx] : null;

            for (int j = 0; j < actions.Length; j++)
            {
                NodeBase node = actions[j];
                if (node is EffectActionNode effectNode)
                {
                    RunEffect(effectNode, world);
                    continue;
                }

                if (node is RaiseEventNode)
                {
                    // Story 7.5 — raise_event: evaluate the compiled arg programs against the CURRENT dispatch
                    // frame (a handler may forward event.<param> payloads) into the SEPARATE raise scratch (never
                    // clobbering the frame), then defer: same-tick raises APPEND to the FIFO work list (handlers
                    // never nest — the drain dispatches them flat), next-tick raises ride the checksummed queue
                    // (deterministic drop-newest at capacity). Zero heap allocation.
                    EventDispatchPlan.RaiseCompiled? rp = raisePlans != null && j < raisePlans.Length ? raisePlans[j] : null;
                    if (rp == null) continue; // unreachable for gate/backstop-validated content (fail-safe no-op)
                    int n = rp.ArgPrograms.Length;
                    for (int p = 0; p < n; p++)
                        _raiseScratch[p] = rp.ArgPrograms[p].Eval(_vars, this, _frameScratch, _frameCount);
                    for (int p = n; p < EventBounds.MaxEventParams; p++)
                        _raiseScratch[p] = 0;
                    if (rp.NextTick)
                        _eventQueue.Enqueue(rp.EventIndex, rp.Raiser, _raiseScratch, n);
                    else
                        AppendWorkItem(rp.EventIndex, rp.Raiser, _raiseScratch, n);
                    continue;
                }

                var a = (ActionNode)node;
                switch (a.Kind)
                {
                    case "spawn_unit":
                        if (!string.IsNullOrEmpty(a.UnitId))
                            OnSpawnUnit?.Invoke(a.UnitId, a.Faction, a.X, a.Z, Math.Min(a.Count, 50));
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
                            ExprProgram? rhs = j < valuePrograms.Length ? valuePrograms[j] : null;
                            if (rhs != null)
                                _vars.SetRaw(a.Variable, a.Faction, rhs.Eval(_vars, this, _frameScratch, _frameCount), 0);
                            else
                                _vars.SetInt(a.Variable, a.Faction, a.Value);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Story 7.3 — execute an embedded <see cref="EffectActionNode"/> (run_effect) via the EXISTING
        /// <see cref="EffectExecutor"/> (no second executor). 7.3 has no target-parameterization on the node (that
        /// is later scope — 7.13 action leaves), so the effect runs against a deterministic anchor: the lowest-id
        /// alive entity (its faction is the caster faction). A world with no alive entity anchors at -1, so the
        /// executor runs but every IsAlive-guarded leaf/SearchArea no-ops. Deterministic (ascending-id anchor,
        /// Fixed-only), so it never perturbs a legacy scenario's checksum (goldens embed no run_effect).
        /// </summary>
        private void RunEffect(EffectActionNode node, EntityWorld world)
        {
            if (node.Effect is null) return;

            int anchor = -1;
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i)) { anchor = i; break; }

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
