#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 1.6 (AC3) — the combat-formula slice of FR-44. Proves <see cref="DamageResolver.Apply"/>:
    ///   • applies final = amount * Get(type, armor) to Health, against INDEPENDENTLY-computed Fixed raws
    ///     (incl. a Hero/Hero pair), with NO flat armor subtraction;
    ///   • fires the death sequence (UnitKilled event + RecordKill + Destroy) ONLY when Health reaches zero;
    ///   • uses the CALLER-supplied armor (the projectile spawn snapshot), not the entity's live armor.
    /// World entities are authored with Fixed.FromInt only (no Fixed.FromFloat — the determinism rule).
    /// </summary>
    public class DamageResolverTests
    {
        private static int CreateUnit(EntityWorld w, Faction faction, int hp, ArmorType armor)
        {
            int id = w.Create(FixedVec3.Zero, faction, Fixed.FromInt(hp), Fixed.FromInt(3));
            w.ArmorTypeOf[id] = armor;
            return id;
        }

        private static DamageContext Ctx(EntityWorld w, int target, ArmorType armor, Faction killer,
                                         CombatEventQueue? events = null, MatchStats? stats = null) =>
            new DamageContext(w, target, armor, killer, DamageTable.Default, events, stats);

        // ── Formula (independently-pinned raws) ───────────────────────────────────────────────────────

        [Fact]
        public void Apply_NormalVsMedium_ReducesHealthByAmountTimesMultiplier()
        {
            var w = new EntityWorld();
            int target = CreateUnit(w, Faction.Player2, hp: 100, armor: ArmorType.Medium);
            int before = w.Health[target].Raw;

            bool died = DamageResolver.Apply(Ctx(w, target, ArmorType.Medium, Faction.Player1),
                                             Fixed.FromInt(10), DamageType.Normal);

            // amount 10 (raw 655360) * Normal/Medium 0.75 (raw 49152) = raw 491520. Independently computed.
            Assert.False(died);
            Assert.True(w.IsAlive(target));
            Assert.Equal(491_520, before - w.Health[target].Raw);
        }

        [Fact]
        public void Apply_HeroVsHero_DealsFullDamage()
        {
            var w = new EntityWorld();
            int target = CreateUnit(w, Faction.Player2, hp: 100, armor: ArmorType.Hero);
            int before = w.Health[target].Raw;

            DamageResolver.Apply(Ctx(w, target, ArmorType.Hero, Faction.Player1),
                                 Fixed.FromInt(10), DamageType.Hero);

            // Hero/Hero multiplier is neutral 1.0 → delta equals the amount exactly (raw 655360).
            Assert.Equal(Fixed.FromInt(10).Raw, before - w.Health[target].Raw);
        }

        // ── Death sequence: only on lethal damage ─────────────────────────────────────────────────────

        [Fact]
        public void Apply_LethalDamage_FiresFullDeathSequence()
        {
            var w = new EntityWorld();
            var events = new CombatEventQueue();
            var stats = new MatchStats();
            int target = CreateUnit(w, Faction.Player2, hp: 5, armor: ArmorType.Unarmored);
            FixedVec3 deathPos = w.Position[target];

            bool died = DamageResolver.Apply(Ctx(w, target, ArmorType.Unarmored, Faction.Player1, events, stats),
                                             Fixed.FromInt(10), DamageType.Normal); // 10 dmg vs 5 hp → lethal

            Assert.True(died);
            Assert.False(w.IsAlive(target));
            Assert.Equal(1, stats.Losses(Faction.Player2)); // victim recorded
            Assert.Equal(1, stats.Kills(Faction.Player1));  // killer recorded
            Assert.Equal(1, events.Count);                  // exactly one event
            Assert.Equal(CombatEventType.UnitKilled, events.Get(0).Type);
            Assert.Equal(deathPos, events.Get(0).Position);
        }

        [Fact]
        public void Apply_SubLethalDamage_LeavesTargetAliveWithNoDeathSideEffects()
        {
            var w = new EntityWorld();
            var events = new CombatEventQueue();
            var stats = new MatchStats();
            int target = CreateUnit(w, Faction.Player2, hp: 100, armor: ArmorType.Unarmored);

            bool died = DamageResolver.Apply(Ctx(w, target, ArmorType.Unarmored, Faction.Player1, events, stats),
                                             Fixed.FromInt(10), DamageType.Normal); // 10 dmg vs 100 hp → survives

            Assert.False(died);
            Assert.True(w.IsAlive(target));
            Assert.Equal(0, stats.Losses(Faction.Player2));
            Assert.Equal(0, stats.Kills(Faction.Player1));
            Assert.Equal(0, events.Count); // no UnitKilled when it survives
        }

        // ── Snapshot armor: caller's armor wins, not the entity's live armor ──────────────────────────

        [Fact]
        public void Apply_UsesCallerSuppliedArmor_NotLiveWorldArmor()
        {
            var w = new EntityWorld();
            // Entity's LIVE armor is Unarmored (×1.0); the caller supplies the Heavy SNAPSHOT (×0.5).
            int target = CreateUnit(w, Faction.Player2, hp: 100, armor: ArmorType.Unarmored);
            int before = w.Health[target].Raw;

            DamageResolver.Apply(Ctx(w, target, ArmorType.Heavy, Faction.Player1),
                                 Fixed.FromInt(10), DamageType.Normal);

            // If live Unarmored armor were (wrongly) used → delta 655360. The supplied Heavy 0.5 → delta 327680.
            Assert.Equal(327_680, before - w.Health[target].Raw);
        }
    }
}
