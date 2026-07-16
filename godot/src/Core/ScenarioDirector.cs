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

        public ScenarioDirector(BuildingStore buildings, ResourceStore resources, DslVarTable vars)
        {
            _buildings = buildings;
            _resources = resources;
            _vars      = vars;
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

            // Story 7.4 — compile every condition-expression and set_variable value-expression ONCE (two-phase
            // contract). A compile failure throws a located JsonException, consistent with the cycle-guard posture
            // above (the ScenarioValidator gate rejects the same errors located BEFORE any apply; this is the
            // fail-closed backstop for direct LoadScenario callers). Expression-free scenarios compile nothing.
            (ExprProgram[][] condPrograms, ExprProgram?[][] valuePrograms) = CompileExpressionPrograms(scenario, graph, execs);

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
            _condPrograms    = condPrograms;
            _valuePrograms   = valuePrograms;

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

        /// <summary>
        /// Story 7.4 — compile every expression subgraph the execution view surfaced (condition-in roots and
        /// set_variable value-in roots) into <see cref="ExprProgram"/>s held per trigger, via <see cref="ExprCompiler"/>
        /// against the scenario's declared-variable map. Located <see cref="System.Text.Json.JsonException"/> on any
        /// compile reject (type mismatch, literal-zero divisor, caps, scope misuse — the full 7.4 rulebook).
        /// Pure function of its inputs (review, 7.4 pass 2): fills and returns LOCAL arrays so a mid-compile throw
        /// never strands half-replaced director state — the caller commits them only after everything compiled.
        /// </summary>
        private static (ExprProgram[][] CondPrograms, ExprProgram?[][] ValuePrograms) CompileExpressionPrograms(
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
            bool anyExpr = false;
            foreach (NodeBase n in graph.Nodes)
                if (ExprCompiler.IsExprNode(n)) { anyExpr = true; break; }

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
                    if (act.Kind != "set_variable")
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: a value-in expression edge is only allowed on a set_variable action (kind '{act.Kind}').");
                    if (string.IsNullOrEmpty(act.Variable))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: a set_variable with a value expression needs a target variable.");
                    if (!seenValueInPorts.Add(act.Id))
                        throw new System.Text.Json.JsonException(
                            $"action node {act.Id}: multiple value-in expression edges (forked; exactly one allowed).");
                    if (!ExprCompiler.TryCompile(graph, de.Src, declMap, inCondition: false, out ExprProgram? vp, out string? vErr))
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
                        if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: true, out ExprProgram? p, out string? err))
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
                    if (!ExprCompiler.TryCompile(graph, root, declMap, inCondition: false, out ExprProgram? p, out string? err))
                        throw new System.Text.Json.JsonException($"trigger '{ex.Trigger.Name}' set_variable value expression: {err}");
                    DslValueType target = declMap.TryGetValue(act.Variable!, out var decl) ? decl.Type : DslValueType.Int;
                    if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                        throw new System.Text.Json.JsonException(
                            $"trigger '{ex.Trigger.Name}' set_variable target '{act.Variable}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                    if (p!.ResultType != target)
                        throw new System.Text.Json.JsonException(
                            $"trigger '{ex.Trigger.Name}' set_variable value expression (expr node {root}): result type {p.ResultType} does not match target variable '{act.Variable}' ({target}).");
                    values[j] = p;
                }
                valuePrograms[i] = values;
            }

            return (condPrograms, valuePrograms);
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
                    _expiredTimers.Clear();
                    _vars.TimerTickAndCollectExpired(_expiredTimers);
                    return;
                }

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
                TriggerGraph.TriggerExec ex = _execs[idx];
                TriggerNode t = ex.Trigger;
                if (!t.Enabled || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue;
                if (!AnyEventMatches(ex.Events, events))                            continue;
                if (!AllConditionsMet(ex.Conditions, world))                        continue;
                // Story 7.4: compiled condition-expression programs AND with the legacy conditions above
                // (multi-condition semantics). Pre-checked Bool postfix programs; zero-allocation eval.
                if (!AllExprConditionsPass(idx))                                    continue;

                // Story 7.3: open a trigger-local scope for this firing (allocate/reset trigger-local scratch), run
                // the action chain, then free it — never engine-global, never folded.
                _vars.Enter();
                try { ExecuteActions(ex.Actions, idx < _valuePrograms.Length ? _valuePrograms[idx] : NoValuePrograms, world); }
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

        private void ExecuteActions(NodeBase[] actions, ExprProgram?[] valuePrograms, EntityWorld world)
        {
            for (int j = 0; j < actions.Length; j++)
            {
                NodeBase node = actions[j];
                if (node is EffectActionNode effectNode)
                {
                    RunEffect(effectNode, world);
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
                                _vars.SetRaw(a.Variable, a.Faction, rhs.Eval(_vars, this), 0);
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
