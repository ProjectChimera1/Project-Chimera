#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;   // TriggerDefinition, TriggerEvent, TriggerCondition, TriggerAction
using ProjectChimera.Effects;             // EffectNode (embedded run_effect subgraph, Story 7.3)

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 — the graph-canonical trigger IR container: an id-keyed <see cref="Nodes"/> list plus two sparse
    /// typed edge-lists (<see cref="ExecEdges"/> control flow, <see cref="DataEdges"/> dataflow). All four authoring
    /// tiers (T1/T2/T3/T4) share this ONE representation.
    ///
    /// <see cref="FromFlat"/>/<see cref="ToFlat"/> are an EXACT round-trip identity for the legacy flat
    /// <c>TriggerDefinition[]</c> (all field values AND array/intra-trigger order): the "T2 sentence list is a
    /// linear projection of an exec-edge chain." Node ids are assigned deterministically from flat array order;
    /// graph→flat reconstruction is driven by ascending node id + edge topology, reproducing the original order.
    /// <see cref="ToCanonicalJson"/> emits nodes sorted by ascending id and each edge list sorted by
    /// <c>(Src,SrcPort,Dst,DstPort)</c>, so two structurally-equal graphs serialize byte-identically.
    /// </summary>
    public sealed class TriggerGraph
    {
        // ── Named ports (the fixed pin layout the flat↔graph mapping wires) ──────
        /// <summary>TriggerNode: the event-in port EventNodes fire into (exec edge Dst).</summary>
        public const int TriggerEventInPort     = 0;
        /// <summary>TriggerNode: the condition-in port ConditionNodes gate through (data edge Dst).</summary>
        public const int TriggerConditionInPort = 1;
        /// <summary>TriggerNode: the exec-out port that starts the action chain (exec edge Src).</summary>
        public const int TriggerExecOutPort     = 0;
        /// <summary>ActionNode: the exec-in port (exec edge Dst).</summary>
        public const int ActionExecInPort       = 0;
        /// <summary>ActionNode: the exec-out port that continues the chain (exec edge Src).</summary>
        public const int ActionExecOutPort      = 0;
        /// <summary>EventNode: the exec-out port that fires the trigger (exec edge Src).</summary>
        public const int EventExecOutPort       = 0;
        /// <summary>ConditionNode: the Boolean data-out port that gates the trigger (data edge Src).</summary>
        public const int ConditionDataOutPort   = 0;
        /// <summary>ActionNode (Story 7.4): the typed value-in port a set_variable RHS expression feeds (data edge
        /// Dst). Distinct from <see cref="ActionExecInPort"/> — data ports and exec ports are separate spaces.</summary>
        public const int ActionValueInPort      = 1;
        /// <summary>Expression node (Story 7.4): the first operand-in port (unary operand / binary left / arg 0).</summary>
        public const int ExprOperandPort0       = 0;
        /// <summary>Expression node (Story 7.4): the second operand-in port (binary right / arg 1).</summary>
        public const int ExprOperandPort1       = 1;
        /// <summary>Expression node (Story 7.4): the typed data-out port every expression node emits on (data edge Src).</summary>
        public const int ExprDataOutPort        = 0;
        /// <summary>RaiseEventNode (Story 7.5): the typed value-in DATA ports its raise arguments feed (data edge
        /// Dst), one per declared event param in declaration order. Data ports and exec ports are separate spaces,
        /// so these coexist with <see cref="ActionExecInPort"/>/<see cref="ActionExecOutPort"/> on the same node.</summary>
        public const int RaiseArgInPort0        = 0;
        /// <summary>RaiseEventNode (Story 7.5): the second raise-argument data-in port.</summary>
        public const int RaiseArgInPort1        = 1;
        /// <summary>RaiseEventNode (Story 7.5): the third raise-argument data-in port.</summary>
        public const int RaiseArgInPort2        = 2;
        /// <summary>RaiseEventNode (Story 7.5): the fourth raise-argument data-in port (== EventBounds.MaxEventParams − 1).</summary>
        public const int RaiseArgInPort3        = 3;

        public List<NodeBase> Nodes     { get; } = new();
        public List<ExecEdge> ExecEdges { get; } = new();
        public List<DataEdge> DataEdges { get; } = new();

        /// <summary>
        /// Migrate the flat <see cref="TriggerDefinition"/>[] into the graph IR. Ids are assigned by a single
        /// ascending counter walking triggers in array order; per trigger it emits the TriggerNode, then its
        /// EventNodes, then ConditionNodes, then ActionNodes (e.g. two 1-event/1-cond/2-action triggers →
        /// T0=0,E=1,C=2,A0=3,A1=4 ; T1=5,E=6,C=7,A0=8,A1=9). Wires: EventNode --exec--&gt; Trigger (event-in);
        /// Trigger --exec--&gt; Action0 --exec--&gt; … (the linear action chain); ConditionNode --data(Boolean)--&gt;
        /// Trigger (condition-in gate). Field values are copied verbatim, so <see cref="ToFlat"/> is exact.
        /// </summary>
        public static TriggerGraph FromFlat(TriggerDefinition[] triggers)
        {
            var graph = new TriggerGraph();
            int nextId = 0;

            foreach (TriggerDefinition t in triggers)
            {
                int triggerId = nextId++;
                graph.Nodes.Add(new TriggerNode
                {
                    Id              = triggerId,
                    Name            = t.Name,
                    Enabled         = t.Enabled,
                    RunOnce         = t.RunOnce,
                    CooldownSeconds = t.CooldownSeconds,
                    Priority        = t.Priority,
                });

                foreach (TriggerEvent e in t.Events)
                {
                    int eid = nextId++;
                    graph.Nodes.Add(new EventNode
                    {
                        Id           = eid,
                        Kind         = e.Type,
                        Faction      = e.Faction,
                        BuildingType = e.BuildingType,
                        TimerName    = e.TimerName,
                        Amount       = e.Amount,
                        Count        = e.Count,
                        Operator     = e.Operator,
                    });
                    graph.ExecEdges.Add(new ExecEdge(eid, EventExecOutPort, triggerId, TriggerEventInPort));
                }

                foreach (TriggerCondition c in t.Conditions)
                {
                    int cid = nextId++;
                    graph.Nodes.Add(new ConditionNode
                    {
                        Id           = cid,
                        Kind         = c.Type,
                        Faction      = c.Faction,
                        BuildingType = c.BuildingType,
                        Amount       = c.Amount,
                        Count        = c.Count,
                        Variable     = c.Variable,
                        RegionId     = c.RegionId,
                        Value        = c.Value,
                        Operator     = c.Operator,
                    });
                    graph.DataEdges.Add(new DataEdge(cid, ConditionDataOutPort, triggerId, TriggerConditionInPort, DataWireType.Boolean));
                }

                // The linear action chain: Trigger(ExecOut) → Action0(ExecIn), Action0(ExecOut) → Action1(ExecIn), …
                int prevId   = triggerId;
                int prevPort = TriggerExecOutPort;
                foreach (TriggerAction a in t.Actions)
                {
                    int aid = nextId++;
                    graph.Nodes.Add(new ActionNode
                    {
                        Id           = aid,
                        Kind         = a.Type,
                        UnitId       = a.UnitId,
                        Faction      = a.Faction,
                        X            = a.X,
                        Z            = a.Z,
                        Count        = a.Count,
                        Text         = a.Text,
                        Duration     = a.Duration,
                        TimerName    = a.TimerName,
                        TimerSeconds = a.TimerSeconds,
                        Amount       = a.Amount,
                        Value        = a.Value,
                        Variable     = a.Variable,
                        SoundId      = a.SoundId,
                    });
                    graph.ExecEdges.Add(new ExecEdge(prevId, prevPort, aid, ActionExecInPort));
                    prevId   = aid;
                    prevPort = ActionExecOutPort;
                }
            }

            return graph;
        }

        /// <summary>
        /// Story 7.3 — union another graph INTO this one, preserving both channels' triggers (never silently
        /// dropping either). The <paramref name="other"/> graph's node ids are offset past THIS graph's maximum id
        /// (so the two id spaces never collide), then its nodes and exec/data edges are appended with the same
        /// offset applied to every edge endpoint — topology is preserved exactly. A subsequent
        /// <see cref="BuildExecutionOrder"/> over the union re-derives the global Priority-desc / node-id-asc order
        /// across BOTH channels. Used by <c>ScenarioDirector.LoadScenario</c> to merge the flat
        /// <see cref="FromFlat"/> graph with an authored <see cref="FromJson"/> graph. Mutates this graph; the
        /// <paramref name="other"/> graph's node ids are rewritten in place (it is a throwaway freshly-built graph).
        /// </summary>
        public void Merge(TriggerGraph other)
        {
            int offset = 0;
            foreach (NodeBase n in Nodes)
                if (n.Id + 1 > offset) offset = n.Id + 1;

            foreach (NodeBase n in other.Nodes)
            {
                n.Id += offset;
                Nodes.Add(n);
            }
            foreach (ExecEdge e in other.ExecEdges)
                ExecEdges.Add(new ExecEdge(e.Src + offset, e.SrcPort, e.Dst + offset, e.DstPort));
            foreach (DataEdge e in other.DataEdges)
                DataEdges.Add(new DataEdge(e.Src + offset, e.SrcPort, e.Dst + offset, e.DstPort, e.Wire));
        }

        /// <summary>
        /// Story 7.3 — assemble a single-trigger graph (EventNode --exec--&gt; TriggerNode --exec--&gt;
        /// EffectActionNode) that fires <paramref name="effect"/> via run_effect when <paramref name="eventKind"/>
        /// fires. Ids are the canonical 0=trigger, 1=event, 2=run_effect (the editor's hand-built layout, extracted
        /// here so it is Godot-free and unit-testable). The caller merges the result into the scenario's graph
        /// channel (accumulating, not overwriting).
        ///
        /// <para>Review follow-up: an optional gating condition (<paramref name="conditionKind"/> non-null ⇒ a
        /// ConditionNode with id 3, data-wired to the trigger's condition-in port) — the editor's preset form lets a
        /// creator pick a condition alongside the run_effect action, and silently discarding it would fire the
        /// effect unconditionally against the authored logic. Variable/value/operator apply to
        /// <c>variable_comparison</c>; other kinds carry their defaults.</para>
        /// </summary>
        public static TriggerGraph BuildRunEffectTrigger(string name, string eventKind, EffectNode effect,
            bool enabled = true, bool runOnce = false,
            string? conditionKind = null, string? conditionVariable = null, int conditionValue = 0,
            string conditionOperator = "==")
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = name, Enabled = enabled, RunOnce = runOnce });
            g.Nodes.Add(new EventNode { Id = 1, Kind = eventKind });
            g.Nodes.Add(new EffectActionNode { Id = 2, Effect = effect });
            g.ExecEdges.Add(new ExecEdge(1, EventExecOutPort, 0, TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerExecOutPort, 2, ActionExecInPort));
            if (conditionKind is not null)
            {
                g.Nodes.Add(new ConditionNode
                {
                    Id       = 3,
                    Kind     = conditionKind,
                    Variable = conditionVariable,
                    Value    = conditionValue,
                    Operator = conditionOperator,
                });
                g.DataEdges.Add(new DataEdge(3, ConditionDataOutPort, 0, TriggerConditionInPort, DataWireType.Boolean));
            }
            return g;
        }

        /// <summary>
        /// Story 7.4 — assemble a single-trigger graph whose condition and/or set_variable value are authored as
        /// CEL-shaped expression TEXT (compiled through <see cref="ExprParser"/> into the one graph IR — never a
        /// second executable form). Godot-free and unit-testable; the editor's manual form calls this and merges
        /// the result into the scenario's graph channel (the <see cref="BuildRunEffectTrigger"/> accumulation
        /// pattern). Ids: 0 = trigger, 1 = event, then parser-assigned expression nodes, then the set_variable
        /// action.
        ///
        /// Fail-closed: each expression is parsed AND compiled here (<see cref="ExprCompiler"/>, the same checks
        /// the validator gate re-runs), so a syntax error, type mismatch, literal-zero divisor, or cap violation
        /// throws a located <see cref="JsonException"/> and NOTHING is returned to persist. A condition expression
        /// must infer Bool; a value expression must match the declared target variable's type (Int/Fixed/Bool
        /// targets only; an undeclared target is Int — the runtime Global/Int append semantics).
        /// </summary>
        /// <param name="setVarValue">The literal value for the set_variable action when NO value expression is
        /// given (the legacy literal path — then the target must be Int-typed, per the unchanged 7.3 rule).</param>
        public static TriggerGraph BuildExpressionTrigger(
            string name, string eventKind,
            string? conditionExprText, string? setVarName, int setVarFaction, string? valueExprText,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> varDeclInfo,
            bool enabled = true, bool runOnce = false, int setVarValue = 0)
        {
            // Fail-closed on whitespace-only text (review, 7.4 pass 2): "absent" is spelled null. Degrading "  " to
            // no-condition would silently fire the trigger UNCONDITIONALLY (the exact silent-condition-drop class two
            // prior patches closed), and a whitespace value expression would silently fall back to the literal path.
            // The editor trims before calling; this guards every other (Godot-free/public) caller.
            if (conditionExprText != null && string.IsNullOrWhiteSpace(conditionExprText))
                throw new JsonException("condition expression text is blank (pass null for an unconditioned trigger).");
            if (valueExprText != null && string.IsNullOrWhiteSpace(valueExprText))
                throw new JsonException("value expression text is blank (pass null for the literal value path).");

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = name, Enabled = enabled, RunOnce = runOnce });
            g.Nodes.Add(new EventNode { Id = 1, Kind = eventKind });
            g.ExecEdges.Add(new ExecEdge(1, EventExecOutPort, 0, TriggerEventInPort));

            if (!string.IsNullOrWhiteSpace(conditionExprText))
            {
                (int rootId, DataWireType wire) = ExprParser.Parse(conditionExprText!, g, varDeclInfo);
                if (wire != DataWireType.Boolean)
                    throw new JsonException($"condition expression must evaluate to Bool, got wire type '{wire}'.");
                g.DataEdges.Add(new DataEdge(rootId, ExprDataOutPort, 0, TriggerConditionInPort, DataWireType.Boolean));
                if (!ExprCompiler.TryCompile(g, rootId, varDeclInfo, inCondition: true, out _, out string? condErr))
                    throw new JsonException($"condition expression: {condErr}");
            }

            if (!string.IsNullOrEmpty(setVarName))
            {
                if (!string.IsNullOrWhiteSpace(valueExprText))
                {
                    (int rootId, DataWireType wire) = ExprParser.Parse(valueExprText!, g, varDeclInfo);
                    DslValueType target = varDeclInfo.TryGetValue(setVarName!, out var decl) ? decl.Type : DslValueType.Int;
                    if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                        throw new JsonException(
                            $"set_variable target '{setVarName}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                    if (wire != ExprCompiler.WireOf(target))
                        throw new JsonException(
                            $"value expression wire type '{wire}' does not match the declared type of target variable '{setVarName}' ({target}).");

                    int actionId = 0;
                    foreach (NodeBase n in g.Nodes)
                        if (n.Id + 1 > actionId) actionId = n.Id + 1;
                    g.Nodes.Add(new ActionNode { Id = actionId, Kind = "set_variable", Variable = setVarName, Faction = setVarFaction });
                    g.ExecEdges.Add(new ExecEdge(0, TriggerExecOutPort, actionId, ActionExecInPort));
                    g.DataEdges.Add(new DataEdge(rootId, ExprDataOutPort, actionId, ActionValueInPort, wire));
                    if (!ExprCompiler.TryCompile(g, rootId, varDeclInfo, inCondition: false, out _, out string? valErr))
                        throw new JsonException($"value expression: {valErr}");
                }
                else
                {
                    // Literal path (unchanged 7.3 semantics): Int-only targets — the validator gate enforces it.
                    int actionId = 0;
                    foreach (NodeBase n in g.Nodes)
                        if (n.Id + 1 > actionId) actionId = n.Id + 1;
                    g.Nodes.Add(new ActionNode { Id = actionId, Kind = "set_variable", Variable = setVarName, Faction = setVarFaction, Value = setVarValue });
                    g.ExecEdges.Add(new ExecEdge(0, TriggerExecOutPort, actionId, ActionExecInPort));
                }
            }

            return g;
        }

        /// <summary>
        /// Story 7.5 — assemble a single-trigger graph for the custom-event layer, Godot-free and unit-testable
        /// (the <see cref="BuildRunEffectTrigger"/>/<see cref="BuildExpressionTrigger"/> family; the editor's
        /// manual form calls this and MERGES the result into the scenario's graph channel). The trigger subscribes
        /// to <paramref name="eventKind"/> (a built-in kind, or <c>custom_event</c> naming
        /// <paramref name="customEventName"/>), optionally gated by a condition expression, and carries up to two
        /// chained actions: a <c>raise_event</c> (with per-declared-param argument expression TEXT, an authored
        /// raiser slot, and the next-tick flag) and/or a <c>set_variable</c> (expression or literal value).
        ///
        /// Fail-closed: every expression is parsed AND compiled here against the declared-variable map and the
        /// subscribed event's parameter map (so <c>event.&lt;param&gt;</c> reads work exactly as they will at the
        /// gate), raise arguments are arity/type-matched against the declared params, and the authored raiser must
        /// be −1 (system) or ∈ the target event's allowed_raisers — a violation throws a located
        /// <see cref="JsonException"/> and NOTHING is returned to persist.
        /// </summary>
        public static TriggerGraph BuildCustomEventTrigger(
            string name,
            string eventKind, string? customEventName,
            string? conditionExprText,
            string? raiseEventName, IReadOnlyList<string>? raiseArgExprTexts, int raiser, bool raiseNextTick,
            string? setVarName, int setVarFaction, string? valueExprText,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> varDeclInfo,
            IReadOnlyList<ScenarioCustomEvent>? customEvents,
            bool enabled = true, bool runOnce = false, int setVarValue = 0)
        {
            if (conditionExprText != null && string.IsNullOrWhiteSpace(conditionExprText))
                throw new JsonException("condition expression text is blank (pass null for an unconditioned trigger).");
            if (valueExprText != null && string.IsNullOrWhiteSpace(valueExprText))
                throw new JsonException("value expression text is blank (pass null for the literal value path).");

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = name, Enabled = enabled, RunOnce = runOnce });

            // ── Subscription + the trigger's event-parameter map (the same map the gate/backstop derive) ──
            IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? paramMap;
            if (eventKind == NodeKinds.CustomEvent)
            {
                if (string.IsNullOrWhiteSpace(customEventName))
                    throw new JsonException("a custom_event subscription needs the declared custom-event name.");
                ScenarioCustomEvent ev = FindDeclaredEvent(customEvents, customEventName!)
                    ?? throw new JsonException($"custom event '{customEventName}' is not declared in scenario.custom_events.");
                g.Nodes.Add(new EventNode { Id = 1, Kind = NodeKinds.CustomEvent, EventName = customEventName });
                paramMap = EventDispatchPlan.ParamMapOf(ev);
            }
            else
            {
                g.Nodes.Add(new EventNode { Id = 1, Kind = eventKind });
                paramMap = eventKind == "unit_dies" ? EventDispatchPlan.UnitDiesParams : null;
            }
            g.ExecEdges.Add(new ExecEdge(1, EventExecOutPort, 0, TriggerEventInPort));

            if (!string.IsNullOrWhiteSpace(conditionExprText))
            {
                (int rootId, DataWireType wire) = ExprParser.Parse(conditionExprText!, g, varDeclInfo, paramMap);
                if (wire != DataWireType.Boolean)
                    throw new JsonException($"condition expression must evaluate to Bool, got wire type '{wire}'.");
                g.DataEdges.Add(new DataEdge(rootId, ExprDataOutPort, 0, TriggerConditionInPort, DataWireType.Boolean));
                if (!ExprCompiler.TryCompile(g, rootId, varDeclInfo, inCondition: true, paramMap, out _, out string? condErr))
                    throw new JsonException($"condition expression: {condErr}");
            }

            int prevExecId = 0, prevExecPort = TriggerExecOutPort;

            // ── Optional raise_event action (arg expressions arity/type-matched against the declared params) ──
            if (!string.IsNullOrEmpty(raiseEventName))
            {
                ScenarioCustomEvent target = FindDeclaredEvent(customEvents, raiseEventName!)
                    ?? throw new JsonException($"raise_event names undeclared custom event '{raiseEventName}'.");
                ScenarioEventParam[] declParams = target.Params ?? System.Array.Empty<ScenarioEventParam>();
                int argCount = raiseArgExprTexts?.Count ?? 0;
                if (argCount != declParams.Length)
                    throw new JsonException(
                        $"raise_event '{raiseEventName}' declares {declParams.Length} parameter(s) but {argCount} argument expression(s) were given.");
                if (raiser != -1)
                {
                    int[] allowed = target.AllowedRaisers ?? System.Array.Empty<int>();
                    if (System.Array.IndexOf(allowed, raiser) < 0)
                        throw new JsonException(
                            $"raise_event '{raiseEventName}' raiser {raiser} is not in the event's allowed_raisers (or use -1 = system).");
                }

                int raiseId = NextFreeId(g);
                var raiseNode = new RaiseEventNode { Id = raiseId, Name = raiseEventName!, Raiser = raiser, NextTick = raiseNextTick };
                g.Nodes.Add(raiseNode);
                g.ExecEdges.Add(new ExecEdge(prevExecId, prevExecPort, raiseId, ActionExecInPort));
                prevExecId = raiseId; prevExecPort = ActionExecOutPort;

                for (int p = 0; p < declParams.Length; p++)
                {
                    string argText = raiseArgExprTexts![p];
                    if (string.IsNullOrWhiteSpace(argText))
                        throw new JsonException($"raise_event '{raiseEventName}' argument {p} ('{declParams[p].Name}') needs expression text.");
                    (int argRoot, DataWireType argWire) = ExprParser.Parse(argText, g, varDeclInfo, paramMap);
                    DataWireType wanted = ExprCompiler.WireOf(declParams[p].Type);
                    if (argWire != wanted)
                        throw new JsonException(
                            $"raise_event '{raiseEventName}' argument {p} ('{declParams[p].Name}') must be {declParams[p].Type}-typed, got wire '{argWire}'.");
                    g.DataEdges.Add(new DataEdge(argRoot, ExprDataOutPort, raiseId, RaiseArgInPort0 + p, wanted));
                    if (!ExprCompiler.TryCompile(g, argRoot, varDeclInfo, inCondition: false, paramMap, out _, out string? argErr))
                        throw new JsonException($"raise_event argument {p}: {argErr}");
                }
            }

            // ── Optional set_variable action (the BuildExpressionTrigger semantics, event-param-aware) ──
            if (!string.IsNullOrEmpty(setVarName))
            {
                if (!string.IsNullOrWhiteSpace(valueExprText))
                {
                    (int rootId, DataWireType wire) = ExprParser.Parse(valueExprText!, g, varDeclInfo, paramMap);
                    DslValueType target = varDeclInfo.TryGetValue(setVarName!, out var decl) ? decl.Type : DslValueType.Int;
                    if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                        throw new JsonException(
                            $"set_variable target '{setVarName}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                    if (wire != ExprCompiler.WireOf(target))
                        throw new JsonException(
                            $"value expression wire type '{wire}' does not match the declared type of target variable '{setVarName}' ({target}).");
                    int actionId = NextFreeId(g);
                    g.Nodes.Add(new ActionNode { Id = actionId, Kind = "set_variable", Variable = setVarName, Faction = setVarFaction });
                    g.ExecEdges.Add(new ExecEdge(prevExecId, prevExecPort, actionId, ActionExecInPort));
                    g.DataEdges.Add(new DataEdge(rootId, ExprDataOutPort, actionId, ActionValueInPort, wire));
                    if (!ExprCompiler.TryCompile(g, rootId, varDeclInfo, inCondition: false, paramMap, out _, out string? valErr))
                        throw new JsonException($"value expression: {valErr}");
                }
                else
                {
                    int actionId = NextFreeId(g);
                    g.Nodes.Add(new ActionNode { Id = actionId, Kind = "set_variable", Variable = setVarName, Faction = setVarFaction, Value = setVarValue });
                    g.ExecEdges.Add(new ExecEdge(prevExecId, prevExecPort, actionId, ActionExecInPort));
                }
            }

            return g;
        }

        /// <summary>Deterministic linear scan for a declared custom event by name (null when absent).</summary>
        private static ScenarioCustomEvent? FindDeclaredEvent(IReadOnlyList<ScenarioCustomEvent>? events, string name)
        {
            if (events == null) return null;
            for (int i = 0; i < events.Count; i++)
                if (events[i] != null && events[i].Name == name) return events[i];
            return null;
        }

        /// <summary>The lowest unused node id (max + 1) — the Build-helper id-assignment convention.</summary>
        private static int NextFreeId(TriggerGraph g)
        {
            int next = 0;
            foreach (NodeBase n in g.Nodes)
                if (n.Id + 1 > next) next = n.Id + 1;
            return next;
        }

        /// <summary>
        /// Lower the graph back to the flat <see cref="TriggerDefinition"/>[]. TriggerNodes are ordered by ascending
        /// <see cref="NodeBase.Id"/> → array order. Per trigger: events = EventNodes with an exec edge into its
        /// event-in port (ascending id); conditions = ConditionNodes with a data edge into its condition-in port
        /// (ascending id); actions = follow the exec chain out of it. On a graph produced by <see cref="FromFlat"/>
        /// this reproduces the original array exactly (the losslessness core).
        /// </summary>
        public TriggerDefinition[] ToFlat()
        {
            // Story 7.5 — GRAPH-CHANNEL-ONLY kinds have NO flat representation: fail closed (located throw), never
            // a lossy lowering that would silently drop a raise/subscription/param-read from the flat projection.
            foreach (NodeBase n in Nodes)
            {
                if (n is RaiseEventNode)
                    throw new JsonException(
                        $"node {n.Id}: raise_event has no flat TriggerDefinition form (graph-channel-only, Story 7.5) — ToFlat fails closed.");
                if (n is EventNode ce && ce.Kind == NodeKinds.CustomEvent)
                    throw new JsonException(
                        $"node {n.Id}: a custom_event subscription has no flat TriggerDefinition form (graph-channel-only, Story 7.5) — ToFlat fails closed.");
                if (n is ExprEventParamNode)
                    throw new JsonException(
                        $"node {n.Id}: expr_event_param has no flat TriggerDefinition form (graph-channel-only, Story 7.5) — ToFlat fails closed.");
            }

            // Keyed lookup by id (indexer access only — never enumerated, so deterministic order is preserved).
            var byId = new Dictionary<int, NodeBase>(Nodes.Count);
            foreach (NodeBase n in Nodes)
                byId[n.Id] = n;

            List<TriggerNode> triggerNodes = Nodes.OfType<TriggerNode>().OrderBy(n => n.Id).ToList();
            var result = new List<TriggerDefinition>(triggerNodes.Count);

            // Review patch (Story 7.2): sort the exec edges ONCE (the tuple order is loop-invariant), not per
            // action hop. Deterministic first-match on this stable list drives every chain walk below.
            List<ExecEdge> sortedExec = ExecEdges.OrderBy(x => x).ToList();

            foreach (TriggerNode tn in triggerNodes)
            {
                var def = new TriggerDefinition
                {
                    Name            = tn.Name,
                    Enabled         = tn.Enabled,
                    RunOnce         = tn.RunOnce,
                    CooldownSeconds = tn.CooldownSeconds,
                    Priority        = tn.Priority,
                };

                // Events: exec edges into the trigger's event-in port, source EventNodes ascending by id.
                def.Events = ExecEdges
                    .Where(e => e.Dst == tn.Id && e.DstPort == TriggerEventInPort && byId.ContainsKey(e.Src))
                    .Select(e => byId[e.Src])
                    .OfType<EventNode>()
                    .OrderBy(n => n.Id)
                    .Select(n => new TriggerEvent
                    {
                        Type         = n.Kind,
                        Faction      = n.Faction,
                        BuildingType = n.BuildingType,
                        TimerName    = n.TimerName,
                        Amount       = n.Amount,
                        Count        = n.Count,
                        Operator     = n.Operator,
                    })
                    .ToArray();

                // Conditions: data edges into the trigger's condition-in port, source ConditionNodes ascending by id.
                def.Conditions = DataEdges
                    .Where(e => e.Dst == tn.Id && e.DstPort == TriggerConditionInPort && byId.ContainsKey(e.Src))
                    .Select(e => byId[e.Src])
                    .OfType<ConditionNode>()
                    .OrderBy(n => n.Id)
                    .Select(n => new TriggerCondition
                    {
                        Type         = n.Kind,
                        Faction      = n.Faction,
                        BuildingType = n.BuildingType,
                        Amount       = n.Amount,
                        Count        = n.Count,
                        Variable     = n.Variable,
                        RegionId     = n.RegionId,
                        Value        = n.Value,
                        Operator     = n.Operator,
                    })
                    .ToArray();

                // Actions: walk the exec chain out of the trigger (Trigger → Action0 → … → Action_n).
                var actions = new List<TriggerAction>();
                int currentId   = tn.Id;
                int currentPort = TriggerExecOutPort;
                // Review patch (Story 7.2): fail-closed cycle guard. FromFlat never emits a cyclic action chain, but
                // ToFlat/FromJson are public and later stories walk authored graphs — a hand-built/JSON cycle
                // (A→B→A or self-loop) would otherwise spin this `while (true)` unbounded (hang/OOM). Track the exec
                // nodes already visited on THIS chain (seeded with the trigger head) and reject a revisit with a
                // located error, consistent with the module's fail-closed posture (never a silent hang).
                var visited = new HashSet<int> { currentId };
                while (true)
                {
                    ActionNode? next = null;
                    foreach (ExecEdge e in sortedExec)   // deterministic: exactly one match in a FromFlat graph
                    {
                        if (e.Src == currentId && e.SrcPort == currentPort
                            && byId.TryGetValue(e.Dst, out NodeBase? nb) && nb is ActionNode an)
                        {
                            next = an;
                            break;
                        }
                    }
                    if (next is null) break;
                    if (!visited.Add(next.Id))
                        throw new JsonException(
                            $"exec chain cycle at node {next.Id} (trigger '{tn.Name}', id {tn.Id}): the action chain must be acyclic.");
                    actions.Add(new TriggerAction
                    {
                        Type         = next.Kind,
                        UnitId       = next.UnitId,
                        Faction      = next.Faction,
                        X            = next.X,
                        Z            = next.Z,
                        Count        = next.Count,
                        Text         = next.Text,
                        Duration     = next.Duration,
                        TimerName    = next.TimerName,
                        TimerSeconds = next.TimerSeconds,
                        Amount       = next.Amount,
                        Value        = next.Value,
                        Variable     = next.Variable,
                        SoundId      = next.SoundId,
                    });
                    currentId   = next.Id;
                    currentPort = ActionExecOutPort;
                }
                def.Actions = actions.ToArray();

                result.Add(def);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Story 7.3 — one trigger's execution view: its head <see cref="TriggerNode"/> plus the resolved
        /// EventNodes (exec edges into its event-in port), ConditionNodes (data edges into its condition-in port),
        /// and the ordered action chain (exec chain out — <see cref="ActionNode"/> AND <see cref="EffectActionNode"/>,
        /// which the flat <see cref="ToFlat"/> lowering drops). Nodes are held by reference so the tick walks the
        /// graph directly (superseding the 7.2 flat lowering) while run_effect executes via the effect executor.
        /// </summary>
        public sealed class TriggerExec
        {
            public TriggerNode      Trigger    { get; init; } = null!;
            public EventNode[]      Events     { get; init; } = System.Array.Empty<EventNode>();
            public ConditionNode[]  Conditions { get; init; } = System.Array.Empty<ConditionNode>();
            /// <summary>The action chain in exec order — each element is an <see cref="ActionNode"/> or <see cref="EffectActionNode"/>.</summary>
            public NodeBase[]       Actions    { get; init; } = System.Array.Empty<NodeBase>();

            /// <summary>Story 7.4 — expression-root node ids data-wired into this trigger's condition-in port
            /// (ascending id). They are NOT <see cref="ConditionNode"/>s (the OfType walk above drops them), so
            /// they are surfaced separately; the director compiles each into a Bool <c>ExprProgram</c> ANDed with
            /// <see cref="Conditions"/> (multi-condition semantics).</summary>
            public int[]            ConditionExprRoots { get; init; } = System.Array.Empty<int>();

            /// <summary>Story 7.4 — per-action value-in expression-root id, parallel to <see cref="Actions"/>
            /// (-1 = no value expression). Only a <c>set_variable</c> <see cref="ActionNode"/> may carry one
            /// (enforced at the validator gate and at LoadScenario compile).</summary>
            public int[]            ActionValueExprRoots { get; init; } = System.Array.Empty<int>();

            /// <summary>Story 7.5 — per-action raise-argument expression roots, parallel to <see cref="Actions"/>.
            /// A row is null for every non-<see cref="RaiseEventNode"/> action; for a raise action it is an
            /// <c>int[EventBounds.MaxEventParams]</c> whose slot p holds the lowest expression-node src data-wired
            /// into <see cref="RaiseArgInPort0"/>+p (-1 = no edge — deterministic first; missing/forked/extra
            /// edges are located rejects at the validator gate / LoadScenario compile, not here).</summary>
            public int[]?[]         RaiseArgRoots { get; init; } = System.Array.Empty<int[]?>();

            /// <summary>Story 7.5 — the registry index of the custom event this trigger subscribes to (-1 = none;
            /// built-in events only). Resolved AGAINST the scenario's closed custom-event registry by
            /// <see cref="EventDispatchPlan.TryBuild"/> at load — BuildExecutionOrder has no registry, so it
            /// leaves the -1 default.</summary>
            public int              SubscribedCustomEvent { get; set; } = -1;

            /// <summary>Story 7.5 — true when any of this trigger's compiled programs (condition / set_variable
            /// value / raise-argument) reads an <c>event.&lt;param&gt;</c>. A param-reading trigger dispatches
            /// once per matching occurrence (per-occurrence is opt-in by construction — legacy triggers keep
            /// once-per-tick). Set at load (plan + director compile), not here.</summary>
            public bool             ReadsEventParams { get; set; }
        }

        /// <summary>
        /// Build the direct-execution view of this graph in the TOTAL trigger order (Priority desc, then ascending
        /// persistent node-id — for a <see cref="FromFlat"/> graph this equals Priority desc, then declaration
        /// index, so legacy flat scenarios execute in exactly the legacy order). Per trigger, the events/conditions
        /// are resolved by the SAME deterministic edge-walk as <see cref="ToFlat"/> (ascending source id), and the
        /// action chain follows the exec edges out of the trigger, collecting <see cref="ActionNode"/> AND
        /// <see cref="EffectActionNode"/> — reusing 7.2's fail-closed cycle guard (a hand-built/JSON cycle rejects
        /// with a located <see cref="JsonException"/> instead of spinning unbounded).
        /// </summary>
        public List<TriggerExec> BuildExecutionOrder()
        {
            var byId = new Dictionary<int, NodeBase>(Nodes.Count);
            foreach (NodeBase n in Nodes)
                byId[n.Id] = n;

            // Total order: Priority desc, then ascending id (a stable OrderByDescending/ThenBy — no Array.Sort).
            List<TriggerNode> triggerNodes = Nodes.OfType<TriggerNode>()
                .OrderByDescending(n => n.Priority).ThenBy(n => n.Id).ToList();

            List<ExecEdge> sortedExec = ExecEdges.OrderBy(x => x).ToList();
            var result = new List<TriggerExec>(triggerNodes.Count);

            foreach (TriggerNode tn in triggerNodes)
            {
                EventNode[] events = ExecEdges
                    .Where(e => e.Dst == tn.Id && e.DstPort == TriggerEventInPort && byId.ContainsKey(e.Src))
                    .Select(e => byId[e.Src]).OfType<EventNode>().OrderBy(n => n.Id).ToArray();

                ConditionNode[] conditions = DataEdges
                    .Where(e => e.Dst == tn.Id && e.DstPort == TriggerConditionInPort && byId.ContainsKey(e.Src))
                    .Select(e => byId[e.Src]).OfType<ConditionNode>().OrderBy(n => n.Id).ToArray();

                // Story 7.4 — expression roots wired into the SAME condition-in port. They are not ConditionNodes
                // (the OfType above drops them), so surface their ids separately, ascending (deterministic).
                int[] condExprRoots = DataEdges
                    .Where(e => e.Dst == tn.Id && e.DstPort == TriggerConditionInPort
                             && byId.TryGetValue(e.Src, out NodeBase? src) && ExprCompiler.IsExprNode(src))
                    .Select(e => e.Src).Distinct().OrderBy(id => id).ToArray();

                // Walk the exec chain out of the trigger: Trigger → node0 → … (ActionNode, EffectActionNode, or —
                // Story 7.5 — RaiseEventNode; raise nodes chain exactly like actions on the same exec ports).
                var actions = new List<NodeBase>();
                int currentId = tn.Id, currentPort = TriggerExecOutPort;
                var visited = new HashSet<int> { currentId };
                while (true)
                {
                    NodeBase? next = null;
                    foreach (ExecEdge e in sortedExec)
                    {
                        if (e.Src == currentId && e.SrcPort == currentPort
                            && byId.TryGetValue(e.Dst, out NodeBase? nb)
                            && (nb is ActionNode || nb is EffectActionNode || nb is RaiseEventNode))
                        {
                            next = nb;
                            break;
                        }
                    }
                    if (next is null) break;
                    if (!visited.Add(next.Id))
                        throw new JsonException(
                            $"exec chain cycle at node {next.Id} (trigger '{tn.Name}', id {tn.Id}): the action chain must be acyclic.");
                    actions.Add(next);
                    currentId   = next.Id;
                    currentPort = ActionExecOutPort;
                }

                // Story 7.4 — per-action value-in expression root (-1 = none): the lowest expr-node src data-wired
                // into the action's value-in port (deterministic first; forks are rejected at the validator gate).
                // Story 7.5 — a RaiseEventNode never takes a value-in expression (its data ports are raise args),
                // so it is skipped here and resolved into raiseArgRoots below instead.
                var valueRoots    = new int[actions.Count];
                var raiseArgRoots = new int[]?[actions.Count];
                for (int i = 0; i < actions.Count; i++)
                {
                    valueRoots[i] = -1;
                    if (actions[i] is RaiseEventNode)
                    {
                        // Raise-arg roots: per port 0..MaxEventParams-1, the lowest expr-node src (deterministic
                        // first; missing/forked/extra edges are located rejects at gate/compile, not here).
                        var roots = new int[EventBounds.MaxEventParams];
                        for (int p = 0; p < roots.Length; p++) roots[p] = -1;
                        foreach (DataEdge e in DataEdges)
                            if (e.Dst == actions[i].Id && e.DstPort >= 0 && e.DstPort < EventBounds.MaxEventParams
                                && byId.TryGetValue(e.Src, out NodeBase? rsrc) && ExprCompiler.IsExprNode(rsrc)
                                && (roots[e.DstPort] < 0 || e.Src < roots[e.DstPort]))
                                roots[e.DstPort] = e.Src;
                        raiseArgRoots[i] = roots;
                        continue;
                    }
                    foreach (DataEdge e in DataEdges)
                        if (e.Dst == actions[i].Id && e.DstPort == ActionValueInPort
                            && byId.TryGetValue(e.Src, out NodeBase? src) && ExprCompiler.IsExprNode(src)
                            && (valueRoots[i] < 0 || e.Src < valueRoots[i]))
                            valueRoots[i] = e.Src;
                }

                result.Add(new TriggerExec
                {
                    Trigger              = tn,
                    Events               = events,
                    Conditions           = conditions,
                    Actions              = actions.ToArray(),
                    ConditionExprRoots   = condExprRoots,
                    ActionValueExprRoots = valueRoots,
                    RaiseArgRoots        = raiseArgRoots,
                });
            }

            return result;
        }

        /// <summary>
        /// Canonical serialization: nodes emitted sorted by ascending id; exec/data edges each sorted by
        /// <c>(Src,SrcPort,Dst,DstPort)</c>. Uses <see cref="DslJson.Options"/> (the closed-registry
        /// <see cref="NodeBaseJsonConverter"/> + <c>FixedJsonConverter</c>). Two structurally-equal graphs (nodes/
        /// edges added out of order) serialize byte-identically.
        /// </summary>
        public string ToCanonicalJson()
        {
            var shape = new GraphJsonShape
            {
                Nodes     = Nodes.OrderBy(n => n.Id).ToList(),
                ExecEdges = ExecEdges.OrderBy(e => e).ToList(),
                DataEdges = DataEdges.OrderBy(e => e).ToList(),
            };
            return JsonSerializer.Serialize(shape, DslJson.Options);
        }

        /// <summary>Deserialize a graph from canonical (or any) JSON via <see cref="DslJson.Options"/>.
        /// Node ids must be non-negative (parse-level sanity, not 7.7 structural validation): every graph this module
        /// emits is 0-based, and <see cref="Merge"/>'s id-offset scheme assumes it — a negative authored id could
        /// alias onto an existing node after offsetting and silently drop/rewire a trigger.</summary>
        public static TriggerGraph FromJson(string json)
        {
            GraphJsonShape shape = JsonSerializer.Deserialize<GraphJsonShape>(json, DslJson.Options)
                ?? throw new JsonException("Graph JSON deserialized to null.");
            var graph = new TriggerGraph();
            if (shape.Nodes is not null)     graph.Nodes.AddRange(shape.Nodes);
            if (shape.ExecEdges is not null) graph.ExecEdges.AddRange(shape.ExecEdges);
            if (shape.DataEdges is not null) graph.DataEdges.AddRange(shape.DataEdges);
            foreach (NodeBase n in graph.Nodes)
                if (n.Id < 0)
                    throw new JsonException($"graph node id {n.Id} must be non-negative (canonical node ids are 0-based).");
            return graph;
        }

        /// <summary>The on-the-wire JSON shape: the three canonical lists. Nodes (de)serialize through the registered
        /// <see cref="NodeBaseJsonConverter"/>; edges through their JSON-property layout.</summary>
        private sealed class GraphJsonShape
        {
            [JsonPropertyName("nodes")]      public List<NodeBase> Nodes     { get; set; } = new();
            [JsonPropertyName("exec_edges")] public List<ExecEdge> ExecEdges { get; set; } = new();
            [JsonPropertyName("data_edges")] public List<DataEdge> DataEdges { get; set; } = new();
        }
    }
}
