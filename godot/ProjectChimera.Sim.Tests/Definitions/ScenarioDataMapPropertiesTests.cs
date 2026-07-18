#nullable enable
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.7 — the map-authoring metadata (Author/Description/SuggestedPlayers) is COSMETIC: it round-trips
    /// through save/load, is OMITTED byte-for-byte when default, and is EXCLUDED from BOTH determinism hashes
    /// (CanonicalModelHash + StartStateHash) so it can never move a golden or false-reject a lobby handshake. Also
    /// pins the CreateBlank factory (valid blank map) and the unchanged AlgoVersions.
    /// </summary>
    public class ScenarioDataMapPropertiesTests
    {
        private static ScenarioData BaseModel() => new()
        {
            Id = "m", DisplayName = "Map",
            MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -40f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://a.json", StartOre = 200f, BaseX =  40f, BaseZ = 0f },
            },
            ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
            Buildings     = System.Array.Empty<ScenarioBuilding>(),
            Units         = System.Array.Empty<ScenarioUnit>(),
            Triggers      = System.Array.Empty<TriggerDefinition>(),
        };

        private static ScenarioData WithMetadata()
        {
            var m = BaseModel();
            m.Author = "Alec";
            m.Description = "A test map with a longer description.";
            m.SuggestedPlayers = 4;
            return m;
        }

        // ── Round-trip ────────────────────────────────────────────────────────

        [Fact]
        public void Metadata_RoundTripsThroughSaveLoad()
        {
            string p = Path.Combine(Path.GetTempPath(), "chimera_mapprops_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ScenarioSerializer.SaveToFile(WithMetadata(), p);
                var loaded = ScenarioSerializer.LoadFromFile(p);
                Assert.NotNull(loaded);
                Assert.Equal("Alec", loaded!.Author);
                Assert.Equal("A test map with a longer description.", loaded.Description);
                Assert.Equal(4, loaded.SuggestedPlayers);
            }
            finally { if (File.Exists(p)) File.Delete(p); }
        }

        [Fact]
        public void DefaultMetadata_KeysAreOmitted()
        {
            // A scenario with none of the new fields must not emit their keys — byte-identical to a pre-6.7 map.
            string json = ScenarioSerializer.Serialize(BaseModel());
            Assert.DoesNotContain("\"author\"", json);
            Assert.DoesNotContain("\"description\"", json);
            Assert.DoesNotContain("\"suggested_players\"", json);
        }

        [Fact]
        public void SetMetadata_KeysArePresent()
        {
            string json = ScenarioSerializer.Serialize(WithMetadata());
            Assert.Contains("\"author\"", json);
            Assert.Contains("\"description\"", json);
            Assert.Contains("\"suggested_players\"", json);
        }

        // ── Hash exclusion (cosmetic) ───────────────────────────────────────────

        [Fact]
        public void CanonicalModelHash_IdenticalWithAndWithoutMetadata()
            => Assert.Equal(CanonicalModelHash.Compute(BaseModel()),
                            CanonicalModelHash.Compute(WithMetadata()));

        [Fact]
        public void StartStateHash_IdenticalWithAndWithoutMetadata()
        {
            var heroes = new HeroStore();
            Assert.Equal(StartStateHash.Compute(BaseModel(), heroes),
                         StartStateHash.Compute(WithMetadata(), heroes));
        }

        [Fact]
        public void AlgoVersions_Unchanged()
        {
            Assert.Equal(12, CanonicalModelHash.AlgoVersion); // Story 7.5 (merge): custom-event registry folded (9→10); Story 7.9: Button fold (10→11)
            Assert.Equal(19, SimChecksum.AlgoVersion); // Story 7.5 (landed via merge): DslEventQueue folded (17→18)
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        // ── CreateBlank factory ─────────────────────────────────────────────────

        [Fact]
        public void CreateBlank_ProducesAValidScenario()
        {
            var blank = ScenarioData.CreateBlank("New Map", "Alec", "desc", 3, MapSize.Medium);
            var result = new ScenarioValidator().Validate(blank);
            Assert.True(result.Ok, result.Error);
        }

        [Fact]
        public void CreateBlank_SetsBoundsFromSizeAndMetadata()
        {
            var blank = ScenarioData.CreateBlank("New Map", "Alec", "d", 2, MapSize.Small);
            Assert.Equal(MapSizes.ToBounds(MapSize.Small), blank.MapBounds);
            Assert.Equal("Alec", blank.Author);
            Assert.Equal(2, blank.SuggestedPlayers);
            Assert.Equal(2, blank.PlayerSlots.Length);
        }

        [Fact]
        public void CreateBlank_ClampsSuggestedPlayersIntoRange()
        {
            var low  = ScenarioData.CreateBlank("m", suggestedPlayers: 1);
            var high = ScenarioData.CreateBlank("m", suggestedPlayers: 9);
            Assert.Equal(2, low.SuggestedPlayers);
            Assert.Equal(2, low.PlayerSlots.Length);
            Assert.Equal(4, high.SuggestedPlayers);
            Assert.Equal(4, high.PlayerSlots.Length);
            // Never emits an invalid scenario, even from an out-of-range request.
            Assert.True(new ScenarioValidator().Validate(low).Ok);
            Assert.True(new ScenarioValidator().Validate(high).Ok);
        }

        [Fact]
        public void CreateBlank_EachSupportedSize_IsValid()
        {
            foreach (MapSize s in MapSizes.All)
            {
                var blank = ScenarioData.CreateBlank("m", size: s);
                Assert.True(new ScenarioValidator().Validate(blank).Ok);
                Assert.Equal(MapSizes.ToBounds(s), blank.MapBounds);
            }
        }

        [Fact]
        public void CreateBlank_ProducesNoAdvisories()
        {
            // Review pass 2 — pin the docstring's "no spurious below-suggested advisory on a freshly created map"
            // promise directly: the clamped placed-slot count equals SuggestedPlayers and every base sits inside the
            // bounds, so a brand-new map is advisory-clean across the whole supported set and player range.
            var validator = new ScenarioValidator();
            foreach (MapSize s in MapSizes.All)
                for (int players = 2; players <= 4; players++)
                {
                    var blank = ScenarioData.CreateBlank("m", suggestedPlayers: players, size: s);
                    Assert.Empty(validator.CollectAdvisories(blank));
                }
        }
    }
}
