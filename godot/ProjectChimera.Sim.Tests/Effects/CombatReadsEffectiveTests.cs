#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2a (AC1 teeth) — combat reads the EFFECTIVE attack damage, not the base. A buff injected through
    /// <see cref="ModifierSystem"/> (run at host index 3, immediately before combat) must reach the damage
    /// <see cref="CombatSystem"/> deals the SAME tick. Uses Hero damage vs Hero armor, whose matrix multiplier is a
    /// neutral 1.0 (pinned by <c>DamageResolverTests.Apply_HeroVsHero_DealsFullDamage</c>), so the Health delta
    /// equals the effective damage exactly — an independently-derived raw, with explicit teeth against the base value.
    /// </summary>
    public class CombatReadsEffectiveTests
    {
        [Fact]
        public void CombatSystem_DealsEffectiveAttackDamage_NotBase()
        {
            var world    = new EntityWorld();
            var modifier = new ModifierSystem();
            var combat   = new CombatSystem(new ProjectileStore()); // null events/stats/table → DamageTable.Default

            // Attacker (P1): Base 10 damage, melee range, Hero damage type, ready to strike this tick. CommandState
            // defaults to Idle (auto-attack the nearest in-range enemy).
            int attacker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.BaseAttackDamage[attacker]      = Fixed.FromInt(10);
            world.EffectiveAttackDamage[attacker] = Fixed.FromInt(10); // pre-buff baseline (a spawned unit has Effective==Base)
            world.AttackRange[attacker]    = Fixed.FromInt(2);   // <= MELEE_THRESHOLD (2.5) ⇒ instant melee damage
            world.AttackSpeed[attacker]    = Fixed.FromInt(1);
            world.AttackCooldown[attacker] = Fixed.Zero;          // cooldown elapsed ⇒ strikes this tick
            world.DamageTypeOf[attacker]   = DamageType.Hero;

            // Target (P2): huge HP so it survives; Hero armor ⇒ Hero/Hero multiplier 1.0; one unit away (in melee range).
            int target = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                                      Faction.Player2, Fixed.FromInt(1000), Fixed.FromInt(3));
            world.ArmorTypeOf[target] = ArmorType.Hero; // target's EffectiveAttackDamage stays 0 ⇒ it never fights back

            // Buff: +40 attack damage ⇒ Effective 50 while Base stays 10.
            modifier.AccumulateBonus(attacker, Fixed.FromInt(40), Fixed.Zero, Fixed.Zero);

            int before = world.Health[target].Raw;
            modifier.Tick(world, Fixed.Zero); // EffectiveAttackDamage[attacker] = 10 + 40 = 50
            Assert.Equal(Fixed.FromInt(50).Raw, world.EffectiveAttackDamage[attacker].Raw); // recompute happened
            combat.Tick(world, Fixed.Zero);   // attacker strikes target with its EFFECTIVE damage

            int dealt = before - world.Health[target].Raw;
            // Hero/Hero multiplier is 1.0, so the dealt delta equals the effective damage exactly.
            Assert.Equal(Fixed.FromInt(50).Raw, dealt);   // combat used Effective (50)
            Assert.NotEqual(Fixed.FromInt(10).Raw, dealt); // teeth: NOT Base (10) — RED if CombatSystem read BaseAttackDamage
        }
    }
}
