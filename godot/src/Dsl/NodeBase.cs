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
    /// The CLOSED <c>kind</c> discriminator registry: the closed ECA vocabulary (event/condition/action type
    /// strings) plus the two structural kinds "trigger" and "run_effect", plus (Story 7.4) the five expression
    /// kinds. A <c>kind</c> outside this union is
    /// rejected at parse by <see cref="NodeBaseJsonConverter"/> (fail-closed, AR-22). The three ECA sets are
    /// pairwise disjoint AND disjoint from {trigger, run_effect} and the expr_* kinds, so a kind string maps to
    /// exactly one node type. The expression op/fn sets are FIELD-value vocabularies (not kinds) — also closed,
    /// membership-checked at parse so an unknown op/fn never constructs.
    ///
    /// NOTE: these arrays are DUPLICATED string-for-string from <c>ScenarioValidator</c>'s
    /// <c>_triggerEventTypes</c>/<c>_conditionTypes</c>/<c>_actionTypes</c> — those are <c>private</c> and cannot be
    /// shared, so this is a hand-kept copy, NOT an automatic mirror. When the trigger vocabulary is extended (e.g.
    /// Story 7.13), BOTH lists must be updated together; there is no cross-check guard yet. Story 7.7 (the
    /// authoritative load-time graph validator) is the sanctioned place to unify these into one source of truth.
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

        public static readonly string[] EventTypes     = { "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold" };
        public static readonly string[] ConditionTypes = { "always", "building_exists", "resource_comparison", "unit_count", "variable_comparison", "unit_in_region" };
        public static readonly string[] ActionTypes    = { "spawn_unit", "display_message", "victory", "defeat", "create_timer", "add_resources", "set_variable", "play_sound" };

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
    }
}
