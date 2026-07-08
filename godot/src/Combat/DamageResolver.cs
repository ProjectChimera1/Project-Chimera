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
        /// <summary>Story 3.13: optional transient death feed. On a lethal hit, <see cref="KillEntity"/> records the
        /// victim's <c>{position, faction, XpBounty}</c> here (BEFORE Destroy) so <see cref="HeroXpSystem"/> can credit
        /// hostile heroes in range. Null in bare combat tests / non-XP call sites (no death is recorded).</summary>
        public readonly DeathFeed? Deaths;

        public DamageContext(EntityWorld world, int targetId, ArmorType targetArmor, Faction killer,
                             DamageTable table, CombatEventQueue? events, MatchStats? stats, DeathFeed? deaths = null)
        {
            World = world;
            TargetId = targetId;
            TargetArmor = targetArmor;
            Killer = killer;
            Table = table;
            Events = events;
            Stats = stats;
            Deaths = deaths;
        }
    }

    /// <summary>
    /// The single damage code path (AR-26 / FR-44). final = max(0, amount * matrix[type][armor] − EffectiveArmor)
    /// (Story 2.6 added the flat post-matrix armor term, floored at 0; with the default BaseArmor=0 it is a no-op, so
    /// pre-2.6 combat outcomes are unchanged). Unifies the formula + Health subtraction + death sequence (UnitKilled
    /// event, RecordKill, Destroy) across the three call sites.
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
            // Story 2.6 (Decision #6): flat post-matrix armor subtraction, floored at 0 so a hit never heals. With the
            // default BaseArmor=0 (and no armor modifier) EffectiveArmor=0 → the term is −0, leaving every pre-2.6
            // combat outcome unchanged; the goldens move ONLY from the EffectiveArmor checksum fold (v8), not the math.
            // Story 2.9a: the matrix+floor math is now the shared DamageTable.FinalDamage helper (building damage reuses it).
            Fixed damage = ctx.Table.FinalDamage(amount, type, ctx.TargetArmor, world.EffectiveArmor[t]);
            world.Health[t] = world.Health[t] - damage;
            if (world.Health[t] <= Fixed.Zero)
            {
                KillEntity(world, t, ctx.Killer, ctx.Events, ctx.Stats, ctx.Deaths);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The single ENTITY-DEATH sequence (Story 2.13 extract): push <see cref="CombatEventType.UnitKilled"/> (reading
        /// the dying unit's Position/feedback override BEFORE Destroy — Story 2.7), record the kill (victim, killer), and
        /// <see cref="EntityWorld.Destroy"/> the entity. Reused verbatim by <see cref="Apply"/> on lethal damage AND by
        /// <c>AbilityCastSystem</c> when a <c>cost_health</c> self-cost brings the caster to ≤0 (AC5.4) — so a self-lethal
        /// cast dies through the EXACT same path, never an invented one. Caller ensures Health has already reached ≤0.
        /// </summary>
        public static void KillEntity(EntityWorld world, int id, Faction killer, CombatEventQueue? events,
                                      MatchStats? stats, DeathFeed? deaths = null)
        {
            events?.Push(CombatEventType.UnitKilled, world.Position[id], world.FeedbackProfile[id]);
            stats?.RecordKill(world.FactionOf[id], killer);
            // Story 3.13: record the death for the XP runtime BEFORE Destroy recycles the slot (the corpse's
            // position/faction/bounty are unobservable afterward — D-1). Uniform for hitscan/projectile/self-lethal.
            deaths?.Push(world.Position[id], world.FactionOf[id], world.XpBounty[id]);
            world.Destroy(id);
        }

        /// <summary>
        /// Story 2.9a (AC2): apply matrix damage to a <b>building</b> in <paramref name="buildings"/> — the parallel
        /// path to <see cref="Apply"/> (which is hard-bound to <see cref="EntityWorld"/> and cannot target a building).
        /// This is the SINGLE building-damage entry point shared by the melee instant path (Task 6) and the ranged
        /// projectile impact (Task 4b), so the two can never drift. Buildings use <see cref="ArmorType.Fortified"/> and
        /// have no flat armor (flat term = <see cref="Fixed.Zero"/>). Bounds/Alive are guarded defensively here so a
        /// stale id is a harmless no-op; the friendly-faction / domain checks live at the CALL site (in-tick, before
        /// this is reached). Returns <c>true</c> if the building died this call. Writes only <c>buildings.Health</c> /
        /// <c>buildings.Alive</c>, which are already folded into <see cref="SimChecksum"/> — no new fold.
        /// </summary>
        public static bool ApplyToBuilding(BuildingStore buildings, int b, Fixed amount, DamageType type,
                                           DamageTable table, CombatEventQueue? events = null)
        {
            if (b < 0 || b >= buildings.Count || !buildings.Alive[b]) return false;
            Fixed damage = table.FinalDamage(amount, type, ArmorType.Fortified, Fixed.Zero);
            buildings.Health[b] = buildings.Health[b] - damage;
            if (buildings.Health[b] <= Fixed.Zero)
            {
                events?.Push(CombatEventType.BuildingDestroyed, buildings.Position[b]);
                buildings.Destroy(b);
                return true;
            }
            return false;
        }
    }
}
