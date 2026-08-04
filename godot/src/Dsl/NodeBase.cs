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

    /// <summary>An event that can fire a trigger. <see cref="Kind"/> ∈ the closed GRAPH event-type set
    /// (<see cref="NodeKinds.EventTypes"/> ∪ {<see cref="NodeKinds.CustomEvent"/>} — Story 7.5). Carries the
    /// <see cref="TriggerEvent"/> field set 1:1 for the flat kinds; a <c>custom_event</c> subscription uses only
    /// <see cref="EventName"/> (graph-channel-only — <see cref="TriggerGraph.ToFlat"/> fails closed on it).</summary>
    public sealed class EventNode : NodeBase
    {
        /// <summary>Discriminator ∈ <see cref="NodeKinds.EventTypes"/> (e.g. "unit_dies") or
        /// <see cref="NodeKinds.CustomEvent"/> (Story 7.5, graph-channel-only).</summary>
        public string Kind { get; set; } = "";

        public int Faction { get; set; } = 0;
        public string? BuildingType { get; set; }
        public string? TimerName { get; set; }
        public Fixed Amount { get; set; } = Fixed.Zero;
        public int Count { get; set; } = 0;
        public string Operator { get; set; } = ">=";

        /// <summary>Story 7.5 — the declared custom-event NAME this node subscribes to (JSON <c>event_name</c>).
        /// Used ONLY by kind <see cref="NodeKinds.CustomEvent"/> (required there, rejected elsewhere by the
        /// converter's per-kind allow-lists). Membership in the scenario's closed <c>custom_events</c> registry is
        /// enforced at the validator gate AND the LoadScenario backstop.</summary>
        public string? EventName { get; set; }
    }

    /// <summary>
    /// Story 7.5 — the <c>raise_event</c> action node (graph-channel-only; <see cref="TriggerGraph.ToFlat"/> fails
    /// closed on it). Raises the declared custom event <see cref="Name"/>: same-tick into the FIFO drain by
    /// default, or cross-tick through the checksummed <c>DslEventQueue</c> when <see cref="NextTick"/> is true.
    /// Its typed arguments are expression DATA edges into ports <see cref="TriggerGraph.RaiseArgInPort0"/>..3
    /// (exactly one per declared param, type/wire-checked at load). <see cref="Raiser"/> is the authored raiser
    /// slot: −1 = system (the default), else it must be ∈ the event's <c>allowed_raisers</c> (load-time membership
    /// only in 7.5 — runtime raiser enforcement on the lockstep bus is Story 7.9).
    /// </summary>
    public sealed class RaiseEventNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.RaiseEvent;

        /// <summary>The declared custom-event name to raise (registry membership enforced at load).</summary>
        public string Name { get; set; } = "";

        /// <summary>The authored raiser slot: −1 = system (default) or a slot ∈ the event's allowed_raisers.</summary>
        public int Raiser { get; set; } = -1;

        /// <summary>False (default) = same-tick raise into the FIFO drain; true = enqueue into the next-tick
        /// <c>DslEventQueue</c> (the sanctioned A→B→A feedback channel — same-tick cycles are rejected at load).</summary>
        public bool NextTick { get; set; } = false;
    }

    /// <summary>
    /// Story 7.5 — an event-parameter READ leaf (text surface <c>event.&lt;name&gt;</c>). Compiles only for a
    /// trigger subscribed to exactly ONE event kind declaring <see cref="Name"/> — a declared custom event's typed
    /// param, or the built-in <c>unit_dies</c> payload map (victim / killer / killer_faction). Ref-typed payload
    /// params (EntityRef/FactionRef) read as opaque Int raw handles — the one sanctioned ref→Int surface.
    /// </summary>
    public sealed class ExprEventParamNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprEventParam;

        /// <summary>The declared event-parameter name to read.</summary>
        public string Name { get; set; } = "";
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
    /// ({count, distance, min, max, abs} plus the Story 7.13 state reads); arguments wire into ports 0..arity-1.</summary>
    public sealed class ExprCallNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ExprCall;

        /// <summary>Built-in name ∈ <see cref="NodeKinds.ExprCallFns"/> (membership enforced at parse).</summary>
        public string Fn { get; set; } = "";

        /// <summary>
        /// Story 7.13 — the OPTIONAL closed-vocabulary SELECTOR of a state-read built-in: the <c>tag</c> of
        /// <c>unit_count_tag</c>, the <c>category</c> of <c>unit_count_category</c>, the <c>resource</c> of
        /// <c>player_resource</c>, or the <c>region</c> of <c>region_unit_count</c>. It is a STATIC string-enum
        /// field (never a data operand edge) resolved to its int id/index at COMPILE (tag/category/resource) or at
        /// runtime via <c>RegionStore</c> (region) — no string ever enters the tick. Empty on every other built-in
        /// (a stray selector on a non-selector fn is a located compile reject); omit-when-empty at serialize.
        /// </summary>
        public string Selector { get; set; } = "";
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

    // ── Story 7.13 — the action-LEAF nodes (dedicated classes, graph-channel-only, chaining like an action). They
    //    carry STATIC typed fields (no data operand edges), so they need no compiled expression programs. order_units
    //    runs sim-side (reuses OrderApplier); move_camera / cinematic_mode / play_vfx are PRESENTATION-ONLY (director
    //    delegates, never folded into SimChecksum). All four fail closed in TriggerGraph.ToFlat (no flat form). ──

    /// <summary>
    /// Story 7.13 — the sim-side <c>order_units</c> action: issue <see cref="Command"/> to every alive unit matching
    /// the ascending-id selection (<see cref="Faction"/> −1 = any; optional <see cref="RegionId"/> point-in-region),
    /// via <c>OrderApplier.ApplyActiveOrder</c> — reusing the existing <c>UnitCommand</c> semantics (folds through
    /// the existing entity/order state, no new checksum fold). <see cref="X"/>/<see cref="Z"/> are the target point
    /// (Move/AttackMove); ignored for Stop/HoldPosition. An empty selection is a deterministic no-op.
    /// </summary>
    public sealed class OrderUnitsNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.OrderUnits;

        /// <summary>Order command ∈ <see cref="NodeKinds.OrderCommands"/> (move/attack_move/stop/hold_position;
        /// membership enforced at parse).</summary>
        public string Command { get; set; } = "";

        /// <summary>Selection faction filter (0-based slot); −1 = any faction.</summary>
        public int Faction { get; set; } = -1;

        /// <summary>Optional selection region id (null = no region filter); resolved at runtime via RegionStore.</summary>
        public string? RegionId { get; set; }

        /// <summary>Target point X (16.16 Fixed) for Move/AttackMove; ignored for Stop/HoldPosition.</summary>
        public Fixed X { get; set; } = Fixed.Zero;

        /// <summary>Target point Z (16.16 Fixed) for Move/AttackMove; ignored for Stop/HoldPosition.</summary>
        public Fixed Z { get; set; } = Fixed.Zero;
    }

    /// <summary>Story 7.13 — the PRESENTATION-ONLY <c>move_camera</c> action: pan the camera to the named
    /// <c>ScenarioCamera</c> (<see cref="CameraName"/>) via a director presentation delegate. Never folded into
    /// <c>SimChecksum</c>; an unknown camera name is a located reject at load.</summary>
    public sealed class MoveCameraNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.MoveCamera;

        /// <summary>The declared <c>ScenarioCamera</c> name to pan to (validated at load).</summary>
        public string CameraName { get; set; } = "";
    }

    /// <summary>Story 7.13 — the PRESENTATION-ONLY <c>cinematic_mode</c> action: toggle the cinematic letterbox/UI
    /// via a director presentation delegate. Never folded into <c>SimChecksum</c>.</summary>
    public sealed class CinematicModeNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.CinematicMode;

        /// <summary>True = enter cinematic mode; false = exit.</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>Story 7.13 — the PRESENTATION-ONLY <c>play_vfx</c> action: request a one-shot VFX (<see cref="VfxId"/>)
    /// at point (<see cref="X"/>,<see cref="Z"/>) via a director presentation delegate. Never folded into
    /// <c>SimChecksum</c>.</summary>
    public sealed class PlayVfxNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.PlayVfx;

        /// <summary>The VFX id to play (presentation-resolved; unknown ids are a silent presentation no-op).</summary>
        public string VfxId { get; set; } = "";

        /// <summary>VFX position X (16.16 Fixed).</summary>
        public Fixed X { get; set; } = Fixed.Zero;

        /// <summary>VFX position Z (16.16 Fixed).</summary>
        public Fixed Z { get; set; } = Fixed.Zero;
    }

    /// <summary>
    /// Story 7.13 — the <c>random_choice</c> WEIGHTED exec container (graph-channel-only; <see cref="TriggerGraph.ToFlat"/>
    /// fails closed on it). On fire it evaluates its weighted exec-out branches in ASCENDING port-index order, sums the
    /// integer <see cref="Weights"/>, draws <c>world.Rng.NextInt(totalWeight)</c> (the SINGLE shared <see cref="Fixed"/>-free
    /// <c>SimRng</c> stream, folded LAST in <c>SimChecksum</c> — no second stream, no reorder), and selects the branch by
    /// subtracting down the pre-sorted weight array. Branch k hangs off exec-out port
    /// <c>TriggerGraph.RandomChoiceBranchOutPort0 + k</c> (k = 0..<see cref="Weights"/>.Length−1); port 0 is the
    /// continuation (runs after the taken branch, like <see cref="BranchNode"/>). A zero-total-weight or empty
    /// (<see cref="Weights"/>.Length == 0) <c>random_choice</c> rejects at LOAD.
    /// </summary>
    public sealed class RandomChoiceNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.RandomChoice;

        /// <summary>The per-branch integer weights, parallel to exec-out ports
        /// <c>RandomChoiceBranchOutPort0 + k</c>. Each ≥ 0, at least one branch, total &gt; 0 (enforced at load).</summary>
        public int[] Weights { get; set; } = System.Array.Empty<int>();
    }

    // ── Story 7.13 — the three trigger-control action leaves (dedicated graph-only classes; ToFlat fails closed on
    //    each, like raise_event). enable_trigger/disable_trigger flip the target trigger's folded runtime enabled
    //    flag; run_trigger synchronously runs the target's chain, depth-capped. All reference a target by its
    //    persistent TRIGGER-NODE id (a graph concept — hence no flat form). Self/mutual run cycles reject at load. ──

    /// <summary>Story 7.13 — the <c>enable_trigger</c> action: set the target trigger's runtime enabled flag TRUE
    /// (folded into <c>SimChecksum</c> v21). <see cref="TargetTriggerId"/> is a persistent <see cref="TriggerNode"/>
    /// id; an unresolved target rejects at load.</summary>
    public sealed class EnableTriggerNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.EnableTrigger;

        /// <summary>The persistent node id of the target <see cref="TriggerNode"/> (validated at load).</summary>
        public int TargetTriggerId { get; set; } = -1;
    }

    /// <summary>Story 7.13 — the <c>disable_trigger</c> action: set the target trigger's runtime enabled flag FALSE
    /// (folded into <c>SimChecksum</c> v21). <see cref="TargetTriggerId"/> is a persistent <see cref="TriggerNode"/>
    /// id; an unresolved target rejects at load.</summary>
    public sealed class DisableTriggerNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.DisableTrigger;

        /// <summary>The persistent node id of the target <see cref="TriggerNode"/> (validated at load).</summary>
        public int TargetTriggerId { get; set; } = -1;
    }

    /// <summary>Story 7.13 — the <c>run_trigger</c> action: synchronously execute the target trigger's action chain in
    /// place, bounded by <c>EventBounds.MaxRunTriggerDepth</c> (a seatbelt halting at the whole-trigger boundary, never
    /// mid-Sequence). <see cref="TargetTriggerId"/> is a persistent <see cref="TriggerNode"/> id; an unresolved target
    /// and any self/mutual run cycle reject at load.</summary>
    public sealed class RunTriggerNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.RunTrigger;

        /// <summary>The persistent node id of the target <see cref="TriggerNode"/> (validated at load, cycle-checked).</summary>
        public int TargetTriggerId { get; set; } = -1;
    }

    // ── Story 7.14 — the three objective action-leaf nodes (dedicated graph-only classes; ToFlat fails closed on
    //    each, like enable_trigger). They flip an authored objective's state by writing the reserved Global Int DSL
    //    variable backing that objective (via _vars.SetInt at ExecuteItem) — so they carry NO new folded store and
    //    force NO SimChecksum bump (the value rides the existing v16 DslVarTable fold). The objective is referenced by
    //    its STRING id (a scenario-data concept, not a node id → no id-offset remap on merge). Titles/descriptions are
    //    strings and NEVER enter the tick; only the int ordinal folds. An unknown objective id rejects at load. ──

    /// <summary>Story 7.14 — the <c>show_objective</c> action: reveal an objective (Hidden→Active) by writing its
    /// reserved var to <see cref="ObjectiveState.Active"/>. <see cref="ObjectiveId"/> references an authored objective
    /// by id; an unresolved id rejects at load.</summary>
    public sealed class ShowObjectiveNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.ShowObjective;

        /// <summary>The authored objective id whose state this leaf flips (validated at load).</summary>
        public string ObjectiveId { get; set; } = "";
    }

    /// <summary>Story 7.14 — the <c>complete_objective</c> action: mark an objective Complete by writing its reserved
    /// var to <see cref="ObjectiveState.Complete"/>. <see cref="ObjectiveId"/> references an authored objective by id;
    /// an unresolved id rejects at load.</summary>
    public sealed class CompleteObjectiveNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.CompleteObjective;

        /// <summary>The authored objective id whose state this leaf flips (validated at load).</summary>
        public string ObjectiveId { get; set; } = "";
    }

    /// <summary>Story 7.14 — the <c>fail_objective</c> action: mark an objective Failed by writing its reserved var to
    /// <see cref="ObjectiveState.Failed"/>. <see cref="ObjectiveId"/> references an authored objective by id; an
    /// unresolved id rejects at load.</summary>
    public sealed class FailObjectiveNode : NodeBase
    {
        /// <summary>The closed-registry discriminator this node serializes under.</summary>
        public string Kind => NodeKinds.FailObjective;

        /// <summary>The authored objective id whose state this leaf flips (validated at load).</summary>
        public string ObjectiveId { get; set; } = "";
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
    ///
    /// SANCTIONED graph⊃flat DIVERGENCE (Story 7.5): the GRAPH event vocabulary is <see cref="GraphEventTypes"/>
    /// (= <see cref="EventTypes"/> ∪ {<see cref="CustomEvent"/>} — the ONLY graph⊃flat event divergence), and the
    /// graph channel additionally admits the <see cref="RaiseEvent"/>/<see cref="ExprEventParam"/> kinds. The flat
    /// <c>TriggerDefinition</c> POCOs, their JSON schema, and the aliased flat vocab sets stay FROZEN, and
    /// <see cref="TriggerGraph.ToFlat"/> fails closed (located throw) on every 7.5 kind rather than lowering lossily.
    /// </summary>
    internal static class NodeKinds
    {
        public const string Trigger   = "trigger";
        public const string RunEffect = "run_effect";

        // ── Story 7.5 — the graph-channel-only custom-event kinds (never in the flat vocab; ToFlat fails closed) ──
        /// <summary>The custom-event SUBSCRIPTION kind on <see cref="EventNode"/> (graph event set = EventTypes ∪ this).</summary>
        public const string CustomEvent    = "custom_event";
        /// <summary>The <see cref="RaiseEventNode"/> action kind.</summary>
        public const string RaiseEvent     = "raise_event";
        /// <summary>The <see cref="ExprEventParamNode"/> expression kind (text surface <c>event.&lt;name&gt;</c>).</summary>
        public const string ExprEventParam = "expr_event_param";

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

        // ── Story 7.13 — the four graph-channel-only action-leaf kinds (dedicated node classes; ToFlat fails
        //    closed on each, like raise_event). order_units is sim-side; the other three are presentation-only. ──
        public const string OrderUnits    = "order_units";
        public const string MoveCamera    = "move_camera";
        public const string CinematicMode = "cinematic_mode";
        public const string PlayVfx       = "play_vfx";

        // ── Story 7.13 — the weighted exec container + the three trigger-control action leaves (dedicated node
        //    classes; ToFlat fails closed on each). ──
        public const string RandomChoice   = "random_choice";
        public const string EnableTrigger  = "enable_trigger";
        public const string DisableTrigger = "disable_trigger";
        public const string RunTrigger     = "run_trigger";

        // ── Story 7.14 — the three objective action-leaf kinds (dedicated node classes; ToFlat fails closed on each,
        //    like enable_trigger). They write the reserved Global Int DSL var backing an authored objective — no new
        //    folded store, no SimChecksum bump. Graph-channel-only (not in ActionTypes/FlatActionTypes). ──
        public const string ShowObjective     = "show_objective";
        public const string CompleteObjective = "complete_objective";
        public const string FailObjective     = "fail_objective";

        // Story 7.13 — five NEW built-in event sources APPEND to the flat + graph event vocabularies. Four are
        // raised deterministically by the sim (unit_damaged/unit_trained/ability_cast/hero_level) at their
        // tick-boundary sites; player_chat is externally-driven (registered here; its raise wire is a later commit).
        // Appending to EventTypes is checksum-neutral: no existing scenario subscribes, so CanonicalModelHash moves
        // only by its version bump. Their typed param schemas live in EventDispatchPlan.BuiltinEventParams.
        public static readonly string[] EventTypes     =
        {
            "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold",
            "unit_damaged", "unit_trained", "ability_cast", "hero_level", "player_chat",
        };
        /// <summary>Story 7.5 — the GRAPH event vocabulary: <see cref="EventTypes"/> ∪ {<see cref="CustomEvent"/>}
        /// (the sanctioned graph⊃flat divergence — the flat sets stay frozen; ToFlat fails closed on custom_event).</summary>
        public static readonly string[] GraphEventTypes =
        {
            "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold",
            "unit_damaged", "unit_trained", "ability_cast", "hero_level", "player_chat",
            CustomEvent,
        };
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
        // Story 7.13 — the state-read built-ins APPEND to the closed fn vocabulary (checksum-neutral: a graph using
        // none of them folds byte-identically; MixGraphNode already folds Fn). entity_hp/owner/position take an
        // entity-Int operand; unit_count_tag/category/player_resource/region_unit_count carry a static Selector.
        public static readonly string[] ExprCallFns   =
        {
            "count", "distance", "min", "max", "abs",
            "entity_hp", "entity_owner", "entity_position",
            "unit_count_tag", "unit_count_category", "player_resource", "region_unit_count",
        };

        // ── Story 7.13 — the closed order-command vocabulary (a static field value on order_units, not a kind). ──
        public static readonly string[] OrderCommands = { "move", "attack_move", "stop", "hold_position" };

        /// <summary>Story 7.13 — true for a state-read built-in that carries a static <c>Selector</c> (never an
        /// operand edge). The other reads (entity_hp/entity_owner/entity_position) and the 7.4 fns take none.</summary>
        public static bool FnUsesSelector(string? fn) =>
            fn == "unit_count_tag" || fn == "unit_count_category"
            || fn == "player_resource" || fn == "region_unit_count";

        /// <summary>Story 7.13 — resolve a <c>unit_count_tag</c> selector to its <see cref="UnitTag"/> bit (int).</summary>
        public static bool TryResolveTagSelector(string? s, out int bit)
        {
            switch (s)
            {
                case "organic":    bit = (int)UnitTag.Organic;    return true;
                case "mechanical": bit = (int)UnitTag.Mechanical; return true;
                case "magical":    bit = (int)UnitTag.Magical;    return true;
                default:           bit = 0;                       return false;
            }
        }

        /// <summary>Story 7.13 — resolve a <c>unit_count_category</c> selector to its <see cref="UnitCategory"/> int.</summary>
        public static bool TryResolveCategorySelector(string? s, out int category)
        {
            switch (s)
            {
                case "worker":    category = (int)UnitCategory.Worker;    return true;
                case "melee":     category = (int)UnitCategory.Melee;     return true;
                case "ranged":    category = (int)UnitCategory.Ranged;    return true;
                case "siege":     category = (int)UnitCategory.Siege;     return true;
                case "air":       category = (int)UnitCategory.Air;       return true;
                case "structure": category = (int)UnitCategory.Structure; return true;
                default:          category = -1;                          return false;
            }
        }

        /// <summary>Story 7.13 — resolve a <c>player_resource</c> selector to its resource-kind index (0=ore, 1=crystal).</summary>
        public static bool TryResolveResourceSelector(string? s, out int kind)
        {
            switch (s)
            {
                case "ore":     kind = 0; return true;
                case "crystal": kind = 1; return true;
                default:        kind = -1; return false;
            }
        }

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
            RaiseEventNode      => RaiseEvent,
            ExprEventParamNode  => ExprEventParam,
            OrderUnitsNode      => OrderUnits,
            MoveCameraNode      => MoveCamera,
            CinematicModeNode   => CinematicMode,
            PlayVfxNode         => PlayVfx,
            RandomChoiceNode    => RandomChoice,
            EnableTriggerNode   => EnableTrigger,
            DisableTriggerNode  => DisableTrigger,
            RunTriggerNode      => RunTrigger,
            ShowObjectiveNode     => ShowObjective,
            CompleteObjectiveNode => CompleteObjective,
            FailObjectiveNode     => FailObjective,
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
            // Story 7.5: raise_event chains like an action (single exec-out continuation).
            // Story 7.13: the four action-leaf nodes chain identically (single exec-out continuation).
            // Story 7.13: enable/disable/run_trigger chain like an action (single exec-out continuation).
            ActionNode or EffectActionNode or RaiseEventNode
                or OrderUnitsNode or MoveCameraNode or CinematicModeNode or PlayVfxNode
                or EnableTriggerNode or DisableTriggerNode or RunTriggerNode
                // Story 7.14: the three objective action-leaf nodes chain like an action (single exec-out continuation).
                or ShowObjectiveNode or CompleteObjectiveNode or FailObjectiveNode
                                              => port == TriggerGraph.ActionExecOutPort,
            ForEachNode or ForEachBatchedNode => port == TriggerGraph.ActionExecOutPort
                                              || port == TriggerGraph.ForEachBodyOutPort,
            BranchNode                        => port == TriggerGraph.ActionExecOutPort
                                              || port == TriggerGraph.BranchThenOutPort
                                              || port == TriggerGraph.BranchElseOutPort,
            // Story 7.13: random_choice — port 0 continuation, plus one branch port per weight
            // (RandomChoiceBranchOutPort0 .. +Weights.Length−1).
            RandomChoiceNode rc               => port == TriggerGraph.ActionExecOutPort
                                              || (port >= TriggerGraph.RandomChoiceBranchOutPort0
                                                  && port < TriggerGraph.RandomChoiceBranchOutPort0 + rc.Weights.Length),
            _                                 => false, // expression/condition nodes are data-side only
        };

        /// <summary>True when <paramref name="port"/> is a legal EXEC-IN port of <paramref name="n"/>.</summary>
        public static bool IsExecIn(NodeBase n, int port) => n switch
        {
            TriggerNode => port == TriggerGraph.TriggerEventInPort,
            ActionNode or EffectActionNode or ForEachNode or ForEachBatchedNode or BranchNode or RaiseEventNode
                or OrderUnitsNode or MoveCameraNode or CinematicModeNode or PlayVfxNode
                or RandomChoiceNode or EnableTriggerNode or DisableTriggerNode or RunTriggerNode
                // Story 7.14: the three objective action-leaf nodes receive exec like an action.
                or ShowObjectiveNode or CompleteObjectiveNode or FailObjectiveNode
                        => port == TriggerGraph.ActionExecInPort,
            _           => false, // events FIRE exec, never receive it; expr/condition nodes are data-side
        };

        /// <summary>True when <paramref name="port"/> is a legal DATA-OUT port of <paramref name="n"/>.</summary>
        public static bool IsDataOut(NodeBase n, int port) => n switch
        {
            ConditionNode => port == TriggerGraph.ConditionDataOutPort,
            ExprLiteralNode or ExprVarNode or ExprUnaryNode or ExprBinaryNode or ExprCallNode
                or ExprArrayGetNode or ExprArrayLenNode or ExprEventParamNode
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
            // Story 7.5: raise-arg data ports 0..3 (one edge per declared param; arity/type checked by the plan).
            RaiseEventNode                    => port >= TriggerGraph.RaiseArgInPort0
                                              && port <= TriggerGraph.RaiseArgInPort3,
            ExprUnaryNode or ExprArrayGetNode => port == TriggerGraph.ExprOperandPort0,
            ExprBinaryNode or ExprCallNode    => port == TriggerGraph.ExprOperandPort0
                                              || port == TriggerGraph.ExprOperandPort1,
            _                                 => false, // events/run_effect/literals/vars/array-len/event-param take no data-in
        };

        /// <summary>True when (<paramref name="n"/>, <paramref name="port"/>) accepts MULTIPLE data edges — only
        /// the trigger condition-in port (conditions and condition-expression roots AND together).</summary>
        public static bool AllowsDataFanIn(NodeBase n, int port) =>
            n is TriggerNode && port == TriggerGraph.TriggerConditionInPort;

        /// <summary>DW-358 — true when (<paramref name="n"/>, <paramref name="port"/>) accepts MULTIPLE exec
        /// edges — only the trigger event-in port (each subscribed EventNode fires the trigger independently;
        /// <c>FromFlat</c> emits one edge per event). Every other exec-in takes exactly ONE incoming edge: a
        /// second would leave the node executing under multiple owners (cross-trigger convergence escapes the
        /// per-trigger cycle guard, so the structural gate rejects it here).</summary>
        public static bool AllowsExecFanIn(NodeBase n, int port) =>
            n is TriggerNode && port == TriggerGraph.TriggerEventInPort;
    }
}
