#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Inputs for one damage application. The CALLER supplies <see cref="TargetArmor"/> (live world armor
    /// for melee/splash, or the spawn-time SNAPSHOT armor for a projectile primary hit) and
    /// <see cref="Killer"/>, so the resolver never re-reads live armor and the projectile snapshot is
    /// preserved bit-for-bit (Story 1.6 AC2). A plain <c>readonly struct</c> passed <c>in</c> — no Span/ref field.
    /// </summary>
    public readonly struct DamageContext
    {
        public readonly EntityWorld World;
        public readonly int TargetId;
        public readonly ArmorType TargetArmor;
        public readonly Faction Killer;
        public readonly DamageTable Table;
        public readonly CombatEventQueue? Events;
        public readonly MatchStats? Stats;

        public DamageContext(EntityWorld world, int targetId, ArmorType targetArmor, Faction killer,
                             DamageTable table, CombatEventQueue? events, MatchStats? stats)
        {
            World = world;
            TargetId = targetId;
            TargetArmor = targetArmor;
            Killer = killer;
            Table = table;
            Events = events;
            Stats = stats;
        }
    }

    /// <summary>
    /// The single damage code path (AR-26 / FR-44). final = amount * matrix[type][armor]
    /// (NO flat armor subtraction — matches the as-built formula). Unifies the formula + Health
    /// subtraction + death sequence (UnitKilled event, RecordKill, Destroy) across the three call sites.
    /// The pre-hit feedback event and the melee attacker-cleanup stay at the call sites (they differ per
    /// site), so this preserves the exact event/death order the golden checksums pin (Story 1.6 AC2).
    /// </summary>
    public static class DamageResolver
    {
        /// <summary>
        /// Apply <paramref name="amount"/> of <paramref name="type"/> damage to <c>ctx.TargetId</c>,
        /// scaled by the table multiplier for the caller-supplied <c>ctx.TargetArmor</c>. Subtracts Health,
        /// and on lethal damage pushes <see cref="CombatEventType.UnitKilled"/>, records the kill, and
        /// destroys the target. Returns <c>true</c> if the target died (so a melee caller can clear its
        /// attack state); projectile callers ignore the return value.
        /// </summary>
        public static bool Apply(in DamageContext ctx, Fixed amount, DamageType type)
        {
            EntityWorld world = ctx.World;
            int t = ctx.TargetId;
            // Defensive guard for the single reusable damage path: never apply to a dead/destroyed
            // slot. No current caller reaches this (melee, projectile-primary, and splash all check
            // aliveness upstream), so it is a no-op for the golden checksums (AC2). It exists so a
            // FUTURE caller (ability, DoT, second same-tick hit) can't produce a phantom UnitKilled
            // event or an inflated RecordKill by hitting an already-dead target.
            if (!world.IsAlive(t)) return false;
            Fixed multiplier = ctx.Table.Get(type, ctx.TargetArmor);
            Fixed damage = amount * multiplier; // NO − armorValue (as-built formula; D8)
            world.Health[t] = world.Health[t] - damage;
            if (world.Health[t] <= Fixed.Zero)
            {
                ctx.Events?.Push(CombatEventType.UnitKilled, world.Position[t]);
                ctx.Stats?.RecordKill(world.FactionOf[t], ctx.Killer); // RecordKill is (victim, killer)
                world.Destroy(t);
                return true;
            }
            return false;
        }
    }
}
