#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Definitions; // ScenarioCustomEvent / ScenarioEventParam (the closed registry POCOs)

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.5 — the SHARED, Godot-free load-time analysis of the custom-event layer, run at BOTH the
    /// <c>ScenarioValidator</c> gate (surfaced as located <c>ValidationResult.Fail</c>) and the failure-atomic
    /// <c>ScenarioDirector.LoadScenario</c> backstop (surfaced as located throws) — one routine, so the two gates
    /// can never drift. It:
    ///
    ///   • validates the CLOSED custom-event registry (<see cref="ValidateRegistry"/>): unique non-blank names
    ///     disjoint from the built-in event-kind set, count ≤ <see cref="EventBounds.MaxCustomEvents"/>; param
    ///     count ≤ <see cref="EventBounds.MaxEventParams"/>, unique identifier names, types ∈ {Int, Fixed, Bool};
    ///     allowed-raiser slots in engine range, no duplicates;
    ///   • validates graph usage: <c>custom_event</c> subscriptions name a declared event; a custom-subscribing
    ///     trigger has exactly ONE EventNode; <c>raise_event</c> targets are declared, each declared param port
    ///     carries exactly one expression data edge of the declared type/wire (missing/forked/extra/mistyped args
    ///     are located rejects), and the authored raiser is −1 (system) or ∈ the event's allowed_raisers
    ///     (load-time membership only — runtime raiser enforcement on the lockstep bus is Story 7.9);
    ///   • compiles every raise-argument expression against the OWNING trigger's event-parameter map;
    ///   • PROVES the same-tick event-dispatch graph ACYCLIC (edge E1→E2 when a trigger subscribed to E1 has a
    ///     same-tick raise of E2; <c>next_tick</c> raises excluded; unsubscribed triggers are roots) and enforces
    ///     <see cref="EventBounds.MaxEventFanOut"/> / <see cref="EventBounds.MaxEventCascadeDepth"/> /
    ///     <see cref="EventBounds.MaxCascadeOps"/> (memoized worst-case transitive dispatch ops per root
    ///     occurrence) — every reject names its constant.
    ///
    /// The successful result carries everything the runtime needs (registry names/param counts, per-exec
    /// subscription indices, compiled raise plans, and per-trigger/action event-parameter maps for the callers'
    /// own condition/value compiles), so the tick allocates nothing and re-derives nothing.
    /// </summary>
    public sealed class EventDispatchPlan
    {
        /// <summary>The built-in <c>unit_dies</c> payload map: victim (EntityRef), killer (EntityRef, −1 if none),
        /// killer_faction (FactionRef, −1 if none) — ref-typed params SURFACE Int (opaque raw handles, the one
        /// sanctioned ref→Int surface). Frame slots 0/1/2.</summary>
        public static readonly IReadOnlyDictionary<string, (int Slot, DslValueType Type)> UnitDiesParams =
            new Dictionary<string, (int, DslValueType)>(StringComparer.Ordinal)
            {
                ["victim"]         = (0, DslValueType.Int),
                ["killer"]         = (1, DslValueType.Int),
                ["killer_faction"] = (2, DslValueType.Int),
            };

        /// <summary>Number of frame slots the built-in <c>unit_dies</c> payload occupies.</summary>
        public const int UnitDiesParamCount = 3;

        /// <summary>Declared event names, registry (declaration) order — the runtime dispatch identity.</summary>
        public string[] EventNames = Array.Empty<string>();
        /// <summary>Per event: its declared param count (the runtime frame width).</summary>
        public int[] EventParamCounts = Array.Empty<int>();
        /// <summary>Per event: its declared param types, declaration order (raise-arg type checks).</summary>
        public DslValueType[][] EventParamTypes = Array.Empty<DslValueType[]>();
        /// <summary>Story 7.9 — per event (registry order): the declared allowed-raiser faction slots (empty ⇒
        /// system-only). Precomputed for O(1) RUNTIME raiser authorization of a button-originated raise on the
        /// lockstep bus (<c>ScenarioDirector.TryEnqueueExternalDslEvent</c>) — the load-time mirror of 7.5's
        /// authored <c>raise_event.raiser</c> membership, at the new runtime seam.</summary>
        public int[][] EventAllowedRaisers = Array.Empty<int[]>();

        /// <summary>Per exec (parallel to the execs list analyzed): the subscribed custom event index (−1 none).</summary>
        public int[] SubscribedEvent = Array.Empty<int>();
        /// <summary>Per exec, per action (parallel to <c>TriggerExec.Actions</c>): the compiled raise plan, or null.</summary>
        public RaiseCompiled?[][] Raises = Array.Empty<RaiseCompiled?[]>();
        /// <summary>Per exec: true when any RAISE-ARG program reads event params (the caller ORs in its own
        /// condition/value program reads to derive the final per-occurrence flag).</summary>
        public bool[] RaiseArgsReadEventParams = Array.Empty<bool>();

        // Per trigger-node-id AND per chained-action-node-id: the owning trigger's event-parameter map (only
        // triggers with a single param-declaring subscription appear; everything else maps to null).
        private readonly Dictionary<int, IReadOnlyDictionary<string, (int Slot, DslValueType Type)>> _paramMapByNodeId = new();

        /// <summary>The event-parameter map for expression compiles owned by node <paramref name="nodeId"/> (a
        /// trigger head or a chained action node), or null when no single param-declaring subscription applies.</summary>
        public IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? ParamMapFor(int nodeId) =>
            _paramMapByNodeId.TryGetValue(nodeId, out var map) ? map : null;

        /// <summary>One compiled <c>raise_event</c> action: target registry index, next-tick flag, authored raiser,
        /// and the per-declared-param argument programs (evaluated against the current dispatch frame at raise).</summary>
        public sealed class RaiseCompiled
        {
            public int EventIndex;
            public bool NextTick;
            public int Raiser;
            public ExprProgram[] ArgPrograms = Array.Empty<ExprProgram>();
        }

        /// <summary>The SURFACED parameter map of a declared custom event (custom params are Int/Fixed/Bool only,
        /// so surfaced == declared). Deterministic declaration-order slots.</summary>
        public static IReadOnlyDictionary<string, (int Slot, DslValueType Type)> ParamMapOf(ScenarioCustomEvent ev)
        {
            var map = new Dictionary<string, (int, DslValueType)>(StringComparer.Ordinal);
            ScenarioEventParam[] ps = ev.Params ?? Array.Empty<ScenarioEventParam>();
            for (int i = 0; i < ps.Length; i++)
                if (ps[i] != null && !string.IsNullOrEmpty(ps[i].Name))
                    map[ps[i].Name] = (i, ps[i].Type);
            return map;
        }

        /// <summary>
        /// Validate the closed custom-event registry alone (a scenario may declare events without any graph using
        /// them yet). Returns a located error string naming the offending declaration (and, for caps, the
        /// <see cref="EventBounds"/> constant), or null when valid. <paramref name="maxRaiserSlotExclusive"/> is
        /// the engine faction-slot ceiling + 1 (the validator's CheckFactionSlot bound, passed in so this layer
        /// stays Core-policy-free).
        /// </summary>
        public static string? ValidateRegistry(IReadOnlyList<ScenarioCustomEvent>? events, int maxRaiserSlotExclusive)
        {
            if (events == null || events.Count == 0) return null;
            if (events.Count > EventBounds.MaxCustomEvents)
                return $"custom_events declares {events.Count} events, exceeding EventBounds.MaxCustomEvents={EventBounds.MaxCustomEvents}.";

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < events.Count; i++)
            {
                ScenarioCustomEvent ev = events[i];
                if (ev is null) return $"custom_events[{i}] is null.";
                if (string.IsNullOrWhiteSpace(ev.Name))
                    return $"custom_events[{i}].name must be a non-empty name.";
                if (NodeKinds.InSet(NodeKinds.EventTypes, ev.Name) || ev.Name == NodeKinds.CustomEvent)
                    return $"custom_events[{i}].name='{ev.Name}' shadows a built-in event kind.";
                if (!seen.Add(ev.Name))
                    return $"custom_events[{i}].name='{ev.Name}' is a duplicate.";

                ScenarioEventParam[] ps = ev.Params ?? Array.Empty<ScenarioEventParam>();
                if (ps.Length > EventBounds.MaxEventParams)
                    return $"custom_events[{i}] ('{ev.Name}') declares {ps.Length} params, exceeding EventBounds.MaxEventParams={EventBounds.MaxEventParams}.";
                var seenParams = new HashSet<string>(StringComparer.Ordinal);
                for (int p = 0; p < ps.Length; p++)
                {
                    if (ps[p] is null) return $"custom_events[{i}].params[{p}] is null.";
                    if (!ExprParser.IsIdentifier(ps[p].Name))
                        return $"custom_events[{i}].params[{p}].name='{ps[p].Name}' must be a non-empty identifier (letters/digits/underscore, not digit-leading) so event.<name> can read it.";
                    if (!seenParams.Add(ps[p].Name))
                        return $"custom_events[{i}].params[{p}].name='{ps[p].Name}' is a duplicate.";
                    if (ps[p].Type != DslValueType.Int && ps[p].Type != DslValueType.Fixed && ps[p].Type != DslValueType.Bool)
                        return $"custom_events[{i}].params[{p}] ('{ps[p].Name}') is {ps[p].Type}-typed; custom-event params are Int/Fixed/Bool only.";
                }

                int[] raisers = ev.AllowedRaisers ?? Array.Empty<int>();
                var seenRaisers = new HashSet<int>();
                for (int r = 0; r < raisers.Length; r++)
                {
                    if (raisers[r] < 0 || raisers[r] >= maxRaiserSlotExclusive)
                        return $"custom_events[{i}].allowed_raisers[{r}]={raisers[r]} is out of the valid faction-slot range [0,{maxRaiserSlotExclusive - 1}].";
                    if (!seenRaisers.Add(raisers[r]))
                        return $"custom_events[{i}].allowed_raisers[{r}]={raisers[r]} is a duplicate.";
                }
            }
            return null;
        }

        /// <summary>Map every ActionNode nested inside container sub-chains (for_each/for_each_batched body,
        /// branch then/else, any depth) to the owning trigger's event-parameter map — the flat top-level
        /// <c>Actions</c> projection omits them, but their value-in expressions compile against the same map at
        /// both gates. Recursion depth is bounded by the exec walk that built the Items tree.</summary>
        private static void MapNestedActions(TriggerGraph.ExecItem[] items,
            IReadOnlyDictionary<string, (int Slot, DslValueType Type)> paramMap,
            Dictionary<int, IReadOnlyDictionary<string, (int Slot, DslValueType Type)>> byNodeId)
        {
            foreach (TriggerGraph.ExecItem it in items)
            {
                if (it.Node is ActionNode na) byNodeId[na.Id] = paramMap;
                MapNestedActions(it.Body, paramMap, byNodeId);
                MapNestedActions(it.Then, paramMap, byNodeId);
                MapNestedActions(it.Else, paramMap, byNodeId);
            }
        }

        /// <summary>Locate a <see cref="RaiseEventNode"/> anywhere BELOW the top-level action chain (inside a
        /// for_each/for_each_batched body or a branch then/else sub-chain, any depth). Breadth-first over the
        /// nested exec view in item order → deterministic first-fail; iterative (explicit queue), so hostile
        /// nesting depth can never stack-overflow the plan build.</summary>
        private static string? FindNestedRaise(TriggerGraph.TriggerExec ex)
        {
            var queue = new Queue<TriggerGraph.ExecItem>();
            foreach (TriggerGraph.ExecItem top in ex.Items)
            {
                foreach (TriggerGraph.ExecItem c in top.Body) queue.Enqueue(c);
                foreach (TriggerGraph.ExecItem c in top.Then) queue.Enqueue(c);
                foreach (TriggerGraph.ExecItem c in top.Else) queue.Enqueue(c);
            }
            while (queue.Count > 0)
            {
                TriggerGraph.ExecItem it = queue.Dequeue();
                if (it.Node is RaiseEventNode rn)
                    return $"trigger '{ex.Trigger.Name}' (node {ex.Trigger.Id}): raise_event (node {rn.Id}) inside a for_each/for_each_batched/branch body is not supported (top-level action chain only).";
                foreach (TriggerGraph.ExecItem c in it.Body) queue.Enqueue(c);
                foreach (TriggerGraph.ExecItem c in it.Then) queue.Enqueue(c);
                foreach (TriggerGraph.ExecItem c in it.Else) queue.Enqueue(c);
            }
            return null;
        }

        /// <summary>
        /// Run the FULL 7.5 load-time analysis over one execution view (the validator passes the graph channel's
        /// execs; the director passes the merged flat+graph execs — flat triggers carry no 7.5 nodes, so the two
        /// agree by construction). Returns false with a located <paramref name="error"/> on the first violation;
        /// on success <paramref name="plan"/> carries the registry, per-exec subscriptions, compiled raise plans,
        /// and event-parameter maps. Also stamps <c>TriggerExec.SubscribedCustomEvent</c> on the analyzed execs.
        /// Pure and throw-free.
        /// </summary>
        public static bool TryBuild(
            IReadOnlyList<ScenarioCustomEvent>? declaredEvents,
            TriggerGraph graph,
            IReadOnlyList<TriggerGraph.TriggerExec> execs,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVars,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)>? arrayDecls,
            int maxRaiserSlotExclusive,
            out EventDispatchPlan? plan, out string? error)
        {
            plan = null;

            error = ValidateRegistry(declaredEvents, maxRaiserSlotExclusive);
            if (error != null) return false;

            var result = new EventDispatchPlan();
            int eventCount = declaredEvents?.Count ?? 0;
            result.EventNames         = new string[eventCount];
            result.EventParamCounts   = new int[eventCount];
            result.EventParamTypes    = new DslValueType[eventCount][];
            result.EventAllowedRaisers = new int[eventCount][];
            var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
            var paramMaps   = new IReadOnlyDictionary<string, (int Slot, DslValueType Type)>[eventCount];
            for (int i = 0; i < eventCount; i++)
            {
                ScenarioCustomEvent ev = declaredEvents![i];
                ScenarioEventParam[] ps = ev.Params ?? Array.Empty<ScenarioEventParam>();
                result.EventNames[i]       = ev.Name;
                result.EventParamCounts[i] = ps.Length;
                var types = new DslValueType[ps.Length];
                for (int p = 0; p < ps.Length; p++) types[p] = ps[p].Type;
                result.EventParamTypes[i]  = types;
                result.EventAllowedRaisers[i] = ev.AllowedRaisers ?? Array.Empty<int>(); // Story 7.9 — runtime auth lookup
                indexByName[ev.Name]       = i;
                paramMaps[i]               = ParamMapOf(ev);
            }

            // ── Per-node checks over the WHOLE graph (fail-closed even for exec-unreachable nodes): a
            //    custom_event subscription / raise_event must name a declared event; an authored raiser must be
            //    −1 or ∈ the target's allowed_raisers. Ascending node id → deterministic first-fail. ──
            foreach (NodeBase node in graph.Nodes.OrderBy(n => n.Id))
            {
                if (node is EventNode ce && ce.Kind == NodeKinds.CustomEvent)
                {
                    if (string.IsNullOrEmpty(ce.EventName) || !indexByName.ContainsKey(ce.EventName!))
                    {
                        error = $"custom_event node {node.Id}: event_name '{ce.EventName}' is not a declared custom event.";
                        return false;
                    }
                }
                else if (node is RaiseEventNode rn)
                {
                    if (string.IsNullOrEmpty(rn.Name) || !indexByName.TryGetValue(rn.Name, out int target))
                    {
                        error = $"raise_event node {node.Id}: '{rn.Name}' is not a declared custom event.";
                        return false;
                    }
                    if (rn.Raiser != -1)
                    {
                        int[] allowed = declaredEvents![target].AllowedRaisers ?? Array.Empty<int>();
                        if (Array.IndexOf(allowed, rn.Raiser) < 0)
                        {
                            error = $"raise_event node {node.Id}: raiser {rn.Raiser} is not in '{rn.Name}' allowed_raisers (-1 = system is always legal).";
                            return false;
                        }
                    }
                }
            }

            var byId = new Dictionary<int, NodeBase>(graph.Nodes.Count);
            foreach (NodeBase n in graph.Nodes)
                byId[n.Id] = n;
            List<DataEdge> sortedData = graph.DataEdges.OrderBy(e => e).ToList();

            result.SubscribedEvent          = new int[execs.Count];
            result.Raises                   = new RaiseCompiled?[execs.Count][];
            result.RaiseArgsReadEventParams = new bool[execs.Count];

            // Per-raise-action per-port edge counter (hoisted out of the loops — CA2014; cleared per action).
            Span<int> perPort = stackalloc int[EventBounds.MaxEventParams];

            for (int i = 0; i < execs.Count; i++)
            {
                TriggerGraph.TriggerExec ex = execs[i];
                result.SubscribedEvent[i] = -1;
                result.Raises[i] = new RaiseCompiled?[ex.Actions.Length];

                // ── raise_event is TOP-LEVEL-ONLY: the exec walk admits raise nodes inside container sub-chains
                //    (for_each/for_each_batched body, branch then/else), but raise plans key off the flat
                //    top-level Actions — a nested raise would build an ExecItem yet never earn a plan and
                //    silently no-op at runtime. Fail-closed instead. ──
                error = FindNestedRaise(ex);
                if (error != null) return false;

                // ── Subscription: a custom-subscribing trigger has exactly ONE EventNode. ──
                int customIdx = -1;
                foreach (EventNode en in ex.Events)
                {
                    if (en.Kind != NodeKinds.CustomEvent) continue;
                    if (ex.Events.Length != 1)
                    {
                        error = $"trigger '{ex.Trigger.Name}' (node {ex.Trigger.Id}): a trigger subscribing to a custom event must have exactly one event node (found {ex.Events.Length}).";
                        return false;
                    }
                    customIdx = indexByName[en.EventName!]; // membership proven in the per-node pass above
                }
                result.SubscribedEvent[i] = customIdx;
                ex.SubscribedCustomEvent = customIdx;

                // ── The trigger's event-parameter map (the single-subscription rule): a declared custom event's
                //    params, or the built-in unit_dies payload — exactly one EventNode either way. ──
                IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? paramMap = null;
                if (customIdx >= 0)
                    paramMap = paramMaps[customIdx];
                else if (ex.Events.Length == 1 && ex.Events[0].Kind == "unit_dies")
                    paramMap = UnitDiesParams;
                if (paramMap != null)
                {
                    result._paramMapByNodeId[ex.Trigger.Id] = paramMap;
                    foreach (NodeBase act in ex.Actions)
                        result._paramMapByNodeId[act.Id] = paramMap;
                    // Nested actions (7.6 container bodies) carry the same map: the director compiles the whole
                    // Items tree with the owning trigger's map, and the validator's data-edge loop keys by the
                    // consuming action's id — parity requires nested set_variable values to resolve it too.
                    MapNestedActions(ex.Items, paramMap, result._paramMapByNodeId);
                }

                // ── raise_event actions: declared-port arg edges (exactly one per port, expression src, canonical
                //    src port, declared wire) + arg compile against the OWNING trigger's map. ──
                for (int j = 0; j < ex.Actions.Length; j++)
                {
                    if (ex.Actions[j] is not RaiseEventNode rn) continue;
                    int target = indexByName[rn.Name];
                    int declCount = result.EventParamCounts[target];
                    DslValueType[] declTypes = result.EventParamTypes[target];

                    // Edge-shape scan (canonical order → deterministic first-fail): count per port; extra ports.
                    perPort.Clear();
                    foreach (DataEdge de in sortedData)
                    {
                        if (de.Dst != rn.Id) continue;
                        if (de.DstPort < 0 || de.DstPort >= declCount)
                        {
                            error = $"raise_event node {rn.Id}: event '{rn.Name}' declares {declCount} parameter(s), but port {de.DstPort} has an incoming data edge.";
                            return false;
                        }
                        bool srcIsExpr = byId.TryGetValue(de.Src, out NodeBase? src) && ExprCompiler.IsExprNode(src);
                        if (!srcIsExpr)
                        {
                            error = $"raise_event node {rn.Id}: the argument edge into port {de.DstPort} has a non-expression source (node {de.Src}).";
                            return false;
                        }
                        if (de.SrcPort != TriggerGraph.ExprDataOutPort)
                        {
                            error = $"raise_event node {rn.Id}: the argument edge into port {de.DstPort} leaves src node {de.Src} on port {de.SrcPort}; expression nodes emit only on port {TriggerGraph.ExprDataOutPort}.";
                            return false;
                        }
                        if (de.Wire != ExprCompiler.WireOf(declTypes[de.DstPort]))
                        {
                            error = $"raise_event node {rn.Id}: the argument edge into port {de.DstPort} carries wire '{de.Wire}' but '{rn.Name}' declares that param {declTypes[de.DstPort]} (wire color must equal the declared type).";
                            return false;
                        }
                        perPort[de.DstPort]++;
                    }
                    for (int p = 0; p < declCount; p++)
                    {
                        if (perPort[p] == 0)
                        {
                            error = $"raise_event node {rn.Id}: event '{rn.Name}' param {p} has no argument edge (exactly one expression data edge required per declared param).";
                            return false;
                        }
                        if (perPort[p] > 1)
                        {
                            error = $"raise_event node {rn.Id}: event '{rn.Name}' param {p} has {perPort[p]} argument edges (forked; exactly one required).";
                            return false;
                        }
                    }

                    // Compile each arg program from the exec view's resolved roots (deterministic; edge shape
                    // proven exactly-one above, so the resolved root IS the single edge's src).
                    int[]? roots = j < ex.RaiseArgRoots.Length ? ex.RaiseArgRoots[j] : null;
                    var argPrograms = new ExprProgram[declCount];
                    for (int p = 0; p < declCount; p++)
                    {
                        int root = roots != null && p < roots.Length ? roots[p] : -1;
                        if (root < 0)
                        {
                            error = $"raise_event node {rn.Id}: event '{rn.Name}' param {p} resolved no expression root.";
                            return false;
                        }
                        if (!ExprCompiler.TryCompile(graph, root, declaredVars, inCondition: false, paramMap,
                                out ExprProgram? ap, out string? aErr, arrayDecls))
                        {
                            error = $"raise_event node {rn.Id} argument {p}: {aErr}";
                            return false;
                        }
                        if (ap!.ResultType != declTypes[p])
                        {
                            error = $"raise_event node {rn.Id}: argument {p} evaluates {ap.ResultType} but '{rn.Name}' declares that param {declTypes[p]}.";
                            return false;
                        }
                        argPrograms[p] = ap;
                        result.RaiseArgsReadEventParams[i] |= ap.ReadsEventParams;
                    }

                    result.Raises[i][j] = new RaiseCompiled
                    {
                        EventIndex  = target,
                        NextTick    = rn.NextTick,
                        Raiser      = rn.Raiser,
                        ArgPrograms = argPrograms,
                    };
                }
            }

            // ── DAG proof + cascade cost caps over the same-tick dispatch graph. ──
            error = ProveDagAndCaps(result, execs);
            if (error != null) return false;

            plan = result;
            error = null;
            return true;
        }

        /// <summary>
        /// The same-tick dispatch-graph proof: fan-out per event, acyclicity (cycle path named), longest same-tick
        /// cascade depth, and the memoized worst-case transitive dispatch ops from any single root occurrence.
        /// Every reject is a located error naming its <see cref="EventBounds"/> constant.
        /// </summary>
        private static string? ProveDagAndCaps(EventDispatchPlan plan, IReadOnlyList<TriggerGraph.TriggerExec> execs)
        {
            int eventCount = plan.EventNames.Length;
            if (eventCount == 0) return null;

            // Subscribers per event (ascending exec order — the dispatch order itself).
            var subscribers = new List<int>[eventCount];
            for (int e = 0; e < eventCount; e++) subscribers[e] = new List<int>();
            for (int i = 0; i < execs.Count; i++)
                if (plan.SubscribedEvent[i] >= 0)
                    subscribers[plan.SubscribedEvent[i]].Add(i);

            for (int e = 0; e < eventCount; e++)
                if (subscribers[e].Count > EventBounds.MaxEventFanOut)
                    return $"event '{plan.EventNames[e]}' has {subscribers[e].Count} subscribed triggers, exceeding EventBounds.MaxEventFanOut={EventBounds.MaxEventFanOut}.";

            // Same-tick edges E1→E2 (next_tick raises excluded — they ride the bounded, checksummed queue).
            var sameTickTargets = new List<int>[eventCount]; // per SUBSCRIBED exec's raises, grouped per event
            for (int e = 0; e < eventCount; e++)
            {
                sameTickTargets[e] = new List<int>();
                foreach (int i in subscribers[e])
                    foreach (RaiseCompiled? rc in plan.Raises[i])
                        if (rc != null && !rc.NextTick)
                            sameTickTargets[e].Add(rc.EventIndex);
            }

            // Cycle detection: DFS with tri-color marking + an explicit path stack so the located error NAMES the
            // cycle (E1→E2→E1). Deterministic ascending-event-index roots.
            var color = new byte[eventCount]; // 0 white, 1 on-path, 2 done
            var path  = new List<int>();
            for (int e = 0; e < eventCount; e++)
            {
                string? cyc = CycleDfs(e, sameTickTargets, color, path, plan.EventNames);
                if (cyc != null) return cyc;
            }

            // Depth (longest same-tick path, counted in events) + memoized worst-case transitive dispatch ops per
            // root OCCURRENCE (acyclicity proven above, so memoization terminates). Computed in long so the check
            // itself never overflows before the cap trips.
            var depthMemo = new int[eventCount];
            var opsMemo   = new long[eventCount];
            var computed  = new bool[eventCount];
            for (int e = 0; e < eventCount; e++)
            {
                ComputeCost(e, subscribers, plan, depthMemo, opsMemo, computed);
                if (depthMemo[e] > EventBounds.MaxEventCascadeDepth)
                    return $"event '{plan.EventNames[e]}' starts a same-tick cascade {depthMemo[e]} events deep, exceeding EventBounds.MaxEventCascadeDepth={EventBounds.MaxEventCascadeDepth}.";
                if (opsMemo[e] > EventBounds.MaxCascadeOps)
                    return $"event '{plan.EventNames[e]}' costs {opsMemo[e]} worst-case transitive dispatch ops per occurrence, exceeding EventBounds.MaxCascadeOps={EventBounds.MaxCascadeOps}.";
            }
            return null;
        }

        private static string? CycleDfs(int e, List<int>[] targets, byte[] color, List<int> path, string[] names)
        {
            if (color[e] == 2) return null;
            if (color[e] == 1)
            {
                // Back edge: name the cycle path from the first occurrence of e to here, then back to e.
                int start = path.IndexOf(e);
                var cycle = new List<string>();
                for (int k = start; k < path.Count; k++) cycle.Add(names[path[k]]);
                cycle.Add(names[e]);
                return $"same-tick event dispatch cycle: {string.Join("→", cycle)} (break it with a next_tick raise).";
            }
            color[e] = 1;
            path.Add(e);
            foreach (int t in targets[e])
            {
                string? cyc = CycleDfs(t, targets, color, path, names);
                if (cyc != null) return cyc;
            }
            path.RemoveAt(path.Count - 1);
            color[e] = 2;
            return null;
        }

        private static void ComputeCost(int e, List<int>[] subscribers, EventDispatchPlan plan,
            int[] depthMemo, long[] opsMemo, bool[] computed)
        {
            if (computed[e]) return;
            computed[e] = true; // safe pre-mark: the graph is proven acyclic before this runs

            int maxChildDepth = 0;
            long ops = 0;
            foreach (int i in subscribers[e])
            {
                ops += 1; // one dispatch of trigger i per occurrence of e
                foreach (RaiseCompiled? rc in plan.Raises[i])
                {
                    if (rc == null || rc.NextTick) continue;
                    ComputeCost(rc.EventIndex, subscribers, plan, depthMemo, opsMemo, computed);
                    if (depthMemo[rc.EventIndex] > maxChildDepth) maxChildDepth = depthMemo[rc.EventIndex];
                    ops += opsMemo[rc.EventIndex];
                    if (ops > EventBounds.MaxCascadeOps) ops = EventBounds.MaxCascadeOps + 1; // saturate (cap already tripped)
                }
            }
            depthMemo[e] = 1 + maxChildDepth;
            opsMemo[e]   = ops;
        }
    }
}
