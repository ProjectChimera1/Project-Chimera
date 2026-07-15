#nullable enable
using ProjectChimera.Core;              // HeroStore
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 6.4 — named regions are EXCLUDED from <see cref="CanonicalModelHash"/> / <see cref="StartStateHash"/>
    /// on the SAME basis as <see cref="ScenarioData.Triggers"/> (consumed only by the trigger system, no other sim
    /// consumer). This keeps 6.4 GOLDEN-NEUTRAL: adding/removing/changing regions must NOT move either hash and
    /// must NOT bump either AlgoVersion — so the no-regions serialization stays byte-identical and no golden
    /// re-records. Teeth: a future accidental fold of Regions into either hash turns these RED.
    /// </summary>
    public class CanonicalModelHashRegionExclusionTests
    {
        private static ScenarioData BaseModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
        };

        [Fact]
        public void AlgoVersions_Unchanged() // 7 for canonical (Story 6.6 blocking prop/water), 2 for start-state
        {
            // Story 6.6 bumped CanonicalModelHash 6→7 to fold the blocking-prop + water footprints — a SEPARATE
            // concern from Regions, which remain EXCLUDED (the with/without-regions equality below still holds at v7).
            Assert.Equal(7, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void AddingRegions_DoesNotChangeCanonicalHash()
        {
            var withoutRegions = BaseModel();
            var withRegions = BaseModel();
            withRegions.Regions = new[]
            {
                new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
                new ScenarioRegion { Id = "base", Name = "Base", MinX = -50f, MinZ = -50f, MaxX = -30f, MaxZ = -30f },
            };
            Assert.Equal(CanonicalModelHash.Compute(withoutRegions), CanonicalModelHash.Compute(withRegions));
        }

        [Fact]
        public void ChangingRegionBounds_DoesNotChangeCanonicalHash()
        {
            var a = BaseModel();
            a.Regions = new[] { new ScenarioRegion { Id = "r", Name = "R", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f } };
            var b = BaseModel();
            b.Regions = new[] { new ScenarioRegion { Id = "r", Name = "R", MinX = -99f, MinZ = -99f, MaxX = 99f, MaxZ = 99f } };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void NullRegions_And_EmptyRegions_HashIdenticallyToOneAnother()
        {
            var nullRegions = BaseModel(); // Regions == null
            var emptyRegions = BaseModel();
            emptyRegions.Regions = System.Array.Empty<ScenarioRegion>();
            Assert.Equal(CanonicalModelHash.Compute(nullRegions), CanonicalModelHash.Compute(emptyRegions));
        }

        [Fact]
        public void AddingRegions_DoesNotChangeStartStateHash()
        {
            var withoutRegions = BaseModel();
            var withRegions = BaseModel();
            withRegions.Regions = new[]
            {
                new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
            };
            var heroes = new HeroStore(); // empty → no hero rows folded
            Assert.Equal(StartStateHash.Compute(withoutRegions, heroes), StartStateHash.Compute(withRegions, heroes));
        }
    }
}
