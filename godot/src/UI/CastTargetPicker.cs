#nullable enable
using ProjectChimera.Core;              // EntityWorld, Faction
using ProjectChimera.Core.Definitions;  // AbilityTargeting, TargetAffinity

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 15.11 (DW-286 / DW-290, review P5) — the Godot-FREE cores of the cast-target UI, extracted from
    /// <see cref="SelectionSystem"/> / <see cref="CommandCardSystem"/> so they are Tier-1 unit-testable (the
    /// Godot-coupled panels are not). Two pieces, both pure functions over the <see cref="EntityWorld"/> SoA:
    ///
    ///   • <see cref="FindNearest"/> — the affinity-aware click-picker. Presentation math (float distance) is fine
    ///     here: this runs at INPUT time on the local client and only decides which entity id the cast order names;
    ///     the deterministic sim never runs it.
    ///   • <see cref="IsTargetingCastable"/> — the single is-castable-targeting predicate the command card consults
    ///     from BOTH its disable-gate and its press-router (the DW-290 anti-divergence guarantee).
    /// </summary>
    public static class CastTargetPicker
    {
        /// <summary>
        /// Nearest entity to (<paramref name="hitX"/>, <paramref name="hitZ"/>) within <paramref name="radius"/> that
        /// matches <paramref name="affinity"/> relative to <paramref name="localFaction"/>:
        ///   • <see cref="TargetAffinity.Enemy"/> (and the historical default) → alive, NOT the local faction and NOT
        ///     Neutral. The caster is the local faction, so it is never a candidate (no explicit exclusion needed).
        ///   • <see cref="TargetAffinity.Ally"/> → alive, the local faction, EXCLUDING the caster (heal-OTHER).
        ///   • <see cref="TargetAffinity.Any"/> → alive, any allegiance, EXCLUDING only the caster.
        /// Returns the matched entity id, or -1 if nothing matches in radius. Pass <paramref name="casterId"/> = -1 to
        /// skip caster exclusion (the enemy pick, which cannot select the caster anyway).
        /// </summary>
        public static int FindNearest(EntityWorld world, float hitX, float hitZ, float radius,
                                      Faction localFaction, int casterId, TargetAffinity affinity)
        {
            int   bestId = -1;
            float bestSq = radius * radius;
            int   cap    = world.HighWaterMark;

            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) continue;
                if ((world.Flags[i] & EntityFlags.Phased) != 0) continue; // DW-938: inside a building — untargetable
                if (!Matches(world, i, localFaction, casterId, affinity)) continue;
                FixedVec3 pos = world.Position[i];
                float dx = pos.X.ToFloat() - hitX;
                float dz = pos.Z.ToFloat() - hitZ;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; bestId = i; }
            }
            return bestId;
        }

        /// <summary>The allegiance predicate for <see cref="FindNearest"/> (see its summary for each affinity's rule).</summary>
        private static bool Matches(EntityWorld world, int i, Faction localFaction, int casterId, TargetAffinity affinity)
        {
            switch (affinity)
            {
                case TargetAffinity.Ally:
                    return i != casterId && world.FactionOf[i] == localFaction;
                case TargetAffinity.Any:
                    return i != casterId;
                default: // Enemy (and null/absent → the historical enemy-only default)
                    Faction f = world.FactionOf[i];
                    return f != localFaction && f != Faction.Neutral;
            }
        }

        /// <summary>
        /// Story 15.11 (DW-290) — the SINGLE is-castable-targeting predicate. A targeting mode is castable iff it is one
        /// of the closed <see cref="AbilityTargeting"/> values (Self/None/TargetUnit/GroundPoint); an unknown/unparseable
        /// string (<c>null</c>) is not. <see cref="CommandCardSystem"/> consults THIS from both its disable-gate and its
        /// press-router, so the enabled state and the press action can never diverge as modes are added.
        /// </summary>
        public static bool IsTargetingCastable(AbilityTargeting? targeting) => targeting is
            AbilityTargeting.Self or AbilityTargeting.None or AbilityTargeting.TargetUnit or AbilityTargeting.GroundPoint;
    }
}
