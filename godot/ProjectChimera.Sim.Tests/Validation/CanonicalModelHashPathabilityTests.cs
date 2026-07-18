#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 6.5 — the pathability layer folds into <see cref="CanonicalModelHash"/> (AlgoVersion 6) because it is
    /// lockstep-critical (feeds movement → Position → SimChecksum). An absent paint + slope-off hashes IDENTICALLY
    /// to the pre-6.5 baseline (digest 0 / toggle 0 / threshold 0), so every existing scenario is unaffected; a real
    /// painted layer or slope-config change moves the handshake hash, and propagates into <see cref="StartStateHash"/>
    /// via the content seed.
    /// </summary>
    public class CanonicalModelHashPathabilityTests
    {
        private static ScenarioData Base() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
        };

        private static string PaintedBase64(params int[] cells)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            foreach (int c in cells) mask[c] = true;
            return PathabilityGrid.ToBase64(mask)!;
        }

        [Fact]
        public void AlgoVersion_IsEleven() => Assert.Equal(11, CanonicalModelHash.AlgoVersion); // 7.5 re-land merge: 9→10 (custom-events fold); Story 7.9: 10→11 (Button fold). Pathability folds below unchanged.

        [Fact]
        public void AbsentPaint_And_AllClearPaint_HashEqual()
        {
            var absent = Base();                              // PathabilityBlocked == null
            var allClear = Base();
            allClear.PathabilityBlocked = System.Convert.ToBase64String(PathabilityGrid.Pack(new bool[PathabilityGrid.CELL_COUNT]));
            Assert.Equal(CanonicalModelHash.Compute(absent), CanonicalModelHash.Compute(allClear));
        }

        [Fact]
        public void PaintedLayer_MovesHash()
        {
            var baseModel = Base();
            var painted = Base();
            painted.PathabilityBlocked = PaintedBase64(4096);
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(painted));
        }

        [Fact]
        public void DifferentPaintedLayers_HashDifferently()
        {
            var a = Base(); a.PathabilityBlocked = PaintedBase64(100);
            var b = Base(); b.PathabilityBlocked = PaintedBase64(200);
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void SlopeAutoBlock_And_Threshold_MoveHash()
        {
            var baseModel = Base();
            var toggled = Base(); toggled.SlopeAutoBlock = true;
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(toggled));

            var thr = Base(); thr.SlopeAutoBlock = true; thr.SlopeBlockThreshold = 2.5f;
            Assert.NotEqual(CanonicalModelHash.Compute(toggled), CanonicalModelHash.Compute(thr));
        }

        [Fact]
        public void PathabilityChange_PropagatesInto_StartStateHash()
        {
            var baseModel = Base();
            var painted = Base();
            painted.PathabilityBlocked = PaintedBase64(4096);
            var heroes = new HeroStore(); // empty; the difference must come from the content seed
            Assert.NotEqual(StartStateHash.Compute(baseModel, heroes), StartStateHash.Compute(painted, heroes));
        }

        [Fact]
        public void Hash_IsStableAndNonZero_WithPaintedLayer()
        {
            var m = Base();
            m.PathabilityBlocked = PaintedBase64(1, 2, 3);
            ulong once = CanonicalModelHash.Compute(m);
            Assert.Equal(once, CanonicalModelHash.Compute(m));
            Assert.NotEqual(0UL, once);
        }
    }
}
