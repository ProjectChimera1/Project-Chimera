#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Deals matrix-scaled combat damage to the target through the single damage code path
    /// (<see cref="DamageResolver.Apply"/>) — the same path melee, projectile, and splash use. The resolver
    /// applies <c>final = amount * table.Get(type, armor)</c> (no flat armor subtraction; the as-built formula),
    /// subtracts Health, and on lethal damage fires the full death sequence (UnitKilled event, RecordKill,
    /// Destroy). Contrast <see cref="DirectHpDeltaEffect"/>, which is flat and armor-independent.
    ///
    /// The target's LIVE armor (<c>world.ArmorTypeOf[t]</c>) is used — an ability hits the unit as it currently
    /// is, unlike a projectile, which carries a spawn-time armor snapshot.
    /// </summary>
    public sealed class DamageEffect : LeafEffect
    {
        /// <summary>Base damage before the type/armor matrix multiplier.</summary>
        public readonly Fixed Amount;

        /// <summary>The damage type, indexing the damage matrix against the target's armor.</summary>
        public readonly DamageType Type;

        /// <summary>Construct a matrix-damage leaf. <paramref name="requireTag"/> (Story 2.11, default None) gates the
        /// single-target apply on the target's tag — e.g. single-target "+X vs Mechanical" bonus term; omit for
        /// byte-identical 2.1 behaviour. The tag gate is a target-selection predicate; it does NOT touch the damage matrix.</summary>
        public DamageEffect(Fixed amount, DamageType type, UnitTag requireTag = UnitTag.None) : base(requireTag)
        {
            Amount = amount;
            Type = type;
        }

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            EntityWorld world = ctx.World;
            int t = ctx.PrimaryTargetId;
            if (!world.IsAlive(t)) return;

            // The one damage path. Killer = caster faction; armor = the target's live armor; the optional
            // event/stats sinks ride the context so AoE/ability kills feed the same feedback + scoreboard.
            var dc = new DamageContext(world, t, world.ArmorTypeOf[t], ctx.CasterFaction,
                                       ctx.DamageTable, ctx.Events, ctx.Stats);
            DamageResolver.Apply(in dc, Amount, Type);
        }
    }
}
