#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 7.6 — the SHARED loop/array/fuel load-gate rulebook, called by BOTH <see cref="ScenarioValidator"/>
    /// (the authoritative pre-tick gate, surfacing <c>ValidationResult.Fail</c>) and
    /// <c>ScenarioDirector.LoadScenario</c> (the fail-closed backstop for direct callers, surfacing a located
    /// <c>JsonException</c>) — ONE implementation, so the RULES are identical by construction (the 7.4
    /// precedent hand-duplicated its rules; the 7.6 surface is large enough that duplication would drift).
    ///
    /// Story 7.7 — gate/backstop reconciliation: BOTH gates now run <see cref="CheckDeclarations"/> +
    /// <see cref="CheckGraph"/> + <see cref="CheckSpawnCounts"/> + <c>GraphStructureGate.Check</c>
    /// UNCONDITIONALLY (the 7.6 <c>HasLoopConstructs</c> legacy-parity guard is removed — there is no
    /// invocation-condition divergence left). The remaining VALIDATOR-ONLY checks are deliberate policy, not
    /// drift: the <c>CheckFactionSlot</c> engine ceiling, <c>EffectBounds</c> on run_effect embeds, the
    /// timer/variable declaration semantics, spawn-position bounds/blocked-cell checks, and the stamp checks —
    /// all live in <see cref="ScenarioValidator"/> alone (authoring-policy rules whose absence cannot crash the
    /// runtime; the compiler/loop-gate structural bounds keep direct loads safe).
    ///
    /// Story 7.7 also adds the loop-var SHADOWING rule here: nested loops sharing one <c>loop_var</c> along a
    /// nesting chain reject located (the inner loop would silently overwrite the outer loop's live value).
    ///
    /// Every reject is a LOCATED error string naming the offending node and, for caps, the
    /// <see cref="DslBounds"/> constant — never a silent runtime truncation. Checks:
    ///   • array DECLARATION rules (element_type/capacity present + in range; Global scope only; no stray
    ///     element_type/capacity on scalars; zero initial);
    ///   • for_each rules (closed source set; entity sources REQUIRE the loud <c>up_to</c> cap — the error
    ///     directs to <c>for_each_batched</c> or an explicit <c>up_to</c>; array sources REQUIRE a declared
    ///     Array name and a TriggerLocal loop var of the element type; region_units REQUIRE a declared region);
    ///   • for_each_batched rules (top-level chain node only, at most one per trigger, ≤
    ///     <see cref="DslBounds.MaxBatchedLoops"/> per scenario, <c>batch_size</c> ∈ 1..MaxForEachItems);
    ///   • branch rules (exactly one Bool expression on the condition-in port, Boolean wire, compiled
    ///     <c>inCondition:false</c>);
    ///   • array-action rules (declared Array target; array_push/array_set REQUIRE an element-typed value-in
    ///     expression edge; array_set REQUIRES an Int index-in edge; array_clear takes neither);
    ///   • loop nesting ≤ <see cref="DslBounds.MaxLoopNesting"/>; static worst-case cost per trigger
    ///     (action = 1, expression = op count, run_effect = embedded node count, for_each = 1 + cap × body,
    ///     branch = 1 + cond + max(then, else), for_each_batched = 1 + batch × body) ≤
    ///     <see cref="DslBounds.MaxDslOpsPerTrigger"/>.
    ///
    /// The engine-ceiling faction bound (<c>CheckFactionSlot</c>) stays a VALIDATOR-ONLY policy check (the 7.4
    /// precedent); this gate applies the structural [-1, PlayerSlots) bound that keeps the runtime safe.
    /// </summary>
    internal static class DslLoopGate
    {
        // Story 7.7: the 7.6 HasLoopConstructs legacy-parity guard is REMOVED — the LoadScenario backstop now runs
        // every shared check unconditionally, exactly like the validator (gate/backstop reconciliation).

        /// <summary>Array-declaration rules (see class remarks). Returns the first located error, or null.</summary>
        public static string? CheckDeclarations(ScenarioVariable[]? variables)
        {
            if (variables == null) return null;
            for (int i = 0; i < variables.Length; i++)
            {
                ScenarioVariable v = variables[i];
                if (v is null) continue; // null entries are the base validator's reject
                if (v.Type == DslValueType.Array)
                {
                    if (v.ElementType is not DslValueType et)
                        return $"scenario.variables[{i}] ('{v.Name}'): an Array declaration requires 'element_type' (Int/Fixed/Bool).";
                    if (et != DslValueType.Int && et != DslValueType.Fixed && et != DslValueType.Bool)
                        return $"scenario.variables[{i}] ('{v.Name}'): array element_type '{et}' must be a scalar (Int/Fixed/Bool).";
                    if (v.Capacity is not int cap)
                        return $"scenario.variables[{i}] ('{v.Name}'): an Array declaration requires 'capacity' (1..DslBounds.MaxArrayCapacity={DslBounds.MaxArrayCapacity}).";
                    if (cap < 1 || cap > DslBounds.MaxArrayCapacity)
                        return $"scenario.variables[{i}] ('{v.Name}'): capacity {cap} is out of range 1..DslBounds.MaxArrayCapacity={DslBounds.MaxArrayCapacity}.";
                    if (v.Scope != VarScope.Global)
                        return $"scenario.variables[{i}] ('{v.Name}'): arrays are Global-scope only this story ({v.Scope} arrays are not supported).";
                    if (v.Initial != Fixed.Zero)
                        return $"scenario.variables[{i}] ('{v.Name}'): an Array declaration seeds EMPTY — 'initial' must be omitted/0.";
                }
                else
                {
                    // Fail-closed: array-only fields on a scalar declaration are an authoring error, not ignorable.
                    if (v.ElementType != null)
                        return $"scenario.variables[{i}] ('{v.Name}'): 'element_type' is only valid on an Array-typed declaration.";
                    if (v.Capacity != null)
                        return $"scenario.variables[{i}] ('{v.Name}'): 'capacity' is only valid on an Array-typed declaration.";
                }
            }
            return null;
        }

        /// <summary>Build the declared-array map (name → element type + capacity) the expression compiler and
        /// the loop gate consume. Malformed declarations are skipped (they reject in
        /// <see cref="CheckDeclarations"/> first).</summary>
        public static Dictionary<string, (DslValueType Elem, int Capacity)> BuildArrayDecls(ScenarioVariable[]? variables)
        {
            var map = new Dictionary<string, (DslValueType Elem, int Capacity)>(StringComparer.Ordinal);
            if (variables != null)
                foreach (ScenarioVariable v in variables)
                    if (v != null && v.Type == DslValueType.Array && !string.IsNullOrWhiteSpace(v.Name)
                        && v.ElementType is DslValueType et && v.Capacity is int cap)
                        map.TryAdd(v.Name, (et, cap));
            return map;
        }

        /// <summary>Located-reject carrier for the recursive walk (caught at the <see cref="CheckGraph"/> boundary
        /// — the gate itself never lets it escape).</summary>
        private sealed class GateException : Exception
        {
            public GateException(string message) : base(message) { }
        }

        /// <summary>
        /// The loop/branch/array-action semantic gate over the parsed graph's execution view (see class remarks).
        /// <paramref name="regionExists"/> resolves a region id against the scenario's declared regions.
        /// Returns the first located error, or null when the graph is admissible.
        /// </summary>
        public static string? CheckGraph(
            TriggerGraph graph,
            List<TriggerGraph.TriggerExec> execs,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls,
            Func<string, bool> regionExists)
        {
            try
            {
                var ctx = new Ctx
                {
                    Graph = graph,
                    ById = new Dictionary<int, NodeBase>(graph.Nodes.Count),
                    DeclMap = declMap,
                    ArrayDecls = arrayDecls,
                    RegionExists = regionExists,
                };
                foreach (NodeBase n in graph.Nodes)
                    ctx.ById[n.Id] = n;

                int batchedTotal = 0;
                foreach (TriggerGraph.TriggerExec ex in execs)
                {
                    int batchedInTrigger = 0;
                    long cost = WalkChain(ctx, ex.Items, loopDepth: 0, walkDepth: 1, topLevel: true, ref batchedInTrigger);
                    batchedTotal += batchedInTrigger;

                    // The static cap-product cost check applies to triggers that CARRY a 7.6 construct (the
                    // legacy-parity posture: a pre-7.6 chain keeps its exact prior load behavior; its dynamic
                    // aggregate stays covered by the fuel seatbelt).
                    if (ContainsLoopConstruct(ex.Items) && cost > DslBounds.MaxDslOpsPerTrigger)
                        throw new GateException(
                            $"trigger '{ex.Trigger.Name}' (node {ex.Trigger.Id}): static worst-case cost {cost} exceeds DslBounds.MaxDslOpsPerTrigger={DslBounds.MaxDslOpsPerTrigger}.");
                }

                if (batchedTotal > DslBounds.MaxBatchedLoops)
                    throw new GateException(
                        $"scenario declares {batchedTotal} for_each_batched nodes, exceeding DslBounds.MaxBatchedLoops={DslBounds.MaxBatchedLoops}.");

                // Index-in data edges (port ActionIndexInPort) are a NEW 7.6 port — gate every edge into it:
                // only an array_set action may take one, and its src must be an expression node (a non-expr src
                // would silently map to root -1, the exact silent-drop class the 7.4 value-in scan closed).
                // Review (7.6): FORKED index-in and branch cond-in edges also reject here, mirroring the
                // value-in fork reject — the runtime would otherwise silently resolve lowest-id-wins.
                var seenIndexInPorts = new HashSet<int>();
                var seenCondInPorts  = new HashSet<int>();
                foreach (DataEdge de in graph.DataEdges.OrderBy(e => e))
                {
                    // Forked branch condition-in: a second expression edge into a branch's cond-in port.
                    if (de.DstPort == TriggerGraph.BranchCondInPort
                        && ctx.ById.TryGetValue(de.Dst, out NodeBase? condDst) && condDst is BranchNode
                        && ctx.ById.TryGetValue(de.Src, out NodeBase? condSrc) && ExprCompiler.IsExprNode(condSrc)
                        && !seenCondInPorts.Add(de.Dst))
                        throw new GateException(
                            $"branch node {de.Dst}: multiple condition-in expression edges (forked; exactly one allowed).");

                    if (de.DstPort != TriggerGraph.ActionIndexInPort) continue;
                    ctx.ById.TryGetValue(de.Dst, out NodeBase? dst);
                    if (dst is ForEachNode or ForEachBatchedNode || dst is BranchNode)
                        continue; // container data ports are a different space (branch cond-in is port 0)
                    if (dst is not ActionNode idxAct || idxAct.Kind != "array_set")
                        throw new GateException(
                            $"node {de.Dst}: an index-in edge (data port {TriggerGraph.ActionIndexInPort}) is only allowed on an array_set action.");
                    bool srcIsExpr = ctx.ById.TryGetValue(de.Src, out NodeBase? src) && ExprCompiler.IsExprNode(src);
                    if (!srcIsExpr)
                        throw new GateException(
                            $"action node {de.Dst}: the index-in edge source (node {de.Src}) is not an expression node.");
                    if (!seenIndexInPorts.Add(de.Dst))
                        throw new GateException(
                            $"action node {de.Dst} (array_set): multiple index-in expression edges (forked; exactly one allowed).");
                    if (de.Wire != DataWireType.Int)
                        throw new GateException(
                            $"action node {de.Dst}: the index-in edge must carry the Int wire, got '{de.Wire}'.");
                }

                return null;
            }
            catch (GateException gex)
            {
                return gex.Message;
            }
        }

        /// <summary>
        /// Review (7.6) — the spawn-count backstop: every executable <c>spawn_unit</c> action (both channels —
        /// flat triggers lower into the graph before this runs — and every nesting level) must carry a count in
        /// 1..<see cref="EffectCaps.MaxSpawnCount"/>. Run UNCONDITIONALLY by <c>ScenarioDirector.LoadScenario</c>
        /// (the "never a silent runtime truncation" posture makes an out-of-range count a loud load reject at
        /// BOTH gates, message-parallel to the <c>ScenarioValidator</c> reject). Returns the first located error,
        /// or null.
        /// </summary>
        public static string? CheckSpawnCounts(List<TriggerGraph.TriggerExec> execs)
        {
            foreach (TriggerGraph.TriggerExec ex in execs)
            {
                string? err = CheckSpawnCounts(ex.Items, ex.Trigger.Name);
                if (err != null) return err;
            }
            return null;
        }

        private static string? CheckSpawnCounts(TriggerGraph.ExecItem[] items, string triggerName)
        {
            foreach (TriggerGraph.ExecItem it in items)
            {
                if (it.Node is ActionNode a && a.Kind == "spawn_unit"
                    && (a.Count < 1 || a.Count > EffectCaps.MaxSpawnCount))
                    return $"trigger '{triggerName}' spawn_unit node {a.Id}: count={a.Count} is out of range 1..EffectCaps.MaxSpawnCount={EffectCaps.MaxSpawnCount}.";
                string? err = CheckSpawnCounts(it.Body, triggerName)
                    ?? CheckSpawnCounts(it.Then, triggerName)
                    ?? CheckSpawnCounts(it.Else, triggerName);
                if (err != null) return err;
            }
            return null;
        }

        private sealed class Ctx
        {
            public TriggerGraph Graph = null!;
            public Dictionary<int, NodeBase> ById = null!;
            public IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> DeclMap = null!;
            public IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> ArrayDecls = null!;
            public Func<string, bool> RegionExists = null!;

            /// <summary>Story 7.7 — the loop variables bound by the ENCLOSING for_each chain (name → owning node
            /// id), maintained add/remove around each body recursion so the shadowing reject names both loops.
            /// Sibling loops (not on one nesting chain) may legally reuse a loop_var.</summary>
            public readonly Dictionary<string, int> ActiveLoopVars = new(StringComparer.Ordinal);
        }

        private static bool ContainsLoopConstruct(TriggerGraph.ExecItem[] items)
        {
            foreach (TriggerGraph.ExecItem it in items)
            {
                if (it.Node is ForEachNode or ForEachBatchedNode or BranchNode) return true;
                if (it.Node is ActionNode a && NodeKinds.IsArrayActionKind(a.Kind)) return true;
                if (ContainsLoopConstruct(it.Body) || ContainsLoopConstruct(it.Then) || ContainsLoopConstruct(it.Else))
                    return true;
            }
            return false;
        }

        /// <summary>Review P9 — the gate-side recursion seatbelt (mirrors the TriggerGraph.WalkChain guard so
        /// this walker can never itself be stack-overflowed by a hostile nested tree handed to
        /// <see cref="CheckGraph"/> directly): reject located, naming the constant, before recursing past
        /// <see cref="DslBounds.MaxExecWalkDepth"/> container levels.</summary>
        private static void RequireWalkDepth(int nextDepth, int nodeId, string kind)
        {
            if (nextDepth > DslBounds.MaxExecWalkDepth)
                throw new GateException(
                    $"{kind} node {nodeId}: container nesting depth {nextDepth} exceeds DslBounds.MaxExecWalkDepth={DslBounds.MaxExecWalkDepth} (the exec-walk recursion seatbelt).");
        }

        /// <summary>Validate one exec chain and return its static worst-case op cost (recursing into container
        /// sub-chains). Throws <see cref="GateException"/> on the first located violation.
        /// <paramref name="walkDepth"/> (review P9) is the chain's container nesting level (top-level = 1),
        /// capped at <see cref="DslBounds.MaxExecWalkDepth"/> — distinct from <paramref name="loopDepth"/>,
        /// which counts LOOP containers only (branches do not raise it).</summary>
        private static long WalkChain(Ctx ctx, TriggerGraph.ExecItem[] items, int loopDepth, int walkDepth,
            bool topLevel, ref int batchedInTrigger)
        {
            long cost = 0;
            foreach (TriggerGraph.ExecItem it in items)
            {
                switch (it.Node)
                {
                    case ForEachNode fe:
                    {
                        int newDepth = loopDepth + 1;
                        if (newDepth > DslBounds.MaxLoopNesting)
                            throw new GateException(
                                $"for_each node {fe.Id}: loop nesting depth {newDepth} exceeds DslBounds.MaxLoopNesting={DslBounds.MaxLoopNesting}.");

                        long iterCap = CheckForEach(ctx, fe);
                        RequireWalkDepth(walkDepth + 1, fe.Id, "for_each");

                        // Story 7.7 — loop-var shadowing along a NESTING chain rejects located: the inner loop
                        // would silently overwrite the outer loop's live TriggerLocal value each iteration (the
                        // ledgered shadowing gap). Sibling loops may reuse a name (removed after the body walk).
                        string? loopVar = fe.LoopVar;
                        if (!string.IsNullOrEmpty(loopVar))
                        {
                            if (ctx.ActiveLoopVars.TryGetValue(loopVar!, out int ownerId))
                                throw new GateException(
                                    $"for_each node {fe.Id}: loop_var '{loopVar}' is already bound by enclosing for_each node {ownerId} " +
                                    "(nested loops on one chain must use distinct loop variables — shadowing would silently overwrite the outer loop's value).");
                            ctx.ActiveLoopVars[loopVar!] = fe.Id;
                        }

                        int inner = batchedInTrigger;
                        long body = WalkChain(ctx, it.Body, newDepth, walkDepth + 1, topLevel: false, ref inner);
                        batchedInTrigger = inner;

                        if (!string.IsNullOrEmpty(loopVar))
                            ctx.ActiveLoopVars.Remove(loopVar!);

                        cost += Sat(1 + Sat(iterCap * body));
                        break;
                    }

                    case ForEachBatchedNode fb:
                    {
                        if (!topLevel)
                            throw new GateException(
                                $"for_each_batched node {fb.Id}: must be a TOP-LEVEL chain node (never nested inside a for_each or branch).");
                        batchedInTrigger++;
                        if (batchedInTrigger > 1)
                            throw new GateException(
                                $"for_each_batched node {fb.Id}: at most ONE for_each_batched per trigger.");
                        CheckForEachBatched(ctx, fb);

                        int newDepth = loopDepth + 1; // the batched loop is itself a loop level for its body
                        if (newDepth > DslBounds.MaxLoopNesting)
                            throw new GateException(
                                $"for_each_batched node {fb.Id}: loop nesting depth {newDepth} exceeds DslBounds.MaxLoopNesting={DslBounds.MaxLoopNesting}.");
                        RequireWalkDepth(walkDepth + 1, fb.Id, "for_each_batched");
                        int inner = batchedInTrigger;
                        long body = WalkChain(ctx, it.Body, newDepth, walkDepth + 1, topLevel: false, ref inner);
                        batchedInTrigger = inner;
                        cost += Sat(1 + Sat(fb.BatchSize * body));
                        break;
                    }

                    case BranchNode br:
                    {
                        if (it.CondExprRoot < 0)
                            throw new GateException(
                                $"branch node {br.Id}: requires a Bool expression wired into its condition-in data port (port {TriggerGraph.BranchCondInPort}).");
                        long condOps = CompileExpr(ctx, it.CondExprRoot, $"branch node {br.Id} condition",
                            out DslValueType condType, out bool condTyped);
                        if (condTyped && condType != DslValueType.Bool)
                            throw new GateException(
                                $"branch node {br.Id}: the condition expression must evaluate to Bool, got {condType}.");
                        RequireWire(ctx, it.CondExprRoot, br.Id, TriggerGraph.BranchCondInPort, DataWireType.Boolean,
                            $"branch node {br.Id}");

                        RequireWalkDepth(walkDepth + 1, br.Id, "branch");
                        int innerT = batchedInTrigger;
                        long thenCost = WalkChain(ctx, it.Then, loopDepth, walkDepth + 1, topLevel: false, ref innerT);
                        long elseCost = WalkChain(ctx, it.Else, loopDepth, walkDepth + 1, topLevel: false, ref innerT);
                        batchedInTrigger = innerT;
                        cost += Sat(1 + condOps + Math.Max(thenCost, elseCost));
                        break;
                    }

                    case RaiseEventNode:
                        // Story 7.5 (merge): an executed raise charges one op at runtime, so mirror it in the
                        // static model. Raise-ARG expression ops stay deliberately uncharged — same deferred
                        // class as condition-expression evaluation (see the deferred-work ledger).
                        cost += 1;
                        break;

                    case ActionNode act when NodeKinds.IsArrayActionKind(act.Kind):
                        cost += CheckArrayAction(ctx, act, it);
                        break;

                    case ActionNode act:
                    {
                        long c = 1;
                        if (it.ValueExprRoot >= 0)
                            c += CompileExpr(ctx, it.ValueExprRoot, $"action node {act.Id} value", out _, out _);
                        if (it.IndexExprRoot >= 0)
                            throw new GateException(
                                $"action node {act.Id} (kind '{act.Kind}'): an index-in expression edge is only allowed on an array_set action.");
                        cost += c;
                        break;
                    }

                    case EffectActionNode eff:
                        cost += CountEffectNodes(eff.Effect);
                        break;
                }
            }
            return Sat(cost);
        }

        private static long CheckForEach(Ctx ctx, ForEachNode fe)
        {
            if (fe.Faction < -1 || fe.Faction >= DslVarTable.PlayerSlots)
                throw new GateException(
                    $"for_each node {fe.Id}: faction {fe.Faction} is out of range (-1 = any faction, 0..{DslVarTable.PlayerSlots - 1}).");

            long iterCap;
            if (fe.Source == "array")
            {
                if (string.IsNullOrEmpty(fe.ArrayName) || !ctx.ArrayDecls.TryGetValue(fe.ArrayName!, out (DslValueType Elem, int Capacity) adecl))
                    throw new GateException(
                        $"for_each node {fe.Id}: source 'array' requires 'array_name' to name a declared Array variable (got '{fe.ArrayName}').");
                if (fe.UpTo < 0 || fe.UpTo > DslBounds.MaxForEachItems)
                    // Review (7.6): for an ARRAY source up_to = 0 is legal ("the full array"), so the range
                    // message must not exclude it (the entity-source message below keeps its 1.. range).
                    throw new GateException(
                        $"for_each node {fe.Id}: up_to {fe.UpTo} is out of range — an array-source loop takes 0 (the full array) or 1..DslBounds.MaxForEachItems={DslBounds.MaxForEachItems}.");
                if (string.IsNullOrEmpty(fe.LoopVar))
                    throw new GateException(
                        $"for_each node {fe.Id}: an array-source loop requires 'loop_var' (a declared TriggerLocal variable of the element type).");
                CheckLoopVar(ctx, fe.Id, fe.LoopVar!, adecl.Elem);
                iterCap = fe.UpTo > 0 ? Math.Min(fe.UpTo, adecl.Capacity) : adecl.Capacity;
            }
            else // faction_units / region_units (membership is parse-enforced)
            {
                if (fe.UpTo == 0)
                    throw new GateException(
                        $"for_each node {fe.Id}: an entity-source for_each requires an explicit 'up_to' cap (1..DslBounds.MaxForEachItems={DslBounds.MaxForEachItems}) — set 'up_to', or use 'for_each_batched' to drip a large set across ticks.");
                if (fe.UpTo < 1 || fe.UpTo > DslBounds.MaxForEachItems)
                    throw new GateException(
                        $"for_each node {fe.Id}: up_to {fe.UpTo} is out of range 1..DslBounds.MaxForEachItems={DslBounds.MaxForEachItems}.");
                if (fe.Source == "region_units")
                {
                    if (string.IsNullOrEmpty(fe.RegionId) || !ctx.RegionExists(fe.RegionId!))
                        throw new GateException(
                            $"for_each node {fe.Id}: region_id '{fe.RegionId}' references no declared region.");
                }
                if (!string.IsNullOrEmpty(fe.LoopVar))
                    CheckLoopVar(ctx, fe.Id, fe.LoopVar!, DslValueType.Int); // entity id → Int
                iterCap = fe.UpTo;
            }
            return iterCap;
        }

        private static void CheckForEachBatched(Ctx ctx, ForEachBatchedNode fb)
        {
            if (fb.Faction < -1 || fb.Faction >= DslVarTable.PlayerSlots)
                throw new GateException(
                    $"for_each_batched node {fb.Id}: faction {fb.Faction} is out of range (-1 = any faction, 0..{DslVarTable.PlayerSlots - 1}).");
            if (fb.BatchSize < 1 || fb.BatchSize > DslBounds.MaxForEachItems)
                throw new GateException(
                    $"for_each_batched node {fb.Id}: batch_size {fb.BatchSize} is out of range 1..DslBounds.MaxForEachItems={DslBounds.MaxForEachItems}.");
            if (fb.Source == "region_units")
            {
                if (string.IsNullOrEmpty(fb.RegionId) || !ctx.RegionExists(fb.RegionId!))
                    throw new GateException(
                        $"for_each_batched node {fb.Id}: region_id '{fb.RegionId}' references no declared region.");
            }
        }

        private static void CheckLoopVar(Ctx ctx, int nodeId, string loopVar, DslValueType required)
        {
            if (!ctx.DeclMap.TryGetValue(loopVar, out (DslValueType Type, VarScope Scope) decl))
                throw new GateException(
                    $"for_each node {nodeId}: loop_var '{loopVar}' is not a declared variable.");
            if (decl.Scope != VarScope.TriggerLocal)
                throw new GateException(
                    $"for_each node {nodeId}: loop_var '{loopVar}' must be TriggerLocal-scoped (it is {decl.Scope}).");
            if (decl.Type != required)
                throw new GateException(
                    $"for_each node {nodeId}: loop_var '{loopVar}' is {decl.Type}-typed; this loop writes {required} values.");
        }

        private static long CheckArrayAction(Ctx ctx, ActionNode act, TriggerGraph.ExecItem it)
        {
            if (string.IsNullOrEmpty(act.Variable) || !ctx.ArrayDecls.TryGetValue(act.Variable!, out (DslValueType Elem, int Capacity) adecl))
                throw new GateException(
                    $"action node {act.Id} ({act.Kind}): 'variable' must name a declared Array variable (got '{act.Variable}').");

            long cost = 1;
            switch (act.Kind)
            {
                case "array_push":
                {
                    if (it.ValueExprRoot < 0)
                        throw new GateException(
                            $"action node {act.Id} (array_push): requires a value-in expression edge (data port {TriggerGraph.ActionValueInPort}) of the element type.");
                    if (it.IndexExprRoot >= 0)
                        throw new GateException(
                            $"action node {act.Id} (array_push): takes no index-in edge (array_push appends; use array_set to write an index).");
                    cost += CompileExpr(ctx, it.ValueExprRoot, $"action node {act.Id} (array_push) value", out DslValueType vt, out bool vtTyped);
                    if (vtTyped && vt != adecl.Elem)
                        throw new GateException(
                            $"action node {act.Id} (array_push): value expression type {vt} does not match array '{act.Variable}' element type {adecl.Elem}.");
                    RequireWire(ctx, it.ValueExprRoot, act.Id, TriggerGraph.ActionValueInPort, ExprCompiler.WireOf(adecl.Elem),
                        $"action node {act.Id} (array_push)");
                    break;
                }
                case "array_set":
                {
                    if (it.ValueExprRoot < 0)
                        throw new GateException(
                            $"action node {act.Id} (array_set): requires a value-in expression edge (data port {TriggerGraph.ActionValueInPort}) of the element type.");
                    if (it.IndexExprRoot < 0)
                        throw new GateException(
                            $"action node {act.Id} (array_set): requires an Int index-in expression edge (data port {TriggerGraph.ActionIndexInPort}).");
                    cost += CompileExpr(ctx, it.ValueExprRoot, $"action node {act.Id} (array_set) value", out DslValueType vt, out bool vtTyped);
                    if (vtTyped && vt != adecl.Elem)
                        throw new GateException(
                            $"action node {act.Id} (array_set): value expression type {vt} does not match array '{act.Variable}' element type {adecl.Elem}.");
                    RequireWire(ctx, it.ValueExprRoot, act.Id, TriggerGraph.ActionValueInPort, ExprCompiler.WireOf(adecl.Elem),
                        $"action node {act.Id} (array_set)");
                    cost += CompileExpr(ctx, it.IndexExprRoot, $"action node {act.Id} (array_set) index", out DslValueType idxT, out bool idxTyped);
                    if (idxTyped && idxT != DslValueType.Int)
                        throw new GateException(
                            $"action node {act.Id} (array_set): the index expression must be Int, got {idxT}.");
                    break;
                }
                default: // array_clear
                {
                    if (it.ValueExprRoot >= 0 || it.IndexExprRoot >= 0)
                        throw new GateException(
                            $"action node {act.Id} (array_clear): takes no value-in or index-in expression edges.");
                    break;
                }
            }
            return cost;
        }

        /// <summary>Compile the expression rooted at <paramref name="root"/> (branch conditions and every
        /// loop-context expression compile <c>inCondition:false</c> — trigger-local/loop-var reads are legal)
        /// and return its op count. Throws located on any compile reject.</summary>
        private static long CompileExpr(Ctx ctx, int root, string where, out DslValueType resultType, out bool typed)
        {
            if (SubgraphReadsEventParams(ctx, root))
            {
                resultType = default;
                typed = false;
                return 0;
            }
            if (!ExprCompiler.TryCompile(ctx.Graph, root, ctx.DeclMap, inCondition: false,
                    out ExprProgram? prog, out string? err, ctx.ArrayDecls))
                throw new GateException($"{where}: {err}");
            resultType = prog!.ResultType;
            typed = true;
            return prog.OpCount;
        }

        /// <summary>Story 7.5 (merge): true when the operand subgraph under <paramref name="root"/> contains an
        /// <see cref="ExprEventParamNode"/>. Such a subgraph cannot compile without its trigger's event-parameter
        /// map, which exists only once <see cref="EventDispatchPlan.TryBuild"/> has run — the validator's
        /// event-param root pass (branch/array roots) + data-edge pass (condition/value roots) and the director's
        /// CompileItems all compile these roots strictly WITH the map, so this static-cost pass skips them: ops
        /// uncharged (the same deferred class as condition-expression charging — see the deferred-work ledger) and
        /// the result type unchecked HERE (the later strict compiles re-assert Bool/element-type/Int-index, so the
        /// load still gates fail-closed). Iterative walk, the P9 posture.</summary>
        private static bool SubgraphReadsEventParams(Ctx ctx, int root)
        {
            if (ctx.ById.TryGetValue(root, out NodeBase? rn) && rn is ExprEventParamNode) return true;
            var seen = new HashSet<int> { root };
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                foreach (DataEdge e in ctx.Graph.DataEdges)
                {
                    if (e.Dst != cur || !seen.Add(e.Src)) continue;
                    if (!ctx.ById.TryGetValue(e.Src, out NodeBase? src) || !ExprCompiler.IsExprNode(src)) continue;
                    if (src is ExprEventParamNode) return true;
                    stack.Push(e.Src);
                }
            }
            return false;
        }

        /// <summary>The consumed edge (<paramref name="src"/> → <paramref name="dst"/>:<paramref name="dstPort"/>)
        /// must carry <paramref name="wire"/> (wire color = type, the 7.4 rule extended to the 7.6 ports).</summary>
        private static void RequireWire(Ctx ctx, int src, int dst, int dstPort, DataWireType wire, string where)
        {
            foreach (DataEdge e in ctx.Graph.DataEdges)
                if (e.Src == src && e.Dst == dst && e.DstPort == dstPort && e.Wire != wire)
                    throw new GateException(
                        $"{where}: the edge from expr node {src} must carry the {wire} wire, got '{e.Wire}'.");
        }

        private static long Sat(long v) => v < 0 || v > int.MaxValue ? int.MaxValue : v;

        /// <summary>
        /// Count the nodes of an embedded run_effect subgraph — the static cost the executor pays per run_effect
        /// (mirrors the AbilityValidator walk shape: Sequence children, SearchArea child, Persistent phases,
        /// ApplyModifier period). Bounded by EffectBounds/EffectCaps at its own gate, so the walk always ends.
        /// </summary>
        public static int CountEffectNodes(EffectNode? root)
        {
            if (root is null) return 0;
            int total = 0;
            var stack = new Stack<EffectNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                EffectNode n = stack.Pop();
                total++;
                if (total >= EffectCaps.MaxTotalEffectNodes) return EffectCaps.MaxTotalEffectNodes;
                switch (n)
                {
                    case SequenceEffect seq:
                        foreach (EffectNode c in seq.Children)
                            if (c is not null) stack.Push(c);
                        break;
                    case SearchAreaEffect sa:
                        if (sa.Child is not null) stack.Push(sa.Child);
                        break;
                    case PersistentEffect p:
                        if (p.InitialEffect is not null) stack.Push(p.InitialEffect);
                        if (p.PeriodEffect is not null) stack.Push(p.PeriodEffect);
                        if (p.ExpireEffect is not null) stack.Push(p.ExpireEffect);
                        break;
                    case ApplyModifierEffect am:
                        if (am.Modifier?.PeriodEffect is not null) stack.Push(am.Modifier.PeriodEffect);
                        break;
                }
            }
            return total;
        }
    }
}
