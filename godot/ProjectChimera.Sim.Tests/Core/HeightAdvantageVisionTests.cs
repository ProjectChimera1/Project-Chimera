#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 6.3 — the height-advantage vision behaviour, verified Godot-free: construct a world, run the as-built
    /// <see cref="FogOfWarSystem"/>, and assert (a) toggle ON ⇒ an elevated unit stamps a STRICTLY larger Visible
    /// circle than an equal-<see cref="EntityWorld.VisionRange"/> ground unit, (b) toggle OFF ⇒ the stamped fog Grid
    /// is BYTE-FOR-BYTE identical to a base-VisionRange-only Grid (the bonus term is not applied at all), and (c) the
    /// pure <see cref="EntityWorld.EffectiveVisionRange"/> math (base + floor(elevation)·bonus, clamped ≥ 0). The new
    /// term is computed entirely in <see cref="Fixed"/> and merges BEFORE the existing per-tick <c>.ToFloat()</c>
    /// boundary — no rewrite of the verified StampCircle path.
    /// </summary>
    public class HeightAdvantageVisionTests
    {
        /// <summary>Build a one-P1-unit world at the origin with an explicit elevation + vision config.</summary>
        private static EntityWorld MakeWorld(Fixed elevation, bool toggle, Fixed bonus, Fixed vision)
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.VisionRange[id]             = vision;
            w.Elevation[id]               = elevation; // Create sampled the null grid → Zero; overwrite explicitly
            w.HeightAdvantageVision       = toggle;
            w.HeightVisionBonusPerStep    = bonus;
            return w;
        }

        private static int CountVisible(FogOfWarSystem fog)
        {
            int c = 0;
            foreach (byte b in fog.Grid) if (b == FogOfWarSystem.VISIBLE) c++;
            return c;
        }

        [Fact]
        public void ToggleOn_ElevatedUnit_StampsStrictlyLargerCircleThanGroundUnit()
        {
            // base 8, +4 per step. Elevated at 2 steps ⇒ effective 16; ground at 0 ⇒ effective 8.
            var elevated = MakeWorld(Fixed.FromInt(2), toggle: true, bonus: Fixed.FromInt(4), vision: Fixed.FromInt(8));
            var ground   = MakeWorld(Fixed.Zero,       toggle: true, bonus: Fixed.FromInt(4), vision: Fixed.FromInt(8));

            var fogE = new FogOfWarSystem(Faction.Player1); fogE.Tick(elevated, Fixed.Zero);
            var fogG = new FogOfWarSystem(Faction.Player1); fogG.Tick(ground,   Fixed.Zero);

            Assert.True(CountVisible(fogE) > CountVisible(fogG),
                $"Elevated unit should stamp a larger Visible area (got elevated={CountVisible(fogE)}, ground={CountVisible(fogG)}).");
        }

        [Fact]
        public void ToggleOff_AnyElevation_ProducesGridByteIdenticalToBaseVisionOnly()
        {
            // Toggle OFF ⇒ the bonus is never applied, so an elevated unit's Grid must equal a base-vision-only Grid.
            var offElevated = MakeWorld(Fixed.FromInt(5), toggle: false, bonus: Fixed.FromInt(4), vision: Fixed.FromInt(8));
            var baseOnly    = MakeWorld(Fixed.Zero,       toggle: false, bonus: Fixed.Zero,       vision: Fixed.FromInt(8));

            var fogOff  = new FogOfWarSystem(Faction.Player1); fogOff.Tick(offElevated, Fixed.Zero);
            var fogBase = new FogOfWarSystem(Faction.Player1); fogBase.Tick(baseOnly,   Fixed.Zero);

            Assert.Equal(fogBase.Grid, fogOff.Grid); // byte-for-byte identical
        }

        [Fact]
        public void EffectiveVisionRange_TogglesAndFloorsSteps()
        {
            // Toggle ON: base + floor(elevation)·bonus.
            var twoSteps = MakeWorld(Fixed.FromInt(2), toggle: true, bonus: Fixed.FromInt(4), vision: Fixed.FromInt(8));
            Assert.Equal((Fixed.FromInt(8) + Fixed.FromInt(2) * Fixed.FromInt(4)).Raw, twoSteps.EffectiveVisionRange(0).Raw); // 16

            // Ground unit (elevation 0) ⇒ exactly the base range.
            var ground = MakeWorld(Fixed.Zero, toggle: true, bonus: Fixed.FromInt(4), vision: Fixed.FromInt(8));
            Assert.Equal(Fixed.FromInt(8).Raw, ground.EffectiveVisionRange(0).Raw);

            // A sub-step (< 1 world unit) elevation floors to 0 steps ⇒ no bonus.
            var subStep = MakeWorld(Fixed.Half, toggle: true, bonus: Fixed.FromInt(4), vision: Fixed.FromInt(8));
            Assert.Equal(Fixed.FromInt(8).Raw, subStep.EffectiveVisionRange(0).Raw);

            // Toggle OFF ⇒ base range regardless of elevation/bonus.
            var off = MakeWorld(Fixed.FromInt(3), toggle: false, bonus: Fixed.FromInt(4), vision: Fixed.FromInt(8));
            Assert.Equal(Fixed.FromInt(8).Raw, off.EffectiveVisionRange(0).Raw);
        }
    }
}
