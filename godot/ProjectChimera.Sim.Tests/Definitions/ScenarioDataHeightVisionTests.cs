#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.3 (review pass 1, VG3): the two new <see cref="ScenarioData"/> fields (<c>height_advantage_vision</c>,
    /// <c>height_vision_bonus_per_step</c>) round-trip through the canonical serializer, and — crucially — are OMITTED
    /// when default so every existing scenario serializes byte-identically (the "moves no scenario golden" guarantee).
    /// A wrong <c>JsonPropertyName</c> or a dropped <c>WhenWritingDefault</c> would otherwise be invisible: the fields
    /// are deliberately not folded into any hash, so no checksum/golden test would catch a silent drop on load.
    /// </summary>
    public class ScenarioDataHeightVisionTests
    {
        [Fact]
        public void HeightVisionFields_RoundTripThroughTheSerializer()
        {
            var s = new ScenarioData
            {
                Id = "hv", DisplayName = "HV",
                HeightAdvantageVision = true, HeightVisionBonusPerStep = 4.5f,
            };
            string path = System.IO.Path.GetTempFileName();
            try
            {
                ScenarioSerializer.SaveToFile(s, path);
                ScenarioData? back = ScenarioSerializer.LoadFromFile(path);
                Assert.NotNull(back);
                Assert.True(back!.HeightAdvantageVision);
                Assert.Equal(4.5f, back.HeightVisionBonusPerStep);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void DefaultScenario_OmitsBothHeightVisionKeys()
        {
            // Default (feature off): neither key is emitted ⇒ existing scenarios serialize byte-identically (no golden move).
            string json = ScenarioSerializer.Serialize(new ScenarioData { Id = "flat", DisplayName = "Flat" });
            Assert.DoesNotContain("height_advantage_vision", json);
            Assert.DoesNotContain("height_vision_bonus_per_step", json);
        }
    }
}
