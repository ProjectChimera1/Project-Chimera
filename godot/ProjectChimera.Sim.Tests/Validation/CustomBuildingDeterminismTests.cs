#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 6.8 — a custom building's authored id folds through the EXISTING <see cref="CanonicalModelHash"/>
    /// <c>MixStr(b.Type)</c> fold with NO algorithm change: identical custom-building scenarios hash identically,
    /// differing custom ids hash differently, and a custom id at a slot/pos differs from a built-in id at the same
    /// slot/pos. The three <c>AlgoVersion</c> pins (CanonicalModelHash 7 / SimChecksum 15 / StartStateHash 2) are
    /// unchanged — reinterpreting <c>ScenarioBuilding.Type</c> as "enum name OR authored id" needs no new hash-folded
    /// field, so no golden moves. (The 23 per-tick goldens' byte-identity is enforced by the golden suite itself.)
    /// </summary>
    public class CustomBuildingDeterminismTests
    {
        private static ScenarioData WithBuilding(string type) => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            Buildings = new[] { new ScenarioBuilding { Type = type, Slot = 0, X = -40f, Z = 5f, PreBuilt = true } },
        };

        [Fact]
        public void AlgoVersions_AreUnchanged() // reinterpret-Type needs no bump
        {
            Assert.Equal(7, CanonicalModelHash.AlgoVersion);
            Assert.Equal(15, SimChecksum.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void IdenticalCustomScenarios_HashIdentically()
        {
            Assert.Equal(
                CanonicalModelHash.Compute(WithBuilding("watchtower")),
                CanonicalModelHash.Compute(WithBuilding("watchtower")));
        }

        [Fact]
        public void DifferingCustomIds_HashDifferently()
        {
            Assert.NotEqual(
                CanonicalModelHash.Compute(WithBuilding("watchtower")),
                CanonicalModelHash.Compute(WithBuilding("sky_forge")));
        }

        [Fact]
        public void CustomId_VsBuiltInName_AtSameSlotAndPos_HashDifferently()
        {
            Assert.NotEqual(
                CanonicalModelHash.Compute(WithBuilding("watchtower")),
                CanonicalModelHash.Compute(WithBuilding("CommandCenter")));
        }

        [Fact]
        public void EmptyBuildings_HashEqual_ToNoBuildingsBaseline()
        {
            // A custom-building feature must not perturb the baseline (no buildings) hash at all.
            var baseline = new ScenarioData { MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings };
            var empty    = new ScenarioData { MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
                                              Buildings = System.Array.Empty<ScenarioBuilding>() };
            Assert.Equal(CanonicalModelHash.Compute(baseline), CanonicalModelHash.Compute(empty));
        }
    }
}
