#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.10 — a LOCATED structural error: the offending node's persistent <see cref="NodeId"/> plus the
    /// exact prose <see cref="Message"/> the string gate produces. <see cref="NodeId"/> is −1 when the error has no
    /// single node locus (a null graph / null node entry — surfaced on the panel status line, not badged).
    /// </summary>
    public readonly struct GraphNodeError
    {
        /// <summary>The offending node's persistent id (−1 = no single-node locus).</summary>
        public int NodeId { get; }
        /// <summary>The located error prose (byte-identical to <see cref="GraphStructureGate.Check"/>'s output).</summary>
        public string Message { get; }

        public GraphNodeError(int nodeId, string message)
        {
            NodeId = nodeId;
            Message = message;
        }
    }

    /// <summary>
    /// Story 7.7 — the WHOLE-GRAPH structural rulebook (the deep validation every prior story deferred here),
    /// shared — like <c>DslLoopGate</c> — by BOTH load gates: <c>ScenarioValidator</c> (the authoritative pre-tick
    /// gate, wrapping the return in <c>ValidationResult.Fail</c>) and <c>ScenarioDirector.LoadScenario</c> (the
    /// fail-closed backstop for direct callers, wrapping it in a located <c>JsonException</c>). ONE implementation,
    /// invoked UNCONDITIONALLY at both, so the rules are identical by construction.
    ///
    /// It runs over the WHOLE graph — unreachable nodes included. Checks, in deterministic first-fail order:
    ///   • duplicate node ids reject;
    ///   • every exec/data edge endpoint must exist (dangling edges reject);
    ///   • exec/data port legality per node kind from the ONE <see cref="NodePorts"/> table (a stray data edge
    ///     into a non-data port, or from a non-data source, rejects);
    ///   • forked exec edges — two exec edges out of one <c>(src, srcPort)</c> — reject (the 7.2/7.6 "first-match"
    ///     tolerance is retired);
    ///   • forked data edges into one <c>(dst, dstPort)</c> reject, generalizing the 7.4/7.6 value-in / branch
    ///     cond-in / index-in fork rejects — EXCEPT the trigger condition-in port, whose fan-in is the sanctioned
    ///     multi-condition AND wiring;
    ///   • expression subgraphs are compile-checked EVEN WHEN UNCONSUMED (no orphan-node semantic skip): every
    ///     expression root — an expr node not consumed as another expr node's operand — must compile under the
    ///     full <see cref="ExprCompiler"/> rulebook, and every expr node must be reachable from some root (a
    ///     root-less, mutually-consuming expression cycle rejects).
    ///
    /// POSTURE (the T3 WIP rule): mere UNREACHABILITY of an individually-valid node is NOT a reject — a 7.10
    /// canvas may hold disconnected-but-sound work-in-progress nodes. Rejection targets malformed STRUCTURE, not
    /// disconnection. Semantic per-node checks over unreachable nodes stay where they live today
    /// (<c>ScenarioValidator</c>'s whole-graph node loop; <c>DslLoopGate</c> for loop/array shapes).
    ///
    /// Pure, Godot-free, float-free; every reject is a LOCATED error naming the offending node/edge.
    ///
    /// <para>Story 7.10 — <see cref="CheckGraphLocated"/> is an ADDITIVE sibling that returns the SAME first-fail
    /// error carrying its node locus (<see cref="GraphNodeError"/>), so the T3 editor can badge the offending node.
    /// Both entry points delegate to ONE private core (<see cref="Evaluate"/>), so the determinism-critical
    /// load-gate string output stays byte-identical by construction — <see cref="Check"/> just projects the core's
    /// <see cref="GraphNodeError.Message"/>.</para>
    /// </summary>
    public static class GraphStructureGate
    {
        /// <summary>
        /// Run the structural rulebook over <paramref name="graph"/>. Returns the first located error STRING, or
        /// null when the graph is structurally sound (the exact pre-7.10 contract — both load gates call this).
        /// </summary>
        public static string? Check(
            TriggerGraph graph,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls)
            => Evaluate(graph, declMap, arrayDecls)?.Message;

        /// <summary>
        /// Story 7.10 — the LOCATED sibling of <see cref="Check"/>: the first structural error carrying its node
        /// locus (empty list when the graph is sound). First-fail like <see cref="Check"/> (same core), so the T3
        /// editor badges exactly the node the load gate would name; an error with no single-node locus carries
        /// <see cref="GraphNodeError.NodeId"/> = −1 (shown on the panel status line).
        /// </summary>
        public static IReadOnlyList<GraphNodeError> CheckGraphLocated(
            TriggerGraph graph,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls)
        {
            GraphNodeError? err = Evaluate(graph, declMap, arrayDecls);
            return err is null ? System.Array.Empty<GraphNodeError>() : new[] { err.Value };
        }

        /// <summary>
        /// Story 7.10 (review) — validate ONLY the proposed NEW edge against the existing graph, WITHOUT re-walking
        /// the whole structural rulebook. The T3 editor used to diff two full <see cref="Check"/> walks (before vs
        /// candidate), which both ADMITTED a genuinely-illegal new edge whenever a pre-existing first-fail error
        /// shadowed it and REJECTED a legitimate edge that merely reordered which pre-existing error sorts first.
        /// This checks the single edge in isolation: both endpoints exist; the source port is a legal out-port and
        /// the dest port a legal in-port for their kinds (the ONE <see cref="NodePorts"/> table); it does not fork
        /// an exec-out or a non-fan-in data-in already in use; and (data edges) the wire type is a defined
        /// <see cref="DataWireType"/>. Returns a LOCATED error string, or null when the edge is admissible. Pure.
        /// </summary>
        public static string? TryValidateNewEdge(
            TriggerGraph graph, bool isData, int src, int srcPort, int dst, int dstPort, DataWireType wire)
        {
            if (graph is null) return "trigger graph is null (nothing to wire).";

            NodeBase? srcNode = null, dstNode = null;
            foreach (NodeBase n in graph.Nodes)
            {
                if (n is null) continue;
                if (n.Id == src) srcNode = n;
                if (n.Id == dst) dstNode = n;
            }
            if (srcNode is null) return $"new edge: source node {src} does not exist.";
            if (dstNode is null) return $"new edge: destination node {dst} does not exist.";

            if (isData)
            {
                if (!NodePorts.IsDataOut(srcNode, srcPort))
                    return $"new data edge ({src}:{srcPort} → {dst}:{dstPort}): port {srcPort} is not a data-out port of node {src} ('{NodeKinds.KindOf(srcNode)}').";
                if (!NodePorts.IsDataIn(dstNode, dstPort))
                    return $"new data edge ({src}:{srcPort} → {dst}:{dstPort}): port {dstPort} is not a data-in port of node {dst} ('{NodeKinds.KindOf(dstNode)}').";
                if (!System.Enum.IsDefined(typeof(DataWireType), wire))
                    return $"new data edge ({src}:{srcPort} → {dst}:{dstPort}): wire type '{wire}' is not a known DataWireType.";
                if (!NodePorts.AllowsDataFanIn(dstNode, dstPort))
                    foreach (DataEdge e in graph.DataEdges)
                        if (e.Dst == dst && e.DstPort == dstPort)
                            return $"new data edge into node {dst} port {dstPort}: that data-in port already has an incoming edge (forked; exactly one allowed).";
                // Cycle: the new edge makes dst consume src, so it closes a loop iff dst already feeds src
                // (transitively) along data edges — i.e. dst is among src's transitive PRODUCERS. Iterative walk
                // (the P9 posture: hostile depth can never stack-overflow the gate).
                var seen = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(src);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    if (!seen.Add(cur)) continue;
                    if (cur == dst)
                        return $"new data edge ({src}:{srcPort} → {dst}:{dstPort}): it would close a data cycle (node {dst} already feeds node {src}).";
                    foreach (DataEdge e in graph.DataEdges)
                        if (e.Dst == cur) stack.Push(e.Src);
                }
            }
            else
            {
                if (!NodePorts.IsExecOut(srcNode, srcPort))
                    return $"new exec edge ({src}:{srcPort} → {dst}:{dstPort}): port {srcPort} is not an exec-out port of node {src} ('{NodeKinds.KindOf(srcNode)}').";
                if (!NodePorts.IsExecIn(dstNode, dstPort))
                    return $"new exec edge ({src}:{srcPort} → {dst}:{dstPort}): port {dstPort} is not an exec-in port of node {dst} ('{NodeKinds.KindOf(dstNode)}').";
                foreach (ExecEdge e in graph.ExecEdges)
                    if (e.Src == src && e.SrcPort == srcPort)
                        return $"new exec edge out of node {src} port {srcPort}: that exec-out port already drives an edge (forked exec chains are not allowed).";
                // Cycle: control would flow src → dst, so the new edge closes a loop iff src is already reachable
                // FROM dst along exec edges (any out-port — a body/then/else chain rejoining an ancestor is still a
                // cycle; WalkChain rejects it at load, so admit-then-badge would be a save-a-brick trap).
                var seen = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(dst);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    if (!seen.Add(cur)) continue;
                    if (cur == src)
                        return $"new exec edge ({src}:{srcPort} → {dst}:{dstPort}): it would close an exec cycle (exec chains must be acyclic).";
                    foreach (ExecEdge e in graph.ExecEdges)
                        if (e.Src == cur) stack.Push(e.Dst);
                }
            }
            return null;
        }

        /// <summary>
        /// Story 7.10 (follow-up review) — detect an exec-edge CYCLE anywhere in the graph, returning a located
        /// error naming a node on the cycle, or null when the exec edges are acyclic. ADDITIVE editor-side scan:
        /// the load-time structural gate (<see cref="Check"/>/<see cref="Evaluate"/>) has no exec-cycle rule — at
        /// load, cycles reject later inside <c>TriggerGraph.WalkChain</c> (a located <c>JsonException</c>) — so
        /// without this the T3 editor would report a clean save on a graph the load path then rejects. The gate's
        /// own output stays byte-identical (this is a sibling, never called by the load gates). Deterministic:
        /// nodes are visited in ascending id order and the reported node is the first back-edge target found.
        /// </summary>
        public static GraphNodeError? FindExecCycle(TriggerGraph graph)
        {
            if (graph is null) return null;
            // Iterative colored DFS over the exec-edge relation. 0=white, 1=gray (on stack), 2=black (done).
            var color = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (NodeBase n in graph.Nodes)
                if (n != null && color.TryAdd(n.Id, 0)) order.Add(n.Id);
            order.Sort();

            foreach (int rootId in order)
            {
                if (color[rootId] != 0) continue;
                var stack = new Stack<(int Id, bool Exit)>();
                stack.Push((rootId, false));
                while (stack.Count > 0)
                {
                    (int id, bool exit) = stack.Pop();
                    if (exit) { color[id] = 2; continue; }
                    if (color[id] != 0) continue; // already entered via another path (diamond) — skip re-entry
                    color[id] = 1;
                    stack.Push((id, true));
                    foreach (ExecEdge e in graph.ExecEdges.OrderBy(x => x))
                    {
                        if (e.Src != id || !color.ContainsKey(e.Dst)) continue;
                        if (color[e.Dst] == 1)
                            return new GraphNodeError(e.Dst,
                                $"node {e.Dst}: exec edges form a cycle (exec chains must be acyclic — this graph will be rejected at load).");
                        if (color[e.Dst] == 0) stack.Push((e.Dst, false));
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The single-source structural walk: returns the first located error, or null when sound. Both
        /// <see cref="Check"/> (string) and <see cref="CheckGraphLocated"/> (located) project from this, so the
        /// prose is byte-identical across both — the message strings are the SAME literals the pre-7.10 gate used.
        /// </summary>
        private static GraphNodeError? Evaluate(
            TriggerGraph graph,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls)
        {
            // ── 0. A null graph is a caller defect, but this gate exists to contain malformed input — return a
            //    located error instead of NRE'ing (both gates wrap the string; neither expects a throw here). ──
            if (graph is null) return new GraphNodeError(-1, "trigger graph is null (nothing to check — a caller passed no parsed graph).");

            // ── 1. Duplicate node ids ─────────────────────────────────────────
            var byId = new Dictionary<int, NodeBase>(graph.Nodes.Count);
            foreach (NodeBase n in graph.Nodes)
            {
                if (n is null) return new GraphNodeError(-1, "graph contains a null node entry.");
                if (!byId.TryAdd(n.Id, n))
                    return new GraphNodeError(n.Id, $"graph node id {n.Id} is declared more than once (duplicate node ids).");
            }

            // ── 2. Exec edges: endpoints exist, ports legal, no forked exec-out ──
            //    Canonical tuple order → deterministic first-fail (the module convention).
            var execOutSeen = new HashSet<(int Src, int Port)>();
            foreach (ExecEdge e in graph.ExecEdges.OrderBy(x => x))
            {
                if (!byId.TryGetValue(e.Src, out NodeBase? src))
                    return new GraphNodeError(e.Src, $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): source node {e.Src} does not exist (dangling edge).");
                if (!byId.TryGetValue(e.Dst, out NodeBase? dst))
                    return new GraphNodeError(e.Dst, $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): destination node {e.Dst} does not exist (dangling edge).");
                if (!NodePorts.IsExecOut(src, e.SrcPort))
                    return new GraphNodeError(e.Src, $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.SrcPort} is not an exec-out port of node {e.Src} ('{NodeKinds.KindOf(src)}').");
                if (!NodePorts.IsExecIn(dst, e.DstPort))
                    return new GraphNodeError(e.Dst, $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.DstPort} is not an exec-in port of node {e.Dst} ('{NodeKinds.KindOf(dst)}').");
                if (!execOutSeen.Add((e.Src, e.SrcPort)))
                    return new GraphNodeError(e.Src, $"node {e.Src}: multiple exec edges leave port {e.SrcPort} (forked exec chains are not allowed — the 'first-match' tolerance is retired; use a branch container).");
            }

            // ── 3. Data edges: endpoints exist, ports legal, no forked data-in (trigger condition-in exempt) ──
            var dataInSeen = new HashSet<(int Dst, int Port)>();
            foreach (DataEdge e in graph.DataEdges.OrderBy(x => x))
            {
                if (!byId.TryGetValue(e.Src, out NodeBase? src))
                    return new GraphNodeError(e.Src, $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): source node {e.Src} does not exist (dangling edge).");
                if (!byId.TryGetValue(e.Dst, out NodeBase? dst))
                    return new GraphNodeError(e.Dst, $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): destination node {e.Dst} does not exist (dangling edge).");
                if (!NodePorts.IsDataOut(src, e.SrcPort))
                    return new GraphNodeError(e.Src, $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.SrcPort} is not a data-out port of node {e.Src} ('{NodeKinds.KindOf(src)}') — only condition and expression nodes emit data.");
                if (!NodePorts.IsDataIn(dst, e.DstPort))
                    return new GraphNodeError(e.Dst, $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.DstPort} is not a data-in port of node {e.Dst} ('{NodeKinds.KindOf(dst)}') (stray data edge).");
                if (!NodePorts.AllowsDataFanIn(dst, e.DstPort) && !dataInSeen.Add((e.Dst, e.DstPort)))
                    return new GraphNodeError(e.Dst, $"node {e.Dst}: multiple data edges enter port {e.DstPort} (forked; exactly one allowed).");
            }

            // ── 4. Expression subgraphs — compile-checked even when UNCONSUMED (no orphan semantic skip). A root
            //    is an expr node that no OTHER expr node consumes as an operand; each compiles under the full
            //    ExprCompiler rulebook (inCondition:false — the loosest legal context; roots that ARE consumed by
            //    a trigger condition-in are additionally compiled inCondition:true by the validator's own pass).
            //    Every expr node must then be reachable from some root: a root-less mutually-consuming cycle
            //    (which no compile walk would ever enter) rejects located. ──
            var consumedAsOperand = new HashSet<int>();
            foreach (DataEdge e in graph.DataEdges)
                if (byId.TryGetValue(e.Dst, out NodeBase? d) && ExprCompiler.IsExprNode(d)
                    && byId.TryGetValue(e.Src, out NodeBase? s) && ExprCompiler.IsExprNode(s))
                    consumedAsOperand.Add(e.Src);

            var reached = new HashSet<int>();
            foreach (NodeBase n in graph.Nodes.OrderBy(x => x.Id)) // ascending id → deterministic first-fail
            {
                if (!ExprCompiler.IsExprNode(n) || consumedAsOperand.Contains(n.Id)) continue;
                // Story 7.5: a subgraph reading event.<param> cannot compile without its trigger's event-parameter
                // map, which exists only once EventDispatchPlan.TryBuild has run — the validator's data-edge pass,
                // the raise-arg compiles, and the director backstop all compile such roots strictly WITH the map.
                // Compiling it map-less here would false-reject every 7.5 scenario, so this pass skips the compile
                // (reachability marking still runs — the root-less-cycle reject below must keep covering them).
                var sub = new HashSet<int>();
                MarkReached(graph, byId, n.Id, sub);
                bool readsEventParams = false;
                foreach (int id in sub)
                    if (byId.TryGetValue(id, out NodeBase? sn) && sn is ExprEventParamNode) { readsEventParams = true; break; }
                if (readsEventParams)
                {
                    // A skipped root must still be CONSUMED by some port — the consuming pass (validator
                    // event-param pass / raise-arg compile / director CompileItems) is what compiles it strictly
                    // with the map. A dangling event-param subgraph would otherwise escape every compile.
                    bool consumed = false;
                    foreach (DataEdge e in graph.DataEdges)
                        if (e.Src == n.Id && byId.TryGetValue(e.Dst, out NodeBase? dc) && !ExprCompiler.IsExprNode(dc))
                        { consumed = true; break; }
                    if (!consumed)
                        return new GraphNodeError(n.Id, $"expression subgraph rooted at node {n.Id} reads event.<param> but is not consumed by any port — event-parameter expressions compile only against a consuming trigger's dispatch plan.");
                }
                else if (!ExprCompiler.TryCompile(graph, n.Id, declMap, inCondition: false,
                        out _, out string? err, arrayDecls))
                    return new GraphNodeError(n.Id, $"expression subgraph rooted at node {n.Id}: {err}");
                reached.UnionWith(sub);
            }
            foreach (NodeBase n in graph.Nodes.OrderBy(x => x.Id))
                if (ExprCompiler.IsExprNode(n) && !reached.Contains(n.Id))
                    return new GraphNodeError(n.Id, $"expr node {n.Id} is not reachable from any expression root (cyclic or mutually-consuming expression wiring).");

            return null;
        }

        /// <summary>Iteratively mark every expr node reachable from <paramref name="rootId"/> along operand data
        /// edges (expr → expr). Iterative (an explicit stack), so hostile chain depth can never stack-overflow the
        /// gate itself (the P9 posture).</summary>
        private static void MarkReached(TriggerGraph graph, Dictionary<int, NodeBase> byId, int rootId, HashSet<int> reached)
        {
            var stack = new Stack<int>();
            stack.Push(rootId);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (!reached.Add(id)) continue;
                foreach (DataEdge e in graph.DataEdges)
                    if (e.Dst == id && byId.TryGetValue(e.Src, out NodeBase? s) && ExprCompiler.IsExprNode(s))
                        stack.Push(e.Src);
            }
        }
    }
}
