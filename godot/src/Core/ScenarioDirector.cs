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
            public int[]? Snapshot;            // for_each loop-entry snapshot buffer (UpTo / array capacity)
            public int RunEffectCost;          // run_effect: embedded effect-node count (the fuel charge)
            public int BatchRow = -1;          // for_each_batched: its DslLoopState continuation row
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

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="loopState">Story 7.6 — the checksummed loop/fuel state (SimulationHost passes its shared
        /// instance so SimChecksum folds the same object; a null — direct test constructors — gets a private one).</param>
        public ScenarioDirector(BuildingStore buildings, ResourceStore resources, DslVarTable vars,
            DslLoopState? loopState = null)
        {
            _buildings = buildings;
            _resources = resources;
            _vars      = vars;
            _loopState = loopState ?? new DslLoopState();
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
        /// Story 7.8 — wire the presentation read rail. <c>SimulationHost</c> passes its shared
        /// <see cref="DslVarReadback"/> so that at each tick boundary the director publishes a version-stamped COPY of
        /// the final post-tick variable state into it (presentation-only; NEVER folded into <c>SimChecksum</c>). Its
        /// declarations are (re)initialized from the same <c>ScenarioData</c> at <see cref="LoadScenario"/>.
        /// </summary>
        public void SetReadback(DslVarReadback? readback) => _readback = readback;

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
                string? loopErr = DslLoopGate.CheckGraph(graph, execs, loopDeclMap, arrayDecls,
                    id => declaredRegions.Contains(id));
                if (loopErr != null) throw new System.Text.Json.JsonException(loopErr);
            }

            // Story 7.8 — the custom-UI widget-tree gate (caps/dup-ids/anchor/depth/bind-resolve+type-match), the
            // SAME shared CustomUiGate the ScenarioValidator runs, applied UNCONDITIONALLY here as the fail-closed
            // backstop for direct LoadScenario callers (parity by construction — the GraphStructureGate posture).
            // A null tree returns null (no-op). Runs against LOCALS before any field commit (failure atomicity).
            string? uiErr = CustomUiGate.Check(scenario.CustomUi, loopDeclMap, arrayDecls);
            if (uiErr != null) throw new System.Text.Json.JsonException(uiErr);

            // Story 7.4 — compile every condition-expression and set_variable value-expression ONCE (two-phase
            // contract). A compile failure throws a located JsonException, consistent with the cycle-guard posture
            // above (the ScenarioValidator gate rejects the same errors located BEFORE any apply; this is the
            // fail-closed backstop for direct LoadScenario callers). Expression-free scenarios compile nothing.
            // Story 7.6 — the per-item compile now also builds the nested CompiledItem execution tree (loop
            // snapshot buffers, branch/array programs, run_effect fuel costs).
            (ExprProgram[][] condPrograms, CompiledItem[][] compiledItems) =
                CompileExpressionPrograms(scenario, graph, execs, arrayDecls);

            // Story 7.6 — collect the for_each_batched continuation rows from the TOP-LEVEL compiled chains, in
            // ascending node-id order (the drain phase's total order across rows). All locals — committed below.
            var batchedRows = new List<(int NodeId, int ExecIdx, int ItemPos)>();
            for (int i = 0; i < compiledItems.Length; i++)
                for (int j = 0; j < compiledItems[i].Length; j++)
                    if (compiledItems[i][j].Node is ForEachBatchedNode fbNode)
                        batchedRows.Add((fbNode.Id, i, j));
            batchedRows.Sort((a, b) => a.NodeId.CompareTo(b.NodeId));

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
            _condPrograms    = condPrograms;
            _items           = compiledItems;

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
                compiledItems[batchedRows[r].ExecIdx][batchedRows[r].ItemPos].BatchRow = r;
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

        /// <summary>
        /// Story 7.4 — compile every expression subgraph the execution view surfaced (condition-in roots and
        /// set_variable value-in roots) into <see cref="ExprProgram"/>s held per trigger, via <see cref="ExprCompiler"/>
        /// against the scenario's declared-variable map. Located <see cref="System.Text.Json.JsonException"/> on any
        /// compile reject (type mismatch, literal-zero divisor, caps, scope misuse — the full 7.4 rulebook).
        /// Pure function of its inputs (review, 7.4 pass 2): fills and returns LOCAL arrays so a mid-compile throw
        /// never strands half-replaced director state — the caller commits them only after everything compiled.
        /// </summary>
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

        private static (ExprProgram[][] CondPrograms, CompiledItem[][] Items) CompileExpressionPrograms(
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
            bool anyExpr = false;
            foreach (NodeBase n in graph.Nodes)
                if (ExprCompiler.IsExprNode(n)) { anyExpr = true; break; }

            // Declared name → (type, scope), the same map shape the validator gate builds. WITH expressions
            // present, duplicates reject like the gate (review, 7.4 pass 2): a last-declaration-wins map would
            // type expressions against one slot while DslVarTable.Resolve reads another (PerPlayer-first),
            // silently confusing typed raws.
            Dictionary<string, (DslValueType Type, VarScope Scope)> declMap = BuildDeclMap(scenario, requireUnique: anyExpr);

            // ── Per-edge parity scan (review, 7.4 pass 2 — mirrors the gate's consumer-edge loop over ALL data
            //    edges, not just exec-surfaced actions): a value-in edge whose src is NOT an expression node maps
            //    to root -1 in BuildExecutionOrder and would otherwise be SILENTLY ignored (the literal Value wins
            //    against the authored wiring); a value-in edge onto a run_effect or an action outside every exec
            //    chain would escape the per-exec loop below entirely. Canonical tuple order → deterministic
            //    first-fail, matching the gate. ──
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
                    if (!ExprCompiler.TryCompile(graph, de.Src, declMap, inCondition: false, out ExprProgram? vp, out string? vErr, arrayDecls))
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

            for (int i = 0; i < execs.Count; i++)
            {
                TriggerGraph.TriggerExec ex = execs[i];

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
                        if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: true, out ExprProgram? p, out string? err, arrayDecls))
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
                    }
                    condPrograms[i] = programs;
                }

                // Story 7.6 — compile the trigger's NESTED execution tree (leaf value/index programs, branch
                // conditions, loop snapshot buffers, run_effect fuel costs). For a container-free legacy chain
                // this reduces to the flat 7.4 value-program compile item-for-item (same rejects, same messages).
                compiledItems[i] = CompileItems(graph, ex.Items, declMap, arrayDecls, ex.Trigger.Name, depth: 1);
            }

            return (condPrograms, compiledItems);
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
        /// </summary>
        private static CompiledItem[] CompileItems(TriggerGraph graph, TriggerGraph.ExecItem[] items,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls,
            string triggerName, int depth)
        {
            if (items.Length == 0) return NoItems;
            var compiled = new CompiledItem[items.Length];
            for (int j = 0; j < items.Length; j++)
            {
                TriggerGraph.ExecItem it = items[j];
                var ci = new CompiledItem { Node = it.Node };

                // Review P9 — the recursion seatbelt: reject located BEFORE recursing into a container's sub-chain.
                if ((it.Node is ForEachNode || it.Node is ForEachBatchedNode || it.Node is BranchNode)
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
                        ci.Body = CompileItems(graph, it.Body, declMap, arrayDecls, triggerName, depth + 1);
                        break;
                    }

                    case ForEachBatchedNode:
                        ci.Body = CompileItems(graph, it.Body, declMap, arrayDecls, triggerName, depth + 1);
                        break;

                    case BranchNode br:
                    {
                        if (it.CondExprRoot < 0)
                            throw new System.Text.Json.JsonException(
                                $"trigger '{triggerName}' branch node {br.Id}: requires a Bool expression wired into its condition-in data port.");
                        // Branch conditions compile inCondition:false — they evaluate INSIDE the trigger-local
                        // scope, so TriggerLocal/loop-var reads are legal (unlike the trigger condition-in).
                        if (!ExprCompiler.TryCompile(graph, it.CondExprRoot, declMap, inCondition: false,
                                out ExprProgram? cp, out string? cErr, arrayDecls))
                            throw new System.Text.Json.JsonException($"trigger '{triggerName}' branch condition: {cErr}");
                        if (cp!.ResultType != DslValueType.Bool)
                            throw new System.Text.Json.JsonException(
                                $"trigger '{triggerName}' branch node {br.Id}: the condition expression must evaluate to Bool, got {cp.ResultType}.");
                        ci.Cond = cp;
                        ci.Then = CompileItems(graph, it.Then, declMap, arrayDecls, triggerName, depth + 1);
                        ci.Else = CompileItems(graph, it.Else, declMap, arrayDecls, triggerName, depth + 1);
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
                            if (!ExprCompiler.TryCompile(graph, it.ValueExprRoot, declMap, inCondition: false,
                                    out ExprProgram? vp, out string? vErr, arrayDecls))
                                throw new System.Text.Json.JsonException($"trigger '{triggerName}' {act.Kind} value expression: {vErr}");
                            ci.Value = vp;
                        }
                        if (it.IndexExprRoot >= 0)
                        {
                            if (!ExprCompiler.TryCompile(graph, it.IndexExprRoot, declMap, inCondition: false,
                                    out ExprProgram? ip, out string? iErr, arrayDecls))
                                throw new System.Text.Json.JsonException($"trigger '{triggerName}' {act.Kind} index expression: {iErr}");
                            ci.Index = ip;
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
                            if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: false, out ExprProgram? p, out string? err, arrayDecls))
                                throw new System.Text.Json.JsonException($"trigger '{triggerName}' set_variable value expression: {err}");
                            DslValueType target = declMap.TryGetValue(act.Variable!, out var decl) ? decl.Type : DslValueType.Int;
                            if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{triggerName}' set_variable target '{act.Variable}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                            if (p!.ResultType != target)
                                throw new System.Text.Json.JsonException(
                                    $"trigger '{triggerName}' set_variable value expression (expr node {root}): result type {p.ResultType} does not match target variable '{act.Variable}' ({target}).");
                            ci.Value = p;
                        }
                        break;
                    }
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
                // director executes below (drains + the sweep) charges against it. Legacy scenarios charge only
                // their fired actions, and the consumed value folds into SimChecksum either way (v17).
                _loopState.ResetFuel();

                if (_execs.Count == 0)
                {
                    // Review (7.3 follow-up): declared ScenarioData.Timers made trigger-less timers representable, and
                    // the folded remaining-ticks must still decrement per the "declared timers start active" contract —
                    // pre-7.3 the early-out was safe because timers could only exist via trigger actions. An empty table
                    // (every legacy scenario) makes this a no-op, so goldens/checksums are unmoved. Expiry events go
                    // nowhere without triggers (timer_expires is a trigger event), which is fine — the state is the point.
                    _expiredTimers.Clear();
                    _vars.TimerTickAndCollectExpired(_expiredTimers);
                    return;
                }

                // Story 7.6 — the batched drain phase runs at the START of the director tick, BEFORE event
                // collection and the trigger sweep (ascending node-id across rows).
                DrainBatchedRows(world);

                var events = CollectEvents(world);
                TickCooldowns();
                EvaluateTriggers(events, world);
                UpdateSnapshots(world);
            }
            finally
            {
                // Review (7.4 pass 2): the seam is scoped to THE TICK — don't retain the world reference between
                // ticks (any future between-tick evaluation entry point, e.g. an editor preview, would otherwise
                // scan a world the director no longer owns; LoadScenario's clear covers only the reset path).
                _exprWorld = null;

                // Story 7.8 — publish the presentation read rail EXACTLY ONCE per tick at the tick boundary, reading
                // the FINAL post-tick _vars (the director ticks LAST, and this runs after the sweep/timers/snapshots
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

        private List<FiredEvent> CollectEvents(EntityWorld world)
        {
            var events = new List<FiredEvent>(16);

            // match_start fires on the very first tick after LoadScenario().
            if (_firstTick)
            {
                events.Add(new FiredEvent("match_start", -1, 0, null));
                _firstTick = false;
            }

            // Entity deaths — compare current Alive flag against previous snapshot.
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                bool wasAlive = (_prevFlags[i] & EntityFlags.Alive) != 0;
                bool isAlive  = world.IsAlive(i);
                if (wasAlive && !isAlive)
                {
                    int slot = (int)world.FactionOf[i] - 1; // Player1=1 → slot 0
                    events.Add(new FiredEvent("unit_dies", slot, 0, null));
                }
            }

            // Building completions (was under construction → now done).
            for (int i = 0; i < _buildings.Count; i++)
            {
                bool wasDone = _prevBuildingDone[i];
                bool isAlive = _buildings.Alive[i];
                bool isDone  = isAlive && _buildings.ConstructionTimer[i] <= Fixed.Zero;

                if (isAlive && !wasDone && isDone)
                    events.Add(new FiredEvent("building_completed", (int)_buildings.FactionOf[i] - 1, 0,
                        _buildings.Type[i].ToString()));
            }

            // Timers — decrement each ACTIVE timer and collect expiries in CREATION-INDEX (declaration) order via the
            // top-level store. Byte-identical to the legacy ScenarioDirector loop (same order, same "fires on the
            // tick it reaches 0"), now that timers live in the folded DslVarTable.
            _expiredTimers.Clear();
            _vars.TimerTickAndCollectExpired(_expiredTimers);
            for (int i = 0; i < _expiredTimers.Count; i++)
                events.Add(new FiredEvent("timer_expires", -1, 0, _expiredTimers[i]));

            // Threshold events — polled every tick so triggers can react to sustained states.
            for (int slot = 0; slot < 2; slot++)
            {
                var faction = (Faction)(slot + 1);
                int oreRaw  = _resources.Ore[(int)faction].Raw;
                int units   = CountAlive(world, faction);
                events.Add(new FiredEvent("resource_threshold",   slot, oreRaw, null));
                events.Add(new FiredEvent("unit_count_threshold", slot, units,  null));
            }

            return events;
        }

        // ── Cooldown bookkeeping ──────────────────────────────────────────────

        private void TickCooldowns()
        {
            for (int i = 0; i < _triggerCooldown.Length; i++)
                if (_triggerCooldown[i] > 0) _triggerCooldown[i]--;
        }

        // ── Trigger evaluation ────────────────────────────────────────────────

        private void EvaluateTriggers(List<FiredEvent> events, EntityWorld world)
        {
            // Walk the precomputed total order (Priority desc, then ascending node-id) built once in LoadScenario.
            // ExecuteActions runs in this order, so equal-priority triggers writing shared state resolve last-writer
            // by ascending declaration/node-id, deterministically across peers (AR-16).
            for (int idx = 0; idx < _execs.Count; idx++)
            {
                // Story 7.6 — the fuel seatbelt halts the SWEEP at a whole-trigger boundary: the in-flight
                // trigger completed (it charged past the budget mid-run, untorn), and every remaining trigger
                // skips this tick and simply re-evaluates next tick — identically on every peer.
                if (_loopState.FuelExhausted) break;

                TriggerGraph.TriggerExec ex = _execs[idx];
                TriggerNode t = ex.Trigger;
                if (!t.Enabled || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue;
                // Story 7.6 — a trigger whose batched continuation row is still draining is SUPPRESSED in the
                // sweep (it cannot re-fire until the drip and its continuation chain complete). The RowCount
                // bound is the reset-window guard (review P8): SimulationHost.ClearForReset clears LoopState
                // rows while this director's bookkeeping survives until the re-apply's LoadScenario — a tick in
                // that window must treat the stale row index as "no active row", never index cleared storage.
                if (_batchRowOfTrigger[idx] >= 0 && _batchRowOfTrigger[idx] < _loopState.RowCount
                    && _loopState.RowActive(_batchRowOfTrigger[idx])) continue;
                if (!AnyEventMatches(ex.Events, events))                            continue;
                if (!AllConditionsMet(ex.Conditions, world))                        continue;
                // Story 7.4: compiled condition-expression programs AND with the legacy conditions above
                // (multi-condition semantics). Pre-checked Bool postfix programs; zero-allocation eval.
                if (!AllExprConditionsPass(idx))                                    continue;

                // Story 7.3: open a trigger-local scope for this firing (allocate/reset trigger-local scratch), run
                // the action chain, then free it — never engine-global, never folded.
                _vars.Enter();
                try { ExecuteTopLevel(idx, world); }
                finally { _vars.Exit(); }

                if (t.RunOnce) _triggerFired[idx] = true;

                int coolTicks = SecondsToTicks(t.CooldownSeconds);
                if (coolTicks > 0) _triggerCooldown[idx] = coolTicks;
            }
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

        private static bool AnyEventMatches(EventNode[] evDefs, List<FiredEvent> fired)
        {
            foreach (var def in evDefs)
                foreach (var f in fired)
                    if (EventMatches(def, f)) return true;
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
        /// multi-condition semantics. No programs (every legacy scenario) ⇒ trivially true.</summary>
        private bool AllExprConditionsPass(int idx)
        {
            ExprProgram[] programs = idx < _condPrograms.Length ? _condPrograms[idx] : NoCondPrograms;
            for (int i = 0; i < programs.Length; i++)
                if (programs[i].Eval(_vars, this) == 0) return false;
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
        /// count, loop/branch entry = 1 + condition ops). <paramref name="anchor"/> is the current entity of the
        /// nearest enclosing ENTITY-source loop (-1 = none → run_effect keeps its legacy lowest-id-alive anchor).
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
                    bool taken = item.Cond != null && item.Cond.Eval(_vars, this) != 0;
                    ExecuteItems(taken ? item.Then : item.Else, world, anchor);
                    // Port-0 continuation items follow this one in the parent chain — they always run.
                    break;
                }

                case EffectActionNode effectNode:
                    _loopState.Charge(item.RunEffectCost);
                    RunEffect(effectNode, world, anchor);
                    break;

                case ActionNode a:
                    _loopState.Charge(1 + (item.Value?.OpCount ?? 0) + (item.Index?.OpCount ?? 0));
                    ExecuteLeaf(a, item, world);
                    break;
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
        /// actions and reconciles the spawn clamp to the named <see cref="EffectCaps.MaxSpawnCount"/>).</summary>
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
                            _vars.SetRaw(a.Variable, a.Faction, item.Value.Eval(_vars, this), 0);
                        else
                            _vars.SetInt(a.Variable, a.Faction, a.Value);
                    }
                    break;
                // ── Story 7.6 — the array actions (total runtime semantics; the gate guarantees shapes) ──
                case "array_push":
                    if (!string.IsNullOrEmpty(a.Variable) && item.Value != null)
                        _vars.ArrayPush(a.Variable, item.Value.Eval(_vars, this)); // at capacity → no-op
                    break;
                case "array_set":
                    if (!string.IsNullOrEmpty(a.Variable) && item.Value != null && item.Index != null)
                    {
                        int idx = item.Index.Eval(_vars, this);
                        _vars.ArraySet(a.Variable, idx, item.Value.Eval(_vars, this)); // OOB → no-op
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

        private readonly struct FiredEvent
        {
            public readonly string  Type;
            public readonly int     Slot;    // -1 = no faction
            public readonly int     Numeric; // typed numeric payload: ore raw-Fixed integer, or unit count
            public readonly string? Data;    // string payload: building type, timer name (null when unused)

            public FiredEvent(string type, int slot, int numeric, string? data)
            {
                Type    = type;
                Slot    = slot;
                Numeric = numeric;
                Data    = data;
            }
        }
    }
}
