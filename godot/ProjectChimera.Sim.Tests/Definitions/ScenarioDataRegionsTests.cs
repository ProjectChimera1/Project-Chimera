#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.4 — <see cref="ScenarioData.Regions"/> round-trips through the canonical serializer, and — crucially —
    /// an absent/empty regions collection is OMITTED (null → no <c>regions</c> key) so a scenario with no regions
    /// serializes byte-for-byte identically to pre-feature (the "no map-package format change / moves no golden"
    /// guarantee). A wrong <c>JsonPropertyName</c> or a dropped <c>WhenWritingNull</c> would otherwise be invisible:
    /// Regions are deliberately excluded from every hash, so no checksum/golden test would catch a silent drop.
    /// </summary>
    public class ScenarioDataRegionsTests
    {
        /// <summary>
        /// DW-523 - the PRODUCTION scenario options (<see cref="ContentJson.ScenarioOptions"/>), not a hand-rolled
        /// replica that was looser than the real loader on the enum axis and missing its widget converter. A
        /// converter or strictness change at the choke point now reaches this suite the moment it lands.
        /// </summary>
        private static readonly JsonSerializerOptions Opt = ContentJson.ScenarioOptions;

        [Fact]
        public void Regions_RoundTrip_PreservingIdNameBounds()
        {
            var model = new ScenarioData
            {
                Id = "m", DisplayName = "M",
                Regions = new[]
                {
                    new ScenarioRegion { Id = "hill", Name = "The Hill", MinX = -10f, MinZ = -5f, MaxX = 10f, MaxZ = 5f },
                    new ScenarioRegion { Id = "base", Name = "P1 Base",  MinX = -50f, MinZ = -50f, MaxX = -30f, MaxZ = -30f },
                },
            };
            string json = ScenarioSerializer.Serialize(model);
            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);

            Assert.NotNull(back);
            Assert.NotNull(back!.Regions);
            Assert.Equal(2, back.Regions!.Length);
            Assert.Equal("hill", back.Regions[0].Id);
            Assert.Equal("The Hill", back.Regions[0].Name);
            Assert.Equal(-10f, back.Regions[0].MinX);
            Assert.Equal(-5f, back.Regions[0].MinZ);
            Assert.Equal(10f, back.Regions[0].MaxX);
            Assert.Equal(5f, back.Regions[0].MaxZ);
            Assert.Equal("base", back.Regions[1].Id);
        }

        [Fact]
        public void NoRegions_OmitsTheKey_ByteIdenticalToPreFeature()
        {
            // Default (null Regions): the `regions` key is not emitted ⇒ existing scenarios serialize byte-identically.
            string json = ScenarioSerializer.Serialize(new ScenarioData { Id = "flat", DisplayName = "Flat" });
            Assert.DoesNotContain("regions", json);

            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);
            Assert.NotNull(back);
            Assert.Null(back!.Regions);
        }

        [Fact]
        public void EmptyRegions_OmitsTheKey_MatchingNull()
        {
            // Review patch (P4): an EMPTY regions array normalizes to the null/absent case at the serializer
            // chokepoint, so it serializes with NO `regions` key — byte-identical to a scenario that never
            // authored regions (matching the null case above), moving no pinned scenario bytes.
            string json = ScenarioSerializer.Serialize(new ScenarioData
            {
                Id = "flat", DisplayName = "Flat", Regions = System.Array.Empty<ScenarioRegion>(),
            });
            Assert.DoesNotContain("regions", json);
        }

        [Fact]
        public void RegionsUsesSnakeCaseCornerKeys()
        {
            var model = new ScenarioData
            {
                Regions = new[] { new ScenarioRegion { Id = "r", Name = "R", MinX = 1f, MinZ = 2f, MaxX = 3f, MaxZ = 4f } },
            };
            string json = ScenarioSerializer.Serialize(model);
            Assert.Contains("\"regions\"", json);
            Assert.Contains("min_x", json);
            Assert.Contains("min_z", json);
            Assert.Contains("max_x", json);
            Assert.Contains("max_z", json);
        }
    }
}
