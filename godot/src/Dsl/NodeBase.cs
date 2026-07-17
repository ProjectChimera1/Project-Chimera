#nullable enable
using ProjectChimera.Core;      // Fixed
using ProjectChimera.Effects;   // EffectNode

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 — the id-keyed root of the graph-canonical trigger IR. Every node carries a PERSISTENT integer
    /// <see cref="Id"/> (assigned deterministically by <see cref="TriggerGraph.FromFlat"/> from flat-array order),
    /// so the graph is addressed by stable id rather than array position (the flat <c>TriggerDefinition[]</c>'s
    /// position-index scheme, which every later DSL feature would otherwise diverge onto).
    ///
    /// The concrete node kinds mirror the existing CLOSED trigger vocabulary (the <see cref="TriggerDefinition"/>
    /// POCOs) 1:1, plus a single <see cref="EffectActionNode"/> that EMBEDS a D1 <see cref="EffectNode"/> subgraph
    /// unchanged (the "superset that contains effect subgraphs, no second executor"). Pure sim-layer C#: Godot-free,
    /// float-free (fractional numerics are <see cref="Fixed"/> 16.16, quantized only at the JSON boundary).
    /// </summary>
    public abstract class NodeBase
    {
        /// <summary>The persistent integer id. Unique within a <see cref="TriggerGraph"/>; drives canonical
        /// ordering and graph→flat reconstruction.</summary>
        public int Id { get; set; }

        /// <summary>
        /// Story 7.7 — the OPTIONAL per-node <c>_editor</c> annotation bag (e.g. 7.10 node positions / authoring
        /// affordances). Round-tripped VERBATIM through <see cref="NodeBaseJsonConverter"/> (allow-listed on every
        /// kind, NEVER interpreted by any gate or the runtime) and serialized deterministically by
        /// <c>TriggerGraph.ToCanonicalJson</c>. EXCLUDED from <c>CanonicalModelHash</c> BY CONSTRUCTION: the v8
        /// typed graph fold reads each kind's semantic fields directly and never touches this bag, so editing
        /// <c>_editor</c> content can never move the MP handshake hash.
        /// </summary>
        public System.Text.Json.JsonElement? Editor { get; set; }
    }

    /// <summary>
    /// The head node of one trigger (WHEN…IF…THEN). Carries the <see cref="TriggerDefinition"/> header fields 1:1.
    /// EventNodes wire into it via exec edges (event-in port); ConditionNodes gate it via Boolean data edges
    /// (condition-in port); the action chain leaves it via an exec edge (exec-out port).
    /// </summary>
    public sealed class TriggerNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.Trigger;

        public string Name { get; set; } = "Trigger";
        public bool Enabled { get; set; } = true;
        public bool RunOnce { get; set; } = false;
        public Fixed CooldownSeconds { get; set; } = Fixed.Zero;
        public int Priority { get; set; } = 0;
    }

    /// <summary>An event that can fire a trigger. <see cref="Kind"/> ∈ the closed event-type set. Carries the
    /// <see cref="TriggerEvent"/> field set 1:1.</summary>
    public sealed class EventNode : NodeBase
    {
        /// <summary>Discriminator ∈ <see cref="NodeKinds.EventTypes"/> (e.g. "unit_dies").</summary>
        public string Kind { get; set; } = "";

        public int Faction { get; set; } = 0;
        public string? BuildingType { get; set; }
        public string? TimerName { get; set; }
        public Fixed Amount { get; set; } = Fixed.Zero;
        public int Count { get; set; } = 0;
        public string Operator { get; set; } = ">=";
    }

    /// <summary>A condition that must be true for a trigger to fire. <see cref="Kind"/> ∈ the closed condition-type
    /// set. Carries the <see cref="TriggerCondition"/> field set 1:1.</summary>
    public sealed class ConditionNode : NodeBase
    {
        /// <summary>Discriminator ∈ <see cref="NodeKinds.ConditionTypes"/> (e.g. "resource_comparison").</summary>
        public string Kind { get; set; } = "always";

        public int Faction { get; set; } = 0;
        public string? BuildingType { get; set; }
        public Fixed Amount { get; set; } = Fixed.Zero;
        public int Count { get; set; } = 0;
        public string? Variable { get; set; }
        public string? RegionId { get; set; }
        public int Value { get; set; } = 0;
        public string Operator { get; set; } = ">=";
    }

    /// <summary>A leaf action executed when a trigger fires. <see cref="Kind"/> ∈ the closed action-type set.
    /// Carries the <see cref="TriggerAction"/> field set 1:1.</summary>
    public sealed class ActionNode : NodeBase
    {
        /// <summary>Discriminator ∈ <see cref="NodeKinds.ActionTypes"/> (e.g. "spawn_unit").</summary>
        public string Kind { get; set; } = "";

        public string? UnitId { get; set; }
        public int Faction { get; set; } = 0;
        public Fixed X { get; set; } = Fixed.Zero;
        public Fixed Z { get; set; } = Fixed.Zero;
        public int Count { get; set; } = 1;
        public string? Text { get; set; }
        public Fixed Duration { get; set; } = Fixed.FromInt(4);
        public string? TimerName { get; set; }
        public Fixed TimerSeconds { get; set; } = Fixed.FromInt(30);
        public Fixed Amount { get; set; } = Fixed.Zero;
        public int Value { get; set; } = 0;
        public string? Variable { get; set; }
        public string? SoundId { get; set; }
    }

    /// <summary>
    /// The embed-capability seam (Story 7.2): a node whose payload is a whole D1 <see cref="EffectNode"/> subgraph
    /// (the same runtime object tree abilities use), (de)serialized by delegating to the existing
    /// <c>EffectNodeJsonConverter</c> — never a reimplementation, never a second effect executor. NO flat action
    /// lowers to it; it proves the IR is a superset that embeds effect subgraphs unchanged. Its tick execution
    /// (reusing the existing effect executor) is later scope.
    /// </summary>
    public sealed class EffectActionNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.RunEffect;

        /// <summary>The embedded effect subgraph root (opaque, position-addressed, no ids of its own).</summary>
        public EffectNode Effect { get; set; } = null!;
    }

    /// <summary>
    /// Story 7.4 — a typed expression LITERAL leaf (Int / Fixed / Bool; the only literal-able value types).
    /// <see cref="Raw"/> holds the value's raw int: the plain integer for Int, <c>Fixed.Raw</c> for Fixed, 0/1 for
    /// Bool. The JSON <c>value</c> property reads as an int / a Fixed via the registered converter / a bool,
    /// dispatched on the <c>type</c> property. Emits its value on <c>ExprDataOutPort</c> (data edge Src).
    /// </summary>
    public sealed class ExprLiteralNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprLiteral;

        /// <summary>The literal's value type — restricted to Int / Fixed / Bool (enforced at (de)serialize AND compile).</summary>
        public DslValueType ValueType { get; set; } = DslValueType.Int;

        /// <summary>The raw int payload (Int value / Fixed.Raw / Bool 0-1).</summary>
        public int Raw { get; set; } = 0;
    }

    /// <summary>
    /// Story 7.4 — a declared-variable READ leaf. <see cref="Faction"/> is the per-player slot for a
    /// <c>name[k]</c> read of a PerPlayer variable; -1 means a BARE read (no slot — required for non-PerPlayer
    /// variables, rejected for PerPlayer ones). The compiler resolves the type/scope against the declared-variable
    /// map; undeclared or ref/array-typed reads are located compile rejects.
    /// </summary>
    public sealed class ExprVarNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprVar;

        /// <summary>The declared variable name to read.</summary>
        public string Name { get; set; } = "";

        /// <summary>The per-player slot (0..7) for a PerPlayer read; -1 = bare (slot-less) read.</summary>
        public int Faction { get; set; } = -1;
    }

    /// <summary>Story 7.4 — a unary expression operator. <see cref="Op"/> ∈ <see cref="NodeKinds.ExprUnaryOps"/>
    /// ({neg, not}); the single operand wires into <c>ExprOperandPort0</c>.</summary>
    public sealed class ExprUnaryNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprUnary;

        /// <summary>Operator ∈ {neg, not} (membership enforced at parse — an unknown op never constructs).</summary>
        public string Op { get; set; } = "";
    }

    /// <summary>Story 7.4 — a binary expression operator. <see cref="Op"/> ∈ <see cref="NodeKinds.ExprBinaryOps"/>;
    /// the left/right operands wire into <c>ExprOperandPort0</c>/<c>ExprOperandPort1</c>.</summary>
    public sealed class ExprBinaryNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprBinary;

        /// <summary>Operator ∈ {add,sub,mul,div,mod,gt,lt,ge,le,eq,ne,and,or} (membership enforced at parse).</summary>
        public string Op { get; set; } = "";
    }

    /// <summary>Story 7.4 — a closed built-in call. <see cref="Fn"/> ∈ <see cref="NodeKinds.ExprCallFns"/>
    /// ({count, distance, min, max, abs}); arguments wire into ports 0..arity-1.</summary>
    public sealed class ExprCallNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprCall;

        /// <summary>Built-in name ∈ {count, distance, min, max, abs} (membership enforced at parse).</summary>
        public string Fn { get; set; } = "";
    }

    /// <summary>
    /// Story 7.6 — the bounded, snapshot-at-entry loop container (the ONLY sanctioned iteration form besides
    /// <see cref="ForEachBatchedNode"/>; no While/recursion/goto exists in the grammar). <see cref="Source"/> ∈
    /// <see cref="NodeKinds.ForEachSources"/>: <c>array</c> iterates a declared Global array's elements (snapshot
    /// copied to a preallocated buffer at loop entry); <c>faction_units</c>/<c>region_units</c> iterate an
    /// ascending-id snapshot of alive units (faction filter, <see cref="Faction"/> −1 = any; <c>region_units</c>
    /// also point-in-region). Iterates min(snapshotCount, <see cref="UpTo"/>); <see cref="UpTo"/> is the LOUD
    /// authored cap — REQUIRED for entity sources (unset rejects at load, directing to <c>for_each_batched</c> or
    /// an explicit <c>up_to</c>). <see cref="LoopVar"/> names a declared TriggerLocal variable (element value for
    /// arrays — REQUIRED; entity id / Int for entity sources — optional). Exec-out port 1 = body chain; port 0 =
    /// continuation.
    /// </summary>
    public sealed class ForEachNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ForEach;

        /// <summary>Collection source ∈ {array, faction_units, region_units} (membership enforced at parse).</summary>
        public string Source { get; set; } = "";

        /// <summary>The declared Array-typed variable name (required when <see cref="Source"/> = array).</summary>
        public string? ArrayName { get; set; }

        /// <summary>Faction filter for entity sources; -1 = any faction.</summary>
        public int Faction { get; set; } = -1;

        /// <summary>The declared region id (required when <see cref="Source"/> = region_units).</summary>
        public string? RegionId { get; set; }

        /// <summary>The loud iteration cap (1..<c>DslBounds.MaxForEachItems</c>). 0 = unset — a LOAD reject for
        /// entity sources; for arrays 0 means "the full array" (capacity ≤ MaxArrayCapacity ≤ MaxForEachItems).</summary>
        public int UpTo { get; set; } = 0;

        /// <summary>The declared TriggerLocal loop variable written before each iteration (element value / entity id).</summary>
        public string? LoopVar { get; set; }
    }

    /// <summary>
    /// Story 7.6 — the cross-tick drip loop (entity sources ONLY; arrays never need batching). Must be a
    /// TOP-LEVEL chain node (never nested inside for_each/branch), at most one per trigger. On fire it snapshots
    /// ascending alive-unit ids into its preallocated <c>DslLoopState</c> continuation row (cap
    /// <c>DslBounds.MaxBatchSnapshot</c>); each subsequent director tick drains <see cref="BatchSize"/> entries
    /// at the START of the tick (dead entities skipped at drain time; the trigger is suppressed in the sweep
    /// while draining). Exec-out port 1 = per-entity body chain; port 0 = the continuation chain, run on the
    /// completion tick. The continuation state is checksummed (self-contained — NOT a 7.5 event queue).
    ///
    /// <para>Review P14 — authoring traps: (1) each drain tick opens a FRESH TriggerLocal scope per row, so a
    /// body accumulator held in a TriggerLocal does NOT survive batch boundaries, and the completion-tick
    /// continuation sees only the FINAL batch's scope — accumulate across batches in Global variables instead.
    /// (2) The node deliberately has NO LoopVar: a body reaches the current entity only implicitly, through the
    /// run_effect anchor override (the 7.6 design ships no cross-tick loop-var resume machinery).</para>
    /// </summary>
    public sealed class ForEachBatchedNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ForEachBatched;

        /// <summary>Collection source ∈ {faction_units, region_units} (entity sources only; enforced at parse).</summary>
        public string Source { get; set; } = "";

        /// <summary>Faction filter; -1 = any faction.</summary>
        public int Faction { get; set; } = -1;

        /// <summary>The declared region id (required when <see cref="Source"/> = region_units).</summary>
        public string? RegionId { get; set; }

        /// <summary>Entities drained per tick (1..<c>DslBounds.MaxForEachItems</c>; gated at load).</summary>
        public int BatchSize { get; set; } = 0;
    }

    /// <summary>
    /// Story 7.6 — the conditional exec container. A Bool expression wires into its condition-in data port
    /// (<c>TriggerGraph.BranchCondInPort</c>, compiled <c>inCondition: false</c> so TriggerLocal / loop-var reads
    /// are LEGAL, unlike the trigger condition-in). Exec-out port 1 = then chain, port 2 = else chain, port 0 =
    /// continuation (always runs after the taken branch).
    /// </summary>
    public sealed class BranchNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.Branch;
    }

    /// <summary>
    /// Story 7.6 — an array element read: <c>arr[i]</c>. <see cref="Name"/> must be a declared Array variable;
    /// the Int index expression wires into operand port 0. Result type = the declared element type. Total
    /// runtime semantics: an out-of-bounds index evaluates to 0 (the div-by-zero precedent). The ONLY legal
    /// array read forms are this node and <see cref="ExprArrayLenNode"/> — a bare <c>expr_var</c> read of an
    /// Array-typed name still rejects at compile.
    /// </summary>
    public sealed class ExprArrayGetNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprArrayGet;

        /// <summary>The declared Array-typed variable name to index.</summary>
        public string Name { get; set; } = "";
    }

    /// <summary>Story 7.6 — an array length read: <c>length(arr)</c>. <see cref="Name"/> must be a declared
    /// Array variable; emits the live element count as Int on <c>ExprDataOutPort</c>.</summary>
    public sealed class ExprArrayLenNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprArrayLen;

        /// <summary>The declared Array-typed variable name to measure.</summary>
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// The CLOSED <c>kind</c> discriminator registry: the closed ECA vocabulary (event/condition/action type
    /// strings) plus the two structural kinds "trigger" and "run_effect", plus (Story 7.4) the five expression
    /// kinds. A <c>kind</c> outside this union is
    /// rejected at parse by <see cref="NodeBaseJsonConverter"/> (fail-closed, AR-22). The three ECA sets are
    /// pairwise disjoint AND disjoint from {trigger, run_effect} and the expr_* kinds, so a kind string maps to
    /// exactly one node type. The expression op/fn sets are FIELD-value vocabularies (not kinds) — also closed,
    /// membership-checked at parse so an unknown op/fn never constructs.
    ///
    /// Story 7.7 — this registry is THE single vocabulary source: <c>ScenarioValidator</c>'s trigger vocabulary
    /// fields alias <see cref="EventTypes"/>/<see cref="ConditionTypes"/>/<see cref="FlatActionTypes"/> directly
    /// (no hand-kept copy remains; <c>NodeKindsLockstepTests</c> asserts the aliasing by reference so a second
    /// copy can never drift back in). When the trigger vocabulary is extended (e.g. Story 7.13), extend it HERE.
    /// </summary>
    internal static class NodeKinds
    {
        public const string Trigger   = "trigger";
        public const string RunEffect = "run_effect";

        // ── Story 7.4 — the five expression node kinds (structural, like trigger/run_effect) ──
        public const string ExprLiteral = "expr_literal";
        public const string ExprVar     = "expr_var";
        public const string ExprUnary   = "expr_unary";
        public const string ExprBinary  = "expr_binary";
        public const string ExprCall    = "expr_call";

        // ── Story 7.6 — the three exec-container kinds + the two array expression kinds. The CLOSED grammar's
        //    only iteration/branch forms: no While/recursion/goto kind exists, so none can be expressed. ──
        public const string ForEach        = "for_each";
        public const string ForEachBatched = "for_each_batched";
        public const string Branch         = "branch";
        public const string ExprArrayGet   = "expr_array_get";
        public const string ExprArrayLen   = "expr_array_len";

        public static readonly string[] EventTypes     = { "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold" };
        public static readonly string[] ConditionTypes = { "always", "building_exists", "resource_comparison", "unit_count", "variable_comparison", "unit_in_region" };
        // Story 7.6: the three array action kinds (array_push/array_set/array_clear) are GRAPH-CHANNEL-ONLY —
        // ToFlat skips them like EffectActionNode and the flat validator's _actionTypes stays untouched, so no
        // flat TriggerDefinition can carry them.
        public static readonly string[] ActionTypes    = { "spawn_unit", "display_message", "victory", "defeat", "create_timer", "add_resources", "set_variable", "play_sound", "array_push", "array_set", "array_clear" };

        /// <summary>
        /// Story 7.7 — the FLAT-channel action vocabulary: <see cref="ActionTypes"/> minus the graph-channel-only
        /// kinds (the array actions have no flat form — <c>ToFlat</c> skips them like <c>run_effect</c>). This is
        /// the EXACT set <c>ScenarioDirector.ExecuteActions</c> handles for a flat <c>TriggerAction</c>, and the
        /// set <c>ScenarioValidator</c> gates flat actions against — derived here, never hand-copied.
        /// </summary>
        public static readonly string[] FlatActionTypes =
            System.Array.FindAll(ActionTypes, k => !IsArrayActionKind(k));

        // ── Story 7.6 — the CLOSED for_each source vocabulary (a field value inside for_each/for_each_batched,
        //    not a kind). Membership is checked at parse AND at both load gates, so an unknown source never
        //    constructs. for_each_batched additionally restricts to the two entity sources at parse. ──
        public static readonly string[] ForEachSources = { "array", "faction_units", "region_units" };

        /// <summary>Story 7.6 — true for the three graph-channel-only array action kinds.</summary>
        public static bool IsArrayActionKind(string? kind) =>
            kind == "array_push" || kind == "array_set" || kind == "array_clear";

        // ── Story 7.7 — the CLOSED comparison-operator vocabulary (a field value inside event/condition nodes and
        //    flat trigger events/conditions, not a kind). ONE source for BOTH channels: ScenarioValidator's
        //    _operators aliases this array (flat gate) and NodeBaseJsonConverter membership-checks it at parse
        //    (graph channel), so an unknown operator never constructs — it previously parsed with a silent
        //    ScenarioDirector.Compare `_ => false` (an inert dead trigger). ──
        public static readonly string[] Operators = { ">", "<", ">=", "<=", "==", "!=" };

        // ── Story 7.4 — the CLOSED expression op/fn vocabularies (field values inside expr_* nodes, not kinds).
        //    Membership is checked at parse (NodeBaseJsonConverter) AND at compile (ExprCompiler), so an unknown
        //    op/fn never constructs and never evaluates. ──
        public static readonly string[] ExprUnaryOps  = { "neg", "not" };
        public static readonly string[] ExprBinaryOps = { "add", "sub", "mul", "div", "mod", "gt", "lt", "ge", "le", "eq", "ne", "and", "or" };
        public static readonly string[] ExprCallFns   = { "count", "distance", "min", "max", "abs" };

        /// <summary>Exact-match membership in a closed string set (case-sensitive). Null is never a member.</summary>
        public static bool InSet(string[] set, string? value)
        {
            if (value is null) return false;
            for (int i = 0; i < set.Length; i++)
                if (set[i] == value) return true;
            return false;
        }

        /// <summary>Story 7.7 — the kind string a node serializes under (the closed-registry discriminator),
        /// resolved from the runtime type. Used by located structural-gate errors and the canonical hash fold.</summary>
        public static string KindOf(NodeBase n) => n switch
        {
            TriggerNode         => Trigger,
            EffectActionNode    => RunEffect,
            EventNode e         => e.Kind,
            ConditionNode c     => c.Kind,
            ActionNode a        => a.Kind,
            ExprLiteralNode     => ExprLiteral,
            ExprVarNode         => ExprVar,
            ExprUnaryNode       => ExprUnary,
            ExprBinaryNode      => ExprBinary,
            ExprCallNode        => ExprCall,
            ForEachNode         => ForEach,
            ForEachBatchedNode  => ForEachBatched,
            BranchNode          => Branch,
            ExprArrayGetNode    => ExprArrayGet,
            ExprArrayLenNode    => ExprArrayLen,
            _                   => n.GetType().Name, // unreachable: the registry is closed at parse
        };
    }

    /// <summary>
    /// Story 7.7 — the ONE exec/data PORT-LEGALITY table per node kind (NodeKinds-adjacent so the closed registry
    /// and its pin layout live side by side; the single source <see cref="GraphStructureGate"/> consumes at BOTH
    /// load gates). An edge endpoint whose port is not in its node's set is a located structural reject — never a
    /// silently-ignored wire. Fan-in policy: the trigger condition-in port is the ONLY data port that accepts
    /// multiple edges (multi-condition AND semantics — the FromFlat wiring); every other data port takes exactly
    /// one edge, and every exec-out port drives exactly one edge (forked exec chains reject).
    /// </summary>
    internal static class NodePorts
    {
        /// <summary>True when <paramref name="port"/> is a legal EXEC-OUT port of <paramref name="n"/>.</summary>
        public static bool IsExecOut(NodeBase n, int port) => n switch
        {
            TriggerNode                       => port == TriggerGraph.TriggerExecOutPort,
            EventNode                         => port == TriggerGraph.EventExecOutPort,
            ActionNode or EffectActionNode    => port == TriggerGraph.ActionExecOutPort,
            ForEachNode or ForEachBatchedNode => port == TriggerGraph.ActionExecOutPort
                                              || port == TriggerGraph.ForEachBodyOutPort,
            BranchNode                        => port == TriggerGraph.ActionExecOutPort
                                              || port == TriggerGraph.BranchThenOutPort
                                              || port == TriggerGraph.BranchElseOutPort,
            _                                 => false, // expression/condition nodes are data-side only
        };

        /// <summary>True when <paramref name="port"/> is a legal EXEC-IN port of <paramref name="n"/>.</summary>
        public static bool IsExecIn(NodeBase n, int port) => n switch
        {
            TriggerNode => port == TriggerGraph.TriggerEventInPort,
            ActionNode or EffectActionNode or ForEachNode or ForEachBatchedNode or BranchNode
                        => port == TriggerGraph.ActionExecInPort,
            _           => false, // events FIRE exec, never receive it; expr/condition nodes are data-side
        };

        /// <summary>True when <paramref name="port"/> is a legal DATA-OUT port of <paramref name="n"/>.</summary>
        public static bool IsDataOut(NodeBase n, int port) => n switch
        {
            ConditionNode => port == TriggerGraph.ConditionDataOutPort,
            ExprLiteralNode or ExprVarNode or ExprUnaryNode or ExprBinaryNode or ExprCallNode
                or ExprArrayGetNode or ExprArrayLenNode
                          => port == TriggerGraph.ExprDataOutPort,
            _             => false, // triggers/events/actions/containers emit no data
        };

        /// <summary>True when <paramref name="port"/> is a legal DATA-IN port of <paramref name="n"/>.</summary>
        public static bool IsDataIn(NodeBase n, int port) => n switch
        {
            TriggerNode                       => port == TriggerGraph.TriggerConditionInPort,
            ActionNode                        => port == TriggerGraph.ActionValueInPort
                                              || port == TriggerGraph.ActionIndexInPort,
            BranchNode                        => port == TriggerGraph.BranchCondInPort,
            ExprUnaryNode or ExprArrayGetNode => port == TriggerGraph.ExprOperandPort0,
            ExprBinaryNode or ExprCallNode    => port == TriggerGraph.ExprOperandPort0
                                              || port == TriggerGraph.ExprOperandPort1,
            _                                 => false, // events/run_effect/literals/vars/array-len take no data-in
        };

        /// <summary>True when (<paramref name="n"/>, <paramref name="port"/>) accepts MULTIPLE data edges — only
        /// the trigger condition-in port (conditions and condition-expression roots AND together).</summary>
        public static bool AllowsDataFanIn(NodeBase n, int port) =>
            n is TriggerNode && port == TriggerGraph.TriggerConditionInPort;
    }
}
