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
    /// The CLOSED <c>kind</c> discriminator registry: the closed ECA vocabulary (event/condition/action type
    /// strings) plus the two structural kinds "trigger" and "run_effect". A <c>kind</c> outside this union is
    /// rejected at parse by <see cref="NodeBaseJsonConverter"/> (fail-closed, AR-22). The three ECA sets are
    /// pairwise disjoint AND disjoint from {trigger, run_effect}, so a kind string maps to exactly one node type.
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

        public static readonly string[] EventTypes     = { "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold" };
        public static readonly string[] ConditionTypes = { "always", "building_exists", "resource_comparison", "unit_count", "variable_comparison", "unit_in_region" };
        public static readonly string[] ActionTypes    = { "spawn_unit", "display_message", "victory", "defeat", "create_timer", "add_resources", "set_variable", "play_sound" };

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
