#nullable enable
using ProjectChimera.Core; // Fixed

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.3 — the CLOSED typed-value set for scoped DSL variables. Pure sim-layer C# (Godot-free, float-free):
    /// fractional numerics are <see cref="Fixed"/> 16.16 stored as <c>Fixed.Raw</c>, quantized only at the JSON
    /// boundary. The set is fixed; a value outside it cannot be declared (the enum is serialized by NAME only via
    /// the registered <c>JsonStringEnumConverter</c>, so an unknown name fails closed at parse).
    ///
    /// 7.3 makes every type declarable / storable / round-trippable / foldable, but the ECA leaf only READS/WRITES
    /// <see cref="Int"/>-typed variables (and <see cref="TimerRef"/> timers). The full typed read/write through
    /// operators is the 7.4 expression layer; <see cref="Array"/> population + loop counters are 7.6.
    /// </summary>
    public enum DslValueType
    {
        /// <summary>A signed 32-bit integer (the only type the 7.3 ECA leaf reads/writes).</summary>
        Int,
        /// <summary>A 16.16 fixed-point number, stored as its <c>Fixed.Raw</c> int.</summary>
        Fixed,
        /// <summary>A boolean, stored as 0/1.</summary>
        Bool,
        /// <summary>An entity id/index int.</summary>
        EntityRef,
        /// <summary>A faction slot int.</summary>
        FactionRef,
        /// <summary>A 2D point: two <c>Fixed.Raw</c> ints (X then Z).</summary>
        Point,
        /// <summary>A timer handle int.</summary>
        TimerRef,
        /// <summary>An array of a scalar element type (a declarable slot only in 7.3 — no population).</summary>
        Array,
    }

    /// <summary>
    /// Story 7.3 — the CLOSED scope set for DSL variables. <see cref="Global"/> and <see cref="PerPlayer"/> persist
    /// across ticks and fold into <c>SimChecksum</c>; <see cref="TriggerLocal"/> is lexically scoped scratch,
    /// allocated when a trigger begins executing its actions and freed at trigger end — never engine-global, never
    /// persisted, never folded.
    /// </summary>
    public enum VarScope
    {
        /// <summary>One shared slot, visible to every trigger.</summary>
        Global,
        /// <summary>One slot per player (0..7), selected by the ECA leaf's Faction field.</summary>
        PerPlayer,
        /// <summary>Per-trigger-firing scratch (allocated on trigger enter, freed on trigger exit).</summary>
        TriggerLocal,
    }

    /// <summary>
    /// Story 7.3 — a Dsl-local variable declaration handed to <see cref="DslVarTable.InitFromDeclarations"/>. The
    /// Core-side <c>ScenarioVariable</c> POCO is translated into this at the load boundary so the table stays
    /// Godot-free and does not reference <c>Core.Definitions</c>. <paramref name="Raw0"/>/<paramref name="Raw1"/> are
    /// the initial value's raw ints (Raw1 is the Point Z component; 0 for scalars).
    /// </summary>
    public readonly struct DslVarDecl
    {
        public readonly string Name;
        public readonly DslValueType Type;
        public readonly VarScope Scope;
        public readonly int Raw0;
        public readonly int Raw1;

        /// <summary>Story 7.6 — the element type of an <see cref="DslValueType.Array"/> declaration (scalar
        /// Int/Fixed/Bool only, gated at load). Defaulted (<see cref="DslValueType.Int"/>) and inert for scalars.</summary>
        public readonly DslValueType ElementType;

        /// <summary>Story 7.6 — the preallocated capacity of an <see cref="DslValueType.Array"/> declaration
        /// (1..<see cref="DslBounds.MaxArrayCapacity"/>, gated at load). 0 (the default) for scalars.</summary>
        public readonly int Capacity;

        public DslVarDecl(string name, DslValueType type, VarScope scope, int raw0, int raw1 = 0,
            DslValueType elementType = DslValueType.Int, int capacity = 0)
        {
            Name = name; Type = type; Scope = scope; Raw0 = raw0; Raw1 = raw1;
            ElementType = elementType; Capacity = capacity;
        }
    }

    /// <summary>
    /// Story 7.3 — a Dsl-local timer declaration (name + remaining ticks). The seconds→ticks conversion happens
    /// once at the Core boundary (<c>ScenarioDirector.SecondsToTicks</c>, which owns <c>TICKS_PER_SECOND</c>), so
    /// the table receives integer ticks only (no float→int truncation, no <c>SimulationLoop</c> dependency).
    /// </summary>
    public readonly struct DslTimerDecl
    {
        public readonly string Name;
        public readonly int Ticks;

        public DslTimerDecl(string name, int ticks) { Name = name; Ticks = ticks; }
    }
}
