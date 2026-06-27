#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.4a (AC3) — the per-ability cooldown ticks down in Fixed time and re-enables EXACTLY at zero. The
    /// seconds→ticks conversion is integer (30 tps), drift-free, and the re-enable boundary is exact: a cast is
    /// refused while the cooldown is still &gt; 0 at the gate, and succeeds the moment it reaches 0.
    /// </summary>
    public class AbilityCooldownTests
    {
        [Fact]
        public void SecondsToTicks_Is30PerSecond_IntegerTruncation()
        {
            Assert.Equal(90,  AbilityCastSystem.SecondsToTicks(Fixed.FromInt(3)));  // minor_heal
            Assert.Equal(180, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(6)));  // fireball (independently derived: 6 * 30)
            Assert.Equal(360, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(12))); // battle_fury
        }

        [Fact]
        public void Cast_StartsCooldown_ThenReachesExactlyZero_AfterThatManyTicks()
        {
            var h = new CastHarness(AbilityTestAbilities.MinorHeal());
            int c = h.Caster("minor_heal", energy: 100);

            h.IssueAndTick(c, -1);             // cast (this tick already counts as the cast tick)
            Assert.Equal(90, h.Cooldown(c));   // 3s * 30

            h.TickCast(89);                    // 89 more decrements → 1 remaining
            Assert.Equal(1, h.Cooldown(c));
            h.TickCast(1);                     // the 90th decrement → exactly 0
            Assert.Equal(0, h.Cooldown(c));
        }

        [Fact]
        public void CastWhileOnCooldown_IsRefused_AndSucceedsExactlyWhenCooldownHitsZero()
        {
            var h = new CastHarness(AbilityTestAbilities.MinorHeal());
            int c = h.Caster("minor_heal", energy: 100);

            h.IssueAndTick(c, -1);             // cast #1 → cd 90, energy 100-20=80
            Assert.Equal(90, h.Cooldown(c));
            Assert.Equal(Fixed.FromInt(80).Raw, h.World.Energy[c].Raw);

            h.TickCast(88);                    // cd 90→2
            Assert.Equal(2, h.Cooldown(c));

            // Attempt #2 while the cooldown will still be > 0 at the gate (this tick's tick-down makes it 1):
            // REFUSED — no second debit, the cooldown is NOT reset.
            h.IssueAndTick(c, -1);
            Assert.Equal(1, h.Cooldown(c));
            Assert.Equal(Fixed.FromInt(80).Raw, h.World.Energy[c].Raw);

            // Attempt #3 on the tick the cooldown reaches 0 (this tick's tick-down makes it 0): SUCCEEDS.
            h.IssueAndTick(c, -1);
            Assert.Equal(90, h.Cooldown(c));                              // re-cast → cooldown restarted
            Assert.Equal(Fixed.FromInt(60).Raw, h.World.Energy[c].Raw);  // 80-20 debited again
        }
    }
}
