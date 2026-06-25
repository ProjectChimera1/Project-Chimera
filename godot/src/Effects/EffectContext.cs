#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Navigation;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// The per-invocation environment an effect graph executes against. A <c>readonly struct</c> that holds the
    /// heavy state behind CLASS REFERENCES (<see cref="World"/>, <see cref="Rng"/>, <see cref="Spatial"/>,
    /// <see cref="DamageTable"/>, …) plus a few value fields. Because the heavy state sits behind references,
    /// copying the struct into a work-stack frame — or producing a re-targeted copy via <see cref="WithTarget"/>
    /// — is cheap AND correct: every copy shares the ONE <see cref="SimRng"/> stream, so a random draw advances
    /// the single shared generator everyone sees (copying RNG state by value would lose draw-advances and desync
    /// silently — the Story 1.5 trap). NEVER store sim state by value here.
    /// </summary>
    public readonly struct EffectContext
    {
        /// <summary>The world all leaves read/mutate (reference).</summary>
        public readonly EntityWorld World;

        /// <summary>The single shared deterministic RNG (== <see cref="World"/>.Rng; reference, never copied by value).</summary>
        public readonly SimRng Rng;

        /// <summary>The rebuilt spatial hash <see cref="SearchAreaEffect"/> queries (reference; null disables area fan-out).</summary>
        public readonly SpatialHash? Spatial;

        /// <summary>The entity that owns/casts this effect (for Self/Ally filtering and damage attribution).</summary>
        public readonly int CasterId;

        /// <summary>The entity the current node acts on (the leaf's target; the SearchArea center).</summary>
        public readonly int PrimaryTargetId;

        /// <summary>The caster's faction (the killer for <see cref="DamageEffect"/>, the allegiance anchor for filters).</summary>
        public readonly Faction CasterFaction;

        /// <summary>The damage matrix the <see cref="DamageEffect"/> leaf resolves through.</summary>
        public readonly DamageTable DamageTable;

        /// <summary>Optional combat-feedback sink (UnitKilled events) threaded to <c>DamageResolver</c>.</summary>
        public readonly CombatEventQueue? Events;

        /// <summary>Optional scoreboard sink (kills/losses) threaded to <c>DamageResolver</c>.</summary>
        public readonly MatchStats? Stats;

        /// <summary>
        /// Build a root context. <see cref="Rng"/> is taken from <paramref name="world"/> (the one shared stream);
        /// callers never pass a separate generator. <paramref name="primaryTargetId"/> defaults to the caster for
        /// self-targeted graphs.
        /// </summary>
        public EffectContext(EntityWorld world, int casterId, int primaryTargetId, Faction casterFaction,
                             DamageTable damageTable, SpatialHash? spatial = null,
                             CombatEventQueue? events = null, MatchStats? stats = null)
            : this(world, world.Rng, spatial, casterId, primaryTargetId, casterFaction, damageTable, events, stats)
        {
        }

        // All-field private ctor used by WithTarget (re-targets without re-reading world.Rng).
        private EffectContext(EntityWorld world, SimRng rng, SpatialHash? spatial, int casterId,
                              int primaryTargetId, Faction casterFaction, DamageTable damageTable,
                              CombatEventQueue? events, MatchStats? stats)
        {
            World = world;
            Rng = rng;
            Spatial = spatial;
            CasterId = casterId;
            PrimaryTargetId = primaryTargetId;
            CasterFaction = casterFaction;
            DamageTable = damageTable;
            Events = events;
            Stats = stats;
        }

        /// <summary>
        /// A copy of this context re-pointed at <paramref name="targetId"/> as the primary target. Used by the
        /// executor to fan a SearchArea child out per matched entity. Cheap (struct copy of references) and shares
        /// the same RNG stream.
        /// </summary>
        public EffectContext WithTarget(int targetId) =>
            new EffectContext(World, Rng, Spatial, CasterId, targetId, CasterFaction, DamageTable, Events, Stats);
    }
}
