#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.4 — the LOAD-TIME phase of the two-phase expression contract: compile an expression subgraph
    /// (graph + root node id + the declared-variable map + condition-vs-action context) into a typed, flat,
    /// preallocated postfix <see cref="ExprProgram"/>. Runs at BOTH the <c>ScenarioValidator</c> pre-tick gate
    /// (surfaced as located <c>ValidationResult.Fail</c>) and <c>ScenarioDirector.LoadScenario</c> (compile-once).
    ///
    /// Every reject carries a LOCATED error ("expr node &lt;id&gt;: reason"):
    ///   • type mismatch (no implicit Int↔Fixed promotion — CEL-shaped strict typing);
    ///   • literal-zero divisor (an <c>expr_literal</c> 0 wired into a div/mod right operand);
    ///   • undeclared variable name; <c>name[k]</c> on a non-PerPlayer variable / bare <c>name</c> on a PerPlayer
    ///     variable; TriggerLocal reads in a CONDITION expression (the 7.3 P4 rule extended);
    ///   • EntityRef/FactionRef/TimerRef/Array-typed variable reads;
    ///   • missing/forked operand edge (exactly one data edge per operand port);
    ///   • an operand edge whose wire type ≠ the operand's inferred type (wire color = type);
    ///   • op count over <see cref="ExprBounds.MaxExprOps"/> / depth over <see cref="ExprBounds.MaxExprDepth"/>;
    ///   • a cyclic operand subgraph ("cycle detected at node N" — the walk-path guard; the depth cap additionally
    ///     bounds recursion on deep acyclic graphs);
    ///   • a Point-typed ROOT (no consumer accepts one; Point flows only into distance() operands).
    ///
    /// Deterministic postfix emission: operands are emitted in ascending port index (left, then right), so two
    /// structurally-equal subgraphs always compile to the identical program. Pure and throw-free (Try-pattern).
    /// </summary>
    public static class ExprCompiler
    {
        /// <summary>True when <paramref name="node"/> is one of the five 7.4 expression node kinds.</summary>
        public static bool IsExprNode(NodeBase? node) =>
            node is ExprLiteralNode or ExprVarNode or ExprUnaryNode or ExprBinaryNode or ExprCallNode;

        /// <summary>The wire color of a value type (wire color = type). Only the four expression-carryable types
        /// map; ref/array types are rejected before this is consulted.</summary>
        public static DataWireType WireOf(DslValueType type) => type switch
        {
            DslValueType.Int   => DataWireType.Int,
            DslValueType.Fixed => DataWireType.Fixed,
            DslValueType.Bool  => DataWireType.Boolean,
            _                  => DataWireType.Point, // DslValueType.Point (the only other carryable type)
        };

        /// <summary>
        /// Compile the expression subgraph rooted at <paramref name="rootId"/>. Returns true with a ready
        /// <paramref name="program"/>, or false with a located <paramref name="error"/>. Never throws.
        /// </summary>
        /// <param name="graph">The graph holding the expression nodes and their operand data edges.</param>
        /// <param name="rootId">The root expression node's persistent id.</param>
        /// <param name="declaredVars">Declared variable name → (type, scope). Undeclared reads are rejected.</param>
        /// <param name="inCondition">True when compiling a trigger CONDITION expression (TriggerLocal reads are
        /// then rejected — conditions evaluate before the trigger-local scope is entered).</param>
        public static bool TryCompile(
            TriggerGraph graph, int rootId,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVars,
            bool inCondition,
            out ExprProgram? program, out string? error)
        {
            program = null;

            var ctx = new Ctx
            {
                ById        = new Dictionary<int, NodeBase>(graph.Nodes.Count),
                SortedData  = graph.DataEdges.OrderBy(e => e).ToList(), // canonical tuple order → deterministic resolution
                Vars        = declaredVars,
                InCondition = inCondition,
            };
            foreach (NodeBase n in graph.Nodes)
                ctx.ById[n.Id] = n;

            DslValueType? rootType = Visit(ctx, rootId, depth: 1);
            if (rootType is null)
            {
                error = ctx.Error!;
                return false;
            }

            // No consumer accepts a Point root (conditions are Bool; set_variable targets are Int/Fixed/Bool),
            // and Eval returns ONE scalar raw — a Point root would silently drop its Z half. Reject located.
            if (rootType == DslValueType.Point)
            {
                error = $"expr node {rootId}: an expression cannot be Point-rooted (Point values feed only distance() operands).";
                return false;
            }

            if (ctx.Ops.Count > ExprBounds.MaxExprOps)
            {
                error = $"expr node {rootId}: expression compiles to {ctx.Ops.Count} ops, exceeding ExprBounds.MaxExprOps={ExprBounds.MaxExprOps}.";
                return false;
            }

            program = new ExprProgram(ctx.Ops.ToArray(), ctx.MaxStack, rootType.Value);
            error = null;
            return true;
        }

        // ── Compile context ──────────────────────────────────────────────────────

        private sealed class Ctx
        {
            public Dictionary<int, NodeBase> ById = null!;
            public List<DataEdge> SortedData = null!;
            public IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> Vars = null!;
            public bool InCondition;
            public readonly List<ExprProgram.Op> Ops = new();
            public readonly HashSet<int> Path = new(); // node ids on the CURRENT walk path (cycle detection; never enumerated)
            public int Stack;
            public int MaxStack;
            public string? Error;
        }

        private static void Emit(Ctx ctx, ExprProgram.OpCode code, int stackDelta, int a = 0, int b = 0, string? name = null)
        {
            ctx.Ops.Add(new ExprProgram.Op(code, a, b, name));
            ctx.Stack += stackDelta;
            if (ctx.Stack > ctx.MaxStack) ctx.MaxStack = ctx.Stack;
        }

        // ── Typed bottom-up walk (returns the node's inferred type, or null with ctx.Error set) ──

        private static DslValueType? Visit(Ctx ctx, int nodeId, int depth)
        {
            if (depth > ExprBounds.MaxExprDepth)
            {
                ctx.Error = $"expr node {nodeId}: expression depth {depth} exceeds ExprBounds.MaxExprDepth={ExprBounds.MaxExprDepth}.";
                return null;
            }

            // Review (7.4 pass 2): abort the walk as soon as the op budget is blown, not only at the post-walk
            // check — diamond-shared operands re-emit per consumer, so a depth-legal shared subgraph could
            // otherwise do ~2^depth visits (and grow Ops to ~65k entries) before the 64-op reject. With this
            // early-out the residual work after the cap trips is bounded by one depth-limited descent.
            if (ctx.Ops.Count > ExprBounds.MaxExprOps)
            {
                ctx.Error = $"expr node {nodeId}: expression compiles to more than ExprBounds.MaxExprOps={ExprBounds.MaxExprOps} ops.";
                return null;
            }

            // A revisit of a node already on the CURRENT walk path is an operand cycle — name it as such (review
            // patch: the depth cap used to trip instead, blaming a depth the author never wrote). The depth cap
            // above still bounds recursion on genuinely deep acyclic subgraphs; diamond sharing (one node feeding
            // two consumers) stays legal because the path entry is removed once the node's subtree completes.
            if (!ctx.Path.Add(nodeId))
            {
                ctx.Error = $"expr node {nodeId}: cycle detected at node {nodeId} (expression operand edges must be acyclic).";
                return null;
            }
            try { return VisitNode(ctx, nodeId, depth); }
            finally { ctx.Path.Remove(nodeId); }
        }

        private static DslValueType? VisitNode(Ctx ctx, int nodeId, int depth)
        {
            if (!ctx.ById.TryGetValue(nodeId, out NodeBase? node))
            {
                ctx.Error = $"expr node {nodeId}: an operand edge references a node id that does not exist.";
                return null;
            }

            switch (node)
            {
                case ExprLiteralNode lit:
                {
                    if (lit.ValueType != DslValueType.Int && lit.ValueType != DslValueType.Fixed && lit.ValueType != DslValueType.Bool)
                    {
                        ctx.Error = $"expr node {nodeId}: literal value type '{lit.ValueType}' is not literal-able (Int/Fixed/Bool only).";
                        return null;
                    }
                    // A Bool literal's Raw normalizes to 0/1 at compile (an in-memory node with Raw=7 must compare
                    // equal to true — the same 0/1 rule DslVarTable applies to Bool writes).
                    int raw = lit.ValueType == DslValueType.Bool ? (lit.Raw != 0 ? 1 : 0) : lit.Raw;
                    Emit(ctx, ExprProgram.OpCode.PushLit, +1, raw);
                    return lit.ValueType;
                }

                case ExprVarNode v:
                {
                    if (string.IsNullOrEmpty(v.Name) || !ctx.Vars.TryGetValue(v.Name, out var decl))
                    {
                        ctx.Error = $"expr node {nodeId}: reads undeclared variable '{v.Name}'.";
                        return null;
                    }
                    if (decl.Type is DslValueType.EntityRef or DslValueType.FactionRef or DslValueType.TimerRef or DslValueType.Array)
                    {
                        ctx.Error = $"expr node {nodeId}: variable '{v.Name}' is {decl.Type}-typed; expressions can read only Int/Fixed/Bool/Point variables.";
                        return null;
                    }
                    if (ctx.InCondition && decl.Scope == VarScope.TriggerLocal)
                    {
                        ctx.Error = $"expr node {nodeId}: variable '{v.Name}' is TriggerLocal-scoped and cannot be read in a condition expression (conditions evaluate before the trigger-local scope is entered).";
                        return null;
                    }
                    if (decl.Scope == VarScope.PerPlayer)
                    {
                        if (v.Faction < 0)
                        {
                            ctx.Error = $"expr node {nodeId}: variable '{v.Name}' is PerPlayer-scoped; read it with a slot ('{v.Name}[k]'), not bare.";
                            return null;
                        }
                        if (v.Faction >= DslVarTable.PlayerSlots)
                        {
                            ctx.Error = $"expr node {nodeId}: per-player slot {v.Faction} is out of range [0,{DslVarTable.PlayerSlots}).";
                            return null;
                        }
                    }
                    else if (v.Faction >= 0)
                    {
                        ctx.Error = $"expr node {nodeId}: variable '{v.Name}' is {decl.Scope}-scoped; a '[k]' slot read is only valid on a PerPlayer variable.";
                        return null;
                    }
                    Emit(ctx, ExprProgram.OpCode.PushVar, +1, v.Faction < 0 ? 0 : v.Faction, name: v.Name);
                    return decl.Type;
                }

                case ExprUnaryNode u:
                {
                    DslValueType? operand = Operand(ctx, nodeId, TriggerGraph.ExprOperandPort0, depth);
                    if (operand is null) return null;
                    switch (u.Op)
                    {
                        case "neg":
                            if (operand != DslValueType.Int && operand != DslValueType.Fixed)
                            {
                                ctx.Error = $"expr node {nodeId}: operator 'neg' requires an Int or Fixed operand, got {operand}.";
                                return null;
                            }
                            Emit(ctx, ExprProgram.OpCode.Neg, 0);
                            return operand;
                        case "not":
                            if (operand != DslValueType.Bool)
                            {
                                ctx.Error = $"expr node {nodeId}: operator 'not' requires a Bool operand, got {operand}.";
                                return null;
                            }
                            Emit(ctx, ExprProgram.OpCode.Not, 0);
                            return DslValueType.Bool;
                        default:
                            ctx.Error = $"expr node {nodeId}: unknown expr_unary operator '{u.Op}'.";
                            return null;
                    }
                }

                case ExprBinaryNode bin:
                    return VisitBinary(ctx, bin, depth);

                case ExprCallNode call:
                    return VisitCall(ctx, call, depth);

                default:
                    ctx.Error = $"expr node {nodeId}: node is not an expression node (only expr_* kinds may feed an expression).";
                    return null;
            }
        }

        private static DslValueType? VisitBinary(Ctx ctx, ExprBinaryNode bin, int depth)
        {
            int id = bin.Id;

            // Literal-zero divisor is a LOAD-TIME reject (the statically-knowable case; a runtime variable zero
            // evaluates to 0 instead). Checked before emission so the located error names the div/mod node.
            // Only NUMERIC (Int/Fixed) literals count — a Bool `false` also has Raw 0, but blaming "the literal 0"
            // would misquote the author; it falls through to the type check below and reports the real error
            // (a Bool operand on a numeric operator) instead (review, 7.4 pass 2).
            if (bin.Op is "div" or "mod")
            {
                if (TryFindOperandEdge(ctx, id, TriggerGraph.ExprOperandPort1, out DataEdge rightEdge)
                    && ctx.ById.TryGetValue(rightEdge.Src, out NodeBase? rightNode)
                    && rightNode is ExprLiteralNode { Raw: 0 } zeroLit
                    && zeroLit.ValueType != DslValueType.Bool)
                {
                    ctx.Error = $"expr node {id}: literal-zero divisor (the right operand of '{bin.Op}' is the literal 0).";
                    return null;
                }
            }

            DslValueType? lt = Operand(ctx, id, TriggerGraph.ExprOperandPort0, depth);
            if (lt is null) return null;
            DslValueType? rt = Operand(ctx, id, TriggerGraph.ExprOperandPort1, depth);
            if (rt is null) return null;

            bool bothInt   = lt == DslValueType.Int   && rt == DslValueType.Int;
            bool bothFixed = lt == DslValueType.Fixed && rt == DslValueType.Fixed;
            bool bothBool  = lt == DslValueType.Bool  && rt == DslValueType.Bool;

            switch (bin.Op)
            {
                case "add": case "sub": case "mul": case "div": case "mod":
                    if (!bothInt && !bothFixed)
                    {
                        ctx.Error = $"expr node {id}: operator '{bin.Op}' type mismatch — requires both operands Int or both Fixed (no implicit promotion), got {lt} and {rt}.";
                        return null;
                    }
                    Emit(ctx, bin.Op switch
                    {
                        "add" => ExprProgram.OpCode.Add,
                        "sub" => ExprProgram.OpCode.Sub,
                        "mul" => bothInt ? ExprProgram.OpCode.MulInt : ExprProgram.OpCode.MulFix,
                        "div" => bothInt ? ExprProgram.OpCode.DivInt : ExprProgram.OpCode.DivFix,
                        _     => ExprProgram.OpCode.Mod,
                    }, -1);
                    return lt;

                case "gt": case "lt": case "ge": case "le":
                    if (!bothInt && !bothFixed)
                    {
                        ctx.Error = $"expr node {id}: comparison '{bin.Op}' requires both operands Int or both Fixed, got {lt} and {rt}.";
                        return null;
                    }
                    Emit(ctx, bin.Op switch
                    {
                        "gt" => ExprProgram.OpCode.Gt,
                        "lt" => ExprProgram.OpCode.Lt,
                        "ge" => ExprProgram.OpCode.Ge,
                        _    => ExprProgram.OpCode.Le,
                    }, -1);
                    return DslValueType.Bool;

                case "eq": case "ne":
                    if (!bothInt && !bothFixed && !bothBool)
                    {
                        ctx.Error = $"expr node {id}: comparison '{bin.Op}' requires both operands of the same Int/Fixed/Bool type, got {lt} and {rt}.";
                        return null;
                    }
                    Emit(ctx, bin.Op == "eq" ? ExprProgram.OpCode.Eq : ExprProgram.OpCode.Ne, -1);
                    return DslValueType.Bool;

                case "and": case "or":
                    if (!bothBool)
                    {
                        ctx.Error = $"expr node {id}: operator '{bin.Op}' requires Bool operands, got {lt} and {rt}.";
                        return null;
                    }
                    Emit(ctx, bin.Op == "and" ? ExprProgram.OpCode.And : ExprProgram.OpCode.Or, -1);
                    return DslValueType.Bool;

                default:
                    ctx.Error = $"expr node {id}: unknown expr_binary operator '{bin.Op}'.";
                    return null;
            }
        }

        private static DslValueType? VisitCall(Ctx ctx, ExprCallNode call, int depth)
        {
            int id = call.Id;
            int arity = call.Fn switch
            {
                "count" => 1, "abs" => 1,
                "distance" => 2, "min" => 2, "max" => 2,
                _ => -1,
            };
            if (arity < 0)
            {
                ctx.Error = $"expr node {id}: unknown built-in '{call.Fn}'.";
                return null;
            }

            // An operand edge into a port ≥ arity is a wrong-arg-count authoring error, not silently ignored.
            foreach (DataEdge e in ctx.SortedData)
                if (e.Dst == id && e.DstPort >= arity)
                {
                    ctx.Error = $"expr node {id}: built-in '{call.Fn}' takes {arity} argument(s), but port {e.DstPort} has an incoming data edge.";
                    return null;
                }

            var argTypes = new DslValueType[arity];
            for (int p = 0; p < arity; p++)
            {
                DslValueType? t = Operand(ctx, id, p, depth);
                if (t is null) return null;
                argTypes[p] = t.Value;
            }

            switch (call.Fn)
            {
                case "count":
                    if (argTypes[0] != DslValueType.Int)
                    {
                        ctx.Error = $"expr node {id}: count(faction) requires an Int argument, got {argTypes[0]}.";
                        return null;
                    }
                    // A LITERAL faction slot outside [0, PlayerSlots) is a LOAD-TIME reject (the statically-knowable
                    // case, mirroring the literal-zero divisor); a COMPUTED out-of-range slot instead evaluates to 0
                    // at the IExprWorld seam.
                    if (TryFindOperandEdge(ctx, id, TriggerGraph.ExprOperandPort0, out DataEdge factionEdge)
                        && ctx.ById.TryGetValue(factionEdge.Src, out NodeBase? factionNode)
                        && factionNode is ExprLiteralNode factionLit
                        && (factionLit.Raw < 0 || factionLit.Raw >= DslVarTable.PlayerSlots))
                    {
                        ctx.Error = $"expr node {id}: count() faction slot {factionLit.Raw} is out of range [0,{DslVarTable.PlayerSlots}).";
                        return null;
                    }
                    Emit(ctx, ExprProgram.OpCode.Count, 0);
                    return DslValueType.Int;

                case "distance":
                    if (argTypes[0] != DslValueType.Point || argTypes[1] != DslValueType.Point)
                    {
                        ctx.Error = $"expr node {id}: distance(a,b) requires two Point arguments, got {argTypes[0]} and {argTypes[1]}.";
                        return null;
                    }
                    Emit(ctx, ExprProgram.OpCode.Distance, -1);
                    return DslValueType.Fixed;

                case "min": case "max":
                {
                    bool bothInt   = argTypes[0] == DslValueType.Int   && argTypes[1] == DslValueType.Int;
                    bool bothFixed = argTypes[0] == DslValueType.Fixed && argTypes[1] == DslValueType.Fixed;
                    if (!bothInt && !bothFixed)
                    {
                        ctx.Error = $"expr node {id}: {call.Fn}(a,b) requires both arguments Int or both Fixed, got {argTypes[0]} and {argTypes[1]}.";
                        return null;
                    }
                    Emit(ctx, call.Fn == "min" ? ExprProgram.OpCode.Min : ExprProgram.OpCode.Max, -1);
                    return argTypes[0];
                }

                default: // "abs"
                    if (argTypes[0] != DslValueType.Int && argTypes[0] != DslValueType.Fixed)
                    {
                        ctx.Error = $"expr node {id}: abs(a) requires an Int or Fixed argument, got {argTypes[0]}.";
                        return null;
                    }
                    Emit(ctx, ExprProgram.OpCode.Abs, 0);
                    return argTypes[0];
            }
        }

        /// <summary>Resolve the EXACTLY-ONE operand edge into (<paramref name="nodeId"/>, <paramref name="port"/>),
        /// recurse into its source, and verify the edge's wire color equals the operand's inferred type.</summary>
        private static DslValueType? Operand(Ctx ctx, int nodeId, int port, int depth)
        {
            if (!TryFindOperandEdge(ctx, nodeId, port, out DataEdge edge))
            {
                // TryFindOperandEdge distinguishes missing vs forked in ctx.Error.
                return null;
            }

            // Every expression node emits on ExprDataOutPort (= 0). A stray src_port is a non-canonical encoding
            // the whole layer would otherwise silently tolerate (compile, validate, round-trip) — reject it located,
            // matching the fail-closed posture the converter applies to ExprVarNode.Faction (review, 7.4 pass 2).
            if (edge.SrcPort != TriggerGraph.ExprDataOutPort)
            {
                ctx.Error = $"expr node {nodeId}: operand port {port} edge leaves src node {edge.Src} on port {edge.SrcPort}; expression nodes emit only on port {TriggerGraph.ExprDataOutPort}.";
                return null;
            }

            DslValueType? t = Visit(ctx, edge.Src, depth + 1);
            if (t is null) return null;

            if (edge.Wire != WireOf(t.Value))
            {
                ctx.Error = $"expr node {nodeId}: operand port {port} edge carries wire '{edge.Wire}' but the operand's inferred type is {t} (wire color must equal type).";
                return null;
            }
            return t;
        }

        /// <summary>Find the single data edge into (<paramref name="nodeId"/>, <paramref name="port"/>). Zero edges
        /// (missing) or more than one (forked) is a located reject via <c>ctx.Error</c>.</summary>
        private static bool TryFindOperandEdge(Ctx ctx, int nodeId, int port, out DataEdge edge)
        {
            int found = 0;
            edge = default;
            foreach (DataEdge e in ctx.SortedData)
                if (e.Dst == nodeId && e.DstPort == port)
                {
                    if (found == 0) edge = e;
                    found++;
                }
            if (found == 1) return true;
            ctx.Error = found == 0
                ? $"expr node {nodeId}: operand port {port} has no incoming data edge (exactly one required)."
                : $"expr node {nodeId}: operand port {port} has {found} incoming data edges (forked; exactly one required).";
            return false;
        }
    }
}
