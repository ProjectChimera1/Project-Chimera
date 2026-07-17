#nullable enable
using ProjectChimera.Core;              // HeroStore
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 — INVERTED from the Story 6.4 exclusion tests this file used to hold (renamed
    /// …RegionExclusionTests → …RegionFoldTests to match): named regions are now
    /// FOLDED into <see cref="CanonicalModelHash"/> (v8) — and, via the content seed, into
    /// <see cref="StartStateHash"/> — because a region is a trigger input that gates sim-mutating actions, and the
    /// "fold with Triggers when 7.7 lands" promise is discharged. Teeth run BOTH ways: a region-geometry change
    /// must move the hash (sensitivity), while array ORDER and null-vs-empty must not (canonical total order).
    /// </summary>
    public class CanonicalModelHashRegionFoldTests
    {
        private static ScenarioData BaseModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
        };

        [Fact]
        public void AlgoVersions_Unchanged() // 8 for canonical (Story 7.7 trigger/DSL fold), 2 for start-state
        {
            // Story 7.7 bumped CanonicalModelHash 7→8 (the ONE named re-baseline). StartStateHash's fold structure
            // is unchanged — its VALUE moves via the canonical seed, so its AlgoVersion stays 2 (the v5/v6/v7
            // precedent).
            Assert.Equal(9, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void AddingRegions_ChangesCanonicalHash()
        {
            var withoutRegions = BaseModel();
            var withRegions = BaseModel();
            withRegions.Regions = new[]
            {
                new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
                new ScenarioRegion { Id = "base", Name = "Base", MinX = -50f, MinZ = -50f, MaxX = -30f, MaxZ = -30f },
            };
            Assert.NotEqual(CanonicalModelHash.Compute(withoutRegions), CanonicalModelHash.Compute(withRegions));
        }

        [Fact]
        public void ChangingRegionBounds_ChangesCanonicalHash()
        {
            var a = BaseModel();
            a.Regions = new[] { new ScenarioRegion { Id = "r", Name = "R", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f } };
            var b = BaseModel();
            b.Regions = new[] { new ScenarioRegion { Id = "r", Name = "R", MinX = -99f, MinZ = -99f, MaxX = 99f, MaxZ = 99f } };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void RegionName_IsCosmetic_DoesNotChangeHash()
        {
            // Name is a display label (the DisplayName basis) — the sim resolves regions by Id only.
            var a = BaseModel();
            a.Regions = new[] { new ScenarioRegion { Id = "r", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f } };
            var b = BaseModel();
            b.Regions = new[] { new ScenarioRegion { Id = "r", Name = "Totally Different", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f } };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void RegionArrayOrder_DoesNotChangeHash()
        {
            // The fold is TOTAL-ordered (Id ordinal, then quantized corners) — JSON array order cannot move it.
            var r1 = new ScenarioRegion { Id = "a", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f };
            var r2 = new ScenarioRegion { Id = "b", MinX = -50f, MinZ = -50f, MaxX = -30f, MaxZ = -30f };
            var fwd = BaseModel(); fwd.Regions = new[] { r1, r2 };
            var rev = BaseModel(); rev.Regions = new[] { r2, r1 };
            Assert.Equal(CanonicalModelHash.Compute(fwd), CanonicalModelHash.Compute(rev));
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
        public void AddingRegions_ChangesStartStateHash()
        {
            // StartStateHash folds the canonical value as its content seed, so region changes propagate.
            var withoutRegions = BaseModel();
            var withRegions = BaseModel();
            withRegions.Regions = new[]
            {
                new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
            };
            var heroes = new HeroStore(); // empty → no hero rows folded
            Assert.NotEqual(StartStateHash.Compute(withoutRegions, heroes), StartStateHash.Compute(withRegions, heroes));
        }
    }
}
