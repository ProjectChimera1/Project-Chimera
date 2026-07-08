using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.12 (AC6) — the DELIVERY golden scenario. Directly exercises the authorable attack-delivery flag +
    /// per-unit projectile speed, decoupled from range, plus the unchanged splash path:
    ///   • a LONG-RANGE Hitscan unit (AttackRange 12, well above the old 2.5 threshold) — proves instant damage with
    ///     NO projectile spawned regardless of range;
    ///   • a SHORT-RANGE Projectile unit (AttackRange 1) with a CUSTOM projectile_speed (6 ≠ the 18 fallback) — proves
    ///     per-unit speed AND range decoupling (a melee-range unit fires a slow shot);
    ///   • a SPLASH Projectile unit (SplashRadius 3) over a tight Neutral cluster — VERIFIES the existing splash path is
    ///     unchanged by this story.
    /// The per-tick <see cref="SimChecksum"/> (now folding Delivery + ProjectileSpeed, v10) captures the targets' health
    /// dropping — pinning the delivery outcomes. If delivery regressed (e.g. the sniper spawning a projectile, or the
    /// short-range unit going hitscan), the sequence would diverge.
    ///
    /// CROSS-PLATFORM SAFE: every value is integer/<see cref="Fixed"/> (Delivery/ProjectileSpeed fold as int/Fixed.Raw).
    /// All targets are high-HP Neutral passives (0 damage → never fight back, survive the run) and Player2 is EMPTY, so
    /// the float-scoring AI no-ops — this golden is NOT Windows-gated and is compared on both CI legs.
    /// </summary>
    public static class DeliveryScenario
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

            int sniper = PopulateScenario(host.World);
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, sniper);
        }

        private static int PopulateScenario(EntityWorld w)
        {
            // ── id 0 — LONG-RANGE HITSCAN sniper. AttackRange 12 (well above 2.5), but Delivery = Hitscan ⇒ instant
            //    damage, NO projectile spawned (range decoupled). Targets the Neutral at (8,0,0), distance 8 ≤ 12. ──
            int sniper = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[sniper] = Fixed.FromInt(15);
            w.AttackRange[sniper] = Fixed.FromInt(12);
            w.AttackSpeed[sniper] = Fixed.FromInt(1);
            w.DamageTypeOf[sniper] = DamageType.Normal;
            w.Delivery[sniper]     = AttackDelivery.Hitscan;     // authored: instant despite the long range
            w.CommandState[sniper] = UnitCommand.Idle;

            // id 1 — the sniper's high-HP Neutral target (survives the run so the sequence keeps evolving).
            w.Create(V(8, 0, 0), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));

            // ── id 2 — SHORT-RANGE PROJECTILE unit. AttackRange 1 (melee-range), but Delivery = Projectile with a
            //    CUSTOM speed 6 (≠ the 18 fallback) ⇒ fires a slow tracking shot at the adjacent Neutral. ──
            int lobber = w.Create(V(0, 0, 20), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[lobber] = Fixed.FromInt(10);
            w.AttackRange[lobber] = Fixed.FromInt(1);
            w.AttackSpeed[lobber] = Fixed.FromInt(1);
            w.DamageTypeOf[lobber] = DamageType.Normal;
            w.Delivery[lobber]        = AttackDelivery.Projectile;  // authored: a projectile despite the short range
            w.ProjectileSpeed[lobber] = Fixed.FromInt(6);           // per-unit speed (proves it is honoured, ≠ 18)
            w.CommandState[lobber] = UnitCommand.Idle;

            // id 3 — the lobber's Neutral target at distance 1 (within AttackRange 1).
            w.Create(V(1, 0, 20), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));

            // ── id 4 — SPLASH Projectile unit. AttackRange 8, SplashRadius 3, default speed. Fires at the nearest
            //    Neutral (5,0,40) and splashes the tight cluster — VERIFY the existing splash path is unchanged. ──
            int splash = w.Create(V(0, 0, 40), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[splash] = Fixed.FromInt(12);
            w.AttackRange[splash] = Fixed.FromInt(8);
            w.AttackSpeed[splash] = Fixed.FromInt(1);
            w.DamageTypeOf[splash] = DamageType.Siege;
            w.Delivery[splash]      = AttackDelivery.Projectile;
            w.SplashRadius[splash]  = Fixed.FromInt(3);
            w.CommandState[splash]  = UnitCommand.Idle;

            // ids 5-7 — a tight Neutral cluster (primary + two within splash radius 3 of the impact).
            w.Create(V(5, 0, 40), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));
            w.Create(V(6, 0, 40), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));
            w.Create(V(5, 0, 41), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));

            return sniper;
        }

        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
