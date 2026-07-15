#nullable enable
using System.IO;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.5 — the authored pathability layer persists correctly: a painted bitset + slope config round-trip
    /// through <see cref="ScenarioSerializer"/>, an all-clear painted layer normalizes to null (key omitted, so a
    /// flat map is byte-identical to pre-feature), and the slope defaults are omitted when unset.
    /// </summary>
    public class ScenarioDataPathabilityTests
    {
        private static string PaintedBase64(params int[] cellIndices)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            foreach (int i in cellIndices) mask[i] = true;
            return PathabilityGrid.ToBase64(mask)!;
        }

        private static ScenarioData RoundTrip(ScenarioData model)
        {
            string path = Path.Combine(Path.GetTempPath(), "chimera_pathability_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ScenarioSerializer.SaveToFile(model, path);
                return ScenarioSerializer.LoadFromFile(path)!;
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void PaintedLayer_And_SlopeConfig_RoundTrip()
        {
            string b64 = PaintedBase64(0, 64, 8000, 16383);
            var model = new ScenarioData
            {
                MapBounds = 120f,
                PathabilityBlocked = b64,
                SlopeAutoBlock = true,
                SlopeBlockThreshold = 2.5f,
            };
            ScenarioData back = RoundTrip(model);
            Assert.Equal(b64, back.PathabilityBlocked);
            Assert.True(back.SlopeAutoBlock);
            Assert.Equal(2.5f, back.SlopeBlockThreshold);
            // The decoded mask survives byte-for-byte.
            Assert.Equal(PathabilityGrid.FromBase64(b64), PathabilityGrid.FromBase64(back.PathabilityBlocked));
        }

        [Fact]
        public void AllClearPaintedLayer_NormalizesToNull_KeyOmitted()
        {
            var allZero = PathabilityGrid.Pack(new bool[PathabilityGrid.CELL_COUNT]);
            var model = new ScenarioData
            {
                MapBounds = 120f,
                PathabilityBlocked = System.Convert.ToBase64String(allZero), // a non-null but all-zero bitset
            };
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("pathability_blocked", json);
            // And the caller's model is NOT permanently mutated by the serialize-time normalization.
            Assert.NotNull(model.PathabilityBlocked);
        }

        [Fact]
        public void FlatDefaultModel_OmitsAllPathabilityKeys()
        {
            var model = new ScenarioData { MapBounds = 120f };
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("pathability_blocked", json);
            Assert.DoesNotContain("slope_auto_block", json);
            Assert.DoesNotContain("slope_block_threshold", json);
        }

        [Fact]
        public void SlopeConfig_PresentOnlyWhenNonDefault()
        {
            var model = new ScenarioData { MapBounds = 120f, SlopeAutoBlock = true, SlopeBlockThreshold = 1.5f };
            string json = ScenarioSerializer.Serialize(model);
            Assert.Contains("slope_auto_block", json);
            Assert.Contains("slope_block_threshold", json);
        }
    }
}
