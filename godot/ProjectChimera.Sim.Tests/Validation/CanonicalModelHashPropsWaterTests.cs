#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 6.6 — a BLOCKING prop / water volume folds into <see cref="CanonicalModelHash"/> (AlgoVersion 7) because
    /// its footprint becomes blocked cells in the same <c>PathabilityGrid</c> (lockstep-critical, feeds movement →
    /// Position → SimChecksum). A NON-blocking prop, a camera, and every rotation/scale are cosmetic and leave the
    /// hash untouched. An absent/empty props+water set hashes IDENTICALLY to the post-rebaseline baseline, and a
    /// blocking-footprint change propagates into <see cref="StartStateHash"/> via the content seed.
    /// </summary>
    public class CanonicalModelHashPropsWaterTests
    {
        private static ScenarioData Base() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
        };

        [Fact]
        public void AlgoVersion_IsEight() => Assert.Equal(8, CanonicalModelHash.AlgoVersion); // Story 7.7: 7→8 (trigger/DSL fold; prop/water folds unchanged)

        [Fact]
        public void EmptyPropsWater_HashEqual_ToBaseline()
        {
            var baseline = Base();
            var withEmpties = Base();
            withEmpties.Props = System.Array.Empty<ScenarioProp>();
            withEmpties.Water = System.Array.Empty<ScenarioWater>();
            Assert.Equal(CanonicalModelHash.Compute(baseline), CanonicalModelHash.Compute(withEmpties));
        }

        [Fact]
        public void BlockingProp_MovesHash()
        {
            var baseline = Base();
            var m = Base();
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = 5f, Z = 5f, BlocksPathing = true } };
            Assert.NotEqual(CanonicalModelHash.Compute(baseline), CanonicalModelHash.Compute(m));
        }

        [Fact]
        public void WaterVolume_MovesHash()
        {
            var baseline = Base();
            var m = Base();
            m.Water = new[] { new ScenarioWater { X = -10f, Z = -10f, W = 20f, H = 20f } };
            Assert.NotEqual(CanonicalModelHash.Compute(baseline), CanonicalModelHash.Compute(m));
        }

        [Fact]
        public void NonBlockingProp_DoesNotMoveHash()
        {
            var baseline = Base();
            var m = Base();
            m.Props = new[] { new ScenarioProp { PropId = "tree", X = 5f, Z = 5f, BlocksPathing = false } };
            Assert.Equal(CanonicalModelHash.Compute(baseline), CanonicalModelHash.Compute(m));
        }

        [Fact]
        public void Camera_DoesNotMoveHash()
        {
            var baseline = Base();
            var m = Base();
            m.Cameras = new[] { new ScenarioCamera { Name = "c", X = 1f, Y = 2f, Z = 3f, Fov = 60f } };
            Assert.Equal(CanonicalModelHash.Compute(baseline), CanonicalModelHash.Compute(m));
        }

        [Fact]
        public void Rotation_Scale_DoNotMoveHash()
        {
            // A blocking prop with rotation/scale hashes IDENTICALLY to the same prop without them — footprint (cell) is
            // all that folds; rotation/scale are cosmetic.
            var plain = Base();
            plain.Props = new[] { new ScenarioProp { PropId = "rock", X = 5f, Z = 5f, BlocksPathing = true } };
            var spun = Base();
            spun.Props = new[] { new ScenarioProp { PropId = "rock", X = 5f, Z = 5f, BlocksPathing = true, Rot = 2.1f, Scale = 3f } };
            Assert.Equal(CanonicalModelHash.Compute(plain), CanonicalModelHash.Compute(spun));
        }

        [Fact]
        public void MovingBlockingProp_ToDifferentCell_MovesHash()
        {
            var a = Base(); a.Props = new[] { new ScenarioProp { PropId = "r", X = 5f, Z = 5f, BlocksPathing = true } };
            var b = Base(); b.Props = new[] { new ScenarioProp { PropId = "r", X = 40f, Z = -20f, BlocksPathing = true } };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void PropOrderIndependent_HashEqual()
        {
            var a = Base();
            a.Props = new[]
            {
                new ScenarioProp { PropId = "r1", X = 5f,  Z = 5f,  BlocksPathing = true },
                new ScenarioProp { PropId = "r2", X = 40f, Z = -20f, BlocksPathing = true },
            };
            var b = Base();
            b.Props = new[]
            {
                new ScenarioProp { PropId = "r2", X = 40f, Z = -20f, BlocksPathing = true },
                new ScenarioProp { PropId = "r1", X = 5f,  Z = 5f,  BlocksPathing = true },
            };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void BlockingProp_PropagatesInto_StartStateHash()
        {
            var baseline = Base();
            var m = Base();
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = 5f, Z = 5f, BlocksPathing = true } };
            var heroes = new HeroStore(); // empty — the difference must come from the content seed
            Assert.NotEqual(StartStateHash.Compute(baseline, heroes), StartStateHash.Compute(m, heroes));
        }
    }
}
