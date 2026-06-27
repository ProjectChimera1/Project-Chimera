#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.4a (AC6) — affordability is checked across energy + ore + crystal, and a refused cast is ATOMIC:
    /// nothing is debited, no cooldown starts, no effect runs. The headline atomicity case is a crystal shortfall
    /// that must NOT have already debited energy/ore (check-all-then-debit-all, never a partial spend).
    /// </summary>
    public class AbilityAffordabilityTests
    {
        private const int P1 = (int)Faction.Player1;

        /// <summary>A SelfHeal costing 50 energy + 10 ore + 5 crystal, 3s cd, heal 40, on a damaged caster.</summary>
        private static CastHarness DamagedHealer(int energy, int ore, int crystal, out int caster)
        {
            var h = new CastHarness(AbilityTestAbilities.SelfHeal(costEnergy: 50, costOre: 10, costCrystal: 5, cooldownSec: 3, heal: 40));
            caster = h.Caster("test_heal", energy: energy);
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(ore));
            h.Resources.AddCrystal(Faction.Player1, Fixed.FromInt(crystal));
            h.World.Health[caster] = Fixed.FromInt(50); // damaged so a successful heal would be observable
            return h;
        }

        private static void AssertNothingHappened(CastHarness h, int c, int energy, int ore, int crystal)
        {
            Assert.Equal(Fixed.FromInt(energy).Raw,  h.World.Energy[c].Raw);          // energy unchanged
            Assert.Equal(Fixed.FromInt(ore).Raw,     h.Resources.Ore[P1].Raw);        // ore unchanged
            Assert.Equal(Fixed.FromInt(crystal).Raw, h.Resources.Crystal[P1].Raw);    // crystal unchanged
            Assert.Equal(0, h.Cooldown(c));                                           // cooldown NOT started
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[c].Raw);               // effect did NOT run (health unchanged)
        }

        [Fact]
        public void InsufficientEnergy_RefusesAtomically()
        {
            var h = DamagedHealer(energy: 30, ore: 100, crystal: 100, out int c); // 30 < 50
            h.IssueAndTick(c, -1);
            AssertNothingHappened(h, c, energy: 30, ore: 100, crystal: 100);
        }

        [Fact]
        public void InsufficientOre_RefusesAtomically()
        {
            var h = DamagedHealer(energy: 100, ore: 4, crystal: 100, out int c); // 4 < 10
            h.IssueAndTick(c, -1);
            AssertNothingHappened(h, c, energy: 100, ore: 4, crystal: 100);
        }

        [Fact]
        public void InsufficientCrystal_RefusesAtomically_WithoutDebitingEnergyOrOre()
        {
            var h = DamagedHealer(energy: 100, ore: 100, crystal: 2, out int c); // 2 < 5 — the LAST check
            h.IssueAndTick(c, -1);
            // The crystal failure must not have debited energy or ore (atomic — check all, then debit all).
            AssertNothingHappened(h, c, energy: 100, ore: 100, crystal: 2);
        }

        [Fact]
        public void AffordingAll_DebitsAllThreeResources_AndRunsTheEffect()
        {
            var h = DamagedHealer(energy: 100, ore: 100, crystal: 100, out int c);
            h.IssueAndTick(c, -1);

            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Energy[c].Raw);      // 100-50
            Assert.Equal(Fixed.FromInt(90).Raw, h.Resources.Ore[P1].Raw);     // 100-10
            Assert.Equal(Fixed.FromInt(95).Raw, h.Resources.Crystal[P1].Raw); // 100-5
            Assert.Equal(90, h.Cooldown(c));                                  // cooldown started (3s * 30)
            Assert.Equal(Fixed.FromInt(90).Raw, h.World.Health[c].Raw);       // healed 50 + 40
        }
    }
}
