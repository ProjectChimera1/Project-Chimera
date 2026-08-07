#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-265 / Story 15.12 — the flat energy-regen path (<see cref="EnergyRegenSystem"/>) and its single seam
    /// (<see cref="EnergyRegenSystem.RegenPerTick"/>). Covers the I/O-matrix rows: a spent caster recovers and clamps
    /// at MaxEnergy; a <c>regen_rate == 0</c> unit is a byte-identical no-op; a recycled slot inherits no regen; and
    /// the seam returns the authored per-tick amount (the value Story 15.21 later extends). Pure C# / Fixed-only.
    /// </summary>
    public class EnergyRegenSystemTests
    {
        private static readonly Fixed Dt = Fixed.Zero; // per-tick amount is authored; dt is unused (like ModifierStore.Advance)

        private static int Caster(EntityWorld w, int maxEnergy, int energy, Fixed regenPerTick)
        {
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.MaxEnergy[id] = Fixed.FromInt(maxEnergy);
            w.Energy[id]    = Fixed.FromInt(energy);
            w.RegenRate[id] = regenPerTick;
            return id;
        }

        [Fact]
        public void SpentCaster_RegensEachTick_AndClampsAtMaxEnergy()
        {
            var w = new EntityWorld();
            var sys = new EnergyRegenSystem();
            int id = Caster(w, maxEnergy: 100, energy: 40, regenPerTick: Fixed.FromInt(2));

            // +2/tick from 40 → reaches 100 after 30 ticks (60/2), then stays clamped.
            for (int t = 1; t <= 30; t++)
            {
                sys.Tick(w, Dt);
                int expected = System.Math.Min(40 + 2 * t, 100);
                Assert.Equal(Fixed.FromInt(expected).Raw, w.Energy[id].Raw);
            }
            Assert.Equal(Fixed.FromInt(100).Raw, w.Energy[id].Raw);
            sys.Tick(w, Dt); // still clamped — no overflow past MaxEnergy
            Assert.Equal(Fixed.FromInt(100).Raw, w.Energy[id].Raw);
        }

        [Fact]
        public void ZeroRegen_IsAByteIdenticalNoOp()
        {
            var w = new EntityWorld();
            var sys = new EnergyRegenSystem();
            int id = Caster(w, maxEnergy: 100, energy: 40, regenPerTick: Fixed.Zero); // the shipped-content default

            long before = w.Energy[id].Raw;
            for (int t = 0; t < 50; t++) sys.Tick(w, Dt);
            Assert.Equal(before, w.Energy[id].Raw); // min(E+0, Max) == E — no folded state moves
        }

        [Fact]
        public void ZeroRegen_DoesNotClampEnergyAboveMaxEnergy_TrueNoOp()
        {
            // P1: a regen==0 unit is a TRUE no-op — the per-tick loop early-outs on the seam's zero result and never
            // touches Energy, so it does NOT clamp a (theoretical) Energy>MaxEnergy slot. Real units never exceed
            // MaxEnergy (ApplyUnitDefinition starts them full, casts only debit), so this is a robustness property, not
            // a gameplay path — and it is what makes shipped regen==0 content byte-identical regardless of any state.
            var w = new EntityWorld();
            var sys = new EnergyRegenSystem();
            int id = Caster(w, maxEnergy: 50, energy: 80, regenPerTick: Fixed.Zero);

            sys.Tick(w, Dt);
            Assert.Equal(Fixed.FromInt(80).Raw, w.Energy[id].Raw); // untouched — no clamp when regen is zero
        }

        [Fact]
        public void NonZeroRegen_ClampsEnergyIntoTheCeiling()
        {
            // When regen IS non-zero the loop runs and clamps into [0, MaxEnergy], so an over-ceiling slot is brought
            // back to MaxEnergy (min(80 + 1, 50) == 50).
            var w = new EntityWorld();
            var sys = new EnergyRegenSystem();
            int id = Caster(w, maxEnergy: 50, energy: 80, regenPerTick: Fixed.FromInt(1));

            sys.Tick(w, Dt);
            Assert.Equal(Fixed.FromInt(50).Raw, w.Energy[id].Raw);
        }

        [Fact]
        public void RecycledSlot_InheritsNoRegen()
        {
            var w = new EntityWorld();
            var sys = new EnergyRegenSystem();
            int first = Caster(w, maxEnergy: 100, energy: 40, regenPerTick: Fixed.FromInt(5));
            w.Destroy(first);

            // A recycled slot (no def applied) must carry no regen — Create defaults RegenRate to Zero.
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused);
            Assert.Equal(Fixed.Zero.Raw, w.RegenRate[reused].Raw);

            w.MaxEnergy[reused] = Fixed.FromInt(100);
            w.Energy[reused]    = Fixed.FromInt(20);
            sys.Tick(w, Dt);
            Assert.Equal(Fixed.FromInt(20).Raw, w.Energy[reused].Raw); // no inherited +5 regen
        }

        [Fact]
        public void RegenPerTick_Seam_ReturnsTheAuthoredRate()
        {
            var w = new EntityWorld();
            int id = Caster(w, maxEnergy: 100, energy: 0, regenPerTick: Fixed.FromInt(3));
            // The ONE method every regen reader goes through; Story 15.21 extends THIS, nothing else in the tick.
            Assert.Equal(w.RegenRate[id].Raw, EnergyRegenSystem.RegenPerTick(w, id).Raw);
            Assert.Equal(Fixed.FromInt(3).Raw, EnergyRegenSystem.RegenPerTick(w, id).Raw);
        }
    }
}
