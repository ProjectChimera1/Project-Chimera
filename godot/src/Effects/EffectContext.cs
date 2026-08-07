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

        /// <summary>
        /// Story 15.11 (DW-280): the GROUND POINT a <see cref="ProjectChimera.Core.Definitions.AbilityTargeting.GroundPoint"/>
        /// cast resolves at. Meaningful ONLY when <see cref="HasTargetPoint"/> is true — a <see cref="SearchAreaEffect"/>
        /// centers its impact here instead of on the (absent) primary target's position. A transient value carried by the
        /// context copy, never sim state — it is NOT read on a Self/TargetUnit cast, so those paths are byte-identical.
        /// </summary>
        public readonly FixedVec3 TargetPoint;

        /// <summary>Story 15.11: true when this context carries a ground <see cref="TargetPoint"/> (a GroundPoint cast). Default
        /// false ⇒ Self/TargetUnit graphs behave exactly as before (they center on <see cref="PrimaryTargetId"/>).</summary>
        public readonly bool HasTargetPoint;

        /// <summary>The caster's faction (the killer for <see cref="DamageEffect"/>, the allegiance anchor for filters).</summary>
        public readonly Faction CasterFaction;

        /// <summary>Story 9.14 — the sim-owned alliance mask, so a <see cref="SearchAreaEffect"/>'s Ally/Enemy filter
        /// is TEAM-aware (an allied faction matches Ally and is excluded from Enemy). Optional (a reference, never
        /// copied by value): null ⇒ allegiance falls back to strict faction equality (byte-identical to pre-9.14 / FFA,
        /// where no distinct factions are allied).</summary>
        public readonly AllianceStore? Alliances;

        /// <summary>The damage matrix the <see cref="DamageEffect"/> leaf resolves through.</summary>
        public readonly DamageTable DamageTable;

        /// <summary>Optional combat-feedback sink (UnitKilled events) threaded to <c>DamageResolver</c>.</summary>
        public readonly CombatEventQueue? Events;

        /// <summary>Optional scoreboard sink (kills/losses) threaded to <c>DamageResolver</c>.</summary>
        public readonly MatchStats? Stats;

        /// <summary>
        /// The Story 2.2b modifier store an <c>ApplyModifierEffect</c>/<c>PersistentEffect</c> resolves against
        /// (install / stack / schedule periods). Optional (a graph with no modifier leaf never reads it); reaching
        /// an <c>ApplyModifier</c>/<c>Persistent</c> node with a NULL store fails CLOSED (a modifier graph needs a
        /// store in context). A reference, like the other heavy state — never copied by value.
        /// </summary>
        public readonly ModifierStore? ModifierStore;

        /// <summary>Story 3.13: optional transient death feed. A <see cref="DamageEffect"/> leaf that lands a lethal
        /// (ability-delivered) hit records the victim here so <see cref="ProjectChimera.Combat.HeroXpSystem"/> credits
        /// hostile heroes in range — so ability kills grant XP exactly like auto-attack kills. Null for graphs run
        /// outside the XP-bearing sim (bare tests), which record no death. A reference, never copied by value.</summary>
        public readonly DeathFeed? Deaths;

        /// <summary>
        /// Build a root context. <see cref="Rng"/> is taken from <paramref name="world"/> (the one shared stream);
        /// callers never pass a separate generator. <paramref name="primaryTargetId"/> defaults to the caster for
        /// self-targeted graphs. <paramref name="modifierStore"/> is null for graphs that install no modifier.
        /// </summary>
        public EffectContext(EntityWorld world, int casterId, int primaryTargetId, Faction casterFaction,
                             DamageTable damageTable, SpatialHash? spatial = null,
                             CombatEventQueue? events = null, MatchStats? stats = null,
                             ModifierStore? modifierStore = null, DeathFeed? deaths = null,
                             AllianceStore? alliances = null,
                             FixedVec3 targetPoint = default, bool hasTargetPoint = false)
            : this(world, world.Rng, spatial, casterId, primaryTargetId, casterFaction, damageTable, events, stats, modifierStore, deaths, alliances, targetPoint, hasTargetPoint)
        {
        }

        // All-field private ctor used by WithTarget (re-targets without re-reading world.Rng).
        private EffectContext(EntityWorld world, SimRng rng, SpatialHash? spatial, int casterId,
                              int primaryTargetId, Faction casterFaction, DamageTable damageTable,
                              CombatEventQueue? events, MatchStats? stats, ModifierStore? modifierStore, DeathFeed? deaths,
                              AllianceStore? alliances, FixedVec3 targetPoint, bool hasTargetPoint)
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
            ModifierStore = modifierStore;
            Deaths = deaths;
            Alliances = alliances;
            TargetPoint = targetPoint;
            HasTargetPoint = hasTargetPoint;
        }

        /// <summary>
        /// A copy of this context re-pointed at <paramref name="targetId"/> as the primary target. Used by the
        /// executor to fan a SearchArea child out per matched entity. Cheap (struct copy of references) and shares
        /// the same RNG stream and modifier store.
        /// <para>Story 15.11: re-pointing to an ENTITY clears the ground <see cref="TargetPoint"/> — a leaf below a
        /// SearchArea acts on the matched entity, and a nested SearchArea should re-center on that entity, not on the
        /// original ground point. The ground point governs only the ROOT SearchArea's center.</para>
        /// </summary>
        public EffectContext WithTarget(int targetId) =>
            new EffectContext(World, Rng, Spatial, CasterId, targetId, CasterFaction, DamageTable, Events, Stats, ModifierStore, Deaths, Alliances, default, false);
    }
}
