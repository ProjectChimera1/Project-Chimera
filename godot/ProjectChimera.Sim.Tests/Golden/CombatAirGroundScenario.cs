using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9a (AC1 / AC3.4) — the COMBAT-AIR-GROUND golden scenario. A Player1 anti-air-only unit
    /// (<c>AttackDomainOf = Air</c>) sits between a NEARER ground enemy and a slightly-farther flier: it must
    /// auto-acquire and attack ONLY the flier, ignoring the closer ground unit it can never damage. Over the run the
    /// checksum captures the flier's health dropping while the ground enemy stays untouched — pinning the domain
    /// filter's deterministic outcome. If the filter regressed (the AA unit hitting the nearer ground enemy), the
    /// sequence would diverge.
    ///
    /// CROSS-PLATFORM SAFE: every hashed field is integer/<see cref="Fixed"/> (AttackDomainOf/CategoryOf are unfolded
    /// authored inputs; the folded Position/Health they steer are Fixed). Both enemies are Neutral passive targets
    /// (0 damage → never fight back) and Player2 is EMPTY, so the float-scoring AI no-ops — this golden is NOT
    /// Windows-gated and is compared on both CI legs.
    /// </summary>
    public static class CombatAirGroundScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval = 1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (P2 EMPTY → AI no-ops → cross-platform safe)
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            int aa = PopulateScenario(host.World);
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, aa);
        }

        private static int PopulateScenario(EntityWorld w)
        {
            // id 0 — the anti-air-only unit. Idle → auto-acquires the nearest AIR enemy only (AttackDomainOf = Air).
            int aa = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[aa] = Fixed.FromInt(15);
            w.AttackRange[aa]    = Fixed.FromInt(2);     // melee (≤ MELEE_THRESHOLD) → instant, no projectiles
            w.AttackSpeed[aa]    = Fixed.FromInt(1);
            w.DamageTypeOf[aa]   = DamageType.Normal;
            w.AttackDomainOf[aa] = AttackDomain.Air;
            w.CommandState[aa]   = UnitCommand.Idle;

            // id 1 — a NEARER ground enemy (Neutral, CategoryOf = Melee). The AA unit MUST ignore it (AC1 teeth).
            w.Create(V(1, 0, 0), Faction.Neutral, Fixed.FromInt(300), Fixed.FromInt(3)); // CategoryOf defaults to Melee

            // id 2 — a slightly-farther flier (Neutral, CategoryOf = Air). The AA unit's only valid target — high HP
            //         so it survives the run and the sequence keeps evolving (its health drops every cooldown).
            int air = w.Create(V(2, 0, 0), Faction.Neutral, Fixed.FromInt(300), Fixed.FromInt(3));
            w.CategoryOf[air] = UnitCategory.Air;

            return aa;
        }

        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
