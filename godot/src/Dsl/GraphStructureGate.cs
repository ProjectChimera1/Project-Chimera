#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace ProjectChimera.Dsl
{
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
    /// </summary>
    public static class GraphStructureGate
    {
        /// <summary>
        /// Run the structural rulebook over <paramref name="graph"/>. Returns the first located error, or null
        /// when the graph is structurally sound. <paramref name="declMap"/>/<paramref name="arrayDecls"/> feed the
        /// unconsumed-expression compile checks (the same maps both gates already build).
        /// </summary>
        public static string? Check(
            TriggerGraph graph,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls)
        {
            // ── 0. A null graph is a caller defect, but this gate exists to contain malformed input — return a
            //    located error instead of NRE'ing (both gates wrap the string; neither expects a throw here). ──
            if (graph is null) return "trigger graph is null (nothing to check — a caller passed no parsed graph).";

            // ── 1. Duplicate node ids ─────────────────────────────────────────
            var byId = new Dictionary<int, NodeBase>(graph.Nodes.Count);
            foreach (NodeBase n in graph.Nodes)
            {
                if (n is null) return "graph contains a null node entry.";
                if (!byId.TryAdd(n.Id, n))
                    return $"graph node id {n.Id} is declared more than once (duplicate node ids).";
            }

            // ── 2. Exec edges: endpoints exist, ports legal, no forked exec-out ──
            //    Canonical tuple order → deterministic first-fail (the module convention).
            var execOutSeen = new HashSet<(int Src, int Port)>();
            foreach (ExecEdge e in graph.ExecEdges.OrderBy(x => x))
            {
                if (!byId.TryGetValue(e.Src, out NodeBase? src))
                    return $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): source node {e.Src} does not exist (dangling edge).";
                if (!byId.TryGetValue(e.Dst, out NodeBase? dst))
                    return $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): destination node {e.Dst} does not exist (dangling edge).";
                if (!NodePorts.IsExecOut(src, e.SrcPort))
                    return $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.SrcPort} is not an exec-out port of node {e.Src} ('{NodeKinds.KindOf(src)}').";
                if (!NodePorts.IsExecIn(dst, e.DstPort))
                    return $"exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.DstPort} is not an exec-in port of node {e.Dst} ('{NodeKinds.KindOf(dst)}').";
                if (!execOutSeen.Add((e.Src, e.SrcPort)))
                    return $"node {e.Src}: multiple exec edges leave port {e.SrcPort} (forked exec chains are not allowed — the 'first-match' tolerance is retired; use a branch container).";
            }

            // ── 3. Data edges: endpoints exist, ports legal, no forked data-in (trigger condition-in exempt) ──
            var dataInSeen = new HashSet<(int Dst, int Port)>();
            foreach (DataEdge e in graph.DataEdges.OrderBy(x => x))
            {
                if (!byId.TryGetValue(e.Src, out NodeBase? src))
                    return $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): source node {e.Src} does not exist (dangling edge).";
                if (!byId.TryGetValue(e.Dst, out NodeBase? dst))
                    return $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): destination node {e.Dst} does not exist (dangling edge).";
                if (!NodePorts.IsDataOut(src, e.SrcPort))
                    return $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.SrcPort} is not a data-out port of node {e.Src} ('{NodeKinds.KindOf(src)}') — only condition and expression nodes emit data.";
                if (!NodePorts.IsDataIn(dst, e.DstPort))
                    return $"data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}): port {e.DstPort} is not a data-in port of node {e.Dst} ('{NodeKinds.KindOf(dst)}') (stray data edge).";
                if (!NodePorts.AllowsDataFanIn(dst, e.DstPort) && !dataInSeen.Add((e.Dst, e.DstPort)))
                    return $"node {e.Dst}: multiple data edges enter port {e.DstPort} (forked; exactly one allowed).";
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
                        return $"expression subgraph rooted at node {n.Id} reads event.<param> but is not consumed by any port — event-parameter expressions compile only against a consuming trigger's dispatch plan.";
                }
                else if (!ExprCompiler.TryCompile(graph, n.Id, declMap, inCondition: false,
                        out _, out string? err, arrayDecls))
                    return $"expression subgraph rooted at node {n.Id}: {err}";
                reached.UnionWith(sub);
            }
            foreach (NodeBase n in graph.Nodes.OrderBy(x => x.Id))
                if (ExprCompiler.IsExprNode(n) && !reached.Contains(n.Id))
                    return $"expr node {n.Id} is not reachable from any expression root (cyclic or mutually-consuming expression wiring).";

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
