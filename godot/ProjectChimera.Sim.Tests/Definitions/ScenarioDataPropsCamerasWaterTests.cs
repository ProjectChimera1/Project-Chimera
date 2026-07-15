#nullable enable
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.6 — props/cameras/water round-trip through <see cref="ScenarioSerializer"/>, an absent/empty
    /// collection omits its key (byte-identical to a pre-feature map — no map-package format change), and the
    /// omit-when-default cosmetic fields (<c>rot</c>/<c>scale</c>/<c>blocks_pathing</c>) are omitted at their
    /// defaults. Rotation round-trips on every entry type.
    /// </summary>
    public class ScenarioDataPropsCamerasWaterTests
    {
        private static ScenarioData RoundTrip(ScenarioData model)
        {
            string path = Path.Combine(Path.GetTempPath(), "chimera_pcw_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ScenarioSerializer.SaveToFile(model, path);
                return ScenarioSerializer.LoadFromFile(path)!;
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void FlatModel_OmitsAllFourKeys()
        {
            var json = ScenarioSerializer.Serialize(new ScenarioData { MapBounds = 120f });
            Assert.DoesNotContain("\"props\"", json);
            Assert.DoesNotContain("\"cameras\"", json);
            Assert.DoesNotContain("\"water\"", json);
        }

        [Fact]
        public void EmptyCollections_NormalizeToNull_KeysOmitted()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                Props   = System.Array.Empty<ScenarioProp>(),
                Cameras = System.Array.Empty<ScenarioCamera>(),
                Water   = System.Array.Empty<ScenarioWater>(),
            };
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("\"props\"", json);
            Assert.DoesNotContain("\"cameras\"", json);
            Assert.DoesNotContain("\"water\"", json);
            // The caller's model is NOT permanently mutated by the serialize-time normalization.
            Assert.NotNull(model.Props);
            Assert.NotNull(model.Cameras);
            Assert.NotNull(model.Water);
        }

        [Fact]
        public void Props_RoundTrip_WithBlockingAndCosmeticFields()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                Props = new[]
                {
                    new ScenarioProp { PropId = "tree", X = 5f, Z = -7f, Rot = 1.5f, Scale = 2.25f, BlocksPathing = true },
                    new ScenarioProp { PropId = "rock", X = -3f, Z = 4f }, // all cosmetic defaults
                },
            };
            ScenarioData back = RoundTrip(model);
            Assert.Equal(2, back.Props!.Length);
            Assert.Equal("tree", back.Props[0].PropId);
            Assert.Equal(5f, back.Props[0].X);
            Assert.Equal(-7f, back.Props[0].Z);
            Assert.Equal(1.5f, back.Props[0].Rot);
            Assert.Equal(2.25f, back.Props[0].Scale);
            Assert.True(back.Props[0].BlocksPathing);
            // The second prop keeps its cosmetic defaults.
            Assert.Equal(0f, back.Props[1].Rot);
            Assert.Null(back.Props[1].Scale);
            Assert.False(back.Props[1].BlocksPathing);
        }

        [Fact]
        public void PropCosmeticDefaults_AreOmitted()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                Props = new[] { new ScenarioProp { PropId = "rock", X = 1f, Z = 2f } },
            };
            string json = ScenarioSerializer.Serialize(model);
            Assert.Contains("\"props\"", json);
            Assert.DoesNotContain("\"rot\"", json);
            Assert.DoesNotContain("\"scale\"", json);
            Assert.DoesNotContain("\"blocks_pathing\"", json);
        }

        [Fact]
        public void Cameras_RoundTrip()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                Cameras = new[]
                {
                    new ScenarioCamera { Name = "intro", X = 1f, Y = 20f, Z = 3f, TargetX = 0f, TargetY = 0f, TargetZ = 0f, Fov = 55f },
                },
            };
            ScenarioData back = RoundTrip(model);
            Assert.Single(back.Cameras!);
            var c = back.Cameras![0];
            Assert.Equal("intro", c.Name);
            Assert.Equal(20f, c.Y);
            Assert.Equal(55f, c.Fov);
            Assert.Equal(3f, c.Z);
        }

        [Fact]
        public void Water_RoundTrip()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                Water = new[] { new ScenarioWater { X = -10f, Z = -10f, W = 20f, H = 30f, Y = -1.5f } },
            };
            ScenarioData back = RoundTrip(model);
            Assert.Single(back.Water!);
            var w = back.Water![0];
            Assert.Equal(-10f, w.X);
            Assert.Equal(20f, w.W);
            Assert.Equal(30f, w.H);
            Assert.Equal(-1.5f, w.Y);
        }

        [Fact]
        public void Rotation_RoundTrips_OnEveryEntryType()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                Buildings     = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 1f, Z = 1f, Rot = 0.75f } },
                Units         = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = 2f, Z = 2f, Rot = 1.25f } },
                ResourceNodes = new[] { new ScenarioResourceNode { X = 3f, Z = 3f, Rot = 2.5f } },
                Props         = new[] { new ScenarioProp { PropId = "tree", X = 4f, Z = 4f, Rot = 3.1f } },
            };
            ScenarioData back = RoundTrip(model);
            Assert.Equal(0.75f, back.Buildings![0].Rot);
            Assert.Equal(1.25f, back.Units![0].Rot);
            Assert.Equal(2.5f, back.ResourceNodes![0].Rot);
            Assert.Equal(3.1f, back.Props![0].Rot);
        }

        [Fact]
        public void Rotation_Default_IsOmitted_OnEveryEntryType()
        {
            var model = new ScenarioData
            {
                MapBounds     = 120f,
                Buildings     = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 1f, Z = 1f } },
                Units         = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = 2f, Z = 2f } },
                ResourceNodes = new[] { new ScenarioResourceNode { X = 3f, Z = 3f } },
            };
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("\"rot\"", json);
        }
    }
}
