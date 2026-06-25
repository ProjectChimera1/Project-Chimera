#nullable enable
namespace ProjectChimera.Effects
{
    /// <summary>
    /// The root of the ONE closed, typed effect vocabulary (AR-8). Every ability, the trigger DSL (Epic 7, a
    /// superset that embeds these same nodes), and AI-balance compile down to a graph of these nodes and run
    /// through the single <see cref="EffectExecutor"/>. There is no second effect system and no scripting
    /// escape hatch — ever (no Lua/JASS/RunScript/customParams/delegate payloads).
    ///
    /// CLOSEDNESS CONTRACT (enforced by EffectVocabularyTests):
    ///   • The constructor is <c>private protected</c>, so NO type outside this assembly can derive a new node.
    ///   • Every concrete node is <c>sealed</c> (no virtual/open extension reachable by data or creators).
    ///   • There are exactly three composition nodes (<see cref="CompositionEffect"/> subtypes:
    ///     Sequence / SearchArea / Persistent); everything else is a sealed <see cref="LeafEffect"/>.
    ///   • No node carries a Delegate/Func/Action/dynamic/object/free-text-code field.
    ///
    /// "Closed" forbids an OPEN extension point reachable by content; it does NOT forbid the engine adding
    /// another <c>sealed</c> leaf in a future story (FireProjectile, SpawnUnit, Victory, …). The set stays
    /// closed to creators and sealed in code.
    ///
    /// Pure simulation: no Godot, no float/double, no System.Random, no wall-clock — Fixed (16.16) only.
    /// </summary>
    public abstract class EffectNode
    {
        // private protected: only types in THIS assembly may derive. External assemblies (UGC, mods, the
        // presentation layer) cannot subclass an EffectNode — the structural guarantee behind AC1's
        // "no open/virtual extension point and no scripting hook."
        private protected EffectNode() { }
    }

    /// <summary>
    /// A terminal effect that mutates world state when reached. Leaves carry the actual mutation via
    /// <see cref="Apply"/>; composition nodes never reach <see cref="Apply"/> (the executor dispatches them by
    /// type). <see cref="Apply"/> is <c>internal</c> so creators/other systems BUILD nodes but never invoke the
    /// mutation directly — only the executor (same assembly) does, after its IsAlive / id-bounds guards.
    /// </summary>
    public abstract class LeafEffect : EffectNode
    {
        private protected LeafEffect() { }

        /// <summary>
        /// Apply this leaf's mutation to <c>ctx.PrimaryTargetId</c>. Implementations MUST guard
        /// <see cref="ProjectChimera.Core.EntityWorld.IsAlive"/> (and any faction/self rule) at entry — a future
        /// caller (DoT/AoE/ability invoked from many sites) will hit dead/recycled target ids.
        /// </summary>
        internal abstract void Apply(in EffectContext ctx);
    }

    /// <summary>
    /// A node that composes other nodes rather than mutating state itself. There are EXACTLY three concrete
    /// composition types (Sequence / SearchArea / Persistent) — AC1's "exactly three composition nodes." The
    /// executor dispatches each by concrete type; composition nodes have no <c>Apply</c>.
    /// </summary>
    public abstract class CompositionEffect : EffectNode
    {
        private protected CompositionEffect() { }
    }
}
