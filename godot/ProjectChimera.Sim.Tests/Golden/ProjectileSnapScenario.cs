using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-25 — the SNAP-BRANCH golden scenario. The DW-25 snap-to-goal clamp changes the folded impact
    /// <c>Position</c> — and, via <c>ProjectileSystem.ApplySplash</c> (which centers splash on the shell's
    /// <c>Position</c>) — the folded SPLASH CENTER on any tick that takes the new <c>step >= dist</c> branch. The
    /// existing DeliveryScenario uses speeds 6 and 18, neither of which overshoots the 0.5u hit radius, so it never
    /// touches the snap branch and left that determinism-relevant path unpinned at the golden surface. This scenario
    /// drives two HIGH-SPEED (step 3.0 u/tick) Projectile units whose shells provably enter the snap branch on final
    /// approach, pinning the cross-platform-deterministic snap position (and snapped splash center) via SimChecksum:
    ///   • a HIGH-SPEED SINGLE-TARGET unit firing at a Neutral 10u away — approach 10→7→4→1, at dist 1 (>0.5) the
    ///     3u step ≥ dist ⇒ SNAP to the goal, then a clean hit next tick (no orbit);
    ///   • a HIGH-SPEED SPLASH unit firing at a tight Neutral cluster 8u away — approach 8→5→2, at dist 2 the 3u step
    ///     ≥ dist ⇒ SNAP to the primary, so the folded splash center is the SNAPPED point (the determinism-relevant
    ///     consequence this golden exists to pin).
    /// The per-tick <see cref="SimChecksum"/> captures the Neutral cluster's health dropping — proving the snap path
    /// does real, deterministic work. If the snap position or the snapped splash center regressed, the sequence would
    /// diverge.
    ///
    /// CROSS-PLATFORM SAFE: every value is integer/<see cref="Fixed"/> (all target coordinates well under ~180u so the
    /// pre-existing 16.16 SqrMagnitude range is never exceeded). All targets are high-HP Neutral passives (0 damage →
    /// never fight back, survive the run) and Player2 is EMPTY, so the float-scoring AI no-ops — this golden is NOT
    /// Windows-gated and is compared on both CI legs.
    /// </summary>
    public static class ProjectileSnapScenario
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

            int firstAttacker = PopulateScenario(host.World);
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, firstAttacker);
        }

        private static int PopulateScenario(EntityWorld w)
        {
            // ── id 0 — HIGH-SPEED SINGLE-TARGET unit. ProjectileSpeed 90 ⇒ step = 90/30 = 3.0 u/tick. Fires at the
            //    Neutral 10u away: the shell approaches 10→7→4→1, and at dist 1 (> the 0.5u hit radius) the 3u step
            //    exceeds the remaining distance ⇒ the DW-25 SNAP branch lands it EXACTLY on the goal, hitting cleanly
            //    the next tick instead of orbiting. ──
            int shooter = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[shooter] = Fixed.FromInt(15);
            w.AttackRange[shooter] = Fixed.FromInt(12);
            w.AttackSpeed[shooter] = Fixed.FromInt(1);
            w.DamageTypeOf[shooter] = DamageType.Normal;
            w.Delivery[shooter]        = AttackDelivery.Projectile;
            w.ProjectileSpeed[shooter] = Fixed.FromInt(90);   // step 3.0 u/tick ⇒ overshoots the 0.5u radius on final approach
            w.CommandState[shooter] = UnitCommand.Idle;

            // id 1 — the shooter's high-HP Neutral target at distance 10 (survives the run so the sequence keeps evolving).
            w.Create(V(10, 0, 0), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));

            // ── id 2 — HIGH-SPEED SPLASH unit. ProjectileSpeed 90 (step 3.0 u/tick), SplashRadius 3. Fires at the
            //    nearest Neutral in a tight cluster 8u away: approach 8→5→2, and at dist 2 the 3u step ≥ dist ⇒ SNAP to
            //    the primary. The impact — and therefore the folded splash center (ApplySplash reads the shell Position)
            //    — is the SNAPPED point, which is exactly the determinism-relevant consequence this golden pins. ──
            int splash = w.Create(V(0, 0, 20), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[splash] = Fixed.FromInt(12);
            w.AttackRange[splash] = Fixed.FromInt(10);
            w.AttackSpeed[splash] = Fixed.FromInt(1);
            w.DamageTypeOf[splash] = DamageType.Siege;
            w.Delivery[splash]        = AttackDelivery.Projectile;
            w.ProjectileSpeed[splash] = Fixed.FromInt(90);   // step 3.0 u/tick ⇒ snaps on final approach to the cluster
            w.SplashRadius[splash]    = Fixed.FromInt(3);
            w.CommandState[splash]    = UnitCommand.Idle;

            // ids 3-5 — a tight Neutral cluster: primary (8,0,20) plus two within splash radius 3 of the snapped impact.
            w.Create(V(8, 0, 20), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));
            w.Create(V(9, 0, 20), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));
            w.Create(V(8, 0, 21), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));

            return shooter;
        }

        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
