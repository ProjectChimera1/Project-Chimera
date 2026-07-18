#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.10 (review) — the Godot-free TYPED-WIRE inference seam behind the T3 editor's drag-to-wire path.
    /// Given a proposed data edge (its SOURCE node id + whether the destination is a condition / branch-cond sink),
    /// it returns the <see cref="DataWireType"/> the edge must carry so that <c>DataEdge.Wire</c> equals the
    /// source's PRODUCED type — the "wire color = type" contract that <c>CanonicalModelHash</c> folds and
    /// <see cref="GraphStructureGate"/>/<see cref="ExprCompiler"/> validate. Before this seam the panel could only
    /// ever author Boolean/Int wires, so a Fixed/Point expression wired into a Fixed/Point operand was rejected at
    /// load. Pure, Godot-free, float-free.
    /// </summary>
    public static class DataWireInference
    {
        /// <summary>
        /// The wire type a NEW data edge must carry.
        ///   • A condition-in / branch-cond-in destination is <see cref="DataWireType.Boolean"/> by port contract.
        ///   • Otherwise the type is derived from the SOURCE node: a <see cref="ConditionNode"/> is Boolean; an
        ///     <see cref="ExprLiteralNode"/> maps its <c>ValueType</c>; an <see cref="ExprVarNode"/> maps its
        ///     declared variable's type; an <see cref="ExprArrayLenNode"/> is Int; any other expression source is
        ///     compiled through <see cref="ExprCompiler"/> to learn its produced type (falling back to Int when it
        ///     cannot compile cleanly here — the load gate remains authoritative).
        /// </summary>
        /// <param name="graph">The editable graph holding the source node and (for compiled sources) its operands.</param>
        /// <param name="srcId">The proposed edge's source node id.</param>
        /// <param name="destIsConditionSink">True when the destination port is a condition-in / branch-cond-in port.</param>
        /// <param name="declMap">Declared variable name → (type, scope) — used to type <see cref="ExprVarNode"/> reads.</param>
        /// <param name="arrayDecls">Declared Array name → (element type, capacity) — lets a compiled array source
        /// resolve; null (the safe default) means array sources fall back to Int.</param>
        public static DataWireType InferWireType(
            TriggerGraph graph,
            int srcId,
            bool destIsConditionSink,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)>? arrayDecls = null)
        {
            // A condition-in / branch-cond-in sink is Boolean by port contract, independent of the source.
            if (destIsConditionSink) return DataWireType.Boolean;
            return TryInferSourceType(graph, srcId, declMap, arrayDecls) ?? DataWireType.Int;
        }

        /// <summary>
        /// Story 7.10 (follow-up review) — the source node's PRODUCED wire type, or null when it cannot be
        /// determined yet (missing source, undeclared variable, an expression that does not compile — e.g. a
        /// work-in-progress node whose operands are not wired). Callers that want a hard "known and wrong"
        /// distinction (the T3 pre-draw condition-sink type check) use this; null means "unknown — let the
        /// authoritative load gate decide", NOT "Int".
        /// </summary>
        public static DataWireType? TryInferSourceType(
            TriggerGraph graph,
            int srcId,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)>? arrayDecls = null)
        {
            NodeBase? src = null;
            foreach (NodeBase n in graph.Nodes)
                if (n != null && n.Id == srcId) { src = n; break; }

            switch (src)
            {
                case null:
                    return null; // unknown source (the gate re-checks authoritatively)
                case ConditionNode:
                    return DataWireType.Boolean;
                case ExprLiteralNode lit:
                    return MapType(lit.ValueType);
                case ExprVarNode v:
                    return !string.IsNullOrEmpty(v.Name) && declMap.TryGetValue(v.Name, out var decl)
                        ? MapType(decl.Type)
                        : null; // undeclared variable → unknown
                case ExprArrayLenNode:
                    return DataWireType.Int;
                default:
                    // Other expression sources (binary / unary / call / array_get / event_param): compile to learn
                    // the produced type; an incomplete/uncompilable subgraph is UNKNOWN, not Int.
                    if (ExprCompiler.IsExprNode(src)
                        && ExprCompiler.TryCompile(graph, srcId, declMap, inCondition: false,
                               out ExprProgram? prog, out _, arrayDecls)
                        && prog != null)
                        return MapType(prog.ResultType);
                    return null;
            }
        }

        /// <summary>Map a produced value type to its wire color. Bool→Boolean, Int→Int, Fixed→Fixed, Point→Point;
        /// array/ref/other fall back to Int (the safe scalar default — those never legally reach a data wire).</summary>
        private static DataWireType MapType(DslValueType type) => type switch
        {
            DslValueType.Bool  => DataWireType.Boolean,
            DslValueType.Int   => DataWireType.Int,
            DslValueType.Fixed => DataWireType.Fixed,
            DslValueType.Point => DataWireType.Point,
            _                  => DataWireType.Int,
        };
    }
}
